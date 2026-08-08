using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Shell.Console;
using HistoryVulcan.Shell.Docking;
using HistoryVulcan.Shell.Themes;
using AvalonDock.Controls;
using AvalonDock.Layout;

namespace HistoryVulcan.Shell;

public partial class ShellWindow
{
    private void ApplyTheme(string mode, bool persist)
    {
        var dark = mode.Equals(ThemeDark, StringComparison.OrdinalIgnoreCase);
        var uri = dark ? DarkTokensUri : LightTokensUri;

        SwapTokens(Resources, uri, atEnd: false);
        SwapTokens(DockManager.Resources, uri, atEnd: true);
        if (Application.Current != null)
            SwapTokens(Application.Current.Resources, uri, atEnd: true);
        ApplyThemeToFloatingWindows(uri);

        _theme = dark ? ThemeDark : ThemeLight;
        if (persist)
        {
            _settings.Set(ThemeSettingsKey, _theme);
            if (_menusInitialized)
                BuildMenus();
        }
    }

    /// <summary>
    /// R4-3:浮动窗口是独立 Window,自带一份 AvalonDock 主题字典(里面是浅色令牌),
    /// 优先级高于应用级资源 —— 不单独换,拖出来的工具页外框就一直是白的。
    /// 新浮动窗口在 WindowsChanged 后补一次。
    /// </summary>
    private void ApplyThemeToFloatingWindows(Uri? tokensUri = null)
    {
        var uri = tokensUri ?? (_theme == ThemeDark ? DarkTokensUri : LightTokensUri);
        foreach (var floating in DockManager.FloatingWindows.ToList())
        {
            // 已经是目标主题就整窗跳过 —— 本方法在布局回调里跑,不幂等会死循环
            SwapTokens(floating.Resources, uri, atEnd: true);
            if (floating.TryFindResource("Shell.Brush.Surface") is Brush surface)
                floating.Background = surface;
            if (floating.TryFindResource("Shell.Brush.Hairline") is Brush hairline)
            {
                floating.BorderBrush = hairline;
                FloatingWindowTheme.ApplyNativeBorder(floating, hairline);
            }
        }
    }

    /// <summary>
    /// 令牌字典换位。必须幂等:这个方法会被布局回调反复调用,
    /// 每次都无脑换字典会让资源全量失效 → 触发布局 → 再回调,直接转成死循环。
    /// </summary>
    private static bool SwapTokens(ResourceDictionary target, Uri uri, bool atEnd)
    {
        var existing = target.MergedDictionaries
            .Where(dictionary => dictionary.Source == LightTokensUri || dictionary.Source == DarkTokensUri)
            .ToList();
        if (existing.Count == 1 && existing[0].Source == uri)
            return false;

        foreach (var dictionary in existing)
            target.MergedDictionaries.Remove(dictionary);

        var tokens = new ResourceDictionary { Source = uri };
        if (atEnd)
            target.MergedDictionaries.Add(tokens);
        else
            target.MergedDictionaries.Insert(0, tokens);
        return true;
    }

    // ---------------------------------------------------------------- 窗格样式

    private void LoadThemeResources()
    {
        try
        {
            _themeResources = new ResourceDictionary { Source = DockManager.Theme.GetResourceUri() };
        }
        catch (Exception ex)
        {
            _themeResources = null;
            _log.Warn("shell", $"读取主题字典失败,窗格保持 AvalonDock 默认外观: {ex.Message}");
        }
    }

    /// <summary>
    /// 窗格外观下发(W-04 标签条置顶 + UI-01 卡片化 + UI-04 专注形态)。
    /// 必须经 DockingManager.AnchorablePaneControlStyle / DocumentPaneControlStyle 属性下发:
    /// 主题字典里的隐式 Style 不会命中窗格控件,浮动窗口也走这两个属性。
    /// 以主题内置窗格样式为基底(继承 ItemContainerStyle 等),只覆盖标签位置与模板。
    /// v5 迁移注意:基底样式的资源键随主题版本变化,需同步调整。
    /// </summary>
    private void ApplyPaneStyles(bool chromeless)
    {
        var suffix = chromeless ? ".Chromeless" : string.Empty;

        if (BuildPaneStyle(
                typeof(AvalonDock.Controls.LayoutAnchorablePaneControl),
                "AvalonDockThemeVs2013AnchorablePaneControlStyle",
                $"Shell.Docking.AnchorablePaneTemplate{suffix}",
                "Shell.Docking.AnchorableTabContainerStyle") is { } anchorableStyle)
        {
            DockManager.AnchorablePaneControlStyle = anchorableStyle;
        }

        if (BuildPaneStyle(
                typeof(AvalonDock.Controls.LayoutDocumentPaneControl),
                "AvalonDockThemeVs2013DocumentPaneControlStyle",
                $"Shell.Docking.DocumentPaneTemplate{suffix}",
                "Shell.Docking.DocumentTabContainerStyle") is { } documentStyle)
        {
            DockManager.DocumentPaneControlStyle = documentStyle;
        }

        ScheduleChromeReserve();
    }

