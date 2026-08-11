using System.IO;
using HistoryVulcan.Core.Logging;

namespace HistoryVulcan.Services.Modules;

/// <summary>
/// 模块目录的变更侦测与重载防抖(MD-01)。
///
/// 从 <see cref="ModuleHost"/> 抽出:它与模块装载没有共享状态,只需要「监听哪个目录」
/// 和「该重载时叫谁」。留在宿主里时,这段逻辑和快照构建、注册表换血、UI 生命周期挤在
/// 同一个类型内,读代码的人无法一眼分清哪些字段属于哪件事。
///
/// 防抖是必须的:拷贝一个大 DLL 会触发多次 Changed 事件,不防抖会把一次部署放大成
/// 若干次整体热重载。
/// </summary>
internal sealed class ModuleDirectoryWatcher : IDisposable
{
    /// <summary>静默期:最后一次文件事件之后再等这么久才真正重载。</summary>
    private const int DebounceMilliseconds = 800;

    private readonly IShellLog _log;
    private readonly Action _reload;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    internal ModuleDirectoryWatcher(IShellLog log, Action reload)
    {
        _log = log;
        _reload = reload;
    }

    /// <summary>开始监听目录;重复调用会先停掉上一个监听。</summary>
    internal void Watch(string directory)
    {
        Stop();
        _watcher = new FileSystemWatcher(directory)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                           | NotifyFilters.Size | NotifyFilters.CreationTime,
            IncludeSubdirectories = true, // V2.2 MH-03:模块槽子目录同样触发热重载
        };
        _watcher.Created += (_, e) => OnFileEvent(e.Name);
        _watcher.Changed += (_, e) => OnFileEvent(e.Name);
        _watcher.Deleted += (_, e) => OnFileEvent(e.Name);
        _watcher.Renamed += (_, e) => OnFileEvent(e.Name);
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>停止监听;已排期的重载不再触发。</summary>
    internal void Stop()
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
            _debounce?.Dispose();
            _debounce = null;
        }
    }

    /// <summary>是否值得为这个文件触发重载(MD-08:模块本体、XML 注释与模块旁面板)。</summary>
    internal static bool IsRelevant(string? file)
    {
        var extension = Path.GetExtension(file ?? "").ToLowerInvariant();
        return extension is ".dll" or ".xml"
            || (file?.EndsWith(".panel.json", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void OnFileEvent(string? file)
    {
        if (!IsRelevant(file))
            return;

        _log.Info("module", $"检测到模块变化: {file ?? "?"},准备热重载...");
        lock (_gate)
        {
            if (_watcher == null)
                return; // 已停止监听,不再排期。

            _debounce ??= new Timer(_ =>
            {
                try
                {
                    _reload();
                }
                catch (Exception ex)
                {
                    // MD-06 同义:热重载失败只告警,不得击穿宿主。
                    _log.Error("module", $"热重载失败: {ex.Message}");
                }
            }, null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(DebounceMilliseconds, Timeout.Infinite);
        }
    }

    public void Dispose() => Stop();
}
