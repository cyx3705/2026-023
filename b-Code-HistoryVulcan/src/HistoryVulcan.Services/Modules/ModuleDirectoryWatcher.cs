using System.IO;
using HistoryVulcan.Core.Logging;

namespace HistoryVulcan.Services.Modules;

/// <summary>
/// 模块目录的变更侦测与重载防抖(MD-01)。
///
/// 从 <c>ModuleHost</c> 抽出:它与模块装载没有共享状态,只需要「监听哪个目录」
/// 和「该重载时叫谁」。留在宿主里时,这段逻辑和快照构建、注册表换血、UI 生命周期挤在
/// 同一个类型内,读代码的人无法一眼分清哪些字段属于哪件事。
///
/// 防抖是必须的:拷贝一个大 DLL 会触发多次 Changed 事件,不防抖会把一次部署放大成
/// 若干次整体热重载。Z 发现模式下每个含 <c>module.manifest.json</c> 的 <c>z-*</c>
/// 目录各挂一个监听;路径集合未变时不得拆掉监听,否则热重载回调会把自己的 Timer 释放掉。
/// </summary>
internal sealed class ModuleDirectoryWatcher : IDisposable
{
    /// <summary>静默期:最后一次文件事件之后再等这么久才真正重载。</summary>
    private const int DebounceMilliseconds = 800;

    private readonly IShellLog _log;
    private readonly Action _reload;
    private readonly object _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private string[] _watched = [];
    private Timer? _debounce;
    private bool _stopped;

    internal ModuleDirectoryWatcher(IShellLog log, Action reload)
    {
        _log = log;
        _reload = reload;
    }

    /// <summary>开始监听一个目录;重复调用会在路径变化时替换监听集合。</summary>
    internal bool Watch(string directory) => Watch([directory]);

    /// <summary>开始监听一组目录;路径集合未变则保持现有监听,避免热重载把自己拆掉。</summary>
    /// <returns>监听集合是否发生了替换。</returns>
    internal bool Watch(IReadOnlyList<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        var next = directories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (_gate)
        {
            if (_watched.Length == next.Length
                && _watched.SequenceEqual(next, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            _stopped = false;
            DisposeWatchers();
            _watched = next;
            foreach (var directory in next)
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                                   | NotifyFilters.Size | NotifyFilters.CreationTime,
                    IncludeSubdirectories = true,
                };
                watcher.Created += (_, e) => OnFileEvent(e.Name);
                watcher.Changed += (_, e) => OnFileEvent(e.Name);
                watcher.Deleted += (_, e) => OnFileEvent(e.Name);
                watcher.Renamed += (_, e) => OnFileEvent(e.Name);
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
        }

        return true;
    }

    /// <summary>停止监听;已排期的重载不再触发。</summary>
    internal void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            DisposeWatchers();
            _watched = [];
            _debounce?.Dispose();
            _debounce = null;
        }
    }

    /// <summary>是否值得为这个文件触发重载(模块本体、XML 注释、清单与模块旁面板)。</summary>
    internal static bool IsRelevant(string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
            return false;

        var name = Path.GetFileName(file);
        if (name.Equals("module.manifest.json", StringComparison.OrdinalIgnoreCase))
            return true;

        var extension = Path.GetExtension(name).ToLowerInvariant();
        return extension is ".dll" or ".xml"
            || name.EndsWith(".panel.json", StringComparison.OrdinalIgnoreCase);
    }

    private void DisposeWatchers()
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
    }

    private void OnFileEvent(string? file)
    {
        if (!IsRelevant(file))
            return;

        _log.Info("module", $"检测到模块变化: {file ?? "?"},准备热重载...");
        lock (_gate)
        {
            if (_watchers.Count == 0)
                return;

            _debounce ??= new Timer(_ =>
            {
                lock (_gate)
                {
                    if (_stopped || _watchers.Count == 0)
                        return;
                }

                try
                {
                    _reload();
                }
                catch (Exception ex)
                {
                    _log.Error("module", $"热重载失败: {ex.Message}");
                }
            }, null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(DebounceMilliseconds, Timeout.Infinite);
        }
    }

    public void Dispose() => Stop();
}
