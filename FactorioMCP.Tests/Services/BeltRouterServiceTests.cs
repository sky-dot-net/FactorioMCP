using FactorioMCP.Services;
using Xunit;
using ExistingUnderground = FactorioMCP.Services.BeltRouterService.ExistingUnderground;

namespace FactorioMCP.Tests.Services;

public class BeltRouterServiceTests
{
    private static readonly (int, int, int, int) Bounds = (-10, -10, 20, 20);

    private static List<BeltRouterService.Placement> Route(
        HashSet<(int, int)> blocked, (int, int) start, (int, int) end,
        List<ExistingUnderground>? undergrounds = null, (int, int, int, int)? bounds = null)
    {
        var route = BeltRouterService.FindRoute(blocked, undergrounds ?? [], start, end, bounds ?? Bounds, 5);
        Assert.NotNull(route);
        return route!;
    }

    /// <summary>Walks the placements and checks they form a connected belt from start to end.</summary>
    private static void AssertConnected(List<BeltRouterService.Placement> route, (int x, int y) start, (int x, int y) end, HashSet<(int, int)> blocked)
    {
        Assert.Equal(start, (route[0].X, route[0].Y));
        Assert.Equal(end, (route[^1].X, route[^1].Y));
        Assert.Equal(route.Count, route.Select(p => (p.X, p.Y)).Distinct().Count());
        for (int i = 0; i < route.Count; i++)
        {
            var p = route[i];
            Assert.DoesNotContain((p.X, p.Y), blocked);
            if (i == route.Count - 1) break;
            var next = route[i + 1];
            if (p.BeltType == "input")
            {
                Assert.Equal("output", next.BeltType);
                Assert.Equal(p.Direction, next.Direction);
                continue;
            }
            var (dx, dy) = p.Direction switch { "north" => (0, -1), "east" => (1, 0), "south" => (0, 1), _ => (-1, 0) };
            Assert.Equal((p.X + dx, p.Y + dy), (next.X, next.Y));
        }
    }

    [Fact]
    public void StraightLine_UsesOnlyBelts()
    {
        var route = Route([], (0, 0), (5, 0));

        Assert.Equal(6, route.Count);
        Assert.All(route, p => Assert.Null(p.BeltType));
        Assert.All(route, p => Assert.Equal("east", p.Direction));
    }

    [Fact]
    public void SmallObstacle_RoutesAround()
    {
        HashSet<(int, int)> blocked = [(3, 0)];
        var route = Route(blocked, (0, 0), (6, 0));

        AssertConnected(route, (0, 0), (6, 0), blocked);
    }

    [Fact]
    public void Wall_IsCrossedWithUnderground()
    {
        // A wall across the whole search area at x=3: walking around is impossible.
        var blocked = Enumerable.Range(-10, 31).Select(y => (3, y)).ToHashSet();
        var route = Route(blocked, (0, 0), (6, 0));

        AssertConnected(route, (0, 0), (6, 0), blocked);
        Assert.Contains(route, p => p.BeltType == "input");
    }

    [Fact]
    public void WallWiderThanUndergroundRange_HasNoRoute()
    {
        var blocked = Enumerable.Range(-10, 31).SelectMany(y => Enumerable.Range(2, 6).Select(x => (x, y))).ToHashSet();

        var route = BeltRouterService.FindRoute(blocked, [], (0, 0), (10, 0), Bounds, 5);

        Assert.Null(route);
    }

    [Fact]
    public void ParallelUndergroundInBetween_IsNotJumpedOver()
    {
        var blocked = Enumerable.Range(-10, 31).Select(y => (3, y)).ToHashSet();
        // An existing east-west underground at (2,0) would pair with a new entrance at (1,0).
        List<ExistingUnderground> existing = [new(2, 0, "underground-belt", false)];
        blocked.Add((2, 0));

        var route = Route(blocked, (0, 0), (6, 0), existing);

        AssertConnected(route, (0, 0), (6, 0), blocked);
        var input = route.First(p => p.BeltType == "input");
        Assert.NotEqual(0, input.Y);
    }

    [Fact]
    public void BlockedStart_IsReported()
    {
        var json = BeltRouterService.PlanToJson([(0, 0)], [], (0, 0), (5, 0), Bounds, 5, "transport-belt", "underground-belt");

        Assert.Contains("start_blocked", json);
    }

    [Fact]
    public void PlanToJson_ReportsStepsAndCounts()
    {
        var blocked = Enumerable.Range(-10, 31).Select(y => (3, y)).ToHashSet();

        var json = BeltRouterService.PlanToJson(blocked, [], (0, 0), (6, 0), Bounds, 5, "transport-belt", "underground-belt");

        Assert.Contains("\"success\":true", json);
        Assert.Contains("\"underground_pairs\":1", json);
        Assert.Contains("\"belt_type\":\"input\"", json);
    }
}
