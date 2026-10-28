namespace FactorioMCP;

/// <summary>
/// The settings file is the only configuration source: environment variables are not read. By default it is
/// <c>appsettings.json</c> next to the exe; <c>--settings &lt;file&gt;</c> picks another one, so several agents can share
/// one build. A missing settings file is created with the defaults on start. Relative paths, in the settings and in
/// <c>--settings</c>, are relative to the exe's folder, so the folder can be copied anywhere and run as is.
/// </summary>
internal static class AppSettings
{
    internal const string DefaultFileName = "appsettings.json";

    internal const string RconHost = "Rcon:Host";
    internal const string RconPort = "Rcon:Port";
    internal const string RconPassword = "Rcon:Password";
    internal const string AgentName = "Agent:Name";
    internal const string AgentColor = "Agent:Color";
    internal const string ControlPlayer = "Agent:ControlPlayer";
    internal const string BuildingsFile = "BuildingsFile";

    /// <summary>Content of a new settings file. Comments are allowed in the file.</summary>
    internal const string DefaultContent = """
        {
          // RCON connection to the Factorio server.
          "Rcon": {
            "Host": "127.0.0.1",
            "Port": 27015,
            "Password": ""
          },

          // Who this MCP server plays as.
          "Agent": {
            // Name of the scripted character this server controls; it is created near spawn on first use.
            // The agent named "engineer" is the MCP engineer: red, named "Factorio MCP Engineer" in chat.
            "Name": "agent",
            // Character colour: "#RRGGBB", "r,g,b" (0-255), "none" (default colour) or "" (engineer red, others default).
            "Color": "",
            // Name of a connected player to control instead of a scripted character; "" = scripted character.
            "ControlPlayer": ""
          },

          // File for the building memory. A relative path is inside the MCP's folder.
          "BuildingsFile": "buildings.json",

          // Area that AI agents may not enter or act in (humans only). The agent "engineer" is exempt.
          // Width or Height 0 = no restricted area, the feature is off.
          "RestrictedArea": {
            "CenterX": 0,
            "CenterY": 0,
            "Width": 0,
            "Height": 0
          }
        }
        """;

    /// <summary>The settings file named by <c>--settings &lt;file&gt;</c> or <c>--settings=&lt;file&gt;</c>, else appsettings.json next to the exe.</summary>
    internal static string ResolvePath(string[] args, string? baseDirectory = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        string? path = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--settings")
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                    throw new ArgumentException("--settings needs a file name, e.g. --settings agent2.json");
                path = args[++i];
            }
            else if (args[i].StartsWith("--settings=", StringComparison.Ordinal))
            {
                path = args[i]["--settings=".Length..];
                if (string.IsNullOrWhiteSpace(path))
                    throw new ArgumentException("--settings needs a file name, e.g. --settings=agent2.json");
            }
        }
        return ResolveRelative(path ?? DefaultFileName, baseDirectory);
    }

    /// <summary>A path from the settings: relative paths are inside the exe's folder.</summary>
    internal static string ResolveRelative(string path, string? baseDirectory = null) =>
        Path.GetFullPath(path, baseDirectory ?? AppContext.BaseDirectory);

    /// <summary>Write the default settings file if it does not exist. Returns true when it was created.</summary>
    internal static bool EnsureExists(string path)
    {
        if (File.Exists(path))
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, DefaultContent.ReplaceLineEndings());
        return true;
    }
}
