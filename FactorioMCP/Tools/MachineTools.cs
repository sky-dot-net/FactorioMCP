using FactorioMCP.Services;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace FactorioMCP.Tools;

/// <summary>
/// MCP tools that configure placed machines: assembler recipes, logistic chest requests and the rocket launch.
/// Player agents needed execute_lua for each of these before.
/// </summary>
[McpServerToolType]
internal sealed class MachineTools(FactorioService factorio, GameCommandQueue queue)
{
    [McpServerTool, Description(
        "Set the recipe of an assembling machine, chemical plant or oil refinery at a position (furnaces pick their " +
        "recipe themselves). The machine must be within reach. Items the old recipe held go back to your inventory. " +
        "Returns {success, entity, recipe, previous_recipe, items_returned} or {success:false, error} where error is " +
        "no_machine, out_of_range, unknown_recipe, recipe_not_researched or wrong_machine (with the machine's categories).")]
    public Task<string> SetRecipe(
        [Description("X coordinate of the machine (any tile it covers)")] double x,
        [Description("Y coordinate of the machine (any tile it covers)")] double y,
        [Description("Recipe name, e.g. 'iron-gear-wheel', 'advanced-oil-processing', 'plastic-bar'")] string recipe,
        CancellationToken cancellationToken = default)
    {
        return queue.ExecuteAsync(nameof(SetRecipe), ct => factorio.SetRecipeAsync(x, y, recipe, ct), cancellationToken);
    }

    [McpServerTool, Description(
        "Set what a requester or buffer chest requests from the logistic network, and optionally limit its usable slots. " +
        "The chest must be within reach. Returns the chest's requests after the change, or {success:false, error} where " +
        "error is no_logistic_chest, chest_cannot_request, out_of_range, unknown_item, invalid_requests or invalid_slot_limit.")]
    public Task<string> SetLogisticRequests(
        [Description("X coordinate of the chest")] double x,
        [Description("Y coordinate of the chest")] double y,
        [Description("JSON array of requests: [{\"item\":\"iron-plate\",\"count\":200}, ...]. count = minimum amount to keep; 0 removes that request.")]
        string requests,
        [Description("true (default): the list replaces all existing requests. false: only the listed items are added or changed.")]
        bool replace = true,
        [Description("Optional: number of usable chest slots (the red bar). 0 removes the limit. Omit to leave it unchanged.")]
        int? slotLimit = null,
        CancellationToken cancellationToken = default)
    {
        return queue.ExecuteAsync(nameof(SetLogisticRequests),
            ct => factorio.SetLogisticRequestsAsync(x, y, requests, replace, slotLimit, ct), cancellationToken);
    }

    [McpServerTool, Description(
        "Launch the finished rocket of a rocket silo. Give the silo position, or omit it to use the nearest silo. " +
        "The silo must be within reach. Returns {success, rockets_launched} or {success:false, error} where error is " +
        "no_rocket_silo, out_of_range, rocket_not_ready (with the silo status and rocket_parts built so far) or launch_refused.")]
    public Task<string> LaunchRocket(
        [Description("Optional X coordinate of the rocket silo")] double? x = null,
        [Description("Optional Y coordinate of the rocket silo")] double? y = null,
        CancellationToken cancellationToken = default)
    {
        return queue.ExecuteAsync(nameof(LaunchRocket), ct => factorio.LaunchRocketAsync(x, y, ct), cancellationToken);
    }
}
