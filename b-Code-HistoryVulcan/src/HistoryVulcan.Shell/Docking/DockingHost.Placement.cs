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

public sealed partial class DockingHost
{
    // ---------------------------------------------------------------- 布局树操作

    private void PlaceAtSide(LayoutAnchorable a, DockSide side, double ratio, string? targetId)
    {
        var root = _manager.Layout;
        ToolWindowDescriptor? descriptor = null;
        if (a.ContentId != null)
            _byId.TryGetValue(a.ContentId, out descriptor);
        var fallbackRatio = descriptor?.DefaultRatio ?? 0.25;
        ratio = NormalizeRatio(ratio, fallbackRatio);

        if (side == DockSide.Center)
        {
            if (descriptor == null)
                throw new InvalidOperationException("未注册的窗口不能进入中央主区");
            if (UsesDocumentIdentity(descriptor))
                ShowCenterDocument(MoveToCenterDocument(descriptor));
            else
                ShowAnchorableAsCenterPage(MoveToAnchorable(descriptor));
            return;
        }

        Detach(a);

        if (side == DockSide.Tab)
        {
            var target = targetId != null ? FindAnchorable(targetId) : null;
            if (target?.Parent is LayoutAnchorablePane targetPane)
            {
                a.CanAutoHide = true;
                targetPane.Children.Add(a);
                targetPane.SelectedContentIndex = targetPane.Children.Count - 1;
                root.CollectGarbage();
                return;
            }

            _log.Warn(LayoutSource, $"标签组目标 {targetId ?? "(空)"} 不可用,改为右侧停靠");
            side = DockSide.Right;
        }

        // 默认布局会把同一侧的窗口合并成一个标签组；运行期模块注册也必须遵守
        // 相同拓扑，避免每注册一个右侧窗口就额外切出一块嵌套侧栏。
        var existingPane = FindSidePane(side, a);
        if (existingPane != null)
        {
            a.CanAutoHide = true;
            existingPane.Children.Add(a);
            existingPane.SelectedContentIndex = existingPane.Children.Count - 1;
            if (a.ContentId != null)
                _ratios[a.ContentId] = ratio;
            root.CollectGarbage();
            return;
        }

        var pane = new LayoutAnchorablePane(a);
        a.CanAutoHide = true;

        switch (side)
        {
            case DockSide.Left:
                pane.DockWidth = DockLengthFor(horizontal: true, ratio);
                EnsureSideRootPanel().Children.Insert(0, pane);
                break;

            case DockSide.Right:
                pane.DockWidth = DockLengthFor(horizontal: true, ratio);
                EnsureSideRootPanel().Children.Add(pane);
                break;

            case DockSide.Top:
            case DockSide.Bottom:
                {
                    var column = EnsureCenterColumn();
                    pane.DockHeight = DockLengthFor(horizontal: false, ratio);
                    if (side == DockSide.Top)
                        column.Children.Insert(0, pane);
                    else
                        column.Children.Add(pane);
                    break;
                }

        }

        if (a.ContentId != null)
            _ratios[a.ContentId] = ratio;
        root.CollectGarbage();
    }

