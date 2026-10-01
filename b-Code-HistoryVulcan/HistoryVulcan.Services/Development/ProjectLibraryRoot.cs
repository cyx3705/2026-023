using System.IO;
using HistoryVulcan.Core.Storage;

namespace HistoryVulcan.Services.Development;

/// <summary>
/// 项目库根：一项目一仓的 HistoryClio。
/// </summary>
internal static class ProjectLibraryRoot
{
    /// <summary>项目库的缺省根：一项目一仓的 HistoryClio。</summary>
    internal const string Default = @"C:\OneHistory\HistoryClio";
    /// <summary>项目库根的设置键。</summary>
    public const string KeyLibraryRoot = "proj.libraryroot";

    /// <summary>按设置解析项目库根；未配置时取缺省值。</summary>
    public static string Resolve(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var current = settings.Get(KeyLibraryRoot);
        if (!string.IsNullOrWhiteSpace(current))
            return Normalize(current);
        return Default;
    }

    /// <summary>取完整路径并去掉末尾分隔符。</summary>
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>判断给定目录是否是一个 git 项目。</summary>
    public static bool IsGitProject(string projectPath)
    {
        var git = Path.Combine(projectPath, ".git");
        return Directory.Exists(git) || File.Exists(git);
    }
}
