using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FactorioMCP.Rcon;

/// <summary>
/// Source RCON protocol client for communicating with a Factorio server over TCP.
/// Supports authentication, command execution with Lua via /c prefix,
/// and automatic reconnection with exponential backoff on connection loss.
/// </summary>
internal class RconClient : IAsyncDisposable, IDisposable
{
    private readonly ILogger<RconClient> _logger;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private int _nextRequestId;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Stored connection parameters for reconnection
    private string? _host;
    private int _port;
    private string? _password;

    private const int MaxReconnectAttempts = 3;
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Number of consecutive "nothing" responses before forcing a reconnect.
    /// Factorio RCON returns "nothing" when the connection is in a stale state
    /// (e.g. after game pause/unpause or packet ID desync that doesn't throw).
    /// </summary>
    private const int MaxConsecutiveNothingResponses = 3;
    private int _consecutiveNothingCount;

    /// <summary>
    /// Lua expression that every service uses to address the controlled player.
    /// </summary>
    internal const string DefaultPlayerExpression = "game.connected_players[1]";

    /// <summary>
    /// Agent name used when neither <c>FACTORIO_PLAYER</c> nor <c>FACTORIO_AGENT</c> is set.
    /// </summary>
    internal const string DefaultAgentName = "agent";

    /// <summary>
    /// Lua expression that replaces <see cref="DefaultPlayerExpression"/> in every command.
    /// Agent mode (default): a scripted character named by <c>FACTORIO_AGENT</c>, needing no client.
    /// Player mode: the connected player named by <c>FACTORIO_PLAYER</c>.
    /// </summary>
    public string PlayerExpression { get; }

    /// <summary>
    /// Lua code prepended to every command in agent mode; defines <c>__agent()</c>. Null in player mode.
    /// </summary>
    public string? LuaPrelude { get; }

    public RconClient(ILogger<RconClient>? logger = null, IConfiguration? configuration = null)
    {
        _logger = logger ?? NullLogger<RconClient>.Instance;

        // Without configuration (unit tests) commands are sent unchanged.
        if (configuration is null)
        {
            PlayerExpression = DefaultPlayerExpression;
            return;
        }

        var playerName = configuration["FACTORIO_PLAYER"];
        if (!string.IsNullOrWhiteSpace(playerName))
        {
            PlayerExpression = BuildPlayerExpression(playerName);
            return;
        }

        var agentName = configuration["FACTORIO_AGENT"];
        agentName = string.IsNullOrWhiteSpace(agentName) ? DefaultAgentName : agentName.Trim();
        ValidateName(agentName, "FACTORIO_AGENT");
        PlayerExpression = AgentExpression;
        LuaPrelude = BuildAgentPrelude(agentName, ResolveAgentColor(agentName, configuration["FACTORIO_AGENT_COLOR"]));
    }

    internal const string AgentExpression = "__agent()";

