using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FactorioMCP.Rcon;
using Microsoft.Extensions.Configuration;

namespace FactorioMCP.Services;

/// <summary>
/// A rectangle on nauvis that only humans may enter, set in the <c>RestrictedArea</c> section of appsettings.json.
/// Width or height 0 (the default) means there is no restricted area and the feature is off.
/// </summary>
internal sealed record RestrictedArea(double CenterX, double CenterY, double Width, double Height)
{
    internal const string SectionName = "RestrictedArea";

    public static RestrictedArea None { get; } = new(0, 0, 0, 0);

    public bool Enabled => Width > 0 && Height > 0;
    public double Left => CenterX - Width / 2;
    public double Right => CenterX + Width / 2;
    public double Top => CenterY - Height / 2;
    public double Bottom => CenterY + Height / 2;

    public bool Contains(double x, double y) =>
        Enabled && x >= Left && x <= Right && y >= Top && y <= Bottom;

    public bool Overlaps(double x1, double y1, double x2, double y2) =>
        Enabled && Math.Max(x1, x2) >= Left && Math.Min(x1, x2) <= Right
                && Math.Max(y1, y2) >= Top && Math.Min(y1, y2) <= Bottom;

    /// <summary>Distance from a point to the area's edge; 0 inside the area.</summary>
    public double DistanceTo(double x, double y)
    {
        var dx = Math.Max(Math.Max(Left - x, 0), x - Right);
        var dy = Math.Max(Math.Max(Top - y, 0), y - Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Whether the straight line between two points passes through the area (Liang-Barsky clipping).</summary>
    public bool CrossedBy(double x1, double y1, double x2, double y2)
    {
        if (!Enabled) return false;
        double t0 = 0, t1 = 1, dx = x2 - x1, dy = y2 - y1;
        foreach (var (p, q) in new[] { (-dx, x1 - Left), (dx, Right - x1), (-dy, y1 - Top), (dy, Bottom - y1) })
        {
            if (p == 0)
            {
                if (q < 0) return false;
                continue;
            }
            var t = q / p;
            if (p < 0) t0 = Math.Max(t0, t);
            else t1 = Math.Min(t1, t);
            if (t0 > t1) return false;
        }
        return true;
    }

    /// <summary>The point 2 tiles outside the nearest edge, for a point inside the area.</summary>
    public (double X, double Y) NearestOutside(double x, double y)
    {
        const double margin = 2;
        var exits = new[]
        {
            (Distance: x - Left, X: Left - margin, Y: y),
            (Distance: Right - x, X: Right + margin, Y: y),
            (Distance: y - Top, X: x, Y: Top - margin),
            (Distance: Bottom - y, X: x, Y: Bottom + margin),
        };
        var exit = exits.MinBy(e => e.Distance);
        return (exit.X, exit.Y);
    }

    public string BoundsText => Invariant($"x {Left:0.##} to {Right:0.##}, y {Top:0.##} to {Bottom:0.##}");

    public JsonObject ToJson() => new()
    {
        ["surface"] = "nauvis",
        ["center"] = new JsonObject { ["x"] = CenterX, ["y"] = CenterY },
        ["width"] = Width,
        ["height"] = Height,
        ["left"] = Left,
        ["top"] = Top,
        ["right"] = Right,
        ["bottom"] = Bottom,
        ["access"] = "humans only",
        ["reason"] = "Closed staff area for the MCP developers, surrounded by a stone wall. " +
                     "AI agents cannot walk into it, build, mine or act inside it.",
    };

    /// <summary>MCP server instructions that tell agents about the area at session start; null when there is none.</summary>
    public string? Instructions => Enabled
        ? Invariant($"This Factorio server has a restricted area on nauvis: {BoundsText} ") +
          Invariant($"(a {Width:0.##} x {Height:0.##} rectangle around ({CenterX:0.##}, {CenterY:0.##})), surrounded by a stone wall. ") +
          "It is a closed staff area that only humans may enter. AI agents cannot walk into it, build, mine or act inside it: " +
          "tools refuse such targets with error 'restricted_area'. This is intended, not a tool fault. " +
          "get_restricted_areas returns the bounds."
        : null;

    /// <summary>
    /// Read the <c>RestrictedArea</c> section. Missing values are 0.
    /// Throws <see cref="InvalidOperationException"/> naming the setting when a value is not a number or a size is negative.
    /// </summary>
    public static RestrictedArea FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        return new RestrictedArea(
            Read(section, "CenterX"), Read(section, "CenterY"),
            Read(section, "Width", size: true), Read(section, "Height", size: true));
    }

    private static double Read(IConfigurationSection section, string key, bool size = false)
    {
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw))
            return 0;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new InvalidOperationException($"Invalid setting {SectionName}:{key} '{raw}' in appsettings.json: use a number.");
        if (size && value < 0)
            throw new InvalidOperationException($"Invalid setting {SectionName}:{key} '{raw}' in appsettings.json: use 0 (no area) or a positive size.");
        return value;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Keeps player agents out of the <see cref="RestrictedArea"/>: refuses tool calls that target a position inside it and
/// explains walks that the area's wall blocked. The MCP engineer agent is exempt. The area is read from configuration on
/// every call, so a changed appsettings.json takes effect without a restart.
/// </summary>
internal sealed class RestrictedAreaGuard
{
    /// <summary>String parameters that hold a JSON array of objects with world positions <c>x</c> and <c>y</c>.</summary>
    internal static readonly HashSet<string> PositionListParameters = ["targets", "placementsJson", "planJson"];

