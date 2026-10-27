using System.Globalization;
using System.Text.Json;
using FactorioMCP.Rcon;

namespace FactorioMCP.Services;

/// <summary>
/// Plans a transport belt route around obstacles with A* on the tile grid.
/// Blocked tiles (entities, water) are read from the game; the search itself is C#-side.
/// Underground belt pairs jump over obstacles up to the prototype's max underground distance.
/// </summary>
internal sealed class BeltRouterService(RconClient rcon)
{
    internal const int MaxSearchTiles = 40_000;
    private const int MaxExpandedNodes = 400_000;
    private const double TurnCost = 0.3;
    /// <summary>Extra cost of an underground pair over belts covering the same distance.</summary>
    private const double UndergroundPenalty = 3.0;

    private static readonly (int dx, int dy, string name)[] Dirs =
        [(0, -1, "north"), (1, 0, "east"), (0, 1, "south"), (-1, 0, "west")];

    internal sealed record Placement(string Entity, int X, int Y, string Direction, string? BeltType);

    /// <summary>
    /// An existing underground belt in the search area: tile and whether it runs along the vertical axis.
    /// </summary>
    internal readonly record struct ExistingUnderground(int X, int Y, string Name, bool Vertical);

    public async Task<string> RouteBeltAsync(
        double startX, double startY, double endX, double endY,
        int margin, string beltName, string undergroundName,
        CancellationToken cancellationToken = default)
    {
        if (!IsPrototypeName(beltName) || !IsPrototypeName(undergroundName))
            return JsonSerializer.Serialize(new { success = false, error = "invalid_entity_name" });

        int sx = (int)Math.Floor(startX), sy = (int)Math.Floor(startY);
        int ex = (int)Math.Floor(endX), ey = (int)Math.Floor(endY);
        margin = Math.Clamp(margin, 0, 50);
        int x1 = Math.Min(sx, ex) - margin, x2 = Math.Max(sx, ex) + margin;
        int y1 = Math.Min(sy, ey) - margin, y2 = Math.Max(sy, ey) + margin;
        long area = (long)(x2 - x1 + 1) * (y2 - y1 + 1);
        if (area > MaxSearchTiles)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "area_too_large",
                message = $"Search area {x2 - x1 + 1}x{y2 - y1 + 1} exceeds {MaxSearchTiles} tiles. Route in shorter legs or lower the margin."
            });
        }

        var lua = string.Create(CultureInfo.InvariantCulture, $$"""
            local surface = game.connected_players[1].surface
            local x1, y1, x2, y2 = {{x1}}, {{y1}}, {{x2}}, {{y2}}
            local ents = surface.find_entities_filtered({ area = { {x1, y1}, {x2 + 1, y2 + 1} } })
            local blocked = {}
            local ug = {}
            for _, e in ipairs(ents) do
                if e.valid and e.type ~= "resource" and e.type ~= "character" and e.type ~= "corpse" and e.type ~= "character-corpse" then
                    local bb = e.bounding_box
                    for tx = math.floor(bb.left_top.x + 0.01), math.ceil(bb.right_bottom.x - 0.01) - 1 do
                        for ty = math.floor(bb.left_top.y + 0.01), math.ceil(bb.right_bottom.y - 0.01) - 1 do
                            if tx >= x1 and tx <= x2 and ty >= y1 and ty <= y2 then
                                blocked[#blocked + 1] = tx .. "," .. ty
                            end
                        end
                    end
                    if e.type == "underground-belt" then
                        ug[#ug + 1] = math.floor(e.position.x) .. "," .. math.floor(e.position.y) .. "," .. e.name .. "," .. e.direction
                    end
                end
            end
            for ty = y1, y2 do
                for tx = x1, x2 do
                    local tile = surface.get_tile(tx, ty)
                    if tile and tile.valid and tile.prototype.collision_mask.layers["water_tile"] then
                        blocked[#blocked + 1] = tx .. "," .. ty
                    end
                end
            end
            local proto = prototypes.entity["{{undergroundName}}"]
            local maxd = proto and proto.max_underground_distance or 5
            rcon.print('{"success":true,"max_underground_distance":' .. maxd .. ',"blocked":"' .. table.concat(blocked, ";") .. '","undergrounds":"' .. table.concat(ug, ";") .. '"}')
            """);

        var raw = await rcon.ExecuteLuaAsync(lua, cancellationToken);

        HashSet<(int, int)> blockedTiles;
        List<ExistingUnderground> undergrounds;
        int maxDistance;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            maxDistance = root.GetProperty("max_underground_distance").GetInt32();
            blockedTiles = ParsePoints(root.GetProperty("blocked").GetString());
            undergrounds = ParseUndergrounds(root.GetProperty("undergrounds").GetString());
        }
        catch (Exception ex2) when (ex2 is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return JsonSerializer.Serialize(new { success = false, error = "game_query_failed", detail = raw });
        }

        return PlanToJson(blockedTiles, undergrounds, (sx, sy), (ex, ey), (x1, y1, x2, y2),
            maxDistance, beltName, undergroundName);
    }

    internal static string PlanToJson(
        HashSet<(int, int)> blocked, IReadOnlyList<ExistingUnderground> undergrounds,
        (int x, int y) start, (int x, int y) end, (int x1, int y1, int x2, int y2) bounds,
        int maxUndergroundDistance, string beltName, string undergroundName)
    {
        if (blocked.Contains(start))
            return JsonSerializer.Serialize(new { success = false, error = "start_blocked", x = start.x, y = start.y });
        if (blocked.Contains(end))
            return JsonSerializer.Serialize(new { success = false, error = "end_blocked", x = end.x, y = end.y });

        var steps = FindRoute(blocked, undergrounds, start, end, bounds, maxUndergroundDistance, beltName, undergroundName);
        if (steps is null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "no_route",
                message = "No route inside the search area. Raise the margin or clear obstacles."
            });
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            belts = steps.Count(s => s.BeltType is null),
            underground_pairs = steps.Count(s => s.BeltType == "input"),
            note = "Place the steps in order. Each underground input is followed by its output.",
            steps = steps.Select(s => s.BeltType is null
                ? (object)new { entity = s.Entity, x = s.X, y = s.Y, direction = s.Direction }
                : new { entity = s.Entity, x = s.X, y = s.Y, direction = s.Direction, belt_type = s.BeltType })
        });
    }

    /// <summary>
    /// A* over (tile, arrival direction, exit-of-underground). Returns the placements from start to end,
    /// or null when no route exists inside the bounds.
    /// </summary>
    internal static List<Placement>? FindRoute(
        HashSet<(int, int)> blocked, IReadOnlyList<ExistingUnderground> undergrounds,
        (int x, int y) start, (int x, int y) end, (int x1, int y1, int x2, int y2) bounds,
        int maxUndergroundDistance, string beltName = "transport-belt", string undergroundName = "underground-belt")
    {
        bool InBounds(int x, int y) => x >= bounds.x1 && x <= bounds.x2 && y >= bounds.y1 && y <= bounds.y2;
        bool Free(int x, int y) => InBounds(x, y) && !blocked.Contains((x, y));
        double H(int x, int y) => Math.Abs(x - end.x) + Math.Abs(y - end.y);

        // State: tile, arrival direction (-1 at the start), and whether the tile is an underground exit.
        var startState = new State(start.x, start.y, -1, false);
        var g = new Dictionary<State, double> { [startState] = 0 };
        var parent = new Dictionary<State, (State prev, Placement[] emitted)>();
        var open = new PriorityQueue<State, double>();
        open.Enqueue(startState, H(start.x, start.y));
        int expanded = 0;

        while (open.TryDequeue(out var cur, out _))
        {
            if (++expanded > MaxExpandedNodes) return null;
            double gc = g[cur];

            if (cur.X == end.x && cur.Y == end.y)
            {
                // Last tile: an underground exit, or a belt facing the arrival direction.
                var last = cur.IsExit
                    ? new Placement(undergroundName, cur.X, cur.Y, Dirs[cur.Dir].name, "output")
                    : new Placement(beltName, cur.X, cur.Y, Dirs[cur.Dir < 0 ? 0 : cur.Dir].name, null);
                var result = new List<Placement> { last };
                var s = cur;
                while (parent.TryGetValue(s, out var p))
                {
                    for (int i = p.emitted.Length - 1; i >= 0; i--) result.Add(p.emitted[i]);
                    s = p.prev;
                }
                result.Reverse();
                return result;
            }

            for (int o = 0; o < 4; o++)
            {
                if (cur.Dir >= 0 && o == (cur.Dir + 2) % 4) continue; // no U-turn
                if (cur.IsExit && o != cur.Dir) continue;              // an exit outputs straight
                var (dx, dy, dname) = Dirs[o];

                // Plain belt on the current tile, facing o.
                int nx = cur.X + dx, ny = cur.Y + dy;
                if (Free(nx, ny))
                {
                    var here = cur.IsExit
                        ? new Placement(undergroundName, cur.X, cur.Y, dname, "output")
                        : new Placement(beltName, cur.X, cur.Y, dname, null);
                    double cost = gc + 1 + (cur.Dir >= 0 && o != cur.Dir ? TurnCost : 0);
                    Relax(new State(nx, ny, o, false), cost, cur, [here]);
                }

                // Underground pair: entrance on the current tile (entered straight), exit k tiles ahead.
                if (cur.IsExit || (cur.Dir >= 0 && o != cur.Dir)) continue;
                for (int k = 2; k <= maxUndergroundDistance; k++)
                {
                    int ux = cur.X + dx * k, uy = cur.Y + dy * k;
                    if (!InBounds(ux, uy)) break;
                    if (!Free(ux, uy)) continue;
                    if (CrossesParallelUnderground(undergrounds, undergroundName, cur.X, cur.Y, ux, uy)) break;
                    var entrance = new Placement(undergroundName, cur.X, cur.Y, dname, "input");
                    Relax(new State(ux, uy, o, true), gc + k + UndergroundPenalty, cur, [entrance]);
                }
            }
        }
        return null;

        void Relax(State next, double cost, State from, Placement[] emitted)
        {
            if (g.TryGetValue(next, out var old) && old <= cost) return;
            g[next] = cost;
            parent[next] = (from, emitted);
            open.Enqueue(next, cost + H(next.X, next.Y));
        }
    }

    /// <summary>
    /// True when an existing underground of the same type lies strictly between entrance and exit on the
    /// same axis: it would pair with the new entrance and break the route.
    /// </summary>
    private static bool CrossesParallelUnderground(
        IReadOnlyList<ExistingUnderground> undergrounds, string name, int ax, int ay, int bx, int by)
    {
        bool vertical = ax == bx;
        foreach (var u in undergrounds)
        {
            if (u.Name != name || u.Vertical != vertical) continue;
            bool between = vertical
                ? u.X == ax && u.Y > Math.Min(ay, by) && u.Y < Math.Max(ay, by)
                : u.Y == ay && u.X > Math.Min(ax, bx) && u.X < Math.Max(ax, bx);
            if (between) return true;
        }
        return false;
    }

    private static bool IsPrototypeName(string name) =>
        name.Length > 0 && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    private static HashSet<(int, int)> ParsePoints(string? s)
    {
        var set = new HashSet<(int, int)>();
        if (string.IsNullOrEmpty(s)) return set;
        foreach (var p in s.Split(';'))
        {
            var xy = p.Split(',');
            set.Add((int.Parse(xy[0], CultureInfo.InvariantCulture), int.Parse(xy[1], CultureInfo.InvariantCulture)));
        }
        return set;
    }

    private static List<ExistingUnderground> ParseUndergrounds(string? s)
    {
        var list = new List<ExistingUnderground>();
        if (string.IsNullOrEmpty(s)) return list;
        foreach (var p in s.Split(';'))
        {
            var f = p.Split(',');
            int dir = int.Parse(f[3], CultureInfo.InvariantCulture);
            // Factorio 2.x: north=0, east=4, south=8, west=12.
            list.Add(new ExistingUnderground(
                int.Parse(f[0], CultureInfo.InvariantCulture), int.Parse(f[1], CultureInfo.InvariantCulture),
                f[2], dir % 8 == 0));
        }
        return list;
    }

    private readonly record struct State(int X, int Y, int Dir, bool IsExit);
}
