using FactorioMCP.Services;
using FactorioMCP.Tests.Rcon;
using Xunit;

namespace FactorioMCP.Tests.Services;

/// <summary>
/// Regression: ghost placement checked can_place_entity{name="entity-ghost", inner_name=...}, which is false for
/// every position, so place_ghost_entity and place_ghost_batch always returned invalid_position.
/// </summary>
public class GhostPlacementTests
{
    private static (BlueprintService Service, PlayerCapturingRconClient Rcon) Agent()
    {
        var rcon = new PlayerCapturingRconClient(null, "builder-2");
        return (new BlueprintService(rcon), rcon);
    }

    [Fact]
    public async Task PlaceGhostEntity_ChecksInnerEntityAsManualGhost()
    {
        var (service, rcon) = Agent();

        await service.PlaceGhostEntityAsync("transport-belt", 1.5, 2.5);

        Assert.Contains("can_place_entity{name=inner, position=pos, force=player.force, direction=dir, build_check_type=defines.build_check_type.manual_ghost}", rcon.LastCommand);
        Assert.DoesNotContain("can_place_entity{name=\"entity-ghost\"", rcon.LastCommand);
        Assert.Contains("\"error\":\"unknown_entity\"", rcon.LastCommand);
        Assert.Contains("\"blocked_by\":\"", rcon.LastCommand);
    }

    [Fact]
    public async Task PlaceGhostBatch_ChecksInnerEntityAsManualGhost()
    {
        var (service, rcon) = Agent();

        await service.PlaceGhostBatchAsync("""[{"name":"transport-belt","x":1.5,"y":2.5}]""");

        Assert.Contains("can_place_entity{name=name, position={x, y}, force=force, direction=dir, build_check_type=defines.build_check_type.manual_ghost}", rcon.LastCommand);
        Assert.DoesNotContain("can_place_entity{name=\"entity-ghost\"", rcon.LastCommand);
        Assert.Contains("\"error\":\"unknown_entity\"", rcon.LastCommand);
    }
}
