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
        "List the restricted areas of this server (on nauvis, walled, humans only). Tools refuse targets inside them " +
        "with error restricted_area. Returns {restricted_areas:[{left, top, right, bottom}]}; empty when there are none.")]
    public string GetRestrictedAreas() => guard.DescribeJson();
}
