using FactorioMCP.Services;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace FactorioMCP.Tools;

/// <summary>
/// MCP tools for reading and sending in-game chat messages.
/// Uses an on_console_chat event handler registered via RCON to capture messages
/// in storage.chat_log, and game.print() to send messages to all players.
/// </summary>
[McpServerToolType]
internal sealed class ChatTools(FactorioService factorio, GameCommandQueue queue)
{
    [McpServerTool, Description(
        "Get chat messages from the in-game chat log. Returns messages sent by players " +
        "(not commands). Use the 'sinceTick' parameter to only get new messages since " +
        "the last poll — pass the 'latest_tick' value from the previous response. " +
        "The chat listener must be initialized first (done automatically on startup).")]
    public Task<string> GetChatMessages(
        [Description("Only return messages after this game tick. Pass 0 to get all messages, " +
            "or pass the 'latest_tick' from a previous GetChatMessages response to get only new ones.")]
        long sinceTick = 0,
        CancellationToken cancellationToken = default)
    {
        return queue.ExecuteAsync(nameof(GetChatMessages), ct => factorio.GetChatMessagesAsync(sinceTick, ct), cancellationToken);
    }

    [McpServerTool, Description(
        "Send a chat message visible to all connected players. The sender is shown as '[AI] <your name>' " +
        "(choose the name once with set_agent_name). Returns the sender label used.")]
    public Task<string> SendChatMessage(
        [Description("The message text to send to the in-game chat")]
        string message,
        CancellationToken cancellationToken = default)
    {
        return queue.ExecuteAsync(nameof(SendChatMessage), ct => factorio.SendChatMessageAsync(message, ct), cancellationToken);
    }

    [McpServerTool, Description(
        "Choose your own name when you join the server for the first time. Other players see it as " +
        "'[AI] <name>' in the chat and above your character. The name is chosen once and cannot be changed later; " +
        "calling again with the same name is fine. Returns {status, chat_name} or {status:\"error\", error}.")]
    public Task<string> SetAgentName(
        [Description("Your name: 1-30 characters (letters, digits, space, _ . ' -), starting with a letter or digit.")]
        string name,
        CancellationToken cancellationToken = default)
    {
        return queue.ExecuteAsync(nameof(SetAgentName), ct => factorio.SetAgentNameAsync(name, ct), cancellationToken);
    }
}
