using FactorioMCP.Services;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace FactorioMCP.Tools;

/// <summary>
/// MCP tools for planning and understanding transport belt placement.
/// Provides route computation and direction guidance for belt networks.
/// </summary>
[McpServerToolType]
internal sealed class BeltTools(BeltPlannerService planner, BeltRouterService router, GameCommandQueue queue)
{
    [McpServerTool, Description(
        "Route a transport belt around obstacles. Reads the occupied tiles (buildings, belts, trees, rocks, " +
        "items on the ground, water) from the game and finds the shortest route from start to end with A*, " +
        "using underground belt pairs to jump over obstacles. Returns ordered steps " +
        "{entity, x, y, direction, belt_type}; place them in order with PlaceEntity. belt_type 'input' is an " +
        "underground entrance, the next 'output' step is its exit; its direction is the travel direction. " +
        "After placing an exit, check that PlaceEntity reports belt_type 'output'. Start and end tiles must be free. " +
        "The search area is the rectangle around start and end, widened by margin tiles.")]
    public Task<string> RouteBelt(
        [Description("X of the first belt tile (where items enter)")] double startX,
        [Description("Y of the first belt tile")] double startY,
        [Description("X of the last belt tile (where items leave)")] double endX,
        [Description("Y of the last belt tile")] double endY,
        [Description("Extra tiles around the start-end rectangle that the route may use (default 10, max 50)")] int margin = 10,
        [Description("Belt entity (default transport-belt)")] string beltName = "transport-belt",
        [Description("Underground belt entity of the same tier (default underground-belt)")] string undergroundName = "underground-belt",
        CancellationToken cancellationToken = default)
    {
        return queue.ExecuteAsync(nameof(RouteBelt),
            ct => router.RouteBeltAsync(startX, startY, endX, endY, margin, beltName, undergroundName, ct),
            cancellationToken);
    }

    [McpServerTool, Description(
        "Plan a transport belt route between two positions. Returns an ordered list of belt tile positions " +
        "and directions that you can place one-by-one with PlaceEntity. Supports straight lines and L-shaped " +
        "routes (one 90° turn). Items flow from start to end position. " +
        "BELT MECHANICS: Belt direction = the direction items FLOW (the way arrows point). A north-facing belt " +
        "moves items upward (decreasing Y). A east-facing belt moves items rightward (increasing X). " +
        "At corners, the belt at the turn point faces the NEW direction after the turn. " +
        "USAGE: Call this tool to get the route plan, then place each belt tile using PlaceEntity with " +
        "'transport-belt' (or 'fast-transport-belt', 'express-transport-belt') and the direction from each step. " +
        "Walk close to the belt tiles before placing — you must be within build distance of each tile.")]
    public string PlanBeltRoute(
        [Description("X coordinate of the first belt tile (where items enter the belt line)")]
        double startX,
        [Description("Y coordinate of the first belt tile")]
        double startY,
        [Description("X coordinate of the last belt tile (where items exit the belt line)")]
        double endX,
        [Description("Y coordinate of the last belt tile")]
        double endY,
        [Description("For L-shaped routes only: 'horizontal_first' (go horizontal then turn vertical) or " +
                     "'vertical_first' (go vertical then turn horizontal). Ignored for straight lines. " +
                     "Choose based on which path avoids obstacles or connects better to existing belts.")]
        string turnPreference = "horizontal_first")
    {
        return planner.PlanRoute(startX, startY, endX, endY, turnPreference);
    }

}
