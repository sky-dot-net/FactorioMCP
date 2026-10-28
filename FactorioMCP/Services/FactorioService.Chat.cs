using FactorioMCP.Rcon;
using System.Globalization;
using System.Text;

namespace FactorioMCP.Services;

internal sealed partial class FactorioService
{
    // ── Chat ─────────────────────────────────────────────────────────

    /// <summary>
    /// Register an <c>on_console_chat</c> event handler that stores incoming chat messages
    /// in <c>storage.chat_log</c>. Idempotent — safe to call multiple times; the handler
    /// is replaced and existing messages are preserved.
    /// </summary>
    public Task<string> InitializeChatListenerAsync(CancellationToken cancellationToken = default)
    {
        return rcon.ExecuteLuaAsync("""
            storage.chat_log = storage.chat_log or {}
            script.on_event(defines.events.on_console_chat, function(e)
                local player_name = "server"
                if e.player_index then
                    local p = game.get_player(e.player_index)
                    if p then player_name = p.name end
                end
                table.insert(storage.chat_log, {
                    tick = e.tick,
                    player_name = player_name,
                    message = e.message
                })
            end)
            {{AgentsCommandLua}}
            rcon.print('{"status":"initialized","existing_messages":'..#storage.chat_log..'}')
            """.Replace("{{AgentsCommandLua}}", AgentsCommandLua), cancellationToken);
    }

    /// <summary>
    /// Registers the in-game console command <c>/agents</c>. Scripted agent characters are not players, so
    /// <c>/players</c> cannot list them; <c>/agents</c> lists their chat names and positions instead.
    /// Command handlers are not saved with the map, so this runs again on every MCP start.
    /// </summary>
    internal const string AgentsCommandLua = """
        if commands.commands["agents"] then commands.remove_command("agents") end
        commands.add_command("agents", "Lists the AI agents (scripted characters) with their chat names and positions.", function(cmd)
            local lines = {}
            for key, c in pairs(storage.agent_by_name or {}) do
                if c.valid then
                    local chosen = storage.agent_display_names and storage.agent_display_names[key]
                    local label = key == "engineer" and "Factorio MCP Engineer" or ("[AI] " .. (chosen or key))
                    lines[#lines + 1] = string.format("  %s at %d, %d", label, math.floor(c.position.x), math.floor(c.position.y))
                end
            end
            local text = "AI agents (" .. #lines .. "):" .. (#lines > 0 and ("\n" .. table.concat(lines, "\n")) or " none")
            local p = cmd.player_index and game.get_player(cmd.player_index)
            if p then p.print(text) else rcon.print(text) end
        end)
        """;

    /// <summary>
    /// Read chat messages from the stored log. Optionally filters to messages
    /// after a given game tick so the caller can poll for new messages only.
    /// </summary>
    public Task<string> GetChatMessagesAsync(long sinceTick = 0, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sinceTick);

        var lua = string.Create(CultureInfo.InvariantCulture, $$"""
            local log = storage.chat_log or {}
            local since_tick = {{sinceTick}}
            local json_escape = function(s)
                return s:gsub('\\', '\\\\'):gsub('"', '\\"'):gsub('\n', '\\n'):gsub('\r', '\\r')
            end
            local parts = {}
            local latest_tick = since_tick
            for _, msg in pairs(log) do
                if msg.tick > since_tick then
                    parts[#parts+1] = '{"tick":'..msg.tick..',"player":"'..json_escape(msg.player_name)..'","message":"'..json_escape(msg.message)..'"}'
                    if msg.tick > latest_tick then latest_tick = msg.tick end
                end
            end
            rcon.print('{"messages":['..table.concat(parts, ",")..'],"count":'..#parts..',"latest_tick":'..latest_tick..'}')
            """);