    /// <summary>Walks that end this close to the area without arriving get a note about it.</summary>
    internal const double WalkNoteDistance = 10;

    private static readonly Regex XParameter = new("^(.*?)([xX])(\\d*)$", RegexOptions.Compiled);

    private readonly IConfiguration? _configuration;
    private readonly RestrictedArea? _area;

    public RestrictedAreaGuard(IConfiguration configuration, RconClient rcon)
    {
        _configuration = configuration;
        Exempt = RconClient.IsEngineer(rcon.AgentName);
    }

    internal RestrictedAreaGuard(RestrictedArea area, bool exempt = false)
    {
        _area = area;
        Exempt = exempt;
    }

    /// <summary>True for the MCP engineer, who may work inside the area.</summary>
    public bool Exempt { get; }

    public RestrictedArea Area => _area ?? RestrictedArea.FromConfiguration(_configuration!);

    /// <summary>Whether this caller may not target the position.</summary>
    public bool Blocks(double x, double y) => !Exempt && Area.Contains(x, y);

    /// <summary>
    /// Check a tool call's arguments. Returns the refusal JSON when an argument targets the area, else null.
    /// Positions are argument pairs named <c>…x</c>/<c>…y</c> or <c>…X</c>/<c>…Y</c> (with an optional number suffix, e.g.
    /// <c>targetX</c>/<c>targetY</c>, <c>x1</c>/<c>y1</c>), rectangles <c>x1,y1,x2,y2</c>, and the entries of the JSON lists in
    /// <see cref="PositionListParameters"/>.
    /// </summary>
    public string? CheckToolCall(string toolName, IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
    {
        if (Exempt || arguments is null)
            return null;

        RestrictedArea area;
        try
        {
            area = Area;
        }
        catch (InvalidOperationException ex)
        {
            return new JsonObject { ["success"] = false, ["error"] = "invalid_setting", ["message"] = ex.Message }.ToJsonString();
        }
        if (!area.Enabled)
            return null;

        var args = arguments.ToDictionary(a => a.Key, a => a.Value);
        if (TryNumber(args, "x1", out var x1) && TryNumber(args, "y1", out var y1)
            && TryNumber(args, "x2", out var x2) && TryNumber(args, "y2", out var y2)
            && area.Overlaps(x1, y1, x2, y2))
        {
            var refusal = Refusal(area, Invariant($"The area x {x1:0.##} to {x2:0.##}, y {y1:0.##} to {y2:0.##} (x1, y1, x2, y2) of {toolName} overlaps"),
                "Use an area that ends outside it.");
            refusal["parameter"] = "x1/y1/x2/y2";
            return refusal.ToJsonString();
        }

        foreach (var (parameter, x, y) in FindPositions(args))
        {
            if (!area.Contains(x, y))
                continue;
            var (nx, ny) = area.NearestOutside(x, y);
            var refusal = Refusal(area, Invariant($"Position ({x:0.##}, {y:0.##}) from {parameter} of {toolName} is inside"),
                Invariant($"Choose a target outside it, e.g. ({nx:0.##}, {ny:0.##})."));
            refusal["parameter"] = parameter;
            refusal["position"] = new JsonObject { ["x"] = x, ["y"] = y };
            refusal["nearest_allowed_position"] = new JsonObject { ["x"] = nx, ["y"] = ny };
            return refusal.ToJsonString();
        }
        return null;
    }

    /// <summary>
    /// Add a <c>restricted_area_note</c> to a walk result (PathfindingService JSON) that did not arrive because of the area:
    /// the target is inside it, the walk ended next to it, or no path was found and the straight way to the target crosses it.
    /// </summary>
    public string AnnotateWalk(string walkResultJson)
    {
        if (Exempt)
            return walkResultJson;
        RestrictedArea area;
        try
        {
            area = Area;
        }
        catch (InvalidOperationException)
        {
            return walkResultJson;
        }
        if (!area.Enabled || JsonNode.Parse(walkResultJson) is not JsonObject walk)
            return walkResultJson;

        var status = (string?)walk["status"];
        if (status == "arrived"
            || walk["x"] is not JsonValue xv || walk["y"] is not JsonValue yv
            || walk["target_x"] is not JsonValue txv || walk["target_y"] is not JsonValue tyv)
            return walkResultJson;
        double x = (double)xv, y = (double)yv, tx = (double)txv, ty = (double)tyv;

        string note;
        if (area.Contains(tx, ty))
        {
            var (nx, ny) = area.NearestOutside(tx, ty);
            note = Invariant($"The target ({tx:0.##}, {ty:0.##}) is inside the restricted area ({area.BoundsText}), a closed staff area that only humans may enter. ") +
                   Invariant($"AI agents cannot walk into it. This is intended, not a tool fault. Pick a target outside it, e.g. ({nx:0.##}, {ny:0.##}).");
            walk["nearest_allowed_position"] = new JsonObject { ["x"] = nx, ["y"] = ny };
        }
        else if (area.DistanceTo(x, y) <= WalkNoteDistance || (status == "no_path" && area.CrossedBy(x, y, tx, ty)))
        {
            note = Invariant($"The restricted area ({area.BoundsText}) lies in your way: a closed staff area that only humans may enter, surrounded by a stone wall. ") +
                   "This is intended, not a tool fault. Walk around it or pick another target.";
        }
        else
        {
            return walkResultJson;
        }

        walk["restricted_area_note"] = note;
        walk["restricted_area"] = area.ToJson();
        return walk.ToJsonString();
    }

    /// <summary>
    /// Add <c>server_rules</c> about the restricted area to a successful set_agent_name result, so an agent learns them on
    /// its first join. Other results are returned unchanged.
    /// </summary>
    public string AddServerRules(string setAgentNameJson)
    {
        RestrictedArea area;
        try
        {
            area = Area;
        }
        catch (InvalidOperationException)
        {
            return setAgentNameJson;
        }
        if (!area.Enabled || JsonNode.Parse(setAgentNameJson) is not JsonObject result || (string?)result["status"] == "error")
            return setAgentNameJson;

        result["server_rules"] = new JsonArray(
            JsonValue.Create(area.Instructions + (Exempt ? " You are the MCP engineer and exempt from this rule." : "")));
        result["restricted_areas"] = new JsonArray(area.ToJson());
        return result.ToJsonString();
    }

    /// <summary>JSON for get_restricted_areas.</summary>
    public string DescribeJson()
    {
        RestrictedArea area;
        try
        {
            area = Area;
        }
        catch (InvalidOperationException ex)
        {
            return new JsonObject { ["success"] = false, ["error"] = "invalid_setting", ["message"] = ex.Message }.ToJsonString();
        }

        var areas = new JsonArray();
        if (area.Enabled)
            areas.Add(area.ToJson());
        return new JsonObject
        {
            ["success"] = true,
            ["restricted_areas"] = areas,
            ["you_are_exempt"] = Exempt,
            ["message"] = area.Enabled
                ? "Only humans may enter these areas. Tools refuse targets inside them with error 'restricted_area'; that is intended, not a tool fault."
                : "This server has no restricted areas.",
        }.ToJsonString();
    }

    /// <summary>All positions in the arguments, with the parameter they came from.</summary>
    internal static IEnumerable<(string Parameter, double X, double Y)> FindPositions(IReadOnlyDictionary<string, JsonElement> args)
    {
        foreach (var (name, value) in args)
        {
            if (value.ValueKind == JsonValueKind.String && PositionListParameters.Contains(name))
            {
                foreach (var position in FindListPositions(name, value.GetString()))
                    yield return position;
                continue;
            }

            var match = XParameter.Match(name);
            if (!match.Success || !TryNumber(value, out var x))
                continue;
            var yName = match.Groups[1].Value + (match.Groups[2].Value == "x" ? "y" : "Y") + match.Groups[3].Value;
            if (TryNumber(args, yName, out var y))
                yield return ($"{name}/{yName}", x, y);
        }
    }

    private static IEnumerable<(string Parameter, double X, double Y)> FindListPositions(string name, string? json)
    {
        JsonElement list;
        try
        {
            using var doc = JsonDocument.Parse(json ?? "");
            list = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            yield break; // the tool reports the invalid JSON itself
        }
        if (list.ValueKind != JsonValueKind.Array)
            yield break;

        var index = 0;
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && (entry.TryGetProperty("x", out var xe) || entry.TryGetProperty("X", out xe)) && TryNumber(xe, out var x)
                && (entry.TryGetProperty("y", out var ye) || entry.TryGetProperty("Y", out ye)) && TryNumber(ye, out var y))
                yield return ($"{name}[{index}]", x, y);
            index++;
        }
    }

    private static bool TryNumber(IReadOnlyDictionary<string, JsonElement> args, string name, out double value)
    {
        value = 0;
        return args.TryGetValue(name, out var element) && TryNumber(element, out value);
    }

    private static bool TryNumber(JsonElement element, out double value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    private static JsonObject Refusal(RestrictedArea area, string subject, string advice) => new()
    {
        ["success"] = false,
        ["error"] = "restricted_area",
        ["message"] = $"{subject} the restricted area ({area.BoundsText}). It is a closed staff area that only humans may enter; " +
                      $"AI agents cannot go there or act inside it. This is intended, not a tool fault. {advice}",
        ["restricted_area"] = area.ToJson(),
    };

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
