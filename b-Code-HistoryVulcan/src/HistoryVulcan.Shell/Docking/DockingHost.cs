using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;

namespace HistoryVulcan.Shell.Docking;

/// <summary>
/// AvalonDock 二次封装(§14.2)。Shell 对外只暴露 IDockingService,
/// 派生应用与四类标准窗口不接触任何 AvalonDock 类型。
/// 职责:窗口注册、默认布局构建、布局持久化(含损坏回退 N-06)、
/// 布局手势 → 等价指令(W-10,含防再入抑制)。
/// </summary>
public sealed partial class DockingHost : IDockingService
{
    private const double MaximumSideAllocation = 0.5;
    private const string LayoutSource = "layout";
    private const double RatioEpsilon = 0.02;
    private const string PlacementSettingsKey = "layout.placements";
    private const string HiddenCenterMetadataPrefix = "HistoryVulcan.HiddenCenter.v1:";

    private readonly DockingManager _manager;
    private readonly ILayoutStore _store;
    private readonly IShellLog _log;
    private readonly ISettingsService? _settings;
    private readonly List<ToolWindowDescriptor> _descriptors = new();
    private readonly Dictionary<string, ToolWindowDescriptor> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> _contents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _pendingTabTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LayoutDocument> _centerDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hiddenCenterIds = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, OrphanPlacement> _orphanPlacements = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _debounce;

    private Dictionary<string, WinState> _baseline = new(StringComparer.OrdinalIgnoreCase);
    private LayoutRoot? _attachedRoot;
    private int _suppress;
    private string? _maximizedId;
    private string? _layoutBeforeMaximize;
    private bool _centerRepairPending;
    private bool _presentationRefreshPending;

    // W-05 比例语义:AvalonDock 对与文档区同面板的侧窗格采用像素语义
    // (LayoutPanelControl.OnFixChildrenDockLengths 会把星值固化为像素),
    // “占主程序窗体百分比”由封装层维护:记录目标比例,在首次排布后与
    // 窗体缩放后重新按比例施加像素尺寸。
    private readonly Dictionary<string, double> _ratios = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _preserveDefaultRatioOnSeed = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _resizeDebounce;
    private bool _windowResizePending;

    public DockingHost(
        DockingManager manager,
        IEnumerable<ToolWindowDescriptor> windows,
        ILayoutStore store,
        IShellLog log,
        ISettingsService? settings = null)
    {
        _manager = manager;
        _store = store;
        _log = log;
        _settings = settings;

        foreach (var d in windows)
        {
            if (_byId.ContainsKey(d.Id))
                throw new InvalidOperationException($"工具窗口 Id 冲突: {d.Id}(禁止静默覆盖,§5.3)");
            _descriptors.Add(d);
            _byId.Add(d.Id, d);
            _owners[d.Id] = "framework";
            _ratios[d.Id] = NormalizeRatio(d.DefaultRatio, 0.25);
        }

        // W-10:拖拽类连续手势在动作结束时才生成指令 —— 用去抖合并布局事件
        _debounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            EmitLayoutDiffs();
        };

        _manager.Loaded += (_, _) => RebaseSoon();

