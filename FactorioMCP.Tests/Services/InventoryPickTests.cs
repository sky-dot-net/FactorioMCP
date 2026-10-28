using FactorioMCP.Services;
using FactorioMCP.Tests.Rcon;
using Xunit;

namespace FactorioMCP.Tests.Services;

/// <summary>
/// Regression: get_entity_inventory and transfer_all_items took entities[1] of a radius-1 search, so a belt or
/// ore next to a chest was picked and the tool answered no_inventory for a chest that was right there.
/// </summary>
public class InventoryPickTests
{
    private static (FactorioService Service, PlayerCapturingRconClient Rcon) Agent()
    {
        var rcon = new PlayerCapturingRconClient(null, "builder-2");
        return (new FactorioService(rcon), rcon);
    }

    [Fact]
    public async Task GetEntityInventory_PicksEntityThatHasTheInventory()
    {
        var (service, rcon) = Agent();

        await service.GetEntityInventoryAsync(74.5, 18.5);

        Assert.Contains("local e = pick_with_inventory(player.surface, 74.5, 18.5, inv_id)", rcon.LastCommand);
        Assert.Contains("e.get_inventory(inv_id)", rcon.LastCommand);
        Assert.DoesNotContain("entities[1]", rcon.LastCommand);
        Assert.Contains("\"entities_here\":\"", rcon.LastCommand);
    }

    [Fact]
    public async Task TransferAllItems_PicksEntityThatHasTheInventory()
    {
        var (service, rcon) = Agent();

        await service.TransferAllItemsAsync(74.5, 18.5);

        Assert.Contains("local e = pick_with_inventory(player.surface, 74.5, 18.5, inv_id)", rcon.LastCommand);
        Assert.DoesNotContain("entities[1]", rcon.LastCommand);
    }

    [Theory]
    [InlineData("chest\"]")]
    [InlineData("Chest")]
    public async Task GetEntityInventory_InvalidInventoryType_Throws(string inventoryType)
    {
        var (service, _) = Agent();

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetEntityInventoryAsync(0, 0, inventoryType));
    }

    [Fact]
    public void PickHelper_PrefersEntityContainingThePointAndSkipsCharacters()
    {
        var helper = FactorioService.LuaPickWithInventory;

        Assert.Contains("consider(surface.find_entities_filtered{ position = { x, y } })", helper);
        Assert.Contains("e.type ~= \"character\" and e.get_inventory(inv_id)", helper);
    }
}
