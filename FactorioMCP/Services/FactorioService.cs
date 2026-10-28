using FactorioMCP.Rcon;

namespace FactorioMCP.Services;

/// <summary>
/// High-level service for controlling a Factorio game instance via RCON Lua commands.
/// All operations execute Lua scripts through the /c console command and return
/// JSON-formatted output from rcon.print() for reliable AI parsing.
/// Split into partial class files by domain — see FactorioService.*.cs.
/// </summary>
internal sealed partial class FactorioService(RconClient rcon)
{
    /// <summary>
    /// Lua helper function that escapes a string for safe embedding in JSON.
    /// Prepend this to any Lua script that concatenates entity names/types into JSON strings.
    /// Usage: <c>..esc(e.name)..</c> instead of <c>..e.name..</c>
    /// </summary>
    internal const string LuaJsonEscape = """local function esc(s) return s:gsub('\\', '\\\\'):gsub('"', '\\"') end""";

    /// <summary>
    /// Lua helper that sorts entity list so non-resource entities come first,
    /// nearest to the query position, and filters out the player character entity.
    /// Prevents accidentally selecting ore under a drill/furnace or selecting the
    /// player's own character.
    /// Usage: <c>sort_entities(entities, qx, qy)</c> — mutates in place. qx/qy optional.
    /// </summary>
    internal const string LuaEntitySort = """
        local function sort_entities(t, qx, qy)
            local pc = game.connected_players[1].character
            local j = 1
            for i = 1, #t do
                if t[i] ~= pc then t[j] = t[i] j = j + 1 end
            end
            for i = j, #t do t[i] = nil end
            table.sort(t, function(a, b)
                local a_res = a.type == "resource" and 1 or 0
                local b_res = b.type == "resource" and 1 or 0
                if a_res ~= b_res then return a_res < b_res end
                if qx and qy then
                    local da = (a.position.x - qx)*(a.position.x - qx) + (a.position.y - qy)*(a.position.y - qy)
                    local db = (b.position.x - qx)*(b.position.x - qx) + (b.position.y - qy)*(b.position.y - qy)
                    return da < db
                end
                return false
            end)
        end
        """;

    /// <summary>
    /// Lua on_tick handler that continuously re-applies walking_state and mining_state
    /// every tick. Required in Factorio 2 where both states reset after each tick.
    /// Reads <c>storage.walk_paths</c> (paths set by PathfindingService), <c>storage.walk_dirs</c>
    /// (fixed directions, from older builds) and <c>storage.mine_states</c> (set by MiningService), all keyed
    /// by player index (negative for agent characters, resolved through <c>storage.agent_chars</c>),
    /// so several MCP instances can each drive their own player or character.
    /// A path is steered every tick, independent of RCON latency and game speed: the character heads for its
    /// current waypoint in the nearest of 8 directions, takes the next waypoint once within one step of it, and
    /// heads for the target after the last one. The walk becomes "arrived" within its tolerance of the target, and
    /// "stuck" when it gets no 0.05 tiles closer to its current waypoint for 45 ticks.
    /// </summary>
    internal const string LuaOnTickHandler = """
        script.on_event(defines.events.on_tick, function()
            local function controller(index)
                if index > 0 then
                    local p = game.get_player(index)
                    if p and p.connected and p.character and p.character.valid then return p end
                else
                    local c = storage.agent_chars and storage.agent_chars[index]
                    if c and c.valid then return c end
                end
            end
            for index, w in pairs(storage.walk_paths or {}) do
                local p = controller(index)
                if w.status == "walking" and not p then
                    w.status = "no_character"
                elseif w.status == "walking" then
                    w.seen = game.tick
                    local pos = p.position
                    local tx, ty = w.target.x - pos.x, w.target.y - pos.y
                    if tx * tx + ty * ty <= w.tolerance * w.tolerance then
                        -- An agent character keeps walking until told to stop
                        w.status = "arrived"
                        p.walking_state = {walking = false, direction = p.walking_state.direction}
                    else
                        local reach = math.max(0.2, p.character_running_speed + 0.05)
                        local wp = w.points[w.i]
                        while wp and (wp.x - pos.x) ^ 2 + (wp.y - pos.y) ^ 2 <= reach * reach do
                            w.i = w.i + 1
                            wp = w.points[w.i]
                            w.best = nil
                        end
                        wp = wp or w.target
                        local dx, dy = wp.x - pos.x, wp.y - pos.y
                        local d = math.sqrt(dx * dx + dy * dy)
                        if not w.best or d < w.best - 0.05 then
                            w.best, w.idle = d, 0
                        else
                            w.idle = w.idle + 1
                        end
                        if w.idle > 45 then
                            w.status = "stuck"
                            p.walking_state = {walking = false, direction = p.walking_state.direction}
                        else
                            local adx, ady = math.abs(dx), math.abs(dy)
                            local dir
                            if ady > adx * 2.414 then dir = dy < 0 and 0 or 8
                            elseif adx > ady * 2.414 then dir = dx > 0 and 4 or 12
                            elseif dx > 0 then dir = dy < 0 and 2 or 6
                            else dir = dy < 0 and 14 or 10 end
                            p.walking_state = {walking = true, direction = dir}
                        end
                    end
                end
            end
            for index, d in pairs(storage.walk_dirs or {}) do
                local p = controller(index)
                if p then p.walking_state = {walking = true, direction = d} end
            end
            for index, m in pairs(storage.mine_states or {}) do
                local p = controller(index)
                if p then
                    p.update_selected_entity(m.position)
                    p.mining_state = {mining = true, position = m.position}
                end
            end
        end)
        """;

    /// <summary>
    /// Get the player's current map position.
    /// </summary>
    public Task<string> GetPlayerPositionAsync(CancellationToken cancellationToken = default)
    {
        return rcon.ExecuteLuaAsync("""
            local p = game.connected_players[1].position
            rcon.print('{"x":'..p.x..',"y":'..p.y..'}')
            """,
            cancellationToken);
    }

    /// <summary>
    /// Execute arbitrary Lua code on the Factorio instance.
    /// </summary>
    public Task<string> ExecuteRawLuaAsync(string luaCode, CancellationToken cancellationToken = default)
    {
        return rcon.ExecuteLuaAsync(luaCode, cancellationToken);
    }
}
