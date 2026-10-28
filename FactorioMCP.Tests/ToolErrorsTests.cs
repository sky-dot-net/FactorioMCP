using System.Text.Json;
using Xunit;

namespace FactorioMCP.Tests;

/// <summary>
/// Regression: about 80 tool results in the player transcripts said only "An error occurred invoking '&lt;tool&gt;'."
/// (the MCP library hides exception messages), so agents could not tell a wrong parameter from a broken tool.
/// </summary>
public class ToolErrorsTests
{
    private static (string Error, string Message) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        return (doc.RootElement.GetProperty("error").GetString()!, doc.RootElement.GetProperty("message").GetString()!);
    }

    [Fact]
    public void ArgumentException_NamesTheParameter()
    {
        var (error, message) = Parse(ToolErrors.ToJson(new ArgumentException("missing a value for the required parameter 'recipe'.", "arguments")));

        Assert.Equal("invalid_argument", error);
        Assert.Contains("'recipe'", message);
    }

    [Fact]
    public void UnreadableGameAnswer_SaysSo()
    {
        var (error, _) = Parse(ToolErrors.ToJson(new JsonException("'C' is an invalid start of a value.")));

        Assert.Equal("unexpected_game_response", error);
    }

    [Fact]
    public void OtherExceptions_KeepTheirMessage()
    {
        var (error, message) = Parse(ToolErrors.ToJson(new NullReferenceException("x was null")));

        Assert.Equal("tool_failed", error);
        Assert.Equal("x was null", message);
    }
}
