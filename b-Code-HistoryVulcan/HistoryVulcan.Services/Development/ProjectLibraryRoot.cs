using System.IO;
using HistoryVulcan.Core.Storage;

namespace HistoryVulcan.Services.Development;

/// <summary>
/// 项目库根：一项目一仓的 HistoryClio。旧 Vesta 配置改写到 Clio。
/// </summary>
internal static class ProjectLibraryRoot
{
    /// <summary>项目库的缺省根：一项目一仓的 HistoryClio。</summary>
    internal const string Default = @"C:\OneHistory\HistoryClio";
    /// <summary>旧库根 HistoryVesta；读到它时改写到 HistoryClio。</summary>
    internal const string LegacyVesta = @"C:\OneHistory\HistoryVesta";
    /// <summary>项目库根的设置键。</summary>
    public const string KeyLibraryRoot = "proj.libraryroot";
    /// <summary>旧的工作区根设置键，仅作兼容读取。</summary>
    public const string KeyWorktreeRoot = "proj.worktreeroot";

    /// <summary>按设置解析项目库根；未配置时取缺省值。</summary>
    public static string Resolve(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var current = settings.Get(KeyLibraryRoot);
        if (!string.IsNullOrWhiteSpace(current))
            return Coerce(current);
        var legacy = settings.Get(KeyWorktreeRoot);
        if (!string.IsNullOrWhiteSpace(legacy))
            return Coerce(legacy);
        return Default;
    }

    /// <summary>把旧库根改写到现行库根，其余原样返回。</summary>
    public static string Coerce(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Equals(LegacyVesta, StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(Path.Combine(full, "HistoryVesta.git")))
        {
            if (Directory.Exists(Default))
                return Default;
        }

        return full;
    }

    /// <summary>判断给定目录是否是一个 git 项目。</summary>
    public static bool IsGitProject(string projectPath)
    {
        var git = Path.Combine(projectPath, ".git");
        return Directory.Exists(git) || File.Exists(git);
    }
}
