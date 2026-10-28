using FactorioMCP.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FactorioMCP.Tests;

/// <summary>
/// The settings file is the only configuration source: appsettings.json next to the exe or --settings &lt;file&gt;.
/// </summary>
public class AppSettingsTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "fmcp-base");

    [Fact]
    public void ResolvePath_DefaultsToAppSettingsNextToTheExe()
    {
        Assert.Equal(Path.Combine(Base, "appsettings.json"), AppSettings.ResolvePath([], Base));
    }

    [Theory]
    [InlineData("--settings", "agent2.json")]
    [InlineData("--settings=agent2.json", null)]
    public void ResolvePath_SettingsArgument_IsRelativeToTheExe(string first, string? second)
    {
        string[] args = second is null ? [first] : [first, second];

        Assert.Equal(Path.Combine(Base, "agent2.json"), AppSettings.ResolvePath(args, Base));
    }

    [Fact]
    public void ResolvePath_SettingsWithoutFile_Throws()
    {
        Assert.Throws<ArgumentException>(() => AppSettings.ResolvePath(["--settings"], Base));
    }

    [Fact]
    public void EnsureExists_WritesDefaultsThatLoad()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fmcp-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "appsettings.json");
        try
        {
            Assert.True(AppSettings.EnsureExists(path));
            Assert.False(AppSettings.EnsureExists(path));

            var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();

            Assert.Equal("127.0.0.1", configuration[AppSettings.RconHost]);
            Assert.Equal("27015", configuration[AppSettings.RconPort]);
            Assert.Equal("agent", configuration[AppSettings.AgentName]);
            Assert.Equal("buildings.json", configuration[AppSettings.BuildingsFile]);
            Assert.False(RestrictedArea.FromConfiguration(configuration).Enabled);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadConnectionSettings_EmptyPassword_NamesTheSetting()
    {
        var configuration = new ConfigurationBuilder().Build();

        var ex = Assert.Throws<InvalidOperationException>(() => RconConnectionService.ReadConnectionSettings(configuration));
        Assert.Contains(AppSettings.RconPassword, ex.Message);
    }

    [Fact]
    public void ReadConnectionSettings_ReadsTheRconSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AppSettings.RconHost] = "10.0.0.5",
                [AppSettings.RconPort] = "27016",
                [AppSettings.RconPassword] = "pw",
            })
            .Build();

        Assert.Equal(("10.0.0.5", 27016, "pw"), RconConnectionService.ReadConnectionSettings(configuration));
    }

    [Fact]
    public void ReadConnectionSettings_InvalidPort_NamesTheSetting()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AppSettings.RconPort] = "abc", [AppSettings.RconPassword] = "pw" })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => RconConnectionService.ReadConnectionSettings(configuration));
        Assert.Contains(AppSettings.RconPort, ex.Message);
    }
}
