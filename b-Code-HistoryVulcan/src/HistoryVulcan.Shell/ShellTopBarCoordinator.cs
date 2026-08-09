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

/// <summary>
/// Keeps main-window chrome and page chrome on one input and command path.
/// Only AvalonDock public model and window APIs are used here.
/// </summary>
internal sealed partial class ShellTopBarCoordinator : IDisposable
{
    private const string ChromeLogSource = "shell.chrome";

    private readonly Window _window;
    private readonly DockingManager _manager;
    private readonly DockingHost _docking;
    private readonly CommandBus _bus;
    private readonly IShellLog _log;
    private readonly RoutedCommand _pageActionCommand;
    private readonly HashSet<FrameworkElement> _focusedTabs = [];
    private readonly HashSet<LayoutDocumentPaneControl> _documentPanes = [];
    private readonly List<(UIElement Target, CommandBinding Binding)> _pageActionBindings = [];
    private readonly FastDoubleClickGesture _doubleClick = new(TimeSpan.FromMilliseconds(250));
    private readonly DelayedDragGesture _hostDrag = new(TimeSpan.FromMilliseconds(120));
    private readonly DispatcherTimer _hostDragTimer;

    private FrameworkElement? _pendingTab;
    private string? _pendingPageId;
    private Point _pendingStart;
    private Point _pendingTabAnchor;
    private bool _pendingDrag;
    private Window? _pendingHostWindow;
    private FrameworkElement? _pendingHostSurface;
    private string? _pendingHostTarget;
    private bool _pendingHostWasMaximized;
    private bool _pendingHostRequiresHold;
    private string? _pendingDockTabTarget;
    private Point _pendingDockTabStart;
    private FrameworkElement? _pendingDockTab;
    private string? _pendingDockTabId;
    private Point _pendingDockTabAnchor;
    private FloatingDragContext? _pendingFloatingContext;
    private TaskCompletionSource<bool>? _pendingFloatingCompletion;
    private bool _disposed;

    public ShellTopBarCoordinator(
        Window window,
        DockingManager manager,
        DockingHost docking,
        CommandBus bus,
        IShellLog log,
        RoutedCommand pageActionCommand)
    {
        _window = window;
        _manager = manager;
        _docking = docking;
        _bus = bus;
        _log = log;
        _pageActionCommand = pageActionCommand;
        _hostDragTimer = new DispatcherTimer(DispatcherPriority.Input, _window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _hostDragTimer.Tick += OnHostDragHoldElapsed;

        _manager.LayoutFloatingWindowControlCreated += OnFloatingWindowCreated;
        _manager.AddHandler(
            UIElement.PreviewMouseMoveEvent,
            new MouseEventHandler(OnDockPreviewMouseMove),
            handledEventsToo: true);
        _manager.AddHandler(
            UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnDockPreviewMouseLeftButtonUp),
            handledEventsToo: true);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        ReleasePendingCapture();
        ReleaseHostWindowCapture();
        ClearPendingDockTab();
        if (_pendingFloatingContext is { } context)
            ClearPendingFloatingContext(context);

        _hostDragTimer.Tick -= OnHostDragHoldElapsed;
        _manager.LayoutFloatingWindowControlCreated -= OnFloatingWindowCreated;
        _manager.RemoveHandler(
            UIElement.PreviewMouseMoveEvent,
            new MouseEventHandler(OnDockPreviewMouseMove));
        _manager.RemoveHandler(
            UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnDockPreviewMouseLeftButtonUp));

