using FactorioMCP.Services;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace FactorioMCP.Tools;

/// <summary>
/// MCP tool that tells agents about the restricted area (appsettings.json section RestrictedArea).
/// </summary>
[McpServerToolType]
internal sealed class RestrictedAreaTools(RestrictedAreaGuard guard)
{
    [McpServerTool, Description(
        "List the restricted areas of this server: closed staff areas that only humans may enter, surrounded by a wall. " +
        "AI agents cannot walk into them, build, mine or act inside them; tools refuse such targets with error " +
        "'restricted_area' and walks blocked by one carry a restricted_area_note. That is intended, not a tool fault. " +
        "Returns {success, restricted_areas:[{surface, center, width, height, left, top, right, bottom, access, reason}], " +
        "you_are_exempt, message}; restricted_areas is empty when the server has none.")]
    public string GetRestrictedAreas() => guard.DescribeJson();
}
