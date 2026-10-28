using System.Globalization;
using System.Text;
using System.Text.Json;
using FactorioMCP.Rcon;

namespace FactorioMCP.Services;

/// <summary>
/// Pathfinding service using Factorio's A* pathfinder with in-game waypoint following.
///
/// Design:
///   1. C# requests a path (async via request_path) and receives it from a minimal event handler.
///   2. C# hands the path to <c>storage.walk_paths[player index]</c>; the shared on_tick handler
///      (<see cref="FactorioService.LuaOnTickHandler"/>) steers the character along it every tick, so steering
///      never lags behind RCON round trips. It marks the walk arrived (within tolerance of the target)
///      or stuck (no progress toward the current waypoint for 45 ticks).
///   3. C# polls the walk state, plans a new path from the current position when the walk is stuck,
///      and reports the result.
/// </summary>
internal sealed class PathfindingService(RconClient rcon, RestrictedAreaGuard? restrictedArea = null)
{
    /// <summary>
    /// Poll interval for the walk state during walking. Can be overridden in tests.
    /// </summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>New paths planned from the current position when a walk gets stuck, before reporting stuck.</summary>
    internal const int MaxReplans = 2;

    /// <summary>Longest walk in tiles (straight-line distance to the target); longer walks return status too_far.</summary>
    internal const int MaxWalkDistance = 1000;

    private bool _pathHandlerInstalled;

    // ── Public API ──────────────────────────────────────────────────

    /// <summary>
    /// Walk to a target position using Factorio's A* pathfinder.
    /// Returns a structured JSON result with status (arrived/stuck/timeout/no_path/too_far/interrupted/restricted_area),
    /// final position, and distance. A walk that the restricted area blocked carries a restricted_area_note.
    /// </summary>
    public async Task<string> WalkToAsync(
        double targetX, double targetY,
        double tolerance,
        double timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var result = await WalkUncheckedAsync(targetX, targetY, tolerance, timeoutSeconds, cancellationToken);
        return restrictedArea is null ? result : restrictedArea.AnnotateWalk(result);
    }

    private async Task<string> WalkUncheckedAsync(
        double targetX, double targetY,
        double tolerance,
        double timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(timeoutSeconds, 0.5));

        // Check if already at target
        var (px, py) = await GetPositionAsync(cancellationToken);
        var dist = Distance(px, py, targetX, targetY);

        if (dist <= tolerance)
            return FormatResult("arrived", px, py, targetX, targetY, dist, tolerance);

        // Covers walks to targets the tools found themselves (move_to_entity, gather_resource, ...)
        if (restrictedArea?.Blocks(targetX, targetY) == true)
            return FormatResult("restricted_area", px, py, targetX, targetY, dist, tolerance);

        // Factorio's pathfinder gives up on long distances (live game: no_path for about 1600 tiles)
        if (dist > MaxWalkDistance)
            return FormatResult("too_far", px, py, targetX, targetY, dist, tolerance)[..^1] + string.Create(CultureInfo.InvariantCulture,
                $$""","max_distance":{{MaxWalkDistance}},"message":"The target is {{dist:0}} tiles away; one walk covers at most {{MaxWalkDistance}} tiles. Walk to a point on the way first."}""");

        // Ensure path handler is installed (once per session)
        await EnsurePathHandlerInstalledAsync(cancellationToken);

        var (pathStatus, waypoints) = await FindPathAsync(targetX, targetY, tolerance, cancellationToken);
        if (waypoints is null)
            return FormatResult(pathStatus, px, py, targetX, targetY, dist, tolerance);

