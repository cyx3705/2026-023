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

    // ReadonlyFallbackRegistrationIsThreadSafe 随 McpExposurePolicy.RegisterReadonly
    // 一并删除（4.8.0）。那个「按名字补登记只读」的兜底通道自 V2.4.4 起恒为空——
    // 只读性的单一真值早已是 CommandDescriptor.Readonly——全仓唯一的调用方
    // 就是这个测试。一个只被自己的测试调用的兜底通道不是兜底，是还没被删掉。
}
