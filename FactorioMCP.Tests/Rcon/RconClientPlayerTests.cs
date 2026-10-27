using FactorioMCP.Rcon;
using FactorioMCP.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FactorioMCP.Tests.Rcon;

/// <summary>
/// Test double that captures commands and is configured with a FACTORIO_PLAYER value.
/// </summary>
internal sealed class PlayerCapturingRconClient(string? playerName) : RconClient(null,
    new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["FACTORIO_PLAYER"] = playerName })
        .Build())
{
    public string? LastCommand { get; private set; }

    public override Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default)
    {
        LastCommand = command;
        return Task.FromResult(string.Empty);
    }
}

public class RconClientPlayerTests
{
    [Fact]
    public void BuildPlayerExpression_NoName_UsesFirstConnectedPlayer()
    {
        Assert.Equal("game.connected_players[1]", RconClient.BuildPlayerExpression(null));
        Assert.Equal("game.connected_players[1]", RconClient.BuildPlayerExpression(" "));
    }

    [Fact]
    public void BuildPlayerExpression_Name_UsesGetPlayer()
    {
        Assert.Equal("game.get_player(\"Agent-2\")", RconClient.BuildPlayerExpression("Agent-2"));
    }

    [Theory]
    [InlineData("a\"b")]
    [InlineData("a b")]
    [InlineData("x\")game.print(1)--")]
    public void BuildPlayerExpression_InvalidName_Throws(string name)
    {
        Assert.Throws<ArgumentException>(() => RconClient.BuildPlayerExpression(name));
    }

    [Fact]
    public async Task ExecuteLua_WithPlayer_ReplacesFirstConnectedPlayer()
    {
        var rcon = new PlayerCapturingRconClient("Agent2");

        await rcon.ExecuteLuaAsync("local p = game.connected_players[1]; local s = game.connected_players[1].surface");

        Assert.Equal("/silent-command local p = game.get_player(\"Agent2\"); local s = game.get_player(\"Agent2\").surface", rcon.LastCommand);
    }

    [Fact]
    public async Task ExecuteLua_WithoutPlayer_LeavesCommandUnchanged()
    {
        var rcon = new PlayerCapturingRconClient(null);

        await rcon.ExecuteLuaAsync("local p = game.connected_players[1]");

        Assert.Equal("/silent-command local p = game.connected_players[1]", rcon.LastCommand);
    }

    [Fact]
    public void OnTickHandler_KeysWalkAndMineStateByPlayerIndex()
    {
        var handler = FactorioService.LuaOnTickHandler;

        Assert.Contains("pairs(storage.walk_dirs or {})", handler);
        Assert.Contains("pairs(storage.mine_states or {})", handler);
        Assert.Contains("game.get_player(index)", handler);
        Assert.DoesNotContain("connected_players[1]", handler);
    }

    [Fact]
    public async Task StartMining_WithPlayer_StoresStateUnderThatPlayersIndex()
    {
        var rcon = new PlayerCapturingRconClient("Agent2");
        var mining = new MiningService(rcon);

        await mining.StartMiningResourceAsync(5, 5);

        Assert.Contains("local player = game.get_player(\"Agent2\")", rcon.LastCommand!);
        Assert.Contains("storage.mine_states[player.index] = {position = epos}", rcon.LastCommand!);
        Assert.DoesNotContain("connected_players[1]", rcon.LastCommand!);
    }
}