        foreach (var tab in _focusedTabs.ToArray())
            DetachFocusedTab(tab);
        foreach (var floating in _manager.FloatingWindows.OfType<LayoutFloatingWindowControl>())
            floating.StateChanged -= OnFloatingWindowStateChanged;
        _documentPanes.Clear();
        foreach (var (target, binding) in _pageActionBindings)
            target.CommandBindings.Remove(binding);
        _pageActionBindings.Clear();
    }

    public void AttachPaneCommandBinding(UIElement pane)
    {
        AttachPageActionBinding(pane);
        if (pane is LayoutDocumentPaneControl documentPane)
        {
            _documentPanes.Add(documentPane);
            UpdateDocumentPaneChrome(documentPane);
        }
    }

    public void Refresh()
    {
        var tabs = _docking.MaximizedId == null
            ? []
            : FindVisualDescendants<FrameworkElement>(_manager)
                .Where(IsRealPageTab)
                .Where(tab => FindAncestor<FrameworkElement>(tab, item =>
                    Equals(item.Tag, "FocusedShellPaneHeader")) != null)
                .ToHashSet();

        foreach (var oldTab in _focusedTabs.ToArray())
        {
            if (tabs.Contains(oldTab))
                continue;
            DetachFocusedTab(oldTab);
        }

        foreach (var tab in tabs)
        {
            if (!_focusedTabs.Add(tab))
                continue;
            tab.PreviewMouseLeftButtonDown += OnFocusedTabMouseDown;
            tab.PreviewMouseMove += OnFocusedTabMouseMove;
            tab.PreviewMouseLeftButtonUp += OnFocusedTabMouseUp;
        }

        foreach (var pane in _documentPanes.ToArray())
        {
            if (!pane.IsLoaded)
            {
                _documentPanes.Remove(pane);
                continue;
            }
            UpdateDocumentPaneChrome(pane);
        }
    }

    public void HandlePaneMouseLeftButtonDown(object? sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            IsInteractive(e.OriginalSource as DependencyObject) ||
            sender is not FrameworkElement pane ||
            !IsPaneHeaderSource(e.OriginalSource as DependencyObject) ||
            !TryResolvePageId(pane, out var id))
        {
            return;
        }

        if (FindFloatingWindow(id) is { } floating)
        {
            BeginHostWindowGesture(floating, pane, $"floating:{id}", e);
            return;
        }

        BeginHostWindowGesture(_window, pane, $"header:{id}", e);
    }

    public void HandlePaneMouseMove(object? sender, MouseEventArgs e)
        => ContinueHostWindowGesture(sender, e);

    public void HandlePaneMouseLeftButtonUp(object? sender, MouseButtonEventArgs e)
        => CompleteHostWindowGesture(sender);

    public void HandlePaneLostMouseCapture(object? sender, MouseEventArgs e)
        => ClearHostWindowGesture(sender);

    public void HandleMainMouseLeftButtonDown(object? sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            IsInteractive(e.OriginalSource as DependencyObject) ||
            sender is not FrameworkElement surface)
        {
            return;
        }

        var target = TryResolvePageId(surface, out var id) ? $"header:{id}" : "header:main";
        BeginHostWindowGesture(_window, surface, target, e);
    }

    public void HandleMainMouseMove(object? sender, MouseEventArgs e)
        => ContinueHostWindowGesture(sender, e);

    public void HandleMainMouseLeftButtonUp(object? sender, MouseButtonEventArgs e)
        => CompleteHostWindowGesture(sender);

    public void HandleMainLostMouseCapture(object? sender, MouseEventArgs e)
        => ClearHostWindowGesture(sender);

    public bool HandleDockTabMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (e.ChangedButton != MouseButton.Left ||
            IsInteractiveCommandControl(source) ||
            FindAncestor<LayoutFloatingWindowControl>(source) != null ||
            !TryResolveTabPageId(source, out var id) ||
            FindAncestor<FrameworkElement>(source, IsRealPageTab) is not { } tab)
        {
            ClearPendingDockTab();
            return false;
        }

        var target = $"tab:{id}";
        var position = e.GetPosition(_manager);
        var range = GetSystemDoubleClickRange(_manager);
        if (!_doubleClick.RegisterPress(
                target,
                Environment.TickCount64,
                _manager.PointToScreen(position),
                range.Width,
                range.Height))
        {
            _pendingDockTabTarget = target;
            _pendingDockTabStart = position;
            _pendingDockTab = tab;
            _pendingDockTabId = id;
            _pendingDockTabAnchor = e.GetPosition(tab);
            return false;
        }

        ClearPendingDockTab();
        _ = _bus.ExecuteAsync(
            _docking.MaximizedId?.Equals(id, StringComparison.OrdinalIgnoreCase) == true
                ? "vulcan.ui.restore"
                : $"vulcan.ui.max name={CommandParser.QuoteArg(id)}",
            "UI");
        e.Handled = true;
        return true;
    }

    internal static bool HasReachedDragThreshold(
        Point start,
        Point current,
        double horizontalThreshold,
        double verticalThreshold,
        double multiplier = 1)
        => Math.Abs(current.X - start.X) >= horizontalThreshold * multiplier ||
           Math.Abs(current.Y - start.Y) >= verticalThreshold * multiplier;

    internal CommandResult SetFloatingWindowState(string id, string state)
    {
        var floating = FindFloatingWindow(id);
        if (floating == null)
            return CommandResult.Fail($"页面 {id} 没有对应的独立浮窗宿主");

        var targetState = state.ToLowerInvariant() switch
        {
            "maximized" => (WindowState?)WindowState.Maximized,
            "normal" => WindowState.Normal,
            "toggle" => GetToggledWindowState(floating.WindowState),
            _ => null,
        };
        if (targetState == null)
            return CommandResult.Fail($"不支持的浮窗状态: {state}");
        if (targetState == WindowState.Maximized)
            SystemCommands.MaximizeWindow(floating);
        else
            SystemCommands.RestoreWindow(floating);
        var label = targetState == WindowState.Maximized ? "最大化" : "普通";
        return CommandResult.Ok($"{id} 独立浮窗已切换为{label}状态");
    }

    public bool TryResolvePageId(DependencyObject? source, out string id)
    {
        for (var current = source; current != null; current = GetParent(current))
        {
            if (TryGetTabModel(current, out var tabModel) && TryGetContentId(tabModel, out id))
                return true;

            if (current is TabItem { DataContext: LayoutContent outerModel } &&
                TryGetContentId(outerModel, out id))
            {
                return true;
            }

            if (current is AnchorablePaneTitle { Model: LayoutContent titleModel } &&
                TryGetContentId(titleModel, out id))
            {
                return true;
            }

            if (current is LayoutAnchorablePaneControl anchorablePane &&
                TryResolveSelectedItem(anchorablePane.SelectedItem, out id))
            {
                return true;
            }

            if (current is LayoutDocumentPaneControl documentPane &&
                TryResolveSelectedItem(documentPane.SelectedItem, out id))
            {
                return true;
            }
        }

        id = string.Empty;
        return false;
    }

    private void AttachPageActionBinding(UIElement target)
    {
        if (target.CommandBindings.OfType<CommandBinding>().Any(binding =>
                ReferenceEquals(binding.Command, _pageActionCommand)))
        {
            return;
        }

        var binding = CreatePageActionBinding();
        target.CommandBindings.Add(binding);
        _pageActionBindings.Add((target, binding));
    }

    private CommandBinding CreatePageActionBinding()
        => new(
            _pageActionCommand,
            OnPageActionExecuted,
            (_, e) => e.CanExecute = e.Parameter is string);

    private async void OnPageActionExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        var action = e.Parameter as string;
        if (action == null)
            return;

        string command;
        if (action.Equals("restore", StringComparison.OrdinalIgnoreCase))
        {
            command = "vulcan.ui.restore";
        }
        else
        {
            if (!TryResolvePageId(e.OriginalSource as DependencyObject, out var id) &&
                !TryResolvePageId(e.Source as DependencyObject, out id))
            {
                _log.Error(ChromeLogSource, $"页面动作 {action} 无法解析页面 ID");
                return;
            }

            var quotedId = CommandParser.QuoteArg(id);
            if (action.Equals("toggle-floating", StringComparison.OrdinalIgnoreCase))
                command = $"vulcan.ui.floatstate name={quotedId} state=toggle";
            else
                command = action.ToLowerInvariant() switch
                {
                    "float" => $"vulcan.ui.float name={quotedId}",
                    "hide" => $"vulcan.ui.hide name={quotedId}",
                    "dock-document" => $"vulcan.ui.dock name={quotedId} pos=center",
                    "autohide" => $"vulcan.ui.autohide name={quotedId}",
                    _ => string.Empty,
                };
        }

        if (string.IsNullOrEmpty(command))
        {
            _log.Error(ChromeLogSource, $"未知页面动作：{action}");
            return;
        }

        var result = await _bus.ExecuteAsync(command, "UI").ConfigureAwait(true);
        if (!result.Success)
            _log.Error(ChromeLogSource, $"页面动作执行失败：{result.Message}");
        e.Handled = true;
    }

    private void OnFocusedTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            IsInteractiveCommandControl(e.OriginalSource as DependencyObject) ||
            !TryResolveTabPageId((DependencyObject)sender, out var id))
        {
            return;
        }

        _pendingTab = (FrameworkElement)sender;
        _pendingPageId = id;
        _pendingStart = e.GetPosition(_pendingTab);
        _pendingTabAnchor = _pendingStart;
        _pendingDrag = false;
        _pendingTab.CaptureMouse();
        e.Handled = true;
    }

    private void OnFocusedTabMouseMove(object sender, MouseEventArgs e)
    {
        if (_pendingTab == null || !ReferenceEquals(sender, _pendingTab) ||
            _pendingPageId == null || e.LeftButton != MouseButtonState.Pressed || _pendingDrag)
        {
            return;
        }

        var current = e.GetPosition(_pendingTab);
        if (Math.Abs(current.X - _pendingStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _pendingStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _pendingDrag = true;
        var id = _pendingPageId;
        var context = new FloatingDragContext(
            id,
            default,
            _pendingTabAnchor,
            ContinueWithDrag: true);
        _doubleClick.Cancel($"tab:{id}");
        ReleasePendingCapture();
        _ = RestoreAndFloatAsync(context);
        e.Handled = true;
    }

    private void OnFocusedTabMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(sender, _pendingTab))
            ReleasePendingCapture();
    }

    private async Task RestoreAndFloatAsync(FloatingDragContext context)
    {
        var id = context.PageId;
        if (_docking.MaximizedId != null)
        {
            var restored = await _bus.ExecuteAsync("vulcan.ui.restore", "UI").ConfigureAwait(true);
            if (!restored.Success)
            {
                _log.Error(ChromeLogSource, $"恢复专注布局失败：{restored.Message}");
                return;
            }

            await _window.Dispatcher.InvokeAsync(
                _window.UpdateLayout,
                DispatcherPriority.Loaded);
        }

        context = context with { EmbeddedSize = ResolveEmbeddedPaneSize(id) };
        ApplyFloatingModelGeometry(context, FindLayoutContent(context.PageId));
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        SetPendingFloatingContext(context, completion);
        var floated = await _bus.ExecuteAsync(
            $"vulcan.ui.float name={CommandParser.QuoteArg(id)}", "UI").ConfigureAwait(true);
        if (!floated.Success)
        {
            ClearPendingFloatingContext(context);
            _log.Error(ChromeLogSource, $"拖出页面失败：{floated.Message}");
            return;
        }

        var completed = await Task.WhenAny(
            completion.Task,
            Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(true);
        if (!ReferenceEquals(completed, completion.Task))
        {
            ClearPendingFloatingContext(context);
            _log.Error(ChromeLogSource, $"拖出页面失败：未创建页面 {id} 的浮窗宿主");
        }
    }

    private void OnFloatingWindowCreated(object? sender, LayoutFloatingWindowControlCreatedEventArgs e)
    {
        var created = e.LayoutFloatingWindowControl;
        created.StateChanged += OnFloatingWindowStateChanged;
        ApplyFloatingWindowStateChrome(created);
        _ = _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Refresh);

        if (_pendingFloatingContext is not { } context ||
            !ModelContainsPage(created.Model, context.PageId))
            return;

        ClearPendingFloatingContext(context, completed: true);
        ApplyFloatingWindowGeometry(created, context);
        if (context.ContinueWithDrag)
            StartFloatingDrag(created);
    }

    private LayoutFloatingWindowControl? FindFloatingWindow(string id)
        => _manager.FloatingWindows
            .OfType<LayoutFloatingWindowControl>()
            .FirstOrDefault(window => ModelContainsPage(window.Model, id));

    private void StartFloatingDrag(LayoutFloatingWindowControl floating)
        => _window.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (Mouse.LeftButton != MouseButtonState.Pressed)
                return;
            try
            {
                floating.DragMove();
            }
            catch (InvalidOperationException ex)
            {
                _log.Warn(ChromeLogSource, $"浮窗拖动未启动：{ex.Message}");
            }
        });

    private void OnFloatingWindowStateChanged(object? sender, EventArgs e)
    {
        if (sender is LayoutFloatingWindowControl floating)
        {
            ApplyFloatingWindowStateChrome(floating);
            foreach (var pane in _documentPanes)
            {
                if (pane.Model is LayoutDocumentPane { SelectedContent: LayoutContent selected } &&
                    ModelContainsPage(floating.Model, selected.ContentId ?? string.Empty))
                {
                    UpdateDocumentPaneChrome(pane);
                }
            }
        }
    }

    private static void ApplyFloatingWindowStateChrome(LayoutFloatingWindowControl floating)
        => floating.Padding = floating.WindowState == WindowState.Maximized
            ? SystemParameters.WindowResizeBorderThickness
            : default;

    private void BeginHostWindowGesture(
        Window hostWindow,
        FrameworkElement surface,
        string target,
        MouseButtonEventArgs e)
    {
        ReleaseHostWindowCapture();
        var range = GetSystemDoubleClickRange(surface);
        if (_doubleClick.RegisterPress(
                target,
                Environment.TickCount64,
                GetScreenPoint(surface, e),
                range.Width,
                range.Height))
        {
            _hostDrag.Cancel();
            if (ReferenceEquals(hostWindow, _window))
            {
                _ = _bus.ExecuteAsync("vulcan.app.window state=toggle", "UI");
            }
            else if (target.StartsWith("floating:", StringComparison.Ordinal))
            {
                var id = target["floating:".Length..];
                _ = _bus.ExecuteAsync(
                    $"vulcan.ui.floatstate name={CommandParser.QuoteArg(id)} state=toggle", "UI");
            }
            e.Handled = true;
            return;
        }

        _pendingHostWindow = hostWindow;
        _pendingHostSurface = surface;
        _pendingHostTarget = target;
        _pendingHostWasMaximized = hostWindow.WindowState == WindowState.Maximized;
        _pendingHostRequiresHold = ShouldDelayHostDrag(
            ReferenceEquals(hostWindow, _window),
            hostWindow.WindowState);
        var multiplier = _pendingHostWasMaximized ? 2d : 1d;
        _hostDrag.Begin(
            Environment.TickCount64,
            e.GetPosition(surface),
            SystemParameters.MinimumHorizontalDragDistance * multiplier,
            SystemParameters.MinimumVerticalDragDistance * multiplier);
        surface.CaptureMouse();
        _hostDragTimer.Stop();
        if (_pendingHostRequiresHold)
            _hostDragTimer.Start();
        e.Handled = true;
    }

    private void ContinueHostWindowGesture(object? sender, MouseEventArgs e)
    {
        if (_pendingHostSurface == null ||
            !ReferenceEquals(sender, _pendingHostSurface) ||
            _pendingHostTarget == null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ReleaseHostWindowCapture();
            return;
        }

        var current = e.GetPosition(_pendingHostSurface);
        var shouldStart = _hostDrag.Update(Environment.TickCount64, current);
        if (!_pendingHostRequiresHold && _hostDrag.HasReachedThreshold)
            shouldStart = true;
        if (_hostDrag.HasReachedThreshold)
            _doubleClick.Cancel(_pendingHostTarget);
        if (shouldStart)
        {
            StartPendingHostDrag(current);
            e.Handled = true;
        }
    }

    private void CompleteHostWindowGesture(object? sender)
    {
        if (ReferenceEquals(sender, _pendingHostSurface))
            ReleaseHostWindowCapture();
    }

    private void ClearHostWindowGesture(object? sender)
    {
        if (ReferenceEquals(sender, _pendingHostSurface))
            ClearHostWindowGesture();
    }

    private void OnHostDragHoldElapsed(object? sender, EventArgs e)
    {
        _hostDragTimer.Stop();
        if (_pendingHostSurface == null || Mouse.LeftButton != MouseButtonState.Pressed)
        {
            ReleaseHostWindowCapture();
            return;
        }

        var current = Mouse.GetPosition(_pendingHostSurface);
        _hostDrag.Update(Environment.TickCount64, current);
        if (_hostDrag.HasReachedThreshold && _pendingHostTarget != null)
            _doubleClick.Cancel(_pendingHostTarget);
        if (_hostDrag.TryActivate(Environment.TickCount64))
            StartPendingHostDrag(current);
    }

    private void StartPendingHostDrag(Point current)
    {
        if (_pendingHostWindow == null ||
            _pendingHostSurface == null ||
            _pendingHostTarget == null)
        {
            return;
        }

        var hostWindow = _pendingHostWindow;
        var surface = _pendingHostSurface;
        var target = _pendingHostTarget;
        var wasMaximized = _pendingHostWasMaximized;
        var pointerPixels = surface.PointToScreen(current);
        _doubleClick.Cancel(target);
        ReleaseHostWindowCapture();

        if (wasMaximized)
        {
            RestoreHostWindowUnderPointer(hostWindow, surface, current, pointerPixels);
            return;
        }

        TryDragWindow(hostWindow);
    }

    private void OnDockPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pendingDockTabTarget == null ||
            _pendingDockTab == null ||
            _pendingDockTabId == null ||
            e.LeftButton != MouseButtonState.Pressed)
            return;

        var current = e.GetPosition(_manager);
        if (!HasReachedDragThreshold(
                _pendingDockTabStart,
                current,
                SystemParameters.MinimumHorizontalDragDistance,
                SystemParameters.MinimumVerticalDragDistance))
        {
            return;
        }

        _doubleClick.Cancel(_pendingDockTabTarget);
        var context = new FloatingDragContext(
            _pendingDockTabId,
            ResolveEmbeddedPaneSize(_pendingDockTab),
            _pendingDockTabAnchor,
            ContinueWithDrag: false);
        ApplyFloatingModelGeometry(context, _pendingDockTab);
        SetPendingFloatingContext(context);
        ClearPendingDockTab();
    }

    private void OnDockPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ClearPendingDockTab();
        if (_pendingFloatingContext is { ContinueWithDrag: false } context)
            _ = ExpirePendingFloatingContextAsync(context);
    }

    private static Point GetScreenPoint(FrameworkElement surface, MouseButtonEventArgs e)
        => surface.PointToScreen(e.GetPosition(surface));

    private static Size GetSystemDoubleClickRange(Visual visual)
    {
        var dpi = (uint)Math.Round(96 * VisualTreeHelper.GetDpi(visual).DpiScaleX);
        return new Size(
            Math.Max(1, NativeMethods.GetSystemMetricsForDpi(36, dpi)),
            Math.Max(1, NativeMethods.GetSystemMetricsForDpi(37, dpi)));
    }

    private void RestoreHostWindowUnderPointer(
        Window hostWindow,
        FrameworkElement surface,
        Point local,
        Point pointerPixels)
    {
        var restore = hostWindow.RestoreBounds;
        var width = double.IsFinite(restore.Width) && restore.Width > 0 ? restore.Width : hostWindow.Width;
        var height = double.IsFinite(restore.Height) && restore.Height > 0 ? restore.Height : hostWindow.Height;
        var fraction = surface.ActualWidth > 0
            ? Math.Clamp(local.X / surface.ActualWidth, 0, 1)
            : 0.5;

        try
        {
            FloatingWindowGeometry.PlaceWindow(
                hostWindow,
                pointerPixels,
                new Size(width, height),
                new Point(width * fraction, Math.Min(local.Y, 24)));
            hostWindow.Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                () => TryDragWindow(hostWindow));
        }
        catch (InvalidOperationException ex)
        {
            _log.Warn(ChromeLogSource, $"窗口最大化下拖恢复失败：{ex.Message}");
        }
    }

    private void TryDragWindow(Window hostWindow)
    {
        try
        {
            hostWindow.Activate();
            hostWindow.DragMove();
        }
        catch (InvalidOperationException ex)
        {
            _log.Warn(ChromeLogSource, $"窗口拖动未启动：{ex.Message}");
        }
    }

    internal static WindowState GetToggledWindowState(WindowState state)
        => state == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    internal static bool ShouldDelayHostDrag(bool isMainWindow, WindowState state)
        => !isMainWindow || state == WindowState.Maximized;

    private void DetachFocusedTab(FrameworkElement tab)
    {
        tab.PreviewMouseLeftButtonDown -= OnFocusedTabMouseDown;
        tab.PreviewMouseMove -= OnFocusedTabMouseMove;
        tab.PreviewMouseLeftButtonUp -= OnFocusedTabMouseUp;
        _focusedTabs.Remove(tab);
    }

    private void ReleasePendingCapture()
    {
        _pendingTab?.ReleaseMouseCapture();
        _pendingTab = null;
        _pendingPageId = null;
        _pendingTabAnchor = default;
        _pendingDrag = false;
    }

    private void ReleaseHostWindowCapture()
    {
        var surface = _pendingHostSurface;
        ClearHostWindowGesture();
        if (surface?.IsMouseCaptured == true)
            surface.ReleaseMouseCapture();
    }

    private void ClearHostWindowGesture()
    {
        _hostDragTimer.Stop();
        _hostDrag.Cancel();
        _pendingHostWindow = null;
        _pendingHostSurface = null;
        _pendingHostTarget = null;
        _pendingHostWasMaximized = false;
        _pendingHostRequiresHold = false;
    }

    private void ClearPendingDockTab()
    {
        _pendingDockTabTarget = null;
        _pendingDockTabStart = default;
        _pendingDockTab = null;
        _pendingDockTabId = null;
        _pendingDockTabAnchor = default;
    }

    private void ClearPendingFloatingContext(
        FloatingDragContext context,
        bool completed = false)
    {
        if (_pendingFloatingContext != context)
            return;

        _pendingFloatingContext = null;
        var completion = _pendingFloatingCompletion;
        _pendingFloatingCompletion = null;
        if (completed)
            completion?.TrySetResult(true);
        else
            completion?.TrySetCanceled();
    }

    private void SetPendingFloatingContext(
        FloatingDragContext context,
        TaskCompletionSource<bool>? completion = null)
    {
        _pendingFloatingCompletion?.TrySetCanceled();
        _pendingFloatingContext = context;
        _pendingFloatingCompletion = completion;
    }

    private async Task ExpirePendingFloatingContextAsync(FloatingDragContext context)
    {
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        if (_disposed || _window.Dispatcher.HasShutdownStarted)
            return;

        await _window.Dispatcher.InvokeAsync(() =>
        {
            if (_pendingFloatingContext == context && FindFloatingWindow(context.PageId) == null)
                ClearPendingFloatingContext(context);
        }, DispatcherPriority.Background);
    }

    private Size ResolveEmbeddedPaneSize(string id)
    {
        var tab = FindVisualDescendants<FrameworkElement>(_manager)
            .Where(IsRealPageTab)
            .FirstOrDefault(candidate =>
                TryResolveTabPageId(candidate, out var candidateId) &&
                candidateId.Equals(id, StringComparison.OrdinalIgnoreCase));
        return tab == null ? default : ResolveEmbeddedPaneSize(tab);
    }

    private static Size ResolveEmbeddedPaneSize(FrameworkElement tab)
    {
        var pane = FindAncestor<FrameworkElement>(tab, element =>
            element is LayoutAnchorablePaneControl or LayoutDocumentPaneControl);
        if (pane == null ||
            !double.IsFinite(pane.ActualWidth) || pane.ActualWidth <= 0 ||
            !double.IsFinite(pane.ActualHeight) || pane.ActualHeight <= 0)
        {
            return default;
        }

        return new Size(pane.ActualWidth, pane.ActualHeight);
    }

    private void ApplyFloatingModelGeometry(FloatingDragContext context, DependencyObject source)
    {
        if (!TryResolveTabPageId(source, out var id))
            id = context.PageId;
        ApplyFloatingModelGeometry(context, FindLayoutContent(id));
    }

    private static void ApplyFloatingModelGeometry(
        FloatingDragContext context,
        LayoutContent? content)
    {
        if (content == null)
            return;

        var size = NormalizeEmbeddedSize(context.EmbeddedSize);
        var pointer = FloatingWindowGeometry.GetCursorPosition();
        content.FloatingWidth = size.Width;
        content.FloatingHeight = size.Height;
        content.FloatingLeft = pointer.X - context.AnchorOffset.X;
        content.FloatingTop = pointer.Y - context.AnchorOffset.Y;
    }

}

internal sealed class FastDoubleClickGesture(TimeSpan interval)
{
    private readonly long _intervalMilliseconds = checked((long)interval.TotalMilliseconds);
    private string? _target;
    private long _timestamp;
    private Point _position;

    public bool RegisterPress(
        string target,
        long timestamp,
        Point position,
        double horizontalRange,
        double verticalRange)
    {
        var matched = _target != null &&
                      string.Equals(_target, target, StringComparison.Ordinal) &&
                      timestamp >= _timestamp &&
                      timestamp - _timestamp <= _intervalMilliseconds &&
                      Math.Abs(position.X - _position.X) <= horizontalRange &&
                      Math.Abs(position.Y - _position.Y) <= verticalRange;

        if (matched)
        {
            Reset();
            return true;
        }

        _target = target;
        _timestamp = timestamp;
        _position = position;
        return false;
    }

    public void Cancel(string target)
    {
        if (string.Equals(_target, target, StringComparison.Ordinal))
            Reset();
    }

    private void Reset()
    {
        _target = null;
        _timestamp = 0;
        _position = default;
    }
}
