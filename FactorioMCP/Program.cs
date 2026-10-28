using FactorioMCP.Rcon;
using FactorioMCP.Services;
using FactorioMCP;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Text.Json;

// The settings file is the only configuration source; see AppSettings.
var settingsPath = AppSettings.ResolvePath(args);
if (AppSettings.EnsureExists(settingsPath))
    await Console.Error.WriteLineAsync($"[Settings] Created {settingsPath} with the defaults; set Rcon:Password and Agent:Name there.");

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
builder.Configuration.Sources.Clear();
builder.Configuration.AddJsonFile(settingsPath, optional: false, reloadOnChange: true);

builder.Services
    .AddSingleton<RconClient>()
    .AddSingleton<FactorioService>()
    .AddSingleton<PathfindingService>()
    .AddSingleton<EnergyService>()
    .AddSingleton<BlueprintService>()
    .AddSingleton<BuildingMemoryService>()
    .AddSingleton<BeltPlannerService>()
    .AddSingleton<BeltRouterService>()
    .AddSingleton<MiningService>()
    .AddSingleton<VisionService>()
    .AddSingleton<CombatService>()
    .AddSingleton<TrainService>()
    .AddSingleton<LogisticsService>()
    .AddSingleton<FlowService>()
    .AddSingleton<RecipeRateCalculatorService>()
    .AddSingleton<LayoutSynthesisService>()
    .AddSingleton<PowerPoleLayoutService>()
    .AddSingleton<ProductionPlannerService>()
    .AddSingleton<BlueprintCodecService>()
    .AddSingleton<BlueprintAnalysisService>()
    .AddSingleton<GameCommandQueue>()
    .AddSingleton<RestrictedAreaGuard>()
    .AddHostedService<RconConnectionService>()
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "FactorioMCP",
            Version = "1.0.0"
        };
        // Agents learn about the restricted area at session start; a broken setting is reported by every tool call.
        try
        {
            options.ServerInstructions = RestrictedArea.FromConfiguration(builder.Configuration).Instructions;
        }
        catch (InvalidOperationException)
        {
        }
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly()
    .WithRequestFilters(filters =>
    {
        McpRequestFilter<CallToolRequestParams, CallToolResult> filter = next =>
            async (context, cancellationToken) =>
            {
                var toolName = context.Params.Name;
                var args = context.Params.Arguments is { } a
                    ? JsonSerializer.Serialize(a)
                    : "{}";

                await Console.Error.WriteLineAsync($"[ToolCall] {toolName} {args}");

                var refusal = context.Services?.GetService<RestrictedAreaGuard>()?.CheckToolCall(toolName, context.Params.Arguments);
                if (refusal is not null)
                {
                    await Console.Error.WriteLineAsync($"[ToolCall] {toolName} refused: {refusal}");
                    return new CallToolResult { Content = [new TextContentBlock { Text = refusal }], IsError = true };
                }

                var result = await next(context, cancellationToken);

                await Console.Error.WriteLineAsync($"[ToolCall] {toolName} completed");

                return result;
            };

        filters.AddCallToolFilter(filter);
    });

await builder.Build().RunAsync();