        return rcon.ExecuteLuaAsync(lua, cancellationToken);
    }

    /// <summary>
    /// Send a chat message visible to all connected players via <c>game.print()</c>.
    /// Agent mode: the sender is the agent's chat name (<c>[AI] name</c>; the engineer is
    /// "Factorio MCP Engineer") in the character's colour. Player mode: the message is tagged "[AI]".
    /// </summary>
    public Task<string> SendChatMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var escaped = EscapeLuaString(message);
        string lua;
        if (rcon.AgentName is null)
        {
            lua = $$"""
                game.print("[AI] {{escaped}}")
                rcon.print('{"status":"sent","sender":"[AI]"}')
                """;
        }
        else
        {
            var hint = RconClient.IsEngineer(rcon.AgentName) ? "" : $$"""
                if not (storage.agent_display_names and storage.agent_display_names["{{rcon.AgentName}}"]) then
                    hint = ',"hint":"You have not chosen a name yet: call set_agent_name once to choose it."'
                end
                """;
            lua = $$"""
                local agent = __agent()
                local sender = __agent_label()
                local hint = ""
                {{hint}}
                local col = agent.color
                -- the character colour as is, unless it is too dark to read on the chat background
                local light = col
                if col and math.max(col.r, col.g, col.b) < 0.6 then
                    light = { r = 0.6 + 0.4 * col.r, g = 0.6 + 0.4 * col.g, b = 0.6 + 0.4 * col.b }
                end
                game.print(sender .. ": {{escaped}}", { color = light })
                rcon.print('{"status":"sent","sender":"' .. sender .. '"' .. hint .. '}')
                """;
        }

        return rcon.ExecuteLuaAsync(lua, cancellationToken);
    }

    /// <summary>Characters an agent may use in its chosen name: 1-30, starting with a letter or digit.</summary>
    internal static readonly System.Text.RegularExpressions.Regex AgentDisplayNamePattern =
        new(@"^[A-Za-z0-9][A-Za-z0-9 _.'\-]{0,29}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Choose the agent's chat name, once. It is shown as <c>[AI] name</c> in the chat and above the character.
    /// Returns an error result (not an exception) when the agent already has another name, the name is taken by
    /// another agent, the name is invalid, the agent is the engineer, or the server runs in player mode.
    /// </summary>
    public Task<string> SetAgentNameAsync(string name, CancellationToken cancellationToken = default)
    {
        name = name?.Trim() ?? "";
        if (rcon.AgentName is null)
            return Task.FromResult("""{"status":"error","error":"set_agent_name works only for scripted agents (FACTORIO_AGENT). In player mode (FACTORIO_PLAYER) the chat uses the player's own name."}""");
        if (RconClient.IsEngineer(rcon.AgentName))
            return Task.FromResult($$"""{"status":"error","error":"The engineer's chat name is fixed: {{RconClient.EngineerDisplayName}}."}""");
        if (!AgentDisplayNamePattern.IsMatch(name))
            return Task.FromResult($$"""{"status":"error","error":"Invalid name '{{EscapeJson(name)}}': use 1-30 characters (letters, digits, space, _ . ' -), starting with a letter or digit."}""");
        if (name.Contains(RconClient.EngineerDisplayName, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult($$"""{"status":"error","error":"The name '{{name}}' is reserved for the MCP engineer."}""");

        var lua = $$"""
            storage.agent_display_names = storage.agent_display_names or {}
            local key, wanted = "{{rcon.AgentName}}", "{{name}}"
            local current = storage.agent_display_names[key]
            if current and current ~= wanted then
                rcon.print('{"status":"error","error":"This agent already chose the name ' .. current .. '. The name is chosen once, on the first join, and cannot be changed."}')
                return
            end
            for other, taken in pairs(storage.agent_display_names) do
                if other ~= key and string.lower(taken) == string.lower(wanted) then
                    rcon.print('{"status":"error","error":"The name ' .. wanted .. ' is already used by another agent."}')
                    return
                end
            end
            storage.agent_display_names[key] = wanted
            __agent()
            rcon.print('{"status":"ok","chat_name":"' .. __agent_label() .. '"}')
            """;

        return rcon.ExecuteLuaAsync(lua, cancellationToken);
    }

    private static string EscapeJson(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// Escape a string for safe embedding in a Lua double-quoted string literal.
    /// </summary>
    private static string EscapeLuaString(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\0': break; // strip null bytes
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