    private void ResolvePendingTabTargets(string targetId)
    {
        foreach (var (id, pendingTarget) in _pendingTabTargets
                     .Where(item => item.Value.Equals(targetId, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            if (!_byId.TryGetValue(id, out var descriptor) ||
                !TryPlaceAtTabTarget(descriptor, pendingTarget))
                continue;

            _pendingTabTargets.Remove(id);
            _log.Info(LayoutSource, $"窗口 {id} 已挂入延迟可用的标签组 {pendingTarget}");
        }
    }

    private bool TryPlaceAtTabTarget(ToolWindowDescriptor descriptor, string targetId)
    {
        if (FindCenterDocument(targetId) != null)
        {
            var anchorable = MoveToAnchorable(descriptor);
            var hidden = anchorable.IsHidden;
            ShowAnchorableAsCenterPage(anchorable);
            if (hidden)
                anchorable.Hide();
            return true;
        }

        var target = FindAnchorable(targetId);
        if (target?.Parent is not LayoutAnchorablePane pane)
            return false;

        var item = MoveToAnchorable(descriptor);
        var wasHidden = item.IsHidden;
        Detach(item);
        pane.Children.Add(item);
        if (wasHidden)
            item.Hide();
        else
            pane.SelectedContentIndex = pane.Children.Count - 1;
        _manager.Layout.CollectGarbage();
        return true;
    }

    private LayoutAnchorablePane? FindSidePane(DockSide side, LayoutAnchorable excluded)
        => _manager.Layout.Descendents()
            .OfType<LayoutAnchorable>()
            .Where(item => !ReferenceEquals(item, excluded)
                           && item.Parent is LayoutAnchorablePane
                           && !item.IsHidden
                           && !IsFloating(item))
            .FirstOrDefault(item => DetectSide(item) == side)
            ?.Parent as LayoutAnchorablePane;

    private void ConsolidateSidePanes()
    {
        foreach (var side in new[] { DockSide.Left, DockSide.Right, DockSide.Top, DockSide.Bottom })
        {
            var panes = _manager.Layout.Descendents()
                .OfType<LayoutAnchorable>()
                .Where(item => !item.IsHidden && !IsFloating(item) && DetectSide(item) == side)
                .Select(item => item.Parent)
                .OfType<LayoutAnchorablePane>()
                .Distinct()
                .ToList();
            if (panes.Count < 2)
                continue;

            var target = panes
                .OrderByDescending(pane => pane.Children.Count)
                .First();
            foreach (var source in panes.Where(pane => !ReferenceEquals(pane, target)))
            {
                foreach (var item in source.Children.ToArray())
                {
                    source.Children.Remove(item);
                    target.Children.Add(item);
                }
            }
        }

        _manager.Layout.CollectGarbage();
    }

    /// <summary>找到包含主文档区的中央列;若中央区不是垂直面板,则就地包一层。</summary>
    private LayoutPanel EnsureCenterColumn()
    {
        var document = _manager.Layout.Descendents().OfType<LayoutDocumentPane>().SingleOrDefault()
                       ?? throw new InvalidOperationException("布局中找不到中央主文档区");
        for (ILayoutContainer? parent = document.Parent;
             parent != null;
             parent = (parent as ILayoutElement)?.Parent)
        {
            if (parent is LayoutPanel { Orientation: Orientation.Vertical } vertical)
                return vertical;
        }

        var rootPanel = _manager.Layout.RootPanel;
        var center = FindCenterChild(rootPanel)
                     ?? throw new InvalidOperationException("布局中找不到中央主文档区");

        if (center is LayoutPanel { Orientation: Orientation.Vertical } column)
            return column;

        var idx = rootPanel.Children.IndexOf(center);
        rootPanel.Children.RemoveAt(idx);
        var wrap = new LayoutPanel { Orientation = Orientation.Vertical };
        wrap.DockWidth = GetDockLength(center, horizontal: true);
        wrap.Children.Add(center);
        rootPanel.Children.Insert(idx, wrap);
        return wrap;
    }

    private LayoutPanel EnsureSideRootPanel()
    {
        var column = EnsureCenterColumn();
        if (column.Parent is LayoutPanel { Orientation: Orientation.Horizontal } horizontal)
            return horizontal;

        var rootPanel = _manager.Layout.RootPanel;
        var center = ChildContaining(rootPanel, column)
                     ?? throw new InvalidOperationException("布局中找不到中央列");
        if (rootPanel.Orientation == Orientation.Horizontal)
            return rootPanel;

        var index = rootPanel.Children.IndexOf(center);
        rootPanel.Children.RemoveAt(index);
        var wrap = new LayoutPanel { Orientation = Orientation.Horizontal };
        wrap.DockHeight = GetDockLength(center, horizontal: false);
        wrap.Children.Add(center);
        rootPanel.Children.Insert(index, wrap);
        return wrap;
    }

    private bool NeedsCentralWorkspaceRepair()
    {
        if (_maximizedId != null || !_byId.ContainsKey(StandardWindowIds.Mcp))
            return false;

        var document = FindCenterDocument(StandardWindowIds.Mcp);
        return document == null || document.Parent is not LayoutDocumentPane || IsFloating(document);
    }

    /// <summary>
    /// Keep the command catalog as the fixed main document. This repairs the early 3.0 candidate
    /// topology that placed it in a narrow anchorable pane beside an empty document background.
    /// </summary>
    private bool EnsureCentralWorkspace()
    {
        if (_maximizedId != null)
            return false;

        if (!_byId.TryGetValue(StandardWindowIds.Mcp, out var descriptor))
            return false;

        var existing = FindCenterDocument(StandardWindowIds.Mcp);
        var repaired = existing == null || existing.Parent is not LayoutDocumentPane || IsFloating(existing);
        if (!repaired)
        {
            NormalizeMainDocumentSizing((LayoutDocumentPane)existing!.Parent!);
            ScheduleCenterDocumentPresentation();
            return false;
        }

        ShowCenterDocument(MoveToCenterDocument(descriptor));
        _log.Info(LayoutSource, "命令集已恢复为中央主窗口");
        return true;
    }

    private void ScheduleCentralWorkspaceRepair()
    {
        if (_centerRepairPending || _suppress > 0)
            return;
        _centerRepairPending = true;
        _manager.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _centerRepairPending = false;
            if (_suppress > 0 || !NeedsCentralWorkspaceRepair())
                return;
            using (Suppress())
                EnsureCentralWorkspace();
        });
    }

