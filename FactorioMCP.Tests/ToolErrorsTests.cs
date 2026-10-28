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
    public void MissingParameter_IsOneCompactLine()
    {
        var json = ToolErrors.ToJson(new ArgumentException(
            "The arguments dictionary is missing a value for the required parameter 'recipe'.", "arguments"));

        Assert.Equal("""{"success":false,"error":"missing_value_for_param: recipe"}""", json);
    }

    [Fact]
    public void WrongTypeParameter_IsNamedFromTheSchema()
    {
        // Live game: x:"abc" was reported as an unreadable game answer
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"x":"abc","y":0}""");
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"}}}""").RootElement;

        var json = ToolErrors.ToJson(new JsonException("The JSON value could not be converted to System.Double."), args, schema);

        Assert.Equal("""{"success":false,"error":"invalid_value_for_param: x (number)"}""", json);
    }

    [Fact]
    public void UnreadableGameAnswer_SaysSo()
    {
        var (error, _) = Parse(ToolErrors.ToJson(new JsonException("'C' is an invalid start of a value.")));

        Assert.Equal("unexpected_game_response", error);
    }

    [Fact]
    public void RejectedValue_NamesTheParameterAndKeepsTheHint()
    {
        var (error, message) = Parse(ToolErrors.ToJson(new ArgumentException("Invalid inventoryType 'Chest': use fuel, chest.", "inventoryType")));

        Assert.Equal("invalid_value_for_param: inventoryType", error);
        Assert.Equal("Invalid inventoryType 'Chest': use fuel, chest.", message);
    }

    [Fact]
    public void OtherExceptions_KeepTheirMessage()
    {
        var (error, message) = Parse(ToolErrors.ToJson(new NullReferenceException("x was null")));

        Assert.Equal("tool_failed", error);
        Assert.Equal("x was null", message);
    }
}
