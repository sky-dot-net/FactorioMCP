using System.Text.Json;
using FactorioMCP.Services;
using FactorioMCP.Tools;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FactorioMCP.Tests.Services;

/// <summary>
/// The restricted area (appsettings.json section RestrictedArea): player agents may not target positions inside it,
/// learn about it on first join and in get_restricted_areas, and get a note when it blocks a walk.
/// </summary>
public class RestrictedAreaTests
{
    // The test area: 5000 x 5000 around (-4255, 1925) → x -6755..-1755, y -575..4425
    private static readonly RestrictedArea Area = new(-4255, 1925, 5000, 5000);

    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Bounds_FollowCenterAndSize()
    {
        Assert.Equal((-6755.0, -1755.0, -575.0, 4425.0), (Area.Left, Area.Right, Area.Top, Area.Bottom));
        Assert.True(Area.Contains(-4255, 1925));
        Assert.True(Area.Contains(-1755, -575));
        Assert.False(Area.Contains(-1754, 0));
    }

    [Fact]
    public void NoSettings_MeansNoArea()
    {
        var area = RestrictedArea.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.False(area.Enabled);
        Assert.False(area.Contains(0, 0));
        Assert.Null(area.Instructions);
    }

    [Fact]
    public void ZeroSize_MeansNoArea()
    {
        var guard = new RestrictedAreaGuard(new RestrictedArea(0, 0, 0, 0));

        Assert.Null(guard.CheckToolCall("place_entity", Args("""{"x":0,"y":0}""")));
    }

