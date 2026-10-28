using FactorioMCP.Services;
using FactorioMCP.Tests.Rcon;
using Xunit;

namespace FactorioMCP.Tests.Services;

public class MachineToolsTests
{
    private static (FactorioService Service, PlayerCapturingRconClient Rcon) Agent()
    {
        var rcon = new PlayerCapturingRconClient(null, "builder-2");
        return (new FactorioService(rcon), rcon);
    }

    [Fact]
    public async Task SetRecipe_ChecksMachineReachResearchAndCategory()
    {
        var (service, rcon) = Agent();

        await service.SetRecipeAsync(12.5, -3.5, "plastic-bar");

        var lua = rcon.LastCommand!;
        Assert.Contains("pick(player.surface, 12.5, -3.5, { \"assembling-machine\" })", lua);
        Assert.Contains("\"error\":\"no_machine\"", lua);
        Assert.Contains("if d > player.reach_distance then", lua);
        Assert.Contains("\"error\":\"unknown_recipe\"", lua);
        Assert.Contains("\"error\":\"recipe_not_researched\"", lua);
        Assert.Contains("\"error\":\"wrong_machine\"", lua);
        Assert.Contains("e.set_recipe(\"plastic-bar\")", lua);
        Assert.Contains("player.insert(stack)", lua);
    }

    [Fact]
    public void ParseLogisticRequests_ReadsItemsAndCounts()
    {
        var requests = FactorioService.ParseLogisticRequests("""[{"item":"iron-plate","count":200},{"Item":"coal","Count":0}]""");

        Assert.Equal(2, requests.Count);
        Assert.Equal(("iron-plate", 200), (requests[0].Item, requests[0].Count));
        Assert.Equal(("coal", 0), (requests[1].Item, requests[1].Count));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""[{"count":5}]""")]
    [InlineData("""[{"item":"coal","count":-1}]""")]
    public void ParseLogisticRequests_BadInput_ThrowsWithCause(string json)
    {
        var ex = Assert.Throws<ArgumentException>(() => FactorioService.ParseLogisticRequests(json));
        Assert.Matches("JSON|no item name|negative count", ex.Message);
    }

    [Fact]
    public async Task SetLogisticRequests_InvalidJson_ReturnsErrorWithoutLua()
    {
        var (service, rcon) = Agent();

        var result = await service.SetLogisticRequestsAsync(1, 2, "oops");

        Assert.Contains("\"error\":\"invalid_requests\"", result);
        Assert.Null(rcon.LastCommand);
    }

    [Fact]
    public async Task SetLogisticRequests_BuildsRequestsAndSlotLimit()
    {
        var (service, rcon) = Agent();

        await service.SetLogisticRequestsAsync(1.5, 2.5, """[{"item":"iron-plate","count":200}]""", replace: true, slotLimit: 8);

        var lua = rcon.LastCommand!;
        Assert.Contains("{ \"logistic-container\" }", lua);
        Assert.Contains("local requests = { { \"iron-plate\", 200 }, ", lua);
        Assert.Contains("if true then", lua);
        Assert.Contains("section.set_slot(slot, { value = { name = req[1], quality = \"normal\" }, min = req[2] })", lua);
        Assert.Contains("inv.set_bar(9)", lua);
        Assert.Contains("\"error\":\"chest_cannot_request\"", lua);
    }

    [Fact]
    public async Task SetLogisticRequests_SlotLimitZeroRemovesLimit()
    {
        var (service, rcon) = Agent();

        await service.SetLogisticRequestsAsync(0, 0, "[]", replace: false, slotLimit: 0);

        Assert.Contains("inv.set_bar(nil)", rcon.LastCommand);
        Assert.Contains("if false then", rcon.LastCommand);
    }

    [Fact]
    public async Task LaunchRocket_WithoutPosition_UsesNearestSiloAndReportsWhyNot()
    {
        var (service, rcon) = Agent();

        await service.LaunchRocketAsync();

        var lua = rcon.LastCommand!;
        Assert.Contains("type = \"rocket-silo\", force = player.force", lua);
        Assert.Contains("\"error\":\"rocket_not_ready\"", lua);
        Assert.Contains("e.rocket_parts", lua);
        Assert.Contains("e.launch_rocket()", lua);
    }

    [Fact]
    public async Task PlaceEntity_AgentMode_DoesNotPassCharacterAsLuaPlayer()
    {
        // Regression: create_entity{player=...} needs a LuaPlayer; the agent proxy is not one and every
        // placement failed with "Invalid PlayerIdentification".
        var (service, rcon) = Agent();

        await service.PlaceEntityAsync("stone-furnace", 1, 2);

        Assert.Contains("player=(player.object_name == \"LuaPlayer\" and player or nil)", rcon.LastCommand);
        Assert.DoesNotContain("player=player,", rcon.LastCommand);
        // Regression: the item was removed before create_entity raised, so a failed placement lost it.
        Assert.Contains("local ok, placed = pcall(surface.create_entity, create_args)", rcon.LastCommand);
        Assert.Contains("if not ok or not placed then", rcon.LastCommand);
    }

    [Fact]
    public async Task LaunchRocket_WithPosition_PicksThatSilo()
    {
        var (service, rcon) = Agent();

        await service.LaunchRocketAsync(27.5, -63.5);

        Assert.Contains("pick(player.surface, 27.5, -63.5, { \"rocket-silo\" })", rcon.LastCommand);
    }
}
