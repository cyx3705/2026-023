using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HistoryVulcan.Core.Docking;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;

namespace HistoryVulcan.Shell.Docking;

public sealed partial class DockingHost
{
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
        if (a == null || a.IsHidden || _hiddenCenterIds.Contains(id))
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
        // Normalize a tab group as one leader plus followers to keep recovery targets stable.
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
}
