namespace HistoryVulcan.Tests;

/// <summary>
/// 仓库根定位。
/// </summary>
/// <remarks>
/// 多个契约测试按**扫源码**而不是构造组合根来验声明：<c>ServiceComposer.Build</c>
/// 会创建真实的数据目录、日志与设置，测试不该去动使用者的 %AppData%；
/// 而真正要守的事情发生在写下声明的那一刻，扫源码正好守在那里。
/// </remarks>
internal static class RepositoryPaths
{
    /// <summary>从测试输出目录向上找到带 <c>project.manifest.json</c> 的仓库根。</summary>
    public static string Root()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "project.manifest.json")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("未找到 HistoryVulcan 仓库根目录");
    }
}
