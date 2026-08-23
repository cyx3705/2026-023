using HistoryVulcan.Services.Development;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class DevPipelineTests
{
    [Theory]
    [InlineData("HistoryVulcan")]
    [InlineData("historyvulcan")]
    [InlineData("2026-023-HistoryVulcan")]
    public void HostNamesAreRejectedFromTheModulePipeline(string name)
        => Assert.True(DevPipelineCommands.IsHostTarget(name));

    [Theory]
    [InlineData("HistoryJanus")]
    [InlineData("2026-020-HistoryJanus")]
    [InlineData("HistoryDiana")]
    public void ModuleNamesAreNotHostTargets(string name)
        => Assert.False(DevPipelineCommands.IsHostTarget(name));
}
