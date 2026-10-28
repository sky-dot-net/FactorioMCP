using FactorioMCP.Services;
using Xunit;

namespace FactorioMCP.Tests.Services;

/// <summary>
/// Regressions found in the live game (Factorio 2.1.23):
/// - 2.1 renamed furnace_*/assembling_machine_* inventories to crafter_*; insert_items into furnace_source and
///   get_entity_inventory on furnace_result failed, and inspect_entity crashed on every entity.
/// - place_entity used the strict script build check, so one ore on the ground in front of a running drill blocked a
///   furnace that a player may place there; the refusal did not say what blocks.
/// - wait_for_entity_status waited the full timeout for status names that do not exist ('idle', 'output_full').
/// </summary>
public class InventoryAndBuildTests
{
    private readonly CapturingRconClient _rcon = new();
    private readonly FactorioService _service;

    public InventoryAndBuildTests() => _service = new FactorioService(_rcon);

    [Theory]
    [InlineData("furnace_source", "crafter_input")]
    [InlineData("assembling_machine_input", "crafter_input")]
    [InlineData("furnace_result", "crafter_output")]
    [InlineData("assembling_machine_output", "crafter_output")]
    [InlineData("assembling_machine_modules", "crafter_modules")]
    [InlineData("crafter_modules", "crafter_modules")]
    [InlineData("fuel", "fuel")]
    [InlineData(" chest ", "chest")]
    public void InventoryNames_OldNamesMapToFactorio21(string name, string key)
    {
        Assert.Equal(key, InventoryNames.Resolve(name));
    }

    [Theory]
    [InlineData("Chest")]
    [InlineData("chest\"]")]
    public void InventoryNames_InvalidName_Throws(string name)
    {
        Assert.Throws<ArgumentException>(() => InventoryNames.Resolve(name));
    }

    [Fact]
    public async Task InsertItems_OldInventoryName_UsesCrafterInput()
    {
        await _service.InsertItemsAsync(1, 2, "iron-ore", 5, "furnace_source");

        Assert.Contains("defines.inventory[\"crafter_input\"]", _rcon.LastCommand);
        Assert.DoesNotContain("defines.inventory.furnace_source", _rcon.LastCommand);
    }

    [Fact]
    public async Task InsertItems_ModuleSlot_IsAvailable()
    {
        await _service.InsertItemsAsync(1, 2, "speed-module", 2, "assembling_machine_modules");

        Assert.Contains("defines.inventory[\"crafter_modules\"]", _rcon.LastCommand);
    }

    [Fact]
    public async Task InspectEntity_UsesFactorio21InventoryNames()
    {
        await _service.InspectEntityAsync(1, 2);

        Assert.Contains("\"crafter_input\", \"crafter_output\", \"crafter_modules\"", _rcon.LastCommand);
        Assert.DoesNotContain("defines.inventory.furnace_source", _rcon.LastCommand);
        Assert.Contains("local inv = index and not seen_index[index] and e.get_inventory(index)", _rcon.LastCommand);
    }

    [Fact]
    public async Task PlaceEntity_BuildsLikeAPlayer()
    {
        await _service.PlaceEntityAsync("stone-furnace", 1, 2);

        Assert.Contains("build_check_type=defines.build_check_type.manual}", _rcon.LastCommand);
        Assert.Contains("\"blocked_by\":\"'..blocked_by(surface, name, pos, dir)..'\"", _rcon.LastCommand);
        Assert.Contains("take_ground_items(player, placed)", _rcon.LastCommand);
        Assert.DoesNotContain("surface.can_place_entity{name=name, position=pos, force=player.force, direction=dir}", _rcon.LastCommand);
    }

    [Fact]
    public void GroundItems_GoToTheBuilderAndSpillAroundItWhenFull()
    {
        var helper = FactorioService.LuaPlayerBuild;

        Assert.Contains("local inserted = builder.insert{name = name, count = count, quality = quality}", helper);
        Assert.Contains("spill_item_stack{position = builder.position", helper);
        Assert.Contains("item.destroy()", helper);
    }

    [Fact]
    public async Task PlaceEntitySmartAndInserters_UseThePlayerBuildCheck()
    {
        await _service.PlaceEntitySmartAsync("stone-furnace", 1, 2);
        var smart = _rcon.LastCommand!;

        Assert.Contains("if can_build(surface, name, {px, py}, dir, player.force) then", smart);
        Assert.Contains("take_ground_items(player, placed)", smart);
    }

    [Fact]
    public async Task WaitForEntityStatus_UnknownStatus_FailsAtOnce()
    {
        var rcon = new ScriptedRconClient(["no"]);
        var service = new FactorioService(rcon);

        var result = await service.WaitForEntityStatusAsync(1, 2, "idle", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));

        Assert.Contains("\"error\":\"invalid_targetStatus\"", result);
        Assert.Single(rcon.AllCommands);
    }
}
