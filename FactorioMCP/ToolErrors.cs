using System.Text.Json;
using System.Text.Json.Nodes;

namespace FactorioMCP;

/// <summary>
/// Turns an exception from a tool into a short error result that names the cause. Without this the MCP library
/// answers only "An error occurred invoking '&lt;tool&gt;'." and the agent cannot tell what went wrong.
/// </summary>
internal static class ToolErrors
{
    internal static string ToJson(Exception ex)
    {
        var (error, message) = ex switch
        {
            ArgumentException => ("invalid_argument", ex.Message),
            JsonException => ("unexpected_game_response", "The game answered something the tool could not read: " + ex.Message),
            TimeoutException => ("timeout", ex.Message),
            IOException or System.Net.Sockets.SocketException => ("not_connected", "No connection to the game server: " + ex.Message),
            InvalidOperationException when ex.Message.Contains("connect", StringComparison.OrdinalIgnoreCase)
                => ("not_connected", ex.Message),
            _ => ("tool_failed", ex.Message),
        };
        return new JsonObject { ["success"] = false, ["error"] = error, ["message"] = message }.ToJsonString();
    }
}
