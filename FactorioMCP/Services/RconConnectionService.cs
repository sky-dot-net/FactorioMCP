using FactorioMCP.Rcon;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FactorioMCP.Services;

/// <summary>
/// Hosted service that connects and authenticates the RCON client on application startup.
/// Reads connection settings from the settings file (see <see cref="AppSettings"/>).
/// Retries connection with exponential backoff if the server is not yet available.
/// After connecting, initializes game-level listeners (e.g. chat message capture).
/// </summary>
internal sealed class RconConnectionService(
    RconClient rcon,
    FactorioService factorioService,
    IConfiguration configuration,
    ILogger<RconConnectionService> logger) : IHostedService
{
    private const int MaxStartupAttempts = 5;
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(2);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var (host, port, password) = ReadConnectionSettings(configuration);

        var delay = InitialBackoff;
        for (var attempt = 1; attempt <= MaxStartupAttempts; attempt++)
        {
            try
            {
                await rcon.ConnectAndAuthenticateAsync(host, port, password, cancellationToken);
                logger.LogInformation("RCON connected to {Host}:{Port}", host, port);

                await InitializeGameListenersAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < MaxStartupAttempts && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    ex,
                    "RCON connection attempt {Attempt}/{Max} failed. Retrying in {Delay}s...",
                    attempt, MaxStartupAttempts, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
                delay *= 2;
            }
        }

        // Final attempt — let the exception propagate
        await rcon.ConnectAndAuthenticateAsync(host, port, password, cancellationToken);
        await InitializeGameListenersAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>RCON host, port and password from the settings file; the error names the setting that is wrong.</summary>
    internal static (string Host, int Port, string Password) ReadConnectionSettings(IConfiguration configuration)
    {
        var host = configuration[AppSettings.RconHost];
        host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();

        var portSetting = configuration[AppSettings.RconPort];
        var port = 27015;
        if (!string.IsNullOrWhiteSpace(portSetting) && (!int.TryParse(portSetting, out port) || port is < 1 or > 65535))
            throw new InvalidOperationException($"Invalid setting {AppSettings.RconPort} '{portSetting}' in the settings file: use a port number 1-65535.");

        var password = configuration[AppSettings.RconPassword];
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException($"Setting {AppSettings.RconPassword} is empty in the settings file: set the Factorio server's RCON password.");

        return (host, port, password);
    }

    private async Task InitializeGameListenersAsync(CancellationToken cancellationToken)
    {
        try
        {
            await factorioService.InitializeChatListenerAsync(cancellationToken);
            logger.LogInformation("Chat message listener initialized");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to initialize chat listener — chat tools may not work until manually re-initialized");
        }
    }
}