    /// <summary>
    /// Colour of the agent's character, as red/green/blue in the range 0..1.
    /// <c>FACTORIO_AGENT_COLOR</c> is <c>#RRGGBB</c> or <c>r,g,b</c> (0..255); <c>none</c> keeps the default colour.
    /// Without the setting the agent named <c>engineer</c> is red and every other agent keeps the default colour.
    /// </summary>
    internal static (double R, double G, double B)? ResolveAgentColor(string agentName, string? setting)
    {
        setting = setting?.Trim();
        if (string.IsNullOrEmpty(setting))
            return string.Equals(agentName, "engineer", StringComparison.OrdinalIgnoreCase) ? (0.9, 0.05, 0.05) : null;
        if (string.Equals(setting, "none", StringComparison.OrdinalIgnoreCase))
            return null;

        int[] channels;
        if (Regex.IsMatch(setting, "^#[0-9a-fA-F]{6}$"))
            channels = [Convert.ToInt32(setting[1..3], 16), Convert.ToInt32(setting[3..5], 16), Convert.ToInt32(setting[5..7], 16)];
        else if (Regex.IsMatch(setting, @"^\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}$"))
            channels = setting.Split(',').Select(part => int.Parse(part.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        else
            throw new ArgumentException($"Invalid FACTORIO_AGENT_COLOR '{setting}': use #RRGGBB, 'r,g,b' (0-255) or 'none'.", nameof(setting));

        if (channels.Any(channel => channel > 255))
            throw new ArgumentException($"Invalid FACTORIO_AGENT_COLOR '{setting}': each channel must be 0-255.", nameof(setting));
        return (channels[0] / 255.0, channels[1] / 255.0, channels[2] / 255.0);
    }

    private static void ValidateName(string name, string setting)
    {
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9_.\-]+$"))
            throw new ArgumentException($"Invalid {setting} name: '{name}'", nameof(name));
    }

    /// <summary>
    /// Build the Lua expression for a player name, or the first connected player when no name is given.
    /// </summary>
    internal static string BuildPlayerExpression(string? playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            return DefaultPlayerExpression;

        ValidateName(playerName, "FACTORIO_PLAYER");
        return $"game.get_player(\"{playerName}\")";
    }

    /// <summary>
    /// Lua prelude for agent mode. <c>__agent()</c> returns a proxy for the agent's character: it creates the
    /// character near spawn on first use, forwards every LuaControl member to it, and supplies the few
    /// LuaPlayer-only members agent Lua may use (<c>character</c>, <c>index</c>, <c>connected</c>, <c>name</c>,
    /// <c>clear_cursor</c>). The index is negative so it never collides with real player indices; the on_tick
    /// handler resolves negative indices through <c>storage.agent_chars</c>.
    /// </summary>
    internal static string BuildAgentPrelude(string agentName, (double R, double G, double B)? color = null) => $$"""
        local function __agent()
            storage.agent_by_name = storage.agent_by_name or {}
            storage.agent_chars = storage.agent_chars or {}
            local c = storage.agent_by_name["{{agentName}}"]
            if not (c and c.valid) then
                local surface = game.surfaces["nauvis"]
                local force = game.forces["player"]
                local spawn = force.get_spawn_position(surface)
                local pos = surface.find_non_colliding_position("character", spawn, 50, 0.5) or spawn
                c = surface.create_entity({ name = "character", position = pos, force = force })
                storage.agent_by_name["{{agentName}}"] = c
            end
            storage.agent_chars[-c.unit_number] = c
            {{AgentColorLua(color)}}
            return setmetatable({}, {
                __index = function(_, k)
                    if k == "character" then return c end
                    if k == "index" then return -c.unit_number end
                    if k == "connected" then return true end
                    if k == "name" then return "{{agentName}}" end
                    if k == "clear_cursor" then
                        return function() if c.cursor_stack then c.cursor_stack.clear() end end
                    end
                    return c[k]
                end,
                __newindex = function(_, k, v) c[k] = v end
            })
        end
        """;

    private static string AgentColorLua((double R, double G, double B)? color) => color is { } rgb
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"c.color = {{ r = {rgb.R:0.###}, g = {rgb.G:0.###}, b = {rgb.B:0.###}, a = 1 }}")
        : "-- default character colour";

    /// <summary>
    /// Whether the client is currently connected to the RCON server.
    /// </summary>
    public bool IsConnected => _tcp is { Connected: true } && _stream is not null;

    /// <summary>
    /// Connect to the RCON server and authenticate with the given password.
    /// Stores connection parameters for automatic reconnection on failure.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when authentication fails.</exception>
    public async Task ConnectAndAuthenticateAsync(
        string host,
        int port,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        _host = host;
        _port = port;
        _password = password;

        _logger.LogDebug("Connecting to RCON server at {Host}:{Port}", host, port);
        await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("RCON connected and authenticated at {Host}:{Port}", host, port);
    }

