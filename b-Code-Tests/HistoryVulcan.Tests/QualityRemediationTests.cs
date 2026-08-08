using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Mcp;
using HistoryVulcan.Services;
using HistoryVulcan.Shell;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class QualityRemediationTests
{
    [Fact]
    public void ShellConfigDefaultsToMinimalOptionalCapabilities()
    {
        var config = new ShellConfig
        {
            AppName = "Minimal",
            AppVersion = "3.0.1",
        };

        Assert.False(config.EnableModules);
        Assert.False(config.EnableUiModules);
        Assert.False(config.EnableMcp);
        Assert.False(config.EnableRemoteManagementViews);
        Assert.Empty(config.Panels);
    }

    [Fact]
    public void CorruptedSettingsArePreservedAndSubsequentWritesAreAtomic()
    {
        var appName = "HistoryVulcan.Tests." + Guid.NewGuid().ToString("N");
        var paths = new AppPaths(appName, createBusinessDirectories: false);
        var settingsPath = Path.Combine(paths.Root, "settings.json");
        try
        {
            File.WriteAllText(settingsPath, "{broken-json");

            var settings = new SettingsService(paths);
            settings.Set("sample", "value");
            var reloaded = new SettingsService(paths);

            Assert.Equal("value", reloaded.Get("sample"));
            Assert.Single(Directory.GetFiles(paths.Root, "settings.json.corrupt-*"));
            Assert.Empty(Directory.GetFiles(paths.Root, "settings.json.tmp.*"));
        }
        finally
        {
            if (Directory.Exists(paths.Root))
                Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public void FrontendProxyFactoriesUseTheSameGovernanceMetadata()
    {
        var source = new CommandDescriptor
        {
            Name = "ui.dangerous",
            Summary = "dangerous",
            Dangerous = true,
            RequiresUiThread = true,
            AllowUnspecifiedParameters = true,
            AllowMcpExecution = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        };

        var frameworkProxy = FrontendCommandCatalog.CreateProxy(source);
        var capabilityProxy = FrontendCommandCapability.From(
            source, FrontendCommandCatalog.Source).CreateProxy();

        Assert.Equal(capabilityProxy.IsDangerous, frameworkProxy.IsDangerous);
        Assert.Equal(capabilityProxy.RequiresUiThread, frameworkProxy.RequiresUiThread);
        Assert.Equal(capabilityProxy.AllowUnspecifiedParameters, frameworkProxy.AllowUnspecifiedParameters);
        Assert.Equal(capabilityProxy.AllowMcpExecution, frameworkProxy.AllowMcpExecution);
        Assert.NotNull(frameworkProxy.ConfirmPrompt);
    }

    [Fact]
    public void ReadonlyFallbackRegistrationIsThreadSafe()
    {
        var prefix = "parallel." + Guid.NewGuid().ToString("N") + ".";
        var names = Enumerable.Range(0, 256).Select(index => prefix + index).ToArray();

        Parallel.ForEach(names, name => McpExposurePolicy.RegisterReadonly(name));

        Assert.All(names, name => Assert.True(McpExposurePolicy.IsReadonlyAllowed(name)));
        Assert.All(names, name => Assert.Contains(name, McpExposurePolicy.ReadonlyCommandNames));
    }
}
