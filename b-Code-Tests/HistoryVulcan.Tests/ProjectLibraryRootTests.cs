using HistoryVulcan.Services.Development;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// 项目库根按配置取值，只规整路径、不改写（5.7.1，REQ-HOST-077）。
/// 旧库 HistoryVesta 已不存在，此前「读到 Vesta 就改写到 Clio」的特判随之删除。
/// </summary>
public sealed class ProjectLibraryRootTests
{
    [Fact]
    public void ConfiguredLibraryRootIsNormalizedButNeverRewritten()
    {
        var settings = new MemorySettings();
        settings.Set(ProjectLibraryRoot.KeyLibraryRoot, @"C:\OneHistory\HistoryVesta\");

        Assert.Equal(@"C:\OneHistory\HistoryVesta", ProjectLibraryRoot.Resolve(settings));
    }

    [Fact]
    public void OldWorktreeKeyIsIgnoredAndDefaultIsClio()
    {
        var oldKeyOnly = new MemorySettings();
        oldKeyOnly.Set("proj.worktreeroot", @"D:\Library");
        Assert.Equal(ProjectLibraryRoot.Default, ProjectLibraryRoot.Resolve(oldKeyOnly));
    }
}
