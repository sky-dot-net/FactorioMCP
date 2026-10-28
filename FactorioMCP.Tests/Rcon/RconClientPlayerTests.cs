using FactorioMCP.Rcon;
using FactorioMCP.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FactorioMCP.Tests.Rcon;

/// <summary>
/// Test double that captures commands and is configured with FACTORIO_PLAYER / FACTORIO_AGENT values.
/// </summary>
internal sealed class PlayerCapturingRconClient(string? playerName, string? agentName = null) : RconClient(null,
    new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FACTORIO_PLAYER"] = playerName,
            ["FACTORIO_AGENT"] = agentName
        })
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
    public async Task ExecuteLua_WithoutPlayer_UsesDefaultAgentCharacter()
    {
        var rcon = new PlayerCapturingRconClient(null);

        await rcon.ExecuteLuaAsync("local p = game.connected_players[1]");

        Assert.StartsWith("/silent-command local function __agent()", rcon.LastCommand);
        Assert.Contains("storage.agent_by_name[\"agent\"]", rcon.LastCommand);
        Assert.EndsWith("\nlocal p = __agent()", rcon.LastCommand);
    }

    [Fact]
    public async Task ExecuteLua_WithAgentName_UsesThatCharacter()
    {
        var rcon = new PlayerCapturingRconClient(null, "builder-2");

        await rcon.ExecuteLuaAsync("local p = game.connected_players[1]");

        Assert.Contains("storage.agent_by_name[\"builder-2\"]", rcon.LastCommand);
        Assert.DoesNotContain("connected_players[1]", rcon.LastCommand);
    }

    [Fact]
    public async Task ExecuteLua_PlayerSettingWinsOverAgent()
    {
        var rcon = new PlayerCapturingRconClient("Sky", "builder-2");

        await rcon.ExecuteLuaAsync("local p = game.connected_players[1]");

        Assert.Equal("/silent-command local p = game.get_player(\"Sky\")", rcon.LastCommand);
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("x\"]")]
    public void InvalidAgentName_Throws(string name)
    {
        Assert.Throws<ArgumentException>(() => new PlayerCapturingRconClient(null, name));
    }

    [Fact]
    public void AgentPrelude_ProvidesPlayerOnlyMembers()
    {
        var prelude = RconClient.BuildAgentPrelude("agent");

        Assert.Contains("create_entity({ name = \"character\"", prelude);
        Assert.Contains("storage.agent_chars[-c.unit_number] = c", prelude);
        foreach (var member in new[] { "character", "index", "connected", "name", "clear_cursor", "build_from_cursor" })
            Assert.Contains($"k == \"{member}\"", prelude);
    }

    [Fact]
    public void OnTickHandler_ResolvesAgentCharactersByNegativeIndex()
    {
        Assert.Contains("storage.agent_chars and storage.agent_chars[index]", FactorioService.LuaOnTickHandler);
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