        // 主窗体缩放 → 按记录的百分比重算各停靠区尺寸(W-05)
        _resizeDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _resizeDebounce.Tick += (_, _) =>
        {
            _resizeDebounce.Stop();
            using (Suppress())
            {
                ReapplyRatios();
            }
            _manager.Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                () =>
                {
                    _windowResizePending = false;
                    _baseline = ComputeAllStates();
                });
        };
        _manager.SizeChanged += (_, _) =>
        {
            _windowResizePending = true;
            _debounce.Stop();
            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        };
    }

    /// <summary>当前布局方案名(状态栏显示用)。</summary>
    public string CurrentLayoutName { get; private set; } = "默认";

    public string? MaximizedId => _maximizedId;

    /// <summary>true 表示下一次比例处理应从已恢复的布局反向采集比例,而非施加记录值。</summary>
    private bool _seedRatiosFromLayout;

    public event EventHandler<ShellCommandEventArgs>? CommandGenerated;

    public event EventHandler? WindowsChanged;

    /// <summary>已注册窗口(视图菜单构建用)。</summary>
    public IReadOnlyList<ToolWindowDescriptor> Descriptors => _descriptors;

    // ---------------------------------------------------------------- 启动/退出

    /// <summary>启动时调用:恢复上次布局,失败或不存在则构建默认布局(W-07 / N-06)。</summary>
    public void Initialize()
    {
        LoadOrphanPlacements();
        using (Suppress())
        {
            string? xml = null;
            try
            {
                xml = _store.ReadCurrent();
            }
            catch (Exception ex)
            {
                _log.Warn(LayoutSource, $"读取布局文件失败: {ex.Message}");
            }

            var restored = false;
            if (xml != null)
            {
                try
                {
                    ApplyLayoutXml(xml);
                    if (!LayoutHasMainDocumentPane())
                        throw new InvalidOperationException("布局中缺少中央主文档区");
                    EnsureRegisteredWindows();
                    ConsolidateSidePanes();
                    restored = true;
                    _seedRatiosFromLayout = true; // 以文件里的尺寸为准,反向采集比例
                    _log.Info(LayoutSource, "已恢复上次退出时的布局");
                }
                catch (Exception ex)
                {
                    // N-06:布局文件损坏 → 回退默认布局 + 告警,不阻断启动
                    _log.Warn(LayoutSource, $"布局文件损坏,已回退默认布局(原因: {ex.Message})");
                    try { _store.DeleteCurrent(); } catch { /* 清理失败可忽略 */ }
                }
            }

            if (!restored)
                BuildDefaultLayout();

            ApplyStoredCenterVisibility();
            EnsureCentralWorkspace();
            AttachLayout();
        }

        ScheduleReapplyRatios();
        RebaseSoon();
    }

    /// <summary>退出时调用:自动保存当前布局(W-07)。</summary>
    public void SaveCurrentLayout()
    {
        try
        {
            _store.WriteCurrent(_layoutBeforeMaximize ?? SerializeLayout());
            SavePlacements();
            _log.Info(LayoutSource, "退出前已自动保存布局");
        }
        catch (Exception ex)
        {
            _log.Error(LayoutSource, $"保存布局失败: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- IDockingService

    public IReadOnlyList<ToolWindowInfo> ListWindows()
        => _descriptors
            .Select(d =>
            {
                var s = ComputeState(d.Id);
                return new ToolWindowInfo(
                    d.Id, d.Title, s.Visible, s.Floating, s.Side,
                    s.Visible && !s.Floating && s.Ratio > 0 ? s.Ratio : null,
                    _owners.GetValueOrDefault(d.Id, "framework"));
            })
            .ToList();

    public void Show(string id)
    {
        RestoreLayoutFromMaximized();
        EnsureRegistered(id);
        using (Suppress())
        {
            var document = FindCenterDocument(id);
            if (document != null)
            {
                ShowCenterDocument(document);
            }
            else
            {
                var anchorable = FindRequiredAnchorable(id);
                if (anchorable.IsHidden)
                    anchorable.Show();
                anchorable.IsSelected = true;
                anchorable.IsActive = true;
            }
            EnsureCentralWorkspace();
            ReapplyRatios();
        }
    }

    public void Hide(string id)
    {
        RestoreLayoutFromMaximized();
        EnsureRegistered(id);
        using (Suppress())
        {
            var document = FindCenterDocument(id);
            if (document != null)
            {
                if (IsPrimaryCommandDocument(id))
                {
                    _log.Warn(LayoutSource, "命令集是主窗口，不能隐藏");
                }
                else
                {
                    DetachDocument(document);
                    _hiddenCenterIds.Add(id);
                    ScheduleCenterDocumentPresentation();
                }
            }
            else
            {
                var anchorable = FindRequiredAnchorable(id);
                if (!anchorable.IsHidden)
                    anchorable.Hide();
            }
            EnsureCentralWorkspace();
        }
    }

    public void Float(string id)
    {
        RestoreLayoutFromMaximized();
        EnsureRegistered(id);
        using (Suppress())
        {
            var document = FindCenterDocument(id);
            if (document != null)
            {
                if (IsPrimaryCommandDocument(id))
                {
                    _log.Warn(LayoutSource, "命令集是主窗口，不能浮动");
                }
                else
                {
                    ShowCenterDocument(document);
                    if (!IsFloating(document))
                        document.Float();
                }
            }
            else
            {
                var anchorable = FindRequiredAnchorable(id);
                if (anchorable.IsHidden)
                    anchorable.Show();
                if (!IsFloating(anchorable))
                    anchorable.Float();
            }
            EnsureCentralWorkspace();
        }
    }

    internal void ToggleAutoHide(string id)
    {
        RestoreLayoutFromMaximized();
        EnsureRegistered(id);
        using (Suppress())
        {
            if (FindCenterDocument(id) != null)
                throw new InvalidOperationException($"窗口 {id} 是文档页，不支持自动隐藏");

            var anchorable = FindRequiredAnchorable(id);
            if (anchorable.IsHidden)
                anchorable.Show();
            anchorable.ToggleAutoHide();
            EnsureCentralWorkspace();
        }
    }

    public void Dock(string id, DockSide side, double? ratio = null, string? targetId = null)
    {
        RestoreLayoutFromMaximized();
        if (!_byId.TryGetValue(id, out var descriptor))
            throw new ArgumentException($"未注册的窗口: {id}", nameof(id));
        if (ratio is { } providedRatio &&
            (!double.IsFinite(providedRatio) || providedRatio is <= 0 or >= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(ratio), "比例须严格位于 (0,1)");
        }
        _pendingTabTargets.Remove(id);
        using (Suppress())
        {
            if (side == DockSide.Center ||
                side == DockSide.Tab && targetId != null && IsCenterContent(targetId))
            {
                if (UsesDocumentIdentity(descriptor))
                {
                    ShowCenterDocument(MoveToCenterDocument(descriptor));
                }
                else
                {
                    ShowAnchorableAsCenterPage(MoveToAnchorable(descriptor));
                }
            }
            else
            {
                if (IsPrimaryCommandDocument(id))
                {
                    _log.Warn(LayoutSource, "命令集是主窗口，只能停靠在中央主区");
                    ShowCenterDocument(MoveToCenterDocument(descriptor));
                }
                else
                {
                    var anchorable = MoveToAnchorable(descriptor);
                    PlaceAtSide(anchorable, side, ratio ?? descriptor.DefaultRatio, targetId);
                    anchorable.IsSelected = true;
                }
            }
            EnsureCentralWorkspace();
        }
    }

    public void SetRatio(string id, double ratio)
    {
        RestoreLayoutFromMaximized();
        if (!double.IsFinite(ratio) || ratio is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(ratio), "比例须严格位于 (0,1)");

        if (FindCenterDocument(id) != null)
        {
            _log.Warn(LayoutSource, $"窗口 {id} 位于中央主区，不支持比例调整");
            return;
        }

        var a = FindRequiredAnchorable(id);
        var side = DetectSide(a);
        if (side is null or DockSide.Tab or DockSide.Center)
        {
            _log.Warn(LayoutSource, $"窗口 {id} 当前不在可调整比例的四边停靠区");
            return;
        }

        using (Suppress())
        {
            ApplyRatio(a, side.Value, ratio);
            ReapplyRatios();
        }
    }

    public void ResetWindow(string id)
    {
        RestoreLayoutFromMaximized();
        var d = _byId[id];
        using (Suppress())
        {
            if (UsesDocumentIdentity(d))
            {
                ShowCenterDocument(MoveToCenterDocument(d));
            }
            else if (d.DefaultSide == DockSide.Tab && d.DefaultTabTarget != null &&
                     FindCenterDocument(d.DefaultTabTarget) != null)
            {
                ShowAnchorableAsCenterPage(MoveToAnchorable(d));
            }
            else
            {
                var anchorable = MoveToAnchorable(d);
                PlaceAtSide(anchorable, d.DefaultSide, d.DefaultRatio, d.DefaultTabTarget);
                anchorable.IsSelected = true;
            }
            EnsureCentralWorkspace();
        }
    }

    public void ResetLayout()
    {
        RestoreLayoutFromMaximized();
        using (Suppress())
        {
            BuildDefaultLayout();
            EnsureCentralWorkspace();
            AttachLayout();
            CurrentLayoutName = "默认";
            _seedRatiosFromLayout = false;
            foreach (var d in _descriptors)
                _ratios[d.Id] = NormalizeRatio(d.DefaultRatio, 0.25);
        }

        ScheduleReapplyRatios();
        RebaseSoon();
    }

    public void SaveLayout(string name)
    {
        _store.WriteNamed(name, _layoutBeforeMaximize ?? SerializeLayout());
        CurrentLayoutName = name;
        _log.Info(LayoutSource, $"布局方案已保存: {name}");
    }

    public bool LoadLayout(string name)
    {
        RestoreLayoutFromMaximized();
        string? xml;
        try
        {
            xml = _store.ReadNamed(name);
        }
        catch (Exception ex)
        {
            _log.Error(LayoutSource, $"读取布局方案 {name} 失败: {ex.Message}");
            return false;
        }

        if (xml == null)
        {
            _log.Warn(LayoutSource, $"布局方案不存在: {name}");
            return false;
        }

        try
        {
            using (Suppress())
            {
                ApplyLayoutXml(xml);
                if (!LayoutHasMainDocumentPane())
                    throw new InvalidOperationException("布局中缺少中央主文档区");
                EnsureRegisteredWindows();
                EnsureCentralWorkspace();
                AttachLayout();
                CurrentLayoutName = name;
                _seedRatiosFromLayout = true;
            }

            RebaseSoon();
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(LayoutSource, $"加载布局方案 {name} 失败: {ex.Message}");
            using (Suppress())
            {
                BuildDefaultLayout();
                EnsureCentralWorkspace();
                AttachLayout();
                CurrentLayoutName = "默认";
            }

            RebaseSoon();
            return false;
        }
    }

    public IReadOnlyList<string> ListLayouts() => _store.ListNamed();

    public void RegisterWindow(ToolWindowDescriptor descriptor, string owner)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (_byId.ContainsKey(descriptor.Id))
        {
            throw new InvalidOperationException(
                $"工具窗口 Id 冲突: {descriptor.Id}(禁止静默覆盖,§5.3)");
        }

        RestoreLayoutFromMaximized();
        using (Suppress())
        {
            _descriptors.Add(descriptor);
            _byId.Add(descriptor.Id, descriptor);
            _owners[descriptor.Id] = owner;
            var placement = TakeOrphanPlacement(descriptor.Id);
            var side = placement?.Side ?? descriptor.DefaultSide;
            var target = placement?.TabTarget ?? descriptor.DefaultTabTarget;
            var hidden = placement?.Hidden ??
                         (_hiddenCenterIds.Contains(descriptor.Id) || !descriptor.DefaultVisible);
            if (side == DockSide.Center ||
                side == DockSide.Tab && target != null && FindCenterDocument(target) != null)
            {
                var select = placement?.Selected ?? true;
                if (UsesDocumentIdentity(descriptor))
                {
                    var document = MoveToCenterDocument(descriptor);
                    if (hidden && !IsPrimaryCommandDocument(descriptor.Id))
                    {
                        DetachDocument(document);
                        _hiddenCenterIds.Add(descriptor.Id);
                    }
                    else
                    {
                        ShowCenterDocument(document, placement?.CenterIndex, select);
                    }
                }
                else
                {
                    var anchorable = MoveToAnchorable(descriptor);
                    ShowAnchorableAsCenterPage(anchorable, placement?.CenterIndex, select);
                    if (hidden)
                        anchorable.Hide();
                }
            }
            else
            {
                var anchorable = MoveToAnchorable(descriptor);
                var targetPending = side == DockSide.Tab && target != null &&
                                    FindCenterDocument(target) == null &&
                                    FindAnchorable(target)?.Parent is not LayoutAnchorablePane;
                if (targetPending)
                {
                    _pendingTabTargets[descriptor.Id] = target!;
                    _log.Info(LayoutSource, $"窗口 {descriptor.Id} 的标签组目标 {target} 尚未注册，暂时右侧停靠");
                }
                PlaceAtSide(
                    anchorable,
                    targetPending ? DockSide.Right : side,
                    placement?.Ratio ?? descriptor.DefaultRatio,
                    targetPending ? null : target);
                if (hidden)
                    anchorable.Hide();
            }
            EnsureCentralWorkspace();
            ResolvePendingTabTargets(descriptor.Id);
        }

        ScheduleReapplyRatios();
        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UnregisterWindow(string id)
    {
        if (!_byId.TryGetValue(id, out var descriptor))
            return;

        RestoreLayoutFromMaximized();
        using (Suppress())
        {
            var anchorable = FindAnchorable(id);
            if (anchorable != null)
            {
                Detach(anchorable);
                anchorable.Content = null;
            }
            var document = FindCenterDocument(id);
            if (document != null)
            {
                DetachDocument(document);
                document.Content = null;
                _centerDocuments.Remove(id);
                _hiddenCenterIds.Remove(id);
                ScheduleCenterDocumentPresentation();
            }

            _descriptors.Remove(descriptor);
            _byId.Remove(id);
            _ratios.Remove(id);
            _baseline.Remove(id);
            _preserveDefaultRatioOnSeed.Remove(id);
            _owners.Remove(id);
            _pendingTabTargets.Remove(id);
            if (_contents.Remove(id, out var content))
                TryDispose(content, id);
            _manager.Layout.CollectGarbage();
            EnsureCentralWorkspace();
        }

        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UnregisterOwner(string owner)
    {
        RestoreLayoutFromMaximized();
        foreach (var id in _owners
                     .Where(pair => pair.Value.Equals(owner, StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            UnregisterWindow(id);
        }
    }

    public void MaximizeWindow(string id)
    {
        if (_maximizedId != null && _maximizedId.Equals(id, StringComparison.OrdinalIgnoreCase))
            return;
        RestoreLayoutFromMaximized();
        if (!_byId.ContainsKey(id))
            throw new ArgumentException($"未注册的工具窗口: {id}", nameof(id));

        using (Suppress())
        {
            _layoutBeforeMaximize = SerializeLayout();
            BuildMaximizedLayout(id);
            AttachLayout();
            _maximizedId = id;
        }

        WindowsChanged?.Invoke(this, EventArgs.Empty);
        RebaseSoon();
    }

    public void RestoreLayoutFromMaximized()
    {
        if (_maximizedId == null || _layoutBeforeMaximize == null)
            return;

        using (Suppress())
        {
            var xml = _layoutBeforeMaximize;
            ApplyLayoutXml(xml);
            if (!LayoutHasMainDocumentPane())
                throw new InvalidOperationException("布局中缺少中央主文档区");
            EnsureRegisteredWindows();
            EnsureCentralWorkspace();
            AttachLayout();
            _maximizedId = null;
            _layoutBeforeMaximize = null;
            _seedRatiosFromLayout = true;
        }

        WindowsChanged?.Invoke(this, EventArgs.Empty);
        RebaseSoon();
    }

    // ---------------------------------------------------------------- 布局构建与序列化

    // ---------------------------------------------------------------- 状态检测

    private sealed record WinState(bool Visible, bool Floating, DockSide? Side, string? TabTarget, double Ratio);

    private WinState ComputeState(string id)
    {
        var document = FindCenterDocument(id);
        if (document != null)
        {
            if (_hiddenCenterIds.Contains(id) || document.Parent == null)
                return new WinState(false, false, null, null, 0);
            if (IsFloating(document))
                return new WinState(true, true, null, null, 0);
            return new WinState(true, false, DockSide.Center, null, 0);
        }

        var a = FindAnchorable(id);
        if (a == null || a.IsHidden)
            return new WinState(false, false, null, null, 0);

        if (IsFloating(a))
            return new WinState(true, true, null, null, 0);

        if (IsHostedInDocumentPane(a))
            return new WinState(true, false, DockSide.Center, null, 0);

        var side = DetectSide(a);
        var tabLeader = side == null
            ? null
            : (a.Parent as LayoutAnchorablePane)?.Children
                .FirstOrDefault(c => c.ContentId != null && _byId.ContainsKey(c.ContentId));
        // Normalize a tab group as one leader plus followers. Returning an arbitrary sibling for
        // every member creates circular targets (A -> B and B -> A), which cannot be replayed or
        // used as a stable pre-gesture recovery snapshot.
        var tabTarget = tabLeader == null || ReferenceEquals(tabLeader, a)
            ? null
            : tabLeader.ContentId;

        return new WinState(true, false, side, tabTarget, DetectRatio(a, side));
    }

    private static bool IsFloating(LayoutContent content)
        => IsInsideFloatingWindow(content);

    private static bool IsInsideFloatingWindow(ILayoutElement element)
    {
        for (ILayoutContainer? p = element.Parent; p != null; p = (p as ILayoutElement)?.Parent)
        {
            if (p is LayoutFloatingWindow)
                return true;
        }

        return false;
    }

    private DockSide? DetectSide(LayoutAnchorable a)
    {
        if (a.IsHidden || IsFloating(a))
            return null;

        ILayoutElement? centerAnchor = _manager.Layout.RootPanel.Descendents()
            .OfType<LayoutDocumentPane>()
            .SingleOrDefault();
        if (centerAnchor == null)
            return null;

        for (ILayoutContainer? parent = centerAnchor.Parent;
             parent != null;
             parent = (parent as ILayoutElement)?.Parent)
        {
            if (parent is not LayoutPanel panel)
                continue;
            var windowChild = ChildContaining(panel, a);
            var centerChild = ChildContaining(panel, centerAnchor);
            if (windowChild == null || centerChild == null || ReferenceEquals(windowChild, centerChild))
                continue;

            var before = panel.Children.IndexOf(windowChild) < panel.Children.IndexOf(centerChild);
            return panel.Orientation == Orientation.Horizontal
                ? before ? DockSide.Left : DockSide.Right
                : before ? DockSide.Top : DockSide.Bottom;
        }

        return null;
    }

    private double DetectRatio(LayoutAnchorable a, DockSide? side)
    {
        if (side == null)
            return 0;

        var pane = a.Parent as ILayoutElement;
        if (pane == null)
            return 0;

        var fe = FindControlFor(pane);
        if (fe == null || _manager.ActualWidth <= 0 || _manager.ActualHeight <= 0)
            return 0;

        if (side == DockSide.Center)
            return 0;
        var ratio = side is DockSide.Left or DockSide.Right
            ? fe.ActualWidth / _manager.ActualWidth
            : fe.ActualHeight / _manager.ActualHeight;
        ratio = Math.Round(ratio, 2);
        return double.IsFinite(ratio) && ratio is > 0 and < 1 ? ratio : 0;
    }

    private static ILayoutPanelElement? ChildContaining(LayoutPanel panel, ILayoutElement element)
    {
        ILayoutElement current = element;
        while (current.Parent != null && !ReferenceEquals(current.Parent, panel))
            current = current.Parent;
        return ReferenceEquals(current.Parent, panel) ? current as ILayoutPanelElement : null;
    }

    private FrameworkElement? FindControlFor(ILayoutElement model)
        => FindVisualDescendants(_manager)
            .FirstOrDefault(fe => fe is ILayoutControl lc && ReferenceEquals(lc.Model, model));

    private static IEnumerable<FrameworkElement> FindVisualDescendants(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe)
                yield return fe;
            foreach (var g in FindVisualDescendants(child))
                yield return g;
        }
    }

    private LayoutAnchorable? FindAnchorable(string id)
        => _manager.Layout.Descendents()
            .OfType<LayoutAnchorable>()
            .Concat(_manager.Layout.Hidden)
            .FirstOrDefault(a => string.Equals(a.ContentId, id, StringComparison.OrdinalIgnoreCase));

    private LayoutAnchorable FindRequiredAnchorable(string id)
    {
        if (!_byId.ContainsKey(id))
            throw new ArgumentException($"未注册的窗口: {id}", nameof(id));
        return FindAnchorable(id)
               ?? throw new InvalidOperationException($"窗口 {id} 不在当前布局中");
    }

    // ---------------------------------------------------------------- 布局事件 → 指令(W-10)

    private void AttachLayout()
    {
        if (_attachedRoot != null)
            _attachedRoot.Updated -= OnLayoutUpdated;

        _attachedRoot = _manager.Layout;
        _attachedRoot.Updated += OnLayoutUpdated;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_suppress > 0 || _windowResizePending)
            return;

        ScheduleCentralWorkspaceRepair();
        ScheduleCenterDocumentPresentation();

        // 手势进行中持续触发 → 去抖,静默 500ms 后视为动作结束
        _debounce.Stop();
        _debounce.Start();
    }

    private void EmitLayoutDiffs()
    {
        if (_suppress > 0)
            return;

        if (NeedsCentralWorkspaceRepair())
        {
            using (Suppress())
                EnsureCentralWorkspace();
            return;
        }

        var now = ComputeAllStates();
        foreach (var d in _descriptors)
        {
            if (!_baseline.TryGetValue(d.Id, out var was))
                continue;
            var cur = now[d.Id];

            if (was.Visible && !cur.Visible)
            {
                Emit($"vulcan.ui.hide name={d.Id}");
                continue;
            }

            if (!was.Visible && cur.Visible)
                Emit($"vulcan.ui.show name={d.Id}");

            if (!cur.Visible)
                continue;

            if (!was.Floating && cur.Floating)
            {
                Emit($"vulcan.ui.float name={d.Id}");
                continue;
            }

            if (cur.Floating || cur.Side == null)
                continue;

            var dockChanged = was.Floating || was.Side != cur.Side ||
                              !string.Equals(was.TabTarget, cur.TabTarget, StringComparison.OrdinalIgnoreCase);
            if (dockChanged)
            {
                Emit(cur.TabTarget != null
                    ? $"vulcan.ui.dock name={d.Id} pos=tab target={cur.TabTarget}"
                    : cur.Side == DockSide.Center
                        ? $"vulcan.ui.dock name={d.Id} pos=center"
                        : $"vulcan.ui.dock name={d.Id} pos={SideText(cur.Side.Value)} ratio={FormatRatio(cur.Ratio)}");
            }
            else if (was.Ratio > 0 && cur.Ratio > 0 && Math.Abs(was.Ratio - cur.Ratio) > RatioEpsilon)
            {
                // was.Ratio == 0 说明基线建立时尚未完成渲染(尺寸未知),不视为用户手势
                Emit($"vulcan.ui.ratio name={d.Id} value={FormatRatio(cur.Ratio)}");
            }
        }

        // 用户拖拽分隔条 / 重新停靠后,同步更新比例记录(W-05)
        foreach (var (id, s) in now)
        {
            if (s is { Visible: true, Floating: false, Side: not null and not DockSide.Tab and not DockSide.Center, Ratio: > 0 })
                _ratios[id] = s.Ratio;
        }

        using (Suppress())
            ReapplyRatios();

        _baseline = now;
    }

    private Dictionary<string, WinState> ComputeAllStates()
        => _descriptors.ToDictionary(d => d.Id, d => ComputeState(d.Id), StringComparer.OrdinalIgnoreCase);

    private void Emit(string commandText)
    {
        // 以指令回显类别落管道(L-03):控制台按 "[layout] > ..." 样式渲染,可一键屏蔽(C-04)
        _log.Info(Core.Commands.CommandBus.EchoCategoryPrefix + LayoutSource, commandText);
        CommandGenerated?.Invoke(this, new ShellCommandEventArgs
        {
            CommandText = commandText,
            Source = LayoutSource,
        });
    }

    /// <summary>防再入(§14.2 清单 4):指令/API 驱动的布局变更不回声成新指令。</summary>
    private IDisposable Suppress()
    {
        _suppress++;
        _debounce.Stop();
        return new SuppressScope(this);
    }

    private void RebaseSoon()
        => _manager.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle, // 渲染完成后再取快照,保证比例已可测量
            () => _baseline = ComputeAllStates());

    private sealed class SuppressScope : IDisposable
    {
        private DockingHost? _host;

        public SuppressScope(DockingHost host) => _host = host;

        public void Dispose()
        {
            if (_host == null)
                return;
            var host = _host;
            _host = null;
            host._suppress--;
            if (host._suppress == 0)
                host.RebaseSoon(); // 渲染一拍后重建基线,吸收本次程序化变更
        }
    }

    // ---------------------------------------------------------------- 工具

    // AvalonDock 的 ILayoutPositionableElement 为 internal,
    // DockWidth / DockHeight 只能经各具体面板类型访问 —— 用反射统一读写
    private static GridLength GetDockLength(ILayoutElement element, bool horizontal)
    {
        var prop = element.GetType().GetProperty(horizontal ? "DockWidth" : "DockHeight");
        return prop?.GetValue(element) is GridLength len
            ? len
            : new GridLength(1, GridUnitType.Star);
    }

    private static void SetDockLength(ILayoutElement element, bool horizontal, GridLength value)
    {
        var prop = element.GetType().GetProperty(horizontal ? "DockWidth" : "DockHeight");
        if (prop != null && prop.CanWrite)
            prop.SetValue(element, value);
    }

    private static GridLength Star(double value)
        => new(Math.Max(value, 0.02), GridUnitType.Star);

    private static string SideText(DockSide side) => side switch
    {
        DockSide.Left => "left",
        DockSide.Right => "right",
        DockSide.Top => "top",
        DockSide.Bottom => "bottom",
        DockSide.Center => "center",
        _ => "tab",
    };

    private static double NormalizeRatio(double ratio, double fallback)
    {
        if (double.IsFinite(ratio) && ratio is > 0 and < 1)
            return ratio;
        return double.IsFinite(fallback) && fallback is > 0 and < 1 ? fallback : 0.25;
    }

    private static string FormatRatio(double value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);
}
