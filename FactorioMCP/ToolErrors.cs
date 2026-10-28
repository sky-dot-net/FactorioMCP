using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FactorioMCP;

/// <summary>
/// Turns an exception from a tool into a short error result that names the cause. Without this the MCP library
/// answers only "An error occurred invoking '&lt;tool&gt;'." and the agent cannot tell what went wrong.
/// Parameter errors read like exception names: <c>missing_recipe</c>; <c>invalid_inventoryType</c> with the valid
/// values as message.
/// </summary>
internal static partial class ToolErrors
{
    [GeneratedRegex("missing a value for the required parameter '([^']+)'")]
    private static partial Regex MissingParameter();

    /// <param name="ex">The exception the tool threw.</param>
    /// <param name="arguments">The call's arguments, to find a parameter whose value has the wrong type.</param>
    /// <param name="inputSchema">The tool's input schema (JSON schema with <c>properties</c>).</param>
    internal static string ToJson(Exception ex, IEnumerable<KeyValuePair<string, JsonElement>>? arguments = null, JsonElement? inputSchema = null)
    {
        if (MissingParameter().Match(ex.Message) is { Success: true } missing)
            return Error($"missing_{missing.Groups[1].Value}");

        // Argument binding fails with a JsonException that does not name the parameter
        if (ex is JsonException && WrongTypeParameter(arguments, inputSchema) is { } wrong)
            return Error($"invalid_{wrong.Name}", $"expected: {wrong.Expected}");

        // A tool rejected a value: the parameter in the error code, the explanation without .NET's "(Parameter 'x')"
        if (ex is ArgumentException { ParamName: { } param } argument && param != "arguments")
            return Error($"invalid_{param}", argument.Message.Replace($" (Parameter '{param}')", ""));

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

    private static string Error(string error, string? message = null)
    {
        var result = new JsonObject { ["success"] = false, ["error"] = error };
        if (message is not null)
            result["message"] = message;
        return result.ToJsonString();
    }

    /// <summary>The first argument whose JSON kind does not match the type its schema property expects.</summary>
    internal static (string Name, string Expected)? WrongTypeParameter(IEnumerable<KeyValuePair<string, JsonElement>>? arguments, JsonElement? inputSchema)
    {
        if (arguments is null || inputSchema is not { ValueKind: JsonValueKind.Object } schema
            || !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var (name, value) in arguments)
        {
            if (!properties.TryGetProperty(name, out var property) || !property.TryGetProperty("type", out var type))
                continue;
            var expected = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Select(t => t.GetString()).Where(t => t is not null and not "null").FirstOrDefault()
                : type.GetString();
            if (expected is null || value.ValueKind == JsonValueKind.Null)
                continue;
            var matches = expected switch
            {
                "number" => value.ValueKind == JsonValueKind.Number,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ => true,
            };
            if (!matches)
                return (name, expected);
        }
        return null;
    }
}