    private Style? BuildPaneStyle(
        Type paneType, string baseStyleKey, string templateKey, string tabItemStyleKey)
    {
        if (_themeResources == null)
            return null;

        if (FindInDictionary(_themeResources, templateKey) is not ControlTemplate template)
        {
            _log.Warn("shell", $"未找到窗格模板 {templateKey},该窗格保持主题默认外观");
            return null;
        }

        // 基底样式取不到时不放弃改造:直接建裸样式,只是失去主题的 ItemContainerStyle 等继承项
        var baseStyle = FindInDictionary(_themeResources, baseStyleKey) as Style;
        if (baseStyle == null)
            _log.Warn("shell", $"未找到主题窗格基底样式 {baseStyleKey},已改用裸样式");

        var style = baseStyle == null ? new Style(paneType) : new Style(paneType, baseStyle);
        style.Setters.Add(new Setter(TabControl.TabStripPlacementProperty, Dock.Top));
        style.Setters.Add(new Setter(Control.PaddingProperty, default(Thickness)));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));

        // 窗格样式同时应用到 AvalonDock 的独立内容宿主。事件必须随样式下发，
        // 不能只扫描主 DockingManager 的视觉树，否则浮窗页头收不到拖动输入。
        style.Setters.Add(new EventSetter(
            UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnPanePreviewMouseLeftButtonDown))
        {
            HandledEventsToo = true,
        });
        style.Setters.Add(new EventSetter(
            UIElement.PreviewMouseMoveEvent,
            new MouseEventHandler(OnPanePreviewMouseMove))
        {
            HandledEventsToo = true,
        });
        style.Setters.Add(new EventSetter(
            UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnPanePreviewMouseLeftButtonUp))
        {
            HandledEventsToo = true,
        });
        style.Setters.Add(new EventSetter(
            Mouse.LostMouseCaptureEvent,
            new MouseEventHandler(OnPaneLostMouseCapture))
        {
            HandledEventsToo = true,
        });
        style.Setters.Add(new EventSetter(
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnPaneLoaded)));

        // UI-06:页签容器样式随窗格样式下发(同心圆角 + 主题色选中态)
        if (FindInDictionary(_themeResources, tabItemStyleKey) is Style tabStyle)
            style.Setters.Add(new Setter(TabControl.ItemContainerStyleProperty, tabStyle));
        else
            _log.Warn("shell", $"未找到页签样式 {tabItemStyleKey},页签保持主题默认外观");

        return style;
    }

    private void OnPanePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _topBar.HandlePaneMouseLeftButtonDown(sender, e);

    private void OnPanePreviewMouseMove(object sender, MouseEventArgs e)
        => _topBar.HandlePaneMouseMove(sender, e);

    private void OnPanePreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => _topBar.HandlePaneMouseLeftButtonUp(sender, e);

    private void OnPaneLostMouseCapture(object sender, MouseEventArgs e)
        => _topBar.HandlePaneLostMouseCapture(sender, e);

    private void OnPaneLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement pane)
            _topBar.AttachPaneCommandBinding(pane);
    }

    // ---------------------------------------------------------------- 顶部按钮组占位

    private void ScheduleChromeReserve()
    {
        if (_reservePending || _closing)
            return;
        _reservePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _reservePending = false;
            if (_closing)
                return;
            AttachChromeBarToMainDocumentPane();
            ReserveSpaceForChromeBar();

            // R4-3:浮动窗口是布局后才建出来的,在这里补主题最稳妥
            ApplyThemeToFloatingWindows();
        });
    }

    /// <summary>
    /// 窗口按钮组与页签同处一行(3.1 修订),因此必须把最右上那一排页签的右边距撑开,
    /// 否则页签会滑到按钮底下点不到。专注态没有页签行,不需要占位。
    /// </summary>
    private void ReserveSpaceForChromeBar()
    {
        if (!IsLoaded || ChromeBar.ActualWidth <= 0)
            return;

        // ChromeBar 已经属于中央文档窗格时，工具窗格不需要再为它让位。
        // 仅在布局尚未生成中央宿主的回退阶段保留旧的右边距逻辑。
        var reserve = _chromeHost == null
            ? ChromeBar.ActualWidth
            : 0;
        foreach (var header in FindDescendants<Grid>(DockManager)
                     .Where(grid => grid.Tag as string == PaneHeaderTag))
        {
            var wanted = IsAtWindowTopRight(header) ? reserve : 0;
            var margin = header.Margin;
            var target = new Thickness(margin.Left, margin.Top, BaseTabStripRight + wanted, margin.Bottom);
            if (Math.Abs(margin.Right - target.Right) > 0.5)
                header.Margin = target;
        }
    }

    /// <summary>
    /// 将窗口控制栏挂到主窗口的中央文档窗格。AvalonDock 的浮动窗格位于独立
    /// Window，不会出现在 DockManager 的视觉树中，因此不会获得这组按钮。
    /// </summary>
    private void AttachChromeBarToMainDocumentPane()
    {
        if (!IsLoaded || _closing)
            return;

        var focused = _docking.MaximizedId != null;
        ChromeBar.Visibility = Visibility.Visible;

        var host = FindDescendants<ContentControl>(DockManager)
            .FirstOrDefault(control => control.IsVisible &&
                Equals(control.Tag, focused
                    ? "FocusedShellChromeHost"
                    : "ShellChromeHost"));
        if (host == null)
        {
            SetChromeDragSurface(null);
            if (_chromeHost != null)
            {
                _chromeHost.Content = null;
                _chromeHost = null;
            }

            if (!ReferenceEquals(ChromeBar.Parent, RootGrid))
                RootGrid.Children.Insert(1, ChromeBar);
            return;
        }

        if (ReferenceEquals(_chromeHost, host) && ReferenceEquals(host.Content, ChromeBar))
            return;

        if (_chromeHost != null)
            _chromeHost.Content = null;
        if (ChromeBar.Parent is Panel parent)
            parent.Children.Remove(ChromeBar);

        host.Content = ChromeBar;
        _chromeHost = host;
        SetChromeDragSurface(FindAncestor<FrameworkElement>(
            host,
            element => Equals(element.Tag,
                focused ? "FocusedShellPaneHeader" : "ShellPaneHeader")));
    }

    private void SetChromeDragSurface(FrameworkElement? surface)
    {
        if (ReferenceEquals(_chromeDragSurface, surface))
            return;

        if (_chromeDragSurface != null)
        {
            _chromeDragSurface.PreviewMouseLeftButtonDown -= OnMainChromeMouseLeftButtonDown;
            _chromeDragSurface.PreviewMouseMove -= OnMainChromeMouseMove;
            _chromeDragSurface.PreviewMouseLeftButtonUp -= OnMainChromeMouseLeftButtonUp;
            _chromeDragSurface.LostMouseCapture -= OnMainChromeLostMouseCapture;
        }
        _chromeDragSurface = surface;
        if (_chromeDragSurface != null)
        {
            _chromeDragSurface.PreviewMouseLeftButtonDown += OnMainChromeMouseLeftButtonDown;
            _chromeDragSurface.PreviewMouseMove += OnMainChromeMouseMove;
            _chromeDragSurface.PreviewMouseLeftButtonUp += OnMainChromeMouseLeftButtonUp;
            _chromeDragSurface.LostMouseCapture += OnMainChromeLostMouseCapture;
        }
        _topBar.Refresh();
    }

    private void OnMainChromeMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _topBar.HandleMainMouseLeftButtonDown(sender, e);

    private void OnMainChromeMouseMove(object sender, MouseEventArgs e)
        => _topBar.HandleMainMouseMove(sender, e);

    private void OnMainChromeMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => _topBar.HandleMainMouseLeftButtonUp(sender, e);

    private void OnMainChromeLostMouseCapture(object sender, MouseEventArgs e)
        => _topBar.HandleMainLostMouseCapture(sender, e);

    /// <summary>窗格模板里页签行容器的标记(见 ShellDocking.xaml)。</summary>
    private const string PaneHeaderTag = "ShellPaneHeader";

    /// <summary>页签条模板里的右边距基线(Shell.Space.TabStrip 的右值)。</summary>
    private const double BaseTabStripRight = 3;

    private bool IsAtWindowTopRight(FrameworkElement panel)
    {
        if (!panel.IsVisible || !panel.IsDescendantOf(this))
            return false;
        try
        {
            var origin = panel.TransformToAncestor(this).Transform(new Point(0, 0));
            var right = origin.X + panel.ActualWidth;
            return origin.Y <= ChromeBar.ActualHeight && right >= ActualWidth - ChromeBar.ActualWidth - 8;
        }
        catch (InvalidOperationException)
        {
            // 元素刚从可视树摘除(拖拽停靠中),下一轮布局会再算一次
            return false;
        }
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in FindDescendants<T>(child))
                yield return descendant;
        }
    }

    private static T? FindAncestor<T>(DependencyObject source, Func<T, bool>? predicate = null)
        where T : DependencyObject
    {
        for (DependencyObject? current = source; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match && (predicate == null || predicate(match)))
                return match;
        }

        return null;
    }

    // ---------------------------------------------------------------- 专注模式(UI-04)

    /// <summary>
    /// 页面最大化 = 专注态:窗格去标题条与页签并铺满,顶栏只保留主窗口控制组。
    /// 由 DockingHost.WindowsChanged 驱动 —— MaximizeWindow 与
    /// RestoreLayoutFromMaximized 都从那里出口,不需要新增公开 API。
    /// </summary>
}

