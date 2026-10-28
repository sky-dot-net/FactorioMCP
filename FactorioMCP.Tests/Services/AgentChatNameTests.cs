using FactorioMCP.Rcon;
using FactorioMCP.Services;
using FactorioMCP.Tests.Rcon;
using Xunit;

namespace FactorioMCP.Tests.Services;

public class AgentChatNameTests
{
    private static (FactorioService Service, PlayerCapturingRconClient Rcon) Agent(string? agentName, string? playerName = null)
    {
        var rcon = new PlayerCapturingRconClient(playerName, agentName);
        return (new FactorioService(rcon), rcon);
    }

    [Fact]
    public void EngineerLabel_IsFactorioMcpEngineerWithoutAiPrefix()
    {
        var prelude = RconClient.BuildAgentPrelude("engineer");

        Assert.Contains("return \"Factorio MCP Engineer\"", prelude);
        Assert.DoesNotContain("\"[AI] \" ..", prelude);
    }

    [Fact]
    public void OtherAgentLabel_IsAiPrefixedChosenNameOrAgentName()
    {
        var prelude = RconClient.BuildAgentPrelude("builder-2");

        Assert.Contains("return \"[AI] \" .. (chosen or \"builder-2\")", prelude);
        Assert.Contains("storage.agent_display_names[\"builder-2\"]", prelude);
    }

    [Fact]
    public void Prelude_DrawsAndUpdatesNameLabelAboveCharacter()
    {
        var prelude = RconClient.BuildAgentPrelude("agent");

        Assert.Contains("rendering.draw_text(", prelude);
        Assert.Contains("target = { entity = c, offset = { 0, -1.1 } }", prelude);
        Assert.Contains("label.text = text", prelude);
    }

    [Fact]
    public async Task SendChat_AgentMode_UsesAgentLabelAndColour()
    {
        var (service, rcon) = Agent("builder-2");

        await service.SendChatMessageAsync("hello");

        Assert.Contains("local sender = __agent_label()", rcon.LastCommand);
        Assert.Contains("game.print(sender .. \": hello\", { color = light })", rcon.LastCommand);
        Assert.Contains("r = 0.6 + 0.4 * col.r", rcon.LastCommand);
        Assert.Contains("You have not chosen a name yet", rcon.LastCommand);
    }

    [Fact]
    public async Task SendChat_Engineer_HasNoNameHint()
    {
        var (service, rcon) = Agent("engineer");

        await service.SendChatMessageAsync("hello");

        Assert.Contains("local sender = __agent_label()", rcon.LastCommand);
        Assert.DoesNotContain("You have not chosen a name yet", rcon.LastCommand);
    }

    [Fact]
    public async Task SendChat_PlayerMode_KeepsAiTag()
    {
        var (service, rcon) = Agent(null, "Sky");

        await service.SendChatMessageAsync("hello");

        Assert.Contains("game.print(\"[AI] hello\")", rcon.LastCommand);
    }

    [Fact]
    public async Task SetAgentName_StoresNameOnceAndRejectsTakenNames()
    {
        var (service, rcon) = Agent("builder-2");

        await service.SetAgentNameAsync(" Rusty ");

        Assert.Contains("local key, wanted = \"builder-2\", \"Rusty\"", rcon.LastCommand);
        Assert.Contains("cannot be changed", rcon.LastCommand);
        Assert.Contains("is already used by another agent", rcon.LastCommand);
        Assert.Contains("storage.agent_display_names[key] = wanted", rcon.LastCommand);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" lead space is trimmed but empty")]
    [InlineData("[AI] Bob")]
    [InlineData("name\"with quote")]
    [InlineData("this name is far too long to be accepted ok")]
    public async Task SetAgentName_InvalidOrReservedName_ReturnsErrorWithoutLua(string name)
    {
        var (service, rcon) = Agent("builder-2");

        var result = await service.SetAgentNameAsync(name == " lead space is trimmed but empty" ? "   " : name);

        Assert.Contains("\"status\":\"error\"", result);
        Assert.Null(rcon.LastCommand);
    }

    [Fact]
    public async Task SetAgentName_ReservedEngineerName_IsRejected()
    {
        var (service, _) = Agent("builder-2");

        var result = await service.SetAgentNameAsync("Factorio MCP Engineer");

        Assert.Contains("reserved for the MCP engineer", result);
    }

    [Fact]
    public async Task SetAgentName_Engineer_IsFixed()
    {
        var (service, _) = Agent("engineer");

        var result = await service.SetAgentNameAsync("Bob");

        Assert.Contains("chat name is fixed: Factorio MCP Engineer", result);
    }

    [Fact]
    public async Task ChatListener_RegistersAgentsCommand()
    {
        var (service, rcon) = Agent("builder-2");

        await service.InitializeChatListenerAsync();

        Assert.Contains("commands.add_command(\"agents\"", rcon.LastCommand);
        Assert.Contains("key == \"engineer\" and \"Factorio MCP Engineer\"", rcon.LastCommand);
        Assert.DoesNotContain("{{AgentsCommandLua}}", rcon.LastCommand);
    }

    [Fact]
    public async Task SetAgentName_PlayerMode_ExplainsWhyNot()
    {
        var (service, _) = Agent(null, "Sky");

        var result = await service.SetAgentNameAsync("Bob");

        Assert.Contains("works only for scripted agents", result);
    }
}