    private static ILayoutPanelElement? FindCenterChild(LayoutPanel rootPanel)
        => rootPanel.Children.FirstOrDefault(c =>
            c is LayoutDocumentPane ||
            c.Descendents().OfType<LayoutDocumentPane>().Any());

    private static bool IsHostedInDocumentPane(LayoutAnchorable anchorable)
    {
        for (ILayoutContainer? parent = anchorable.Parent;
             parent != null;
             parent = (parent as ILayoutElement)?.Parent)
        {
            if (parent is LayoutDocumentPane)
                return true;
        }

        return false;
    }

    private void Detach(LayoutAnchorable a)
    {
        if (a.IsHidden)
        {
            _manager.Layout.Hidden.Remove(a);
            return;
        }

        if (a.Parent is ILayoutContainer container)
            container.RemoveChild(a);
    }

    private void ApplyRatio(LayoutAnchorable a, DockSide side, double ratio)
    {
        if (side == DockSide.Center)
            return;
        var horizontal = side is DockSide.Left or DockSide.Right;
        var panel = horizontal
            ? _manager.Layout.RootPanel
            : EnsureCenterColumn();

        var child = ChildContaining(panel, a);
        if (child == null)
        {
            _log.Warn(LayoutSource, "未能定位窗口所在的布局分区,比例未调整");
            return;
        }

        SetDockLength(child, horizontal, DockLengthFor(horizontal, ratio));
        foreach (var item in child.Descendents().OfType<LayoutAnchorable>())
        {
            if (item.ContentId != null && _byId.ContainsKey(item.ContentId))
                _ratios[item.ContentId] = ratio;
        }
    }

    /// <summary>
    /// 目标尺寸的表达:主窗体已量得实际尺寸时用像素(AvalonDock 对侧窗格
    /// 的原生语义),否则先用星值占位,待首次排布后由 ReapplyRatios 修正。
    /// </summary>
    private GridLength DockLengthFor(bool horizontal, double ratio)
    {
        var total = horizontal ? _manager.ActualWidth : _manager.ActualHeight;
        return total > 0
            ? new GridLength(Math.Max(ratio * total, 25), GridUnitType.Pixel)
            : Star(ratio);
    }

