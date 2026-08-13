using System.Windows.Threading;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;

namespace HistoryVulcan.Shell.Modules;

/// <summary>把模块界面注册请求编组到 Shell UI 线程。</summary>
public sealed class ShellUiRegistrar : IShellUiRegistrar
{
    private readonly IDockingService _docking;
    private readonly Dispatcher _dispatcher;
    private readonly IShellLog _log;
    private readonly Dictionary<string, HashSet<string>> _byOwner = new(StringComparer.OrdinalIgnoreCase);

    public ShellUiRegistrar(IDockingService docking, Dispatcher dispatcher, IShellLog log)
    {
        _docking = docking;
        _dispatcher = dispatcher;
        _log = log;
    }

    public bool IsUiThread => _dispatcher.CheckAccess();

    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsUiThread)
            action();
        else
            _dispatcher.Invoke(action);
    }

    public IDisposable RegisterToolWindow(ToolWindowDescriptor descriptor, string owner)
    {
        Invoke(() =>
        {
            // 模块注册的窗口一律是工具窗口。声明 Center 的仍然落在中央区，只是以中央页
            // 形态呈现而不是文档页——文档页的拖动行为明显弱于工具窗口并会引出一连串停靠
            // 缺陷。这里只做一次提示，不改写描述符：位置声明是模块的意图，应当保留。
            if (descriptor.DefaultSide == DockSide.Center)
            {
                _log.Log(
                    ShellLogLevel.Debug,
                    "module",
                    $"模块 {owner} 的窗口 {descriptor.Id} 声明中央区：按工具窗口注册并置于中央页。"
                    + "宿主不再提供文档页注册路径。");
            }

            _docking.RegisterWindow(descriptor, owner);
            Track(owner, descriptor.Id);
        });
        return new Registration(() => Invoke(() => UnregisterToolWindow(descriptor.Id)));
    }

    public void UnregisterToolWindow(string id)
        => Invoke(() =>
        {
            _docking.UnregisterWindow(id);
            Untrack(id);
        });

    public void UnregisterOwner(string owner)
        => Invoke(() =>
        {
            try
            {
                _docking.UnregisterOwner(owner);
            }
            catch (Exception ex)
            {
                _log.Warn("module", $"回收模块界面失败 ({owner}): {ex.Message}");
            }
            finally
            {
                _byOwner.Remove(owner);
            }
        });

    private void Track(string owner, string id)
    {
        if (!_byOwner.TryGetValue(owner, out var ids))
            _byOwner[owner] = ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ids.Add(id);
    }

    private void Untrack(string id)
    {
        foreach (var owner in _byOwner.Keys.ToArray())
        {
            _byOwner[owner].Remove(id);
            if (_byOwner[owner].Count == 0)
                _byOwner.Remove(owner);
        }
    }

    private sealed class Registration(Action? dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