        return await FollowPathAsync(waypoints, targetX, targetY, tolerance, deadline, cancellationToken);
    }

    /// <summary>
    /// Get the player's current position.
    /// </summary>
    public Task<string> GetPlayerPositionAsync(CancellationToken cancellationToken = default)
    {
        return rcon.ExecuteLuaAsync("""
            local p = game.connected_players[1].position
            rcon.print('{"x":'..p.x..',"y":'..p.y..'}')
            """, cancellationToken);
    }

    // ── Path planning and following ─────────────────────────────────

    /// <summary>
    /// Request a path from the current position and wait for it.
    /// Returns ("ok", waypoints), or (status, null) with status no_path or no_character.
    /// </summary>
    private async Task<(string Status, List<(double x, double y)>? Waypoints)> FindPathAsync(
        double targetX, double targetY, double tolerance, CancellationToken cancellationToken)
    {
        var requestId = await RequestPathAsync(targetX, targetY, tolerance, cancellationToken);
        if (requestId < 0)
            return ("no_character", null);

        // Wait for path computation (usually 1-2 game ticks)
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            var (status, path) = await GetPathResultAsync(requestId, cancellationToken);

            if (status == "ok" && path is { Count: > 0 })
                return ("ok", path);
            if (status == "no_path")
                return ("no_path", null);
            if (status == "busy")
            {
                // Pathfinder overloaded — old request already cleaned up by
                // GetPathResultAsync (non-ok entries are nil'd), just retry
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                requestId = await RequestPathAsync(targetX, targetY, tolerance, cancellationToken);
                if (requestId < 0)
                    return ("no_character", null);
            }
        }

        return ("no_path", null);
    }

    /// <summary>
    /// Let the on_tick handler walk the path, poll its state, and plan a new path when it is stuck.
    /// "arrived" is reported only within <paramref name="tolerance"/> of the target.
    /// </summary>
    private async Task<string> FollowPathAsync(
        List<(double x, double y)> waypoints,
        double targetX, double targetY,
        double tolerance,
        DateTime deadline,
        CancellationToken cancellationToken)
    {
        await StartFollowingAsync(waypoints, targetX, targetY, tolerance, cancellationToken);
        var replans = 0;

        try
        {
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (px, py, status) = await GetWalkStateAsync(cancellationToken);
                var dist = Distance(px, py, targetX, targetY);

                if (dist <= tolerance)
                {
                    await StopWalkingAsync(cancellationToken);
                    return FormatResult("arrived", px, py, targetX, targetY, dist, tolerance);
                }

                switch (status)
                {
                    case "no_handler":
                        // The game was reloaded (script handlers are not saved): install them again and go on
                        _pathHandlerInstalled = false;
                        await EnsurePathHandlerInstalledAsync(cancellationToken);
                        await StartFollowingAsync(waypoints, targetX, targetY, tolerance, cancellationToken);
                        break;

                    case "none":
                        // Another command stopped this walk
                        await StopWalkingAsync(cancellationToken);
                        return FormatResult("interrupted", px, py, targetX, targetY, dist, tolerance);

                    case "no_character":
                        await StopWalkingAsync(cancellationToken);
                        return FormatResult("no_character", px, py, targetX, targetY, dist, tolerance);

                    case "stuck" or "arrived":
                        // "arrived" while outside the tolerance: the path ended short of the target
                        if (replans >= MaxReplans)
                        {
                            await StopWalkingAsync(cancellationToken);
                            return FormatResult("stuck", px, py, targetX, targetY, dist, tolerance);
                        }
                        replans++;
                        var (pathStatus, path) = await FindPathAsync(targetX, targetY, tolerance, cancellationToken);
                        if (path is null)
                        {
                            await StopWalkingAsync(cancellationToken);
                            return FormatResult(pathStatus == "no_path" ? "stuck" : pathStatus, px, py, targetX, targetY, dist, tolerance);
                        }
                        waypoints = path;
                        await StartFollowingAsync(waypoints, targetX, targetY, tolerance, cancellationToken);
                        break;
                }

                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            try { await StopWalkingAsync(CancellationToken.None); } catch { }
            throw;
        }

        // Timeout
        await StopWalkingAsync(cancellationToken);
        var (finalX, finalY) = await GetPositionAsync(cancellationToken);
        var finalDist = Distance(finalX, finalY, targetX, targetY);
        return FormatResult("timeout", finalX, finalY, targetX, targetY, finalDist, tolerance);
    }

    // ── Lua Commands ────────────────────────────────────────────────

    /// <summary>
    /// Install the path result handler and the shared on_tick handler. Script handlers are not saved with the game,
    /// so <c>__mcp_walk_handlers</c> (a Lua global, also lost on reload) tells whether they are installed.
    /// </summary>
    private const string LuaInstallWalkHandlers = $$"""
        storage.nav_results = storage.nav_results or {}
        script.on_event(defines.events.on_script_path_request_finished, function(event)
            if event.path and #event.path > 0 then
                storage.nav_results[event.id] = {status = "ok", path = event.path, tick = game.tick}
            elseif event.try_again_later then
                storage.nav_results[event.id] = {status = "busy", tick = game.tick}
            else
                storage.nav_results[event.id] = {status = "no_path", tick = game.tick}
            end
        end)
        {{FactorioService.LuaOnTickHandler}}
        __mcp_walk_handlers = true
        """;

    /// <summary>
    /// Install the path result handler and shared on_tick handler once per session.
    /// The on_tick handler steers the character along its path every tick because Factorio 2
    /// resets walking_state every tick — a single set only moves the player for one tick.
    /// </summary>
    private async Task EnsurePathHandlerInstalledAsync(CancellationToken cancellationToken)
    {
        if (_pathHandlerInstalled) return;

        await rcon.ExecuteLuaAsync($$"""
            storage.walk_dirs = storage.walk_dirs or {}
            storage.walk_dirs[game.connected_players[1].index] = nil
            {{LuaInstallWalkHandlers}}
            rcon.print('ok')
            """, cancellationToken);

        _pathHandlerInstalled = true;
    }

    /// <summary>
    /// Request a path from Factorio's A* pathfinder. Installs the handlers again when the game was reloaded since.
    /// </summary>
    private async Task<int> RequestPathAsync(
        double targetX, double targetY, double tolerance,
        CancellationToken cancellationToken)
    {
        var lua = string.Create(CultureInfo.InvariantCulture, $$"""
            if not __mcp_walk_handlers then
                {{LuaInstallWalkHandlers}}
            end
            local p = game.connected_players[1]
            if not p.character or not p.character.valid then
                rcon.print('-1')
                return
            end
            local id = p.surface.request_path{
                bounding_box = p.character.prototype.collision_box,
                collision_mask = p.character.prototype.collision_mask,
                start = p.position,
                goal = {x={{targetX}}, y={{targetY}}},
                force = p.force,
                radius = {{tolerance}},
                entity_to_ignore = p.character,
                pathfind_flags = {prefer_straight_paths = true, no_break = true}
            }
            rcon.print(tostring(id))
            """);

        var result = await rcon.ExecuteLuaAsync(lua, cancellationToken);
        return int.TryParse(result.Trim(), out var id) ? id : -1;
    }

    /// <summary>
    /// Get the result of a path request.
    /// </summary>
    private async Task<(string status, List<(double x, double y)>? path)> GetPathResultAsync(
        int requestId, CancellationToken cancellationToken)
    {
        var lua = string.Create(CultureInfo.InvariantCulture, $$"""
            if storage.nav_results then
                local now = game.tick
                for id, r in pairs(storage.nav_results) do
                    if now - (r.tick or 0) > 600 then
                        storage.nav_results[id] = nil
                    end
                end
            end
            local r = storage.nav_results and storage.nav_results[{{requestId}}]
            if not r then
                rcon.print('{"status":"waiting"}')
                return
            end
            if r.status ~= "ok" then
                storage.nav_results[{{requestId}}] = nil
                rcon.print('{"status":"'..r.status..'"}')
                return
            end
            local pts = {}
            for i, wp in ipairs(r.path) do
                pts[i] = '{"x":'..wp.position.x..',"y":'..wp.position.y..'}'
            end
            storage.nav_results[{{requestId}}] = nil
            rcon.print('{"status":"ok","path":['..table.concat(pts, ',')..']}')
            """);

        var result = await rcon.ExecuteLuaAsync(lua, cancellationToken);

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        var status = root.GetProperty("status").GetString() ?? "unknown";

        if (status != "ok" || !root.TryGetProperty("path", out var pathProp))
            return (status, null);

        var waypoints = new List<(double, double)>(pathProp.GetArrayLength());
        foreach (var wp in pathProp.EnumerateArray())
            waypoints.Add((wp.GetProperty("x").GetDouble(), wp.GetProperty("y").GetDouble()));

        return (status, waypoints);
    }

    private async Task<(double x, double y)> GetPositionAsync(CancellationToken cancellationToken)
    {
        var json = await GetPlayerPositionAsync(cancellationToken);
        return ParsePosition(json);
    }

    /// <summary>
    /// Position and walk status (walking, arrived, stuck, no_character; none when no walk is set; no_handler when the on_tick
    /// handler has not run for half a second while the game runs).
    /// </summary>
    private async Task<(double x, double y, string status)> GetWalkStateAsync(CancellationToken cancellationToken)
    {
        var json = await rcon.ExecuteLuaAsync("""
            local p = game.connected_players[1]
            local pos = p.position
            local w = storage.walk_paths and storage.walk_paths[p.index]
            local status = w and w.status or "none"
            if status == "walking" and game.tick - (w.seen or 0) > 30 then status = "no_handler" end
            rcon.print('{"x":'..pos.x..',"y":'..pos.y..',"status":"'..status..'"}')
            """, cancellationToken);

        var (x, y) = ParsePosition(json);
        using var doc = JsonDocument.Parse(json);
        var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() ?? "walking" : "walking";
        return (x, y, status);
    }

    private async Task StopWalkingAsync(CancellationToken cancellationToken)
    {
        await rcon.ExecuteLuaAsync("""
            local p = game.connected_players[1]
            if storage.walk_dirs then storage.walk_dirs[p.index] = nil end
            if storage.walk_paths then storage.walk_paths[p.index] = nil end
            p.walking_state = {walking = false, direction = defines.direction.north}
            rcon.print('ok')
            """, cancellationToken);
    }

    /// <summary>
    /// Hand the path to the on_tick handler (<c>storage.walk_paths</c>) and draw it on the map:
    /// lines from the player's current position through all waypoints, plus a circle around the target
    /// showing the arrival tolerance.
    /// </summary>
    private Task StartFollowingAsync(
        List<(double x, double y)> waypoints, double targetX, double targetY, double tolerance,
        CancellationToken cancellationToken)
    {
        var points = string.Join(",", waypoints.Select(w =>
            string.Create(CultureInfo.InvariantCulture, $"{{x={w.x},y={w.y}}}")));

        var lua = new StringBuilder();
        lua.Append("local p = game.connected_players[1]\n");
        lua.Append("local s = p.surface\n");
        lua.Append(CultureInfo.InvariantCulture, $"local pts = {{{points}}}\n");
        lua.Append(CultureInfo.InvariantCulture, $"local target = {{x={targetX},y={targetY}}}\n");
        lua.Append("""
            local from = p.position
            for _, wp in ipairs(pts) do
                rendering.draw_line{color={0,1,0.3,0.7},width=2,from=from,to=wp,surface=s,time_to_live=1800,draw_on_ground=true}
                from = wp
            end
            """);
        lua.Append('\n');
        lua.Append(CultureInfo.InvariantCulture,
            $"rendering.draw_circle{{color={{0,1,0,0.5}},radius={tolerance},filled=false,width=2,target=target,surface=s,time_to_live=1800,draw_on_ground=true}}\n");
        lua.Append("storage.walk_dirs = storage.walk_dirs or {}\n");
        lua.Append("storage.walk_dirs[p.index] = nil\n");
        lua.Append("storage.walk_paths = storage.walk_paths or {}\n");
        lua.Append(CultureInfo.InvariantCulture,
            $"storage.walk_paths[p.index] = {{points = pts, i = 1, target = target, tolerance = {tolerance}, idle = 0, status = \"walking\", seen = game.tick}}\n");
        lua.Append("rcon.print('ok')");

        return rcon.ExecuteLuaAsync(lua.ToString(), cancellationToken);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    internal static (double x, double y) ParsePosition(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return (root.GetProperty("x").GetDouble(), root.GetProperty("y").GetDouble());
    }

    internal static double Distance(double x1, double y1, double x2, double y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    internal static string FormatResult(
        string status, double x, double y,
        double targetX, double targetY, double distance, double tolerance)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $$"""{"status":"{{status}}","x":{{x}},"y":{{y}},"target_x":{{targetX}},"target_y":{{targetY}},"distance":{{string.Format(CultureInfo.InvariantCulture, "{0:F2}", distance)}},"tolerance":{{tolerance}}}""");
    }
}