    [Theory]
    [InlineData("Width", "-5")]
    [InlineData("CenterX", "abc")]
    public void InvalidSetting_NamesTheSetting(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"RestrictedArea:{key}"] = value })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => RestrictedArea.FromConfiguration(configuration));
        Assert.Contains($"RestrictedArea:{key}", ex.Message);
    }

    [Fact]
    public void NearestOutside_LeavesThroughTheClosestEdge()
    {
        Assert.Equal((-1753.0, 1000.0), Area.NearestOutside(-1800, 1000));
        Assert.Equal((-5000.0, -577.0), Area.NearestOutside(-5000, -500));
    }

    [Fact]
    public void CrossedBy_DetectsLinesThroughTheArea()
    {
        Assert.True(Area.CrossedBy(-1700, 1000, -7000, 1000));
        Assert.False(Area.CrossedBy(-1700, 1000, -1700, 3000));
    }

    // Refusals stay short (lean results for agents with small context windows): success, error and one sentence.
    [Theory]
    [InlineData("place_entity", """{"name":"stone-furnace","x":-4000,"y":2000}""",
        "(-4000, 2000) is in the restricted area (humans only). Nearest allowed: (-1753, 2000).")]
    [InlineData("walk_to_position", """{"targetX":-4000,"targetY":2000}""",
        "(-4000, 2000) is in the restricted area (humans only). Nearest allowed: (-1753, 2000).")]
    [InlineData("route_belt", """{"startX":-1700,"startY":0,"endX":-1800,"endY":10}""",
        "endX/endY (-1800, 10) is in the restricted area (humans only). Nearest allowed: (-1753, 10).")]
    [InlineData("place_entity", """{"x":"-4000","y":"2000"}""",
        "(-4000, 2000) is in the restricted area (humans only). Nearest allowed: (-1753, 2000).")]
    public void PositionInside_IsRefusedInOneSentence(string tool, string args, string message)
    {
        var guard = new RestrictedAreaGuard(Area);

        var refusal = guard.CheckToolCall(tool, Args(args));

        Assert.NotNull(refusal);
        using var doc = JsonDocument.Parse(refusal);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("restricted_area", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(message, doc.RootElement.GetProperty("message").GetString());
        Assert.Equal(3, doc.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void PositionListEntryInside_IsRefused()
    {
        var guard = new RestrictedAreaGuard(Area);
        var args = new Dictionary<string, JsonElement>
        {
            ["targets"] = JsonSerializer.SerializeToElement("""[{"x":0,"y":0},{"x":-3000,"y":100}]"""),
        };

        var refusal = guard.CheckToolCall("mine_entity_multiple", args);

        Assert.NotNull(refusal);
        Assert.Contains("targets[1] (-3000, 100) is in the restricted area (humans only). Nearest allowed: (-3000, -577).", refusal);
    }

    [Fact]
    public void AreaOverlappingTheRestrictedArea_IsRefused()
    {
        var guard = new RestrictedAreaGuard(Area);

        var refusal = guard.CheckToolCall("get_area_occupancy", Args("""{"x1":-1760,"y1":0,"x2":-1700,"y2":50}"""));

        Assert.NotNull(refusal);
        Assert.Contains("The area x -1760..-1700, y 0..50 overlaps the restricted area (humans only).", refusal);
    }

    [Theory]
    [InlineData("""{"x":0,"y":0}""")]
    [InlineData("""{"x1":-1750,"y1":0,"x2":-1700,"y2":50}""")]
    [InlineData("""{"targetX":-4000}""")]
    public void PositionsOutside_AreAllowed(string args)
    {
        Assert.Null(new RestrictedAreaGuard(Area).CheckToolCall("any_tool", Args(args)));
    }

    [Fact]
    public void BlueprintJson_IsNotScanned()
    {
        // Blueprint positions are relative to the blueprint, not world positions
        var args = new Dictionary<string, JsonElement>
        {
            ["blueprintJson"] = JsonSerializer.SerializeToElement("""[{"x":-4000,"y":2000}]"""),
        };

        Assert.Null(new RestrictedAreaGuard(Area).CheckToolCall("encode_blueprint_string", args));
    }

    [Fact]
    public void Engineer_IsExempt()
    {
        var guard = new RestrictedAreaGuard(Area, exempt: true);

        Assert.Null(guard.CheckToolCall("place_entity", Args("""{"x":-4000,"y":2000}""")));
        Assert.False(guard.Blocks(-4000, 2000));
    }

    [Fact]
    public void AnnotateWalk_StuckAtTheWall_ExplainsTheArea()
    {
        var walk = PathfindingService.FormatResult("stuck", -1750, 1000, -1700, -800, 5, 1);

        var annotated = new RestrictedAreaGuard(Area).AnnotateWalk(walk);

        Assert.Contains("\"restricted_area_note\":\"The restricted area (humans only) blocks the way; walk around it.\"}", annotated);
    }

    [Fact]
    public void AnnotateWalk_NoPathThroughTheArea_ExplainsTheArea()
    {
        var walk = PathfindingService.FormatResult("no_path", -206, -192, -2000, -100, 1800, 1);

        Assert.Contains("restricted_area_note", new RestrictedAreaGuard(Area).AnnotateWalk(walk));
    }

    [Fact]
    public void AnnotateWalk_ArrivedOrFarAway_IsUnchanged()
    {
        var guard = new RestrictedAreaGuard(Area);
        var arrived = PathfindingService.FormatResult("arrived", -1750, 1000, -1750, 1000, 0, 1);
        var farAway = PathfindingService.FormatResult("stuck", 100, 100, 200, 200, 5, 1);
        // Live game: a walk timed out 1500 tiles away; its straight line only clipped the area's corner
        var timedOutFarAway = PathfindingService.FormatResult("timeout", -206.6, -192.8, -2000, -600, 1839, 2);

        Assert.Equal(arrived, guard.AnnotateWalk(arrived));
        Assert.Equal(farAway, guard.AnnotateWalk(farAway));
        Assert.Equal(timedOutFarAway, guard.AnnotateWalk(timedOutFarAway));
    }

    [Fact]
    public async Task WalkTo_TargetInside_IsRefusedWithoutWalking()
    {
        var rcon = new ScriptedRconClient(["""{"x":-1700,"y":1000}"""]);
        var pathfinding = new PathfindingService(rcon, new RestrictedAreaGuard(Area));

        var result = await pathfinding.WalkToAsync(-1800, 1000, 1, 10);

        Assert.Contains("\"status\":\"restricted_area\"", result);
        Assert.Contains("\"restricted_area_note\":\"Target is in the restricted area (humans only). Nearest allowed: (-1753, 1000).\"", result);
        Assert.Single(rcon.AllCommands);
    }

    [Fact]
    public void SetAgentNameResult_GetsServerRules()
    {
        var guard = new RestrictedAreaGuard(Area);

        var result = guard.AddServerRules("""{"status":"named","chat_name":"[AI] Rusty"}""");

        Assert.Contains("\"server_rules\":[\"Restricted area x -6755..-1755, y -575..4425 (humans only): tools refuse targets inside it.\"]", result);
        Assert.DoesNotContain("restricted_areas", result);
        Assert.Equal("""{"status":"error","error":"x"}""", guard.AddServerRules("""{"status":"error","error":"x"}"""));
    }

    [Fact]
    public void GetRestrictedAreas_ListsTheAreaOrNone()
    {
        var on = new RestrictedAreaTools(new RestrictedAreaGuard(Area)).GetRestrictedAreas();
        var off = new RestrictedAreaTools(new RestrictedAreaGuard(RestrictedArea.None)).GetRestrictedAreas();

        var exempt = new RestrictedAreaTools(new RestrictedAreaGuard(Area, exempt: true)).GetRestrictedAreas();

        Assert.Equal("""{"restricted_areas":[{"left":-6755,"top":-575,"right":-1755,"bottom":4425}]}""", on);
        Assert.Equal("""{"restricted_areas":[]}""", off);
        Assert.Contains("\"you_are_exempt\":true", exempt);
    }
}