    /// <summary>
    /// Execute a raw command string on the server and return the response body.
    /// Automatically attempts reconnection on connection failure.
    /// Also detects stale connections that return "nothing" repeatedly and forces reconnect.
    /// </summary>
    public virtual async Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stream is null || _host is null)
            {
                throw new InvalidOperationException("Not connected. Call ConnectAndAuthenticateAsync first.");
            }

            try
            {
                var result = await ExecuteCoreAsync(command, cancellationToken).ConfigureAwait(false);

                // Detect stale connection: "nothing" responses indicate the RCON connection is in a bad state
                if (string.Equals(result, "nothing", StringComparison.OrdinalIgnoreCase))
                {
                    _consecutiveNothingCount++;
                    if (_consecutiveNothingCount >= MaxConsecutiveNothingResponses)
                    {
                        _logger.LogWarning(
                            "RCON returned 'nothing' {Count} times consecutively. Forcing reconnect to recover stale connection",
                            _consecutiveNothingCount);
                        _consecutiveNothingCount = 0;
                        await ReconnectWithBackoffAsync(cancellationToken).ConfigureAwait(false);
                        return await ExecuteCoreAsync(command, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    _consecutiveNothingCount = 0;
                }

                return result;
            }
            catch (Exception ex) when (IsConnectionException(ex))
            {
                // Connection lost — attempt reconnection then retry the command
                _logger.LogWarning(ex, "RCON connection lost during command execution. Attempting reconnection");
                _consecutiveNothingCount = 0;
                await ReconnectWithBackoffAsync(cancellationToken).ConfigureAwait(false);
                return await ExecuteCoreAsync(command, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Execute a Lua command via Factorio's /silent-command (suppresses chat output).
    /// </summary>
    public Task<string> ExecuteLuaAsync(string luaCode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(luaCode);
        if (PlayerExpression != DefaultPlayerExpression)
            luaCode = luaCode.Replace(DefaultPlayerExpression, PlayerExpression, StringComparison.Ordinal);
        if (LuaPrelude is not null)
            luaCode = LuaPrelude + "\n" + luaCode;
        return ExecuteAsync($"/silent-command {luaCode}", cancellationToken);
    }

    /// <summary>
    /// Force a full RCON reconnect. Useful for recovering from persistent stale connections
    /// where all commands return "nothing" but no exception is thrown.
    /// </summary>
    public async Task ForceReconnectAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_host is null || _password is null)
            {
                throw new InvalidOperationException("Not connected. Call ConnectAndAuthenticateAsync first.");
            }

            _logger.LogInformation("Forcing RCON reconnect to {Host}:{Port}", _host, _port);
            _consecutiveNothingCount = 0;
            await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("RCON forced reconnect successful");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Create a fresh TCP connection and authenticate with stored credentials.
    /// Per the Source RCON spec, the server may send two packets in response to
    /// auth: an empty <c>SERVERDATA_RESPONSE_VALUE</c> followed by the
    /// <c>SERVERDATA_AUTH_RESPONSE</c>. We drain packets until we find one whose
    /// ID is <c>-1</c> (auth failure) or matches our auth ID (success).
    /// </summary>
    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        CloseConnection();

        _tcp = new TcpClient();
        await _tcp.ConnectAsync(_host!, _port, cancellationToken).ConfigureAwait(false);
        _stream = _tcp.GetStream();

        var authId = Interlocked.Increment(ref _nextRequestId);
        await SendPacketAsync(authId, RconPacketType.Auth, _password!, cancellationToken).ConfigureAwait(false);

        // Read up to 2 packets to handle servers that send an extra ResponseValue before AuthResponse
        for (var i = 0; i < 2; i++)
        {
            var response = await ReadPacketAsync(cancellationToken).ConfigureAwait(false);

            if (response.Id == -1)
            {
                throw new InvalidOperationException("RCON authentication failed. Check the password.");
            }

            if (response.Id == authId)
            {
                return; // Auth succeeded
            }

            // Response ID doesn't match — likely the empty ResponseValue packet; read another
            _logger.LogDebug("Auth: skipping stale response with ID {Id}, expecting {Expected}", response.Id, authId);
        }

        // If we get here, neither packet matched — something is very wrong
        throw new InvalidOperationException("RCON authentication failed. No matching auth response received.");
    }

    /// <summary>
    /// Send a command and read the response on an already-established connection.
    /// Factorio returns the complete response in a single packet.
    /// Validates that the response packet ID matches the request ID. If there is
    /// a mismatch (stale packet from auth or a previous command), drains up to
    /// <see cref="MaxDrainAttempts"/> packets looking for the correct one.
    /// </summary>
    private async Task<string> ExecuteCoreAsync(string command, CancellationToken cancellationToken)
    {
        var commandId = Interlocked.Increment(ref _nextRequestId);

        await SendPacketAsync(commandId, RconPacketType.ExecCommand, command, cancellationToken).ConfigureAwait(false);

        // Read response, draining any stale packets with mismatched IDs
        for (var attempt = 0; attempt < MaxDrainAttempts; attempt++)
        {
            var packet = await ReadPacketAsync(cancellationToken).ConfigureAwait(false);
            if (packet.Id == commandId)
            {
                return packet.Body;
            }

            _logger.LogWarning(
                "RCON response ID mismatch: expected {Expected}, got {Actual} (body: \"{Body}\"). Draining stale packet",
                commandId, packet.Id, packet.Body.Length > 80 ? packet.Body[..80] + "..." : packet.Body);
        }

        _logger.LogError("RCON failed to find matching response after draining {Max} stale packets", MaxDrainAttempts);
        throw new IOException($"RCON response ID mismatch persisted after draining {MaxDrainAttempts} packets. Connection may be corrupt.");
    }

    private const int MaxDrainAttempts = 5;

    /// <summary>
    /// Attempt to reconnect with exponential backoff, up to <see cref="MaxReconnectAttempts"/> times.
    /// </summary>
    /// <exception cref="IOException">Thrown when all reconnection attempts fail.</exception>
    private async Task ReconnectWithBackoffAsync(CancellationToken cancellationToken)
    {
        var delay = InitialBackoff;

        for (var attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
        {
            _logger.LogInformation(
                "RCON reconnection attempt {Attempt}/{Max} to {Host}:{Port}",
                attempt, MaxReconnectAttempts, _host, _port);

            try
            {
                await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("RCON reconnected successfully on attempt {Attempt}", attempt);
                return;
            }
            catch (Exception ex) when (attempt < MaxReconnectAttempts && IsConnectionException(ex))
            {
                _logger.LogWarning(
                    ex,
                    "RCON reconnection attempt {Attempt}/{Max} failed. Retrying in {Delay}s",
                    attempt, MaxReconnectAttempts, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay *= 2; // Exponential backoff
            }
        }

        _logger.LogError(
            "RCON reconnection failed after {Max} attempts to {Host}:{Port}",
            MaxReconnectAttempts, _host, _port);

        throw new IOException(
            $"Failed to reconnect to RCON server at {_host}:{_port} after {MaxReconnectAttempts} attempts.");
    }

    /// <summary>
    /// Determines whether the exception indicates a connection-level failure
    /// that can potentially be recovered by reconnecting.
    /// </summary>
    private static bool IsConnectionException(Exception ex) =>
        ex is IOException or SocketException or ObjectDisposedException;

    /// <summary>
    /// Close and dispose the current TCP connection if one exists.
    /// </summary>
    private void CloseConnection()
    {
        if (_stream is not null || _tcp is not null)
        {
            _logger.LogDebug("Closing RCON connection");
        }

        _stream?.Dispose();
        _stream = null;
        _tcp?.Dispose();
        _tcp = null;
    }

    /// <summary>
    /// Build and send a single RCON packet over the wire.
    /// </summary>
    private async Task SendPacketAsync(
        int id,
        RconPacketType type,
        string body,
        CancellationToken cancellationToken)
    {
        var buffer = new RconPacket(id, type, body).ToBytes();

        await _stream!.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Read and decode a single RCON response packet from the wire.
    /// </summary>
    private async Task<RconPacket> ReadPacketAsync(CancellationToken cancellationToken)
    {
        var sizeBuffer = new byte[4];
        await ReadExactAsync(sizeBuffer, 4, cancellationToken).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32LittleEndian(sizeBuffer);

        var payload = new byte[size];
        await ReadExactAsync(payload, size, cancellationToken).ConfigureAwait(false);

        return RconPacket.FromPayload(payload);
    }

    /// <summary>
    /// Read exactly <paramref name="count"/> bytes from the network stream.
    /// </summary>
    /// <exception cref="IOException">Thrown when the connection is closed before all bytes are read.</exception>
    private async Task ReadExactAsync(byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await _stream!.ReadAsync(
                buffer.AsMemory(offset, count - offset),
                cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                throw new IOException("RCON connection closed unexpectedly.");
            }

            offset += read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        _tcp?.Dispose();
        _lock.Dispose();
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _lock.Dispose();
    }
}
