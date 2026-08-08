using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Shell.Docking;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;

namespace HistoryVulcan.Shell;

internal sealed partial class ShellTopBarCoordinator : IDisposable
{
    private static Size NormalizeEmbeddedSize(Size size)
        => double.IsFinite(size.Width) && size.Width > 0 &&
           double.IsFinite(size.Height) && size.Height > 0
            ? size
            : new Size(720, 520);

    private void ApplyFloatingWindowGeometry(
        LayoutFloatingWindowControl floating,
        FloatingDragContext context)
    {
        try
        {
            floating.WindowState = WindowState.Normal;
            FloatingWindowGeometry.PlaceWindow(
                floating,
                FloatingWindowGeometry.GetCursorPosition(),
                NormalizeEmbeddedSize(context.EmbeddedSize),
                context.AnchorOffset);
        }
        catch (InvalidOperationException ex)
        {
            _log.Warn(ChromeLogSource, $"浮窗几何应用失败：{ex.Message}");
        }
    }

    private LayoutContent? FindLayoutContent(string id)
        => _manager.Layout.Descendents()
            .OfType<LayoutContent>()
            .FirstOrDefault(content =>
                content.ContentId?.Equals(id, StringComparison.OrdinalIgnoreCase) == true);

    private void UpdateDocumentPaneChrome(LayoutDocumentPaneControl pane)
    {
        pane.ApplyTemplate();
        var selected = (pane.Model as LayoutDocumentPane)?.SelectedContent as LayoutContent;
        var floating = selected?.ContentId is { Length: > 0 } id
            ? FindFloatingWindow(id)
            : null;

        if (pane.Template.FindName("ShellChromeHost", pane) is ContentControl chromeHost)
            chromeHost.Visibility = floating == null ? Visibility.Visible : Visibility.Collapsed;
        if (pane.Template.FindName("FloatingDocumentMaxRestore", pane) is Button button)
        {
            button.Visibility = floating == null ? Visibility.Collapsed : Visibility.Visible;
            button.ToolTip = floating?.WindowState == WindowState.Maximized
                ? "向下还原"
                : "最大化";
        }
        if (pane.Template.FindName("FloatingDocumentMaxRestoreIcon", pane) is
            System.Windows.Shapes.Path icon &&
            pane.TryFindResource(floating?.WindowState == WindowState.Maximized
                ? "Shell.Icon.Restore"
                : "Shell.Icon.Maximize") is Geometry geometry)
        {
            icon.Data = geometry;
        }
    }

    private static bool TryResolveTabPageId(DependencyObject? source, out string id)
    {
        for (var current = source; current != null; current = GetParent(current))
        {
            if (TryGetTabModel(current, out var model) && TryGetContentId(model, out id))
                return true;
        }

        id = string.Empty;
        return false;
    }

    private static bool TryGetTabModel(DependencyObject current, out LayoutContent model)
    {
        if (current is LayoutAnchorableTabItem { Model: LayoutContent anchorable })
        {
            model = anchorable;
            return true;
        }

        if (current is LayoutDocumentTabItem { Model: LayoutContent document })
        {
            model = document;
            return true;
        }

        model = null!;
        return false;
    }

    private static bool TryResolveSelectedItem(object? selectedItem, out string id)
    {
        if (selectedItem is DependencyObject dependency &&
            TryGetTabModel(dependency, out var tabModel) && TryGetContentId(tabModel, out id))
        {
            return true;
        }

        if (selectedItem is LayoutContent selected && TryGetContentId(selected, out id))
            return true;

        id = string.Empty;
        return false;
    }

    private static bool TryGetContentId(LayoutContent content, out string id)
    {
        id = content.ContentId ?? string.Empty;
        return !string.IsNullOrWhiteSpace(id);
    }

    private static bool ModelContainsPage(ILayoutElement? element, string id)
    {
        if (element is LayoutContent content &&
            content.ContentId?.Equals(id, StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return element is ILayoutContainer container &&
               container.Children.Any(child => ModelContainsPage(child, id));
    }

    private static bool IsRealPageTab(FrameworkElement element)
        => element is LayoutAnchorableTabItem or LayoutDocumentTabItem;

    private static bool IsPaneHeaderSource(DependencyObject? source)
        => FindAncestor<FrameworkElement>(source, element =>
            Equals(element.Tag, "ShellPaneHeader") ||
            Equals(element.Tag, "FocusedShellPaneHeader")) != null;

    private static bool IsInteractiveCommandControl(DependencyObject? source)
    {
        for (var current = source; current != null; current = GetParent(current))
        {
            if (current is ButtonBase or MenuItem or TextBoxBase or ComboBox)
                return true;
        }

        return false;
    }

    private static bool IsInteractive(DependencyObject? source)
    {
        for (var current = source; current != null; current = GetParent(current))
        {
            if (current is ButtonBase or MenuItem or TextBoxBase or ComboBox or TabItem or
                LayoutAnchorableTabItem or LayoutDocumentTabItem)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject current)
        => current is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(current)
            : LogicalTreeHelper.GetParent(current);

    private static T? FindAncestor<T>(DependencyObject? source, Func<T, bool>? predicate = null)
        where T : DependencyObject
    {
        for (var current = source; current != null; current = GetParent(current))
        {
            if (current is T match && (predicate == null || predicate(match)))
                return match;
        }

        return null;
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in FindVisualDescendants<T>(child))
                yield return descendant;
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern int GetSystemMetricsForDpi(int index, uint dpi);
    }
}

