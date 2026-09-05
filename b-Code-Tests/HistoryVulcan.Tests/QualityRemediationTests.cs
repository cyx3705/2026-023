using HistoryVulcan.Core.Commands;
using HistoryVulcan.Services;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class QualityRemediationTests
{

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


}