    private void ScheduleReapplyRatios()
        => _manager.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            () =>
            {
                using (Suppress())
                {
                    ReapplyRatios();
                }
            });

    /// <summary>
    /// 按记录的“占主程序窗体百分比”重设各停靠分区尺寸(W-05)。
    /// 恢复布局后的首次调用改为反向采集:以布局文件里的尺寸为准更新比例记录。
    /// </summary>
    private void ReapplyRatios()
    {
        if (_maximizedId != null)
            return;
        if (_manager.ActualWidth <= 0 || _manager.ActualHeight <= 0)
            return;

        if (_seedRatiosFromLayout)
        {
            _seedRatiosFromLayout = false;
            var addedAfterSavedLayout = _preserveDefaultRatioOnSeed.ToArray();
            foreach (var d in _descriptors)
            {
                if (_preserveDefaultRatioOnSeed.Contains(d.Id))
                    continue;
                var s = ComputeState(d.Id);
                if (s is { Visible: true, Floating: false, Side: not null and not DockSide.Tab, Ratio: > 0 })
                    _ratios[d.Id] = s.Ratio;
            }

            _preserveDefaultRatioOnSeed.Clear();

            // Restored pixel panes and newly inserted star panes otherwise share incompatible
            // sizing semantics. Reapply each new descriptor's declared ratio after layout has
            // an actual size so it cannot collapse to 1-3%.
            foreach (var id in addedAfterSavedLayout)
            {
                var anchorable = FindAnchorable(id);
                var side = anchorable == null ? null : DetectSide(anchorable);
                if (anchorable != null && side is not null and not DockSide.Tab and not DockSide.Center)
                    ApplyRatio(anchorable, side.Value, _ratios[id]);
            }

        }

        var rootPanel = _manager.Layout.RootPanel;
        var center = FindCenterChild(rootPanel);
        if (center == null)
            return;

        var rootHorizontal = rootPanel.Orientation == Orientation.Horizontal;
        ReapplyPanelRatios(rootPanel, center, rootHorizontal);

        if (center is LayoutPanel column)
        {
            var innerDoc = column.Children.FirstOrDefault(c =>
                c is LayoutDocumentPane || c.Descendents().OfType<LayoutDocumentPane>().Any());
            var columnHorizontal = column.Orientation == Orientation.Horizontal;
            if (innerDoc != null)
                ReapplyPanelRatios(column, innerDoc, columnHorizontal);
        }
    }

    private void ReapplyPanelRatios(
        LayoutPanel panel,
        ILayoutPanelElement center,
        bool horizontal)
    {
        var sides = panel.Children
            .Where(child => !ReferenceEquals(child, center))
            .Select(child => (Child: child, Ratio: RatioOfSubtree(child)))
            .Where(item => item.Ratio is > 0)
            .Select(item => (item.Child, Ratio: item.Ratio!.Value))
            .ToList();
        var requested = sides.Sum(item => item.Ratio);
        var scale = requested > MaximumSideAllocation
            ? MaximumSideAllocation / requested
            : 1d;

        foreach (var (child, ratio) in sides)
        {
            var effective = ratio * scale;
            SetDockLength(child, horizontal, DockLengthFor(horizontal, effective));
            foreach (var anchorable in child.Descendents().OfType<LayoutAnchorable>())
            {
                if (anchorable.ContentId != null && _byId.ContainsKey(anchorable.ContentId))
                    _ratios[anchorable.ContentId] = effective;
            }
        }
    }

    /// <summary>分区内第一个已注册窗口的目标比例(分区尺寸由其代表)。</summary>
    private double? RatioOfSubtree(ILayoutPanelElement subtree)
    {
        var anchorables = subtree is LayoutAnchorable self
            ? new[] { self }.AsEnumerable()
            : subtree.Descendents().OfType<LayoutAnchorable>();
        foreach (var a in anchorables)
        {
            if (a.ContentId != null && _ratios.TryGetValue(a.ContentId, out var r))
                return r;
        }

        return null;
    }
}

