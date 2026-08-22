using HistoryVulcan.Core.Storage;

namespace HistoryVulcan.Services;

/// <summary>
/// 布局文件存取实现:当前布局为 layout/current.layout.xml,
/// 命名方案为 layout/&lt;名称&gt;.layout.xml(W-07 / W-08)。
/// </summary>
public sealed class FileLayoutStore : ILayoutStore
{
    private const string Extension = ".layout.xml";
    private const string CurrentName = "current";

    private readonly string _dir;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public FileLayoutStore(AppPaths paths) => _dir = paths.LayoutDir;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public string? ReadCurrent() => ReadNamed(CurrentName);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public void WriteCurrent(string payload) => WriteNamed(CurrentName, payload);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public void DeleteCurrent()
    {
        var path = PathOf(CurrentName);
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public string? ReadNamed(string name)
    {
        var path = PathOf(name);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public void WriteNamed(string name, string payload)
        => File.WriteAllText(PathOf(name), payload);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public IReadOnlyList<string> ListNamed()
    {
        return Directory.EnumerateFiles(_dir, "*" + Extension)
            .Select(f => Path.GetFileName(f)[..^Extension.Length])
            .Where(n => !string.Equals(n, CurrentName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string PathOf(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"非法布局名: {name}", nameof(name));
        }

        return Path.Combine(_dir, name + Extension);
    }
}
