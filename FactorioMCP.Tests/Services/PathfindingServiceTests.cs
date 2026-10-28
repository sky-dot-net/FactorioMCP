using FactorioMCP.Services;
using Xunit;

namespace FactorioMCP.Tests.Services;

public class PathfindingServiceTests
{
    // ── Walking: in-game steering, replanning, honest arrival ───────

    private static readonly string TwoPointPath = """{"status":"ok","path":[{"x":5,"y":0},{"x":10,"y":0}]}""";

    [Fact]
    public async Task WalkTo_Stuck_PlansANewPathAndArrives()
    {
        var rcon = new ScriptedRconClient([
            """{"x":0,"y":0}""",                          // GetPosition
            "ok",                                          // EnsurePathHandlerInstalled
            "1", TwoPointPath, "ok",                       // RequestPath, GetPathResult, StartFollowing
            """{"x":3,"y":0,"status":"stuck"}""",         // walk state: blocked
            "2", TwoPointPath, "ok",                       // new path from here
            """{"x":10,"y":0,"status":"arrived"}""",
            "ok",                                          // StopWalking
        ]);
        var pathfinding = new PathfindingService(rcon) { PollInterval = TimeSpan.FromMilliseconds(10) };

        var result = await pathfinding.WalkToAsync(10, 0, 0.5, 10);

        Assert.Contains("\"status\":\"arrived\"", result);
        Assert.Equal(2, rcon.AllCommands.Count(c => c.Contains("request_path")));
    }

    [Fact]
    public async Task WalkTo_StuckAfterEveryReplan_ReportsStuck()
    {
        var responses = new List<string> { """{"x":0,"y":0}""", "ok", "1", TwoPointPath, "ok", """{"x":3,"y":0,"status":"stuck"}""" };
        for (var i = 0; i < PathfindingService.MaxReplans; i++)
            responses.AddRange(["2", TwoPointPath, "ok", """{"x":3,"y":0,"status":"stuck"}"""]);
        responses.Add("ok");
        var rcon = new ScriptedRconClient([.. responses]);
        var pathfinding = new PathfindingService(rcon) { PollInterval = TimeSpan.FromMilliseconds(10) };

        var result = await pathfinding.WalkToAsync(10, 0, 0.5, 10);

        Assert.Contains("\"status\":\"stuck\"", result);
        Assert.Equal(1 + PathfindingService.MaxReplans, rcon.AllCommands.Count(c => c.Contains("request_path")));
    }

    [Fact]
    public async Task WalkTo_PastThePathEndButOutsideTolerance_IsNotArrived()
    {
        // Live game: the old code reported "arrived" 1.79 tiles from the target with tolerance 0.5
        var rcon = new ScriptedRconClient([
            """{"x":0,"y":0}""", "ok", "1", TwoPointPath, "ok",
            """{"x":10.2,"y":1.7,"status":"walking"}""",
        ]);
        var pathfinding = new PathfindingService(rcon) { PollInterval = TimeSpan.FromMilliseconds(10) };

        var result = await pathfinding.WalkToAsync(10, 0, 0.5, 0.5);

        Assert.Contains("\"status\":\"timeout\"", result);
    }

    [Fact]
    public async Task WalkTo_HandlerLostAfterGameReload_InstallsItAgainAndContinues()
    {
        var rcon = new ScriptedRconClient([
            """{"x":0,"y":0}""", "ok", "1", TwoPointPath, "ok",
            """{"x":0,"y":0,"status":"no_handler"}""",
            "ok", "ok",                                    // EnsurePathHandlerInstalled, StartFollowing
            """{"x":10,"y":0,"status":"arrived"}""",
            "ok",
        ]);
        var pathfinding = new PathfindingService(rcon) { PollInterval = TimeSpan.FromMilliseconds(10) };

        var result = await pathfinding.WalkToAsync(10, 0, 0.5, 10);

        Assert.Contains("\"status\":\"arrived\"", result);
        Assert.Equal(2, rcon.AllCommands.Count(c => c.Contains("storage.walk_dirs[game.connected_players[1].index] = nil")));
    }

    [Fact]
    public async Task WalkTo_HandsThePathToTheTickHandler()
    {
        var rcon = new ScriptedRconClient([
            """{"x":0,"y":0}""", "ok", "1", TwoPointPath, "ok",
            """{"x":10,"y":0,"status":"arrived"}""", "ok",
        ]);
        var pathfinding = new PathfindingService(rcon) { PollInterval = TimeSpan.FromMilliseconds(10) };

        await pathfinding.WalkToAsync(10, 0, 0.5, 10);

        // Handlers are installed again after a game reload (they are not saved with the game)
        Assert.Contains("if not __mcp_walk_handlers then", rcon.AllCommands[2]);
        Assert.Contains("local pts = {{x=5,y=0},{x=10,y=0}}", rcon.AllCommands[4]);
        Assert.Contains("storage.walk_paths[p.index] = {points = pts, i = 1, target = target, tolerance = 0.5", rcon.AllCommands[4]);
    }

    [Fact]
    public void TickHandler_SteersEveryTickAndDetectsStuck()
    {
        var handler = FactorioService.LuaOnTickHandler;

        Assert.Contains("for index, w in pairs(storage.walk_paths or {}) do", handler);
        Assert.Contains("local reach = math.max(0.2, p.character_running_speed + 0.05)", handler);
        Assert.Contains("if w.idle > 45 then", handler);
        Assert.Contains("p.walking_state = {walking = true, direction = dir}", handler);
        // Live game: an agent character kept walking 1.3 tiles past the target until the next RCON poll
        Assert.Equal(2, handler.Split("p.walking_state = {walking = false, direction = p.walking_state.direction}").Length - 1);
    }


    // ── ParsePosition ───────────────────────────────────────────────

    [Fact]
    public void ParsePosition_ValidJson_ReturnsCoordinates()
    {
        var (x, y) = PathfindingService.ParsePosition("""{"x":10.5,"y":-3.2}""");
        Assert.Equal(10.5, x);
        Assert.Equal(-3.2, y);
    }

    // ── Distance ────────────────────────────────────────────────────

    [Fact]
    public void Distance_SamePoint_ReturnsZero()
    {
        Assert.Equal(0.0, PathfindingService.Distance(5, 5, 5, 5));
    }

    [Fact]
    public void Distance_KnownDistance_ReturnsCorrect()
    {
        Assert.Equal(5.0, PathfindingService.Distance(0, 0, 3, 4), precision: 6);
    }

    // ── FormatResult ────────────────────────────────────────────────

    [Fact]
    public void FormatResult_ContainsAllFields()
    {
        var result = PathfindingService.FormatResult("arrived", 1.0, 2.0, 3.0, 4.0, 2.83, 5.0);
        Assert.Contains("\"status\":\"arrived\"", result);
        Assert.Contains("\"x\":1", result);
        Assert.Contains("\"y\":2", result);
        Assert.Contains("\"target_x\":3", result);
        Assert.Contains("\"target_y\":4", result);
        Assert.Contains("\"distance\":", result);
        Assert.Contains("2.83", result);
        Assert.Contains("\"tolerance\":5", result);
    }
}
