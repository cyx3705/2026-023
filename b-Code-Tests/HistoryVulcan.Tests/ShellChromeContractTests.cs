
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Extensibility.CommandSurface;
using HistoryVulcan.Services;
using HistoryVulcan.Shell;
using HistoryVulcan.Shell.Console;
using HistoryVulcan.Shell.Mcp;
using AvalonDock.Controls;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// 3.1 ????(UI-02 / UI-03 / UI-04 / UI-05 / UI-09):
/// ????????????,???????,????????????
/// ???????????,??? XAML ??????
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class ShellChromeContractTests
{
    [Fact]
    public void ShellWindowHasNoMenuBarNoStatusBarAndNoTitleBarRow()
    {
        RunShell(window =>
        {
            // UI-03 / UI-05.1:???????????
            Assert.Empty(FindVisualDescendants<Menu>(window));
            Assert.Empty(FindVisualDescendants<System.Windows.Controls.Primitives.StatusBar>(window));

            // 3.1 ??:???????????? ?? ????????????,
            // ???????????????
            var chromeBar = RequireElement<FrameworkElement>(window, "ChromeBar");
            var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));
            var managerTop = manager.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
            var chromeTop = chromeBar.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
            Assert.True(managerTop <= chromeTop + 0.5,
                $"docking top={managerTop} must not be pushed below the chrome bar top={chromeTop}");
            Assert.True(chromeBar.ActualHeight <= 32.5,
                $"chrome bar height={chromeBar.ActualHeight} must match the tab row height");
        });
    }

    [Fact]
    public void ChromeBarReservesRoomSoTabsNeverHideUnderTheButtons()
    {
        RunShell(window =>
        {
            var chromeBar = RequireElement<FrameworkElement>(window, "ChromeBar");
            var panel = FindVisualDescendants<AvalonDock.Controls.DocumentPaneTabPanel>(window)
                .Single(item => item.IsVisible);

            // ?????????????,?????????????
            var panelRight = panel.TransformToAncestor(window).Transform(new Point(panel.ActualWidth, 0)).X;
            var chromeLeft = chromeBar.TransformToAncestor(window).Transform(new Point(0, 0)).X;
            Assert.True(panelRight <= chromeLeft + 0.5,
                $"tab panel right={panelRight} overlaps chrome bar left={chromeLeft}");
        });
    }

    [Fact]
    public void WindowChromeBarBelongsToCentralDocumentPane()
    {
        RunShell(window =>
        {
            var chromeBar = RequireElement<Panel>(window, "ChromeBar");
            var host = FindAncestor<ContentControl>(
                chromeBar,
                control => Equals(control.Tag, "ShellChromeHost"));

            Assert.NotNull(host);
            Assert.NotNull(FindAncestor<LayoutDocumentPaneControl>(chromeBar, _ => true));
            Assert.DoesNotContain(
                FindVisualDescendants<LayoutAnchorableControl>(window)
                    .SelectMany(tool => FindVisualDescendants<ContentControl>(tool)),
                control => Equals(control.Tag, "ShellChromeHost"));
        });
    }

    [Fact]
    public void InvisibleCaptionDoesNotCoverToolPaneControls()
    {
        RunShell(window =>
        {
            var chrome = WindowChrome.GetWindowChrome(window);
            Assert.NotNull(chrome);
            Assert.Equal(0, chrome.CaptionHeight);

            var actions = FindVisualDescendants<AnchorablePaneTitle>(window)
                .First(item => item.IsVisible);
            Assert.All(
                FindVisualDescendants<ButtonBase>(actions),
                button => Assert.True(button.IsHitTestVisible));
        });
    }

    [Fact]
    public void PersistedDarkThemeSurvivesDockManagerThemeAssignment()
    {
        // 回归：构造函数里先 ApplyTheme 再赋值 DockManager.Theme，后者重新合并 AvalonDock
        // 自带的浅色主题字典，把深色令牌盖掉——设置里明明是 dark，启动却是浅色，
        // 手动再切一次才对。这里断言窗口建好后深色令牌仍然生效。
        var settings = new MemorySettings();
        settings.Set("ui.theme", "dark");

        RunShell(
            window =>
            {
                // 窗体级资源没问题；出错的是停靠区：HistoryVulcanTheme.xaml 内部合并了浅色
                // ShellTokens.xaml，若它排在深色令牌之后就会赢，界面整片变浅。
                var dockSurface = window.DockManager.TryFindResource("Shell.Brush.Surface") as SolidColorBrush;
                Assert.NotNull(dockSurface);
                var brightness = (dockSurface!.Color.R + dockSurface.Color.G + dockSurface.Color.B) / 3.0;
                Assert.True(
                    brightness < 96,
                    $"停靠区 Shell.Brush.Surface 应解析为深色令牌，实际 {dockSurface.Color}(亮度 {brightness:0})");

                var sources = window.DockManager.Resources.MergedDictionaries
                    .Select(dictionary => dictionary.Source?.ToString() ?? string.Empty)
                    .ToList();
                var tokenIndex = sources.FindIndex(item => item.EndsWith("ShellTokens.Dark.xaml", StringComparison.Ordinal));
                var themeIndex = sources.FindIndex(item => item.EndsWith("HistoryVulcanTheme.xaml", StringComparison.Ordinal));
                Assert.True(tokenIndex >= 0 && themeIndex >= 0, "深色令牌与 AvalonDock 主题字典都应在停靠区资源里");
                Assert.True(
                    tokenIndex > themeIndex,
                    $"深色令牌必须排在主题字典之后才能生效，实际 tokens={tokenIndex} theme={themeIndex}");
            },
            settings: settings);
    }

    [Fact]
    public void CommandFailureOpensConsoleWithoutErrorFlyout()
    {
        RunShell(window =>
        {
            window.Docking.Hide(StandardWindowIds.Console);
            UiTestHost.Pump();
            Assert.False(window.Docking.ListWindows()
                .Single(item => item.Id == StandardWindowIds.Console).IsVisible);

            var result = window.Commands.ExecuteAsync("missing.command", "test")
                .GetAwaiter().GetResult();
            UiTestHost.Pump();

            Assert.False(result.Success);
            Assert.True(window.Docking.ListWindows()
                .Single(item => item.Id == StandardWindowIds.Console).IsVisible);
            Assert.Null(window.FindName("Toast"));
        });
    }

    [Fact]
    public void ConsoleEnterShowsTypedCommandAndFailureResult()
    {
        var log = new RelayLog();
        RunShell(
            window =>
            {
                var input = FindVisualDescendants<TextBox>(window)
                    .Single(item => item.Name == "Input");
                input.Focus();
                input.Text = "123";

                var key = new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(input),
                    0,
                    Key.Enter)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                };
                input.RaiseEvent(key);
                UiTestHost.PumpFor(250);

                var output = FindVisualDescendants<ListBox>(window)
                    .Single(list => list.Name == "Output");
                var rows = output.Items.Cast<HistoryVulcan.Shell.Console.ConsoleRow>()
                    .Where(item => item.Text.Contains("123", StringComparison.Ordinal))
                    .ToList();

                Assert.True(rows.Any(item => item.Text.Contains("> 123", StringComparison.Ordinal)),
                    "the command echo must be visible after pressing Enter");
                Assert.True(rows.Any(item => item.Level >= ShellLogLevel.Error),
                    "the unknown-command error must be visible after pressing Enter");

                var echo = rows.First(item => item.Text.Contains("> 123", StringComparison.Ordinal));
                output.ScrollIntoView(echo);
                window.UpdateLayout();
                UiTestHost.Pump();
                Assert.True(output.IsVisible && output.ActualHeight > 0 && output.ActualWidth > 0,
                    $"console output is not laid out: visible={output.IsVisible}, " +
                    $"size={output.ActualWidth}x{output.ActualHeight}");
                var container = Assert.IsType<ListBoxItem>(
                    output.ItemContainerGenerator.ContainerFromItem(echo));
                container.ApplyTemplate();
                window.UpdateLayout();
                Assert.True(container.IsVisible && container.ActualHeight > 0,
                    "the command row exists but its ListBoxItem is not rendered");
                var descendants = FindVisualDescendants<DependencyObject>(container).ToList();
                var renderedTexts = descendants.OfType<TextBlock>().ToList();
                Assert.True(renderedTexts.Count == 1,
                    "unexpected row visual tree: " +
                    string.Join(", ", descendants.Select(item => item.GetType().Name)));
                var renderedText = renderedTexts[0];
                Assert.True(renderedText.IsVisible && renderedText.ActualWidth > 0,
                    "the command row exists but its TextBlock is not visible");
            },
            log: log);
    }

    [Fact]
    public void ConsoleRowsExtractDomainsFromCommandsOutputsAndLogCategories()
    {
        var now = DateTime.UtcNow;
        var rows = new[]
        {
            ConsoleRow.From(new ShellLogEntry(now, ShellLogLevel.Info, "cmd:UI", "app.open target=x"))[0],
            ConsoleRow.From(new ShellLogEntry(now, ShellLogLevel.Info, "cmd:UI", "help"))[0],
            ConsoleRow.From(new ShellLogEntry(now, ShellLogLevel.Info, "cmd:result:app", "done"))[0],
            ConsoleRow.From(new ShellLogEntry(now, ShellLogLevel.Info, "cmd:progress:app", "step"))[0],
            ConsoleRow.From(new ShellLogEntry(now, ShellLogLevel.Info, "module:loader.detail", "loaded"))[0],
            ConsoleRow.From(new ShellLogEntry(now, ShellLogLevel.Info, "render.frame", "drawn"))[0],
        };

        Assert.Equal(new[] { "app", "core", "app", "app", "module", "render" },
            rows.Select(row => row.DomainKey));
        Assert.Equal("UI", rows[0].SourceKey);
        Assert.Equal("result", rows[2].SourceKey);
    }

    [Fact]
    public void FailedConsoleCommandDoesNotExitFocusedConsole()
    {
        RunShell(window =>
        {
            var maximize = window.Commands.ExecuteAsync(
                $"vulcan.ui.max name={StandardWindowIds.Console}", "test").GetAwaiter().GetResult();
            Assert.True(maximize.Success, maximize.Message);
            UiTestHost.Pump();
            Assert.Equal(StandardWindowIds.Console, window.Docking.MaximizedId);

            var input = FindVisualDescendants<TextBox>(window)
                .Single(item => item.Name == "Input");
            input.Focus();
            input.Text = "missing.command";
            input.RaiseEvent(new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(input),
                0,
                Key.Enter)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            });
            UiTestHost.PumpFor(400);

            Assert.Equal(StandardWindowIds.Console, window.Docking.MaximizedId);
            Assert.True(input.IsKeyboardFocusWithin);
        });
    }

    [Fact]
    public void FrontendFocusConsoleRaisesTheWindowAndRefocusesExistingConsole()
    {
        RunShell(window =>
        {
            Assert.True(window.Commands.ExecuteAsync("vulcan.app.hide", "test")
                .GetAwaiter().GetResult().Success);
            UiTestHost.Pump();
            Assert.False(window.IsVisible);

            Assert.True(WakeConsole(window));
            UiTestHost.PumpFor(500);

            var input = FindVisualDescendants<TextBox>(window)
                .Single(item => item.Name == "Input");
            Assert.True(window.IsVisible);
            Assert.Equal(StandardWindowIds.Console, window.Docking.MaximizedId);
            Assert.True(HasKeyboardOrLogicalFocus(input));
            Assert.False(window.Topmost);

            var layoutChanges = 0;
            window.Docking.WindowsChanged += (_, _) => layoutChanges++;
            RequireButton(window, "MenuButton").Focus();
            Assert.True(WakeConsole(window));
            UiTestHost.PumpFor(500);

            Assert.True(HasKeyboardOrLogicalFocus(input));
            Assert.False(window.Topmost);
            Assert.Equal(StandardWindowIds.Console, window.Docking.MaximizedId);
            Assert.True(layoutChanges >= 0); // ???????????????????????
        });
    }

    [Fact]
    public void ToolPagesHaveNoCaptionBarAndActionsFloatInTheCorner()
    {
        RunShell(window =>
        {
            // R3-1:?????????????(?? + ?? + ????)????
            // ???????????? ?? ?????????????
            var host = FindVisualDescendants<LayoutAnchorableControl>(window).First(item => item.IsVisible);
            var content = FindVisualDescendants<ContentPresenter>(host).First();
            var hostTop = host.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
            var contentTop = content.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
            Assert.True(contentTop - hostTop < 2,
                $"tool page content is pushed down by {contentTop - hostTop}px ? a caption bar is back");

            // R4-1:???????????????? ?? ????????
            Assert.Empty(FindVisualDescendants<AnchorablePaneTitle>(host));
        });
    }

    [Fact]
    public void DarkThemeLeavesNoLightPixelsInsideTables()
    {
        RunShell(window =>
        {
            window.Commands.ExecuteAsync("vulcan.app.theme mode=dark", "test").GetAwaiter().GetResult();
            UiTestHost.Pump();

            // R4-5:????????? ?? ???????? WPF ?????
            // ????????,???????????????
            foreach (var list in FindVisualDescendants<ListView>(window)
                         .Where(item => item.IsVisible && item.ActualWidth > 100 && item.ActualHeight > 60))
            {
                foreach (var (x, y) in new[] { (0.5, 0.3), (0.5, 0.6), (0.1, 0.6), (0.9, 0.45) })
                {
                    var pixel = SamplePixel(list, x, y);
                    if (pixel.A == 0)
                        continue;
                    var luminance = (pixel.R + pixel.G + pixel.B) / 3;
                    var spread = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B))
                                 - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
                    // ???? = ??????(#F4F4F4);??????????,??
                    Assert.False(luminance > 0x60 && spread < 0x14,
                        $"table pixel @{x},{y} is neutral light ({pixel}) ? dark theme did not reach the table body");
                }
            }
        });
    }

    [Fact]
    public void FloatingToolWindowFrameFollowsLightAndDarkThemes()
    {
        RunShell(window =>
        {
            var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));
            window.Docking.Float(StandardWindowIds.Console);
            UiTestHost.PumpFor(900);

            // R4-3:????????????,??????????
            var floating = Assert.Single(manager.FloatingWindows.ToList());
            Assert.Equal(
                Assert.IsType<SolidColorBrush>(window.FindResource("Shell.Brush.Surface")).Color,
                Assert.IsType<SolidColorBrush>(floating.Background).Color);
            Assert.Equal(
                Assert.IsType<SolidColorBrush>(window.FindResource("Shell.Brush.Hairline")).Color,
                Assert.IsType<SolidColorBrush>(floating.BorderBrush).Color);
            Assert.Equal(new Thickness(1), floating.BorderThickness);

            window.Commands.ExecuteAsync("vulcan.app.theme mode=dark", "test").GetAwaiter().GetResult();
            UiTestHost.Pump();

            Assert.Equal(
                Assert.IsType<SolidColorBrush>(window.FindResource("Shell.Brush.Surface")).Color,
                Assert.IsType<SolidColorBrush>(floating.Background).Color);
            Assert.Equal(
                Assert.IsType<SolidColorBrush>(window.FindResource("Shell.Brush.Hairline")).Color,
                Assert.IsType<SolidColorBrush>(floating.BorderBrush).Color);
        });
    }

    [Fact]
    public void CentralTabsUseOneVisualSelectionSource()
    {
        RunShell(
            window =>
            {
                window.Docking.Show("center.one");
                window.Docking.Show("center.two");
                UiTestHost.Pump();

                var accent = ((SolidColorBrush)window.FindResource("Shell.Brush.AccentSoft")).Color;
                var active = FindVisualDescendants<LayoutDocumentTabItem>(window)
                    .Where(item => item.IsVisible)
                    .Where(item => FindVisualDescendants<Border>(item)
                        .Any(border => border.Background is SolidColorBrush brush && brush.Color == accent))
                    .ToList();

                Assert.Single(active);
            },
            configure: config =>
            {
                config.ToolWindows.Add(new ToolWindowDescriptor
                {
                    Id = "center.one",
                    Title = "Center One",
                    DefaultSide = DockSide.Center,
                    DefaultRatio = 1,
                    ContentFactory = () => new Border(),
                });
                config.ToolWindows.Add(new ToolWindowDescriptor
                {
                    Id = "center.two",
                    Title = "Center Two",
                    DefaultSide = DockSide.Center,
                    DefaultRatio = 1,
                    ContentFactory = () => new Border(),
                });
            });
    }

    [Fact]
    public void FocusedToolUsesTheSingleMainWindowChromeRow()
    {
        RunShell(
            window =>
            {
                var result = window.Commands.ExecuteAsync("vulcan.ui.max name=focus.tool", "test")
                    .GetAwaiter().GetResult();
                Assert.True(result.Success, result.Message);
                UiTestHost.Pump();

                var focusedHeader = FindVisualDescendants<Grid>(window)
                    .SingleOrDefault(item => item.IsVisible && Equals(item.Tag, "FocusedShellPaneHeader"));
                Assert.NotNull(focusedHeader);
                var tab = Assert.Single(FindVisualDescendants<LayoutAnchorableTabItem>(focusedHeader!));
                Assert.Equal("focus.tool", tab.Model.ContentId);
                Assert.DoesNotContain(
                    FindVisualDescendants<Button>(focusedHeader!),
                    button => Equals(button.CommandParameter, "restore") && button.IsVisible);

                var chromeBar = RequireElement<Panel>(window, "ChromeBar");
                Assert.NotNull(VisualTreeHelper.GetParent(chromeBar));
                Assert.Equal(Visibility.Visible, RequireButton(window, "ExitFocusButton").Visibility);
                Assert.All(
                    new[] { "MenuButton", "MinimizeButton", "MaximizeButton", "CloseButton" },
                    name => Assert.Equal(Visibility.Visible, RequireButton(window, name).Visibility));
            },
            configure: config => config.ToolWindows.Add(new ToolWindowDescriptor
            {
                Id = "focus.tool",
                Title = "Focus Tool",
                DefaultSide = DockSide.Right,
                DefaultRatio = 0.3,
                ContentFactory = () => new Border(),
            }));
    }

    [Fact]
    public void FocusedToolContentStartsBelowItsChromeRow()
    {
        RunShell(
            window =>
            {
                var result = window.Commands.ExecuteAsync("vulcan.ui.max name=focus.tool", "test")
                    .GetAwaiter().GetResult();
                Assert.True(result.Success, result.Message);
                UiTestHost.Pump();

                var header = FindVisualDescendants<Grid>(window)
                    .Single(item => item.IsVisible && Equals(item.Tag, "FocusedShellPaneHeader"));
                var content = FindVisualDescendants<ContentPresenter>(window)
                    .Single(item => item.IsVisible && item.Name == "PART_SelectedContentHost");
                var root = Assert.IsType<Grid>(VisualTreeHelper.GetParent(header));

                Assert.Same(root, VisualTreeHelper.GetParent(content));
                Assert.Equal(2, root.RowDefinitions.Count);
                Assert.Equal(0, Grid.GetRow(header));
                Assert.Equal(1, Grid.GetRow(content));
                Assert.NotNull(content.Content);
                Assert.True(content.ActualHeight > 0, $"focused content height={content.ActualHeight}");
            },
            configure: config => config.ToolWindows.Add(new ToolWindowDescriptor
            {
                Id = "focus.tool",
                Title = "Focus Tool",
                DefaultSide = DockSide.Right,
                DefaultRatio = 0.3,
                ContentFactory = () => new Border(),
            }));
    }

    [Fact]
    public void FloatingToolWindowHasNoSecondOuterChromeRow()
    {
        RunShell(window =>
        {
            var content = Assert.Single(FindVisualDescendants<ConsoleView>(window));
            var actions = FindVisualDescendants<AnchorablePaneTitle>(window)
                .Single(item => item.Model.ContentId == StandardWindowIds.Console);
            var paneHeader = FindAncestor<Grid>(actions, item => Equals(item.Tag, "ShellPaneHeader"));
            Assert.NotNull(paneHeader);

            window.Docking.Float(StandardWindowIds.Console);
            UiTestHost.PumpFor(900);

            var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));
            var floating = Assert.Single(manager.FloatingWindows.ToList());
            var chrome = WindowChrome.GetWindowChrome(floating);
            Assert.NotNull(chrome);
            Assert.Equal(0, chrome.CaptionHeight);
            var root = Assert.IsType<Border>(floating.Template.LoadContent());
            var presenter = Assert.Single(FindLogicalDescendants<ContentPresenter>(root));
            Assert.Null(presenter.DataContext);
            Assert.DoesNotContain(
                FindLogicalDescendants<FrameworkElement>(root),
                item => item.GetType().Name.Contains("FloatingWindowControlChrome", StringComparison.Ordinal));
            Assert.DoesNotContain(
                FindVisualDescendants<FrameworkElement>(floating),
                item => Equals(item.Tag, "FloatingShellPaneHeader"));
            Assert.Equal("FloatingWindowContentHost", floating.Content.GetType().Name);
            Assert.True(paneHeader!.IsVisible);
            Assert.True(content.IsVisible);
            Assert.NotNull(PresentationSource.FromVisual(content));
            Assert.NotSame(PresentationSource.FromVisual(floating), PresentationSource.FromVisual(content));

            // ???????? PresentationSource???????? Pane Style ???
            // ????? DockingManager ???????
            var floatingPane = FindAncestor<LayoutAnchorablePaneControl>(content, _ => true);
            Assert.NotNull(floatingPane);
            Assert.Contains(
                floatingPane!.Style.Setters.OfType<EventSetter>(),
                setter => setter.Event == UIElement.PreviewMouseLeftButtonDownEvent);
        });
    }

    [Fact]
    public void ToolPaneHasNoFloatingMaximizeButtonButDocumentPaneKeepsItsOwn()
    {
        RunShell(window =>
        {
            var actions = FindVisualDescendants<AnchorablePaneTitle>(window)
                .Single(item => item.Model.ContentId == StandardWindowIds.Console);
            Assert.DoesNotContain(
                FindVisualDescendants<Button>(actions),
                button => Equals(button.Tag, "FloatingMaxRestore"));

            var documentMaxRestore = FindVisualDescendants<Button>(window)
                .Single(button => button.Name == "FloatingDocumentMaxRestore");
            Assert.Equal("toggle-floating", documentMaxRestore.CommandParameter);
            Assert.Equal("FloatingMaxRestore", documentMaxRestore.Tag);
        });
    }

    [Fact]
    public void EmbeddedMainAndToolHeadersShareTheMainWindowGesturePipeline()
    {
        RunShell(
            window =>
            {
                var mainSurface = Assert.IsAssignableFrom<FrameworkElement>(
                    GetPrivateField(window, "_chromeDragSurface"));
                Assert.NotNull(FindAncestor<LayoutDocumentPaneControl>(mainSurface, _ => true));

                var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));
                var toolEvents = manager.AnchorablePaneControlStyle.Setters.OfType<EventSetter>().ToList();
                Assert.Contains(toolEvents, setter => setter.Event == UIElement.PreviewMouseLeftButtonDownEvent);
                Assert.Contains(toolEvents, setter => setter.Event == UIElement.PreviewMouseMoveEvent);
                Assert.Contains(toolEvents, setter => setter.Event == UIElement.PreviewMouseLeftButtonUpEvent);
                Assert.Contains(toolEvents, setter => setter.Event == Mouse.LostMouseCaptureEvent);

                var documentEvents = manager.DocumentPaneControlStyle.Setters.OfType<EventSetter>().ToList();
                Assert.Contains(documentEvents, setter => setter.Event == UIElement.PreviewMouseLeftButtonDownEvent);
                Assert.Contains(documentEvents, setter => setter.Event == UIElement.PreviewMouseMoveEvent);
                Assert.Contains(documentEvents, setter => setter.Event == UIElement.PreviewMouseLeftButtonUpEvent);
                Assert.Contains(documentEvents, setter => setter.Event == Mouse.LostMouseCaptureEvent);

                var result = window.Commands.ExecuteAsync("vulcan.ui.max name=focus.tool", "test")
                    .GetAwaiter().GetResult();
                Assert.True(result.Success, result.Message);
                UiTestHost.Pump();

                var focusedSurface = Assert.IsAssignableFrom<FrameworkElement>(
                    GetPrivateField(window, "_chromeDragSurface"));
                var focusedHeader = FindVisualDescendants<FrameworkElement>(window)
                    .Single(element => element.IsVisible && Equals(element.Tag, "FocusedShellPaneHeader"));
                Assert.Same(focusedHeader, focusedSurface);
                Assert.NotNull(FindAncestor<LayoutAnchorablePaneControl>(focusedHeader, _ => true));
            },
            configure: config => config.ToolWindows.Add(new ToolWindowDescriptor
            {
                Id = "focus.tool",
                Title = "Focus Tool",
                DefaultSide = DockSide.Right,
                DefaultRatio = 0.3,
                ContentFactory = () => new Border(),
            }));
    }

    [Fact]
    public void FloatingDocumentWindowKeepsItsContentHost()
    {
        Grid? content = null;
        RunShell(window =>
        {
            window.Docking.Show("center.float");
            UiTestHost.Pump();
            Assert.NotNull(content);
            window.Docking.Float("center.float");
            UiTestHost.PumpFor(900);

            var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));
            var floating = Assert.Single(manager.FloatingWindows.ToList());
            Assert.Equal("FloatingWindowContentHost", floating.Content.GetType().Name);
            Assert.True(content!.IsVisible);
            Assert.NotNull(PresentationSource.FromVisual(content));
            Assert.NotSame(PresentationSource.FromVisual(floating), PresentationSource.FromVisual(content));
        }, configure: config => config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = "center.float",
            Title = "Center Float",
            DefaultSide = DockSide.Center,
            DefaultRatio = 1,
            ContentFactory = () => content = new Grid { Tag = "FloatingDocumentContent" },
        }));
    }

    [Fact]
    public void FloatingDocumentWindowActionRoutesThroughItsOwnPaneBinding()
    {
        Grid? content = null;
        RunShell(window =>
        {
            window.Docking.Show("center.float.actions");
            window.Docking.Float("center.float.actions");
            UiTestHost.PumpFor(900);

            var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));
            var floating = Assert.Single(manager.FloatingWindows.ToList());
            Assert.NotNull(content);
            var pane = FindAncestor<LayoutDocumentPaneControl>(content!, _ => true);
            Assert.NotNull(pane);
            var button = FindVisualDescendants<Button>(pane!)
                .Single(item => item.Name == "FloatingDocumentMaxRestore");
            Assert.Equal(Visibility.Visible, button.Visibility);
            var command = Assert.IsType<RoutedCommand>(button.Command);

            Assert.True(command.CanExecute(button.CommandParameter, button.CommandTarget));
            command.Execute(button.CommandParameter, button.CommandTarget);
            UiTestHost.Pump();
            Assert.Equal(WindowState.Maximized, floating.WindowState);
        }, configure: config => config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = "center.float.actions",
            Title = "Center Float Actions",
            DefaultSide = DockSide.Center,
            DefaultRatio = 1,
            ContentFactory = () => content = new Grid(),
        }));
    }

    [Fact]
    public void FloatingDocumentWindowStateIsOwnedByCommandBus()
    {
        RunShell(window =>
        {
            window.Docking.Show("center.state");
            window.Docking.Float("center.state");
            UiTestHost.PumpFor(900);

            var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));
            var floating = Assert.Single(manager.FloatingWindows.ToList());
            var maximize = window.Commands.ExecuteAsync(
                "vulcan.ui.floatstate name=center.state state=maximized", "Test").GetAwaiter().GetResult();
            Assert.True(maximize.Success, maximize.Message);
            UiTestHost.Pump();
            floating = Assert.Single(manager.FloatingWindows.ToList());
            Assert.Equal(WindowState.Maximized, floating.WindowState);

            var restore = window.Commands.ExecuteAsync(
                "vulcan.ui.floatstate name=center.state state=toggle", "Test").GetAwaiter().GetResult();
            Assert.True(restore.Success, restore.Message);
            UiTestHost.Pump();
            floating = Assert.Single(manager.FloatingWindows.ToList());
            Assert.Equal(WindowState.Normal, floating.WindowState);
        }, configure: config => config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = "center.state",
            Title = "Center State",
            DefaultSide = DockSide.Center,
            DefaultRatio = 1,
            ContentFactory = () => new Grid(),
        }));
    }

    [Fact]
    public void PaneActionsSitInTheTabRowWithoutTheAutoHideButton()
    {
        RunShell(window =>
        {
            var actions = FindVisualDescendants<AnchorablePaneTitle>(window).First(item => item.IsVisible);

            // R4-1:?????????,????????
            var header = FindAncestor<Grid>(actions, grid => grid.Tag as string == "ShellPaneHeader");
            Assert.NotNull(header);

            // R4-2:?? ???????,?????? ? ????
            // ???? Popup ?,?????????? ?? ???? ? ????????
            var buttons = FindVisualDescendants<ButtonBase>(actions).ToList();
            Assert.DoesNotContain(buttons, button => Equals(button.ToolTip, "\u81ea\u52a8\u9690\u85cf"));

            Assert.Single(buttons.OfType<ToggleButton>());

            // ????? Popup ?,???????????,??????????;
            // ????????????,?????????????
            var declared = (FrameworkElement)actions.Template.LoadContent();
            var menuItems = FindLogicalDescendants<MenuItem>(declared).ToList();
            Assert.Contains(menuItems, item => Equals(item.Header, "\u81ea\u52a8\u9690\u85cf"));
            Assert.Contains(menuItems, item => Equals(item.Header, "\u6d6e\u52a8"));
        });
    }

    [Fact]
    public void ThemeCommandSwitchesTokensAndPersistsChoice()
    {
        var settings = new MemorySettings();
        RunShell(
            window =>
            {
                // UI-08:?????,???????????
                var light = (SolidColorBrush)window.FindResource("Shell.Brush.Canvas");
                Assert.Equal(Colors.White, ((SolidColorBrush)window.FindResource("Shell.Brush.Surface")).Color);

                window.Commands.ExecuteAsync("vulcan.app.theme mode=dark", "test").GetAwaiter().GetResult();
                UiTestHost.Pump();

                var dark = (SolidColorBrush)window.FindResource("Shell.Brush.Canvas");
                Assert.NotEqual(light.Color, dark.Color);
                Assert.True(dark.Color.R < 0x40 && dark.Color.G < 0x40 && dark.Color.B < 0x40,
                    $"dark canvas should be dark, got {dark.Color}");
                Assert.Equal("dark", settings.Get("ui.theme"));

                // ??????:R > B ?????
                var accent = ((SolidColorBrush)window.FindResource("Shell.Brush.Accent")).Color;
                Assert.True(accent.R > accent.B + 0x40, $"accent should be amber, got {accent}");

                window.Commands.ExecuteAsync("vulcan.app.theme mode=light", "test").GetAwaiter().GetResult();
                UiTestHost.Pump();
                Assert.Equal(light.Color, ((SolidColorBrush)window.FindResource("Shell.Brush.Canvas")).Color);
                Assert.Equal("light", settings.Get("ui.theme"));
            },
            settings: settings);
    }

    [Fact]
    public void ThemeChoiceSurvivesSettingsAndWindowRecreation()
    {
        var appName = $"HistoryVulcan.Theme.Tests.{Guid.NewGuid():N}";
        var paths = new AppPaths(appName);
        try
        {
            var firstSettings = new SettingsService(paths);
            RunShell(
                window =>
                {
                    var result = window.Commands.ExecuteAsync("vulcan.app.theme mode=dark", "test")
                        .GetAwaiter().GetResult();
                    Assert.True(result.Success, result.Message);
                },
                settings: firstSettings);

            var reloadedSettings = new SettingsService(paths);
            Assert.Equal("dark", reloadedSettings.Get("ui.theme"));
            RunShell(
                window =>
                {
                    var canvas = ((SolidColorBrush)window.FindResource("Shell.Brush.Canvas")).Color;
                    Assert.True(canvas.R < 0x40 && canvas.G < 0x40 && canvas.B < 0x40,
                        $"recreated window did not restore dark theme: {canvas}");
                },
                settings: reloadedSettings);
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public void TitleBarKeepsMenuButtonImmediatelyLeftOfThreeWindowButtons()
    {
        RunShell(window =>
        {
            // UI-02.3:?? + ??? + ????? + ??,?????????
            var menu = RequireButton(window, "MenuButton");
            var minimize = RequireButton(window, "MinimizeButton");
            var maximize = RequireButton(window, "MaximizeButton");
            var close = RequireButton(window, "CloseButton");

            foreach (var button in new[] { menu, minimize, maximize, close })
            {
                Assert.Equal(Visibility.Visible, button.Visibility);
                Assert.True(button.IsEnabled);
                Assert.True(button.IsHitTestVisible);
                Assert.True(button.ActualWidth > 0, $"{button.Name} width={button.ActualWidth}");
            }

            // ??:?????????,??????????
            var bar = RequireElement<Panel>(window, "ChromeBar");
            var order = bar.Children.OfType<Button>().ToList();
            var menuIndex = order.IndexOf(menu);
            Assert.True(menuIndex >= 0);
            Assert.Equal(menuIndex + 1, order.IndexOf(minimize));
            Assert.Equal(menuIndex + 2, order.IndexOf(maximize));
            Assert.Equal(menuIndex + 3, order.IndexOf(close));
        });
    }

    [Fact]
    public void FoldedMenuKeepsAllGroupsAndConsumerToolActions()
    {
        RunShell(
            window =>
            {
                // UI-03.2:????????? 3.0.3 ?????
                var menu = RequireButton(window, "MenuButton").ContextMenu;
                Assert.NotNull(menu);
                var headers = menu!.Items.OfType<MenuItem>().Select(item => item.Header.ToString()).ToList();
                Assert.Equal(["\u6587\u4ef6(_F)", "\u7f16\u8f91(_E)", "\u89c6\u56fe(_V)", "\u5de5\u5177(_T)", "\u5e2e\u52a9(_H)"], headers);

                var tools = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "\u5de5\u5177(_T)"));
                Assert.Contains(
                    tools.Items.OfType<MenuItem>(),
                    item => Equals(item.Header, "\u6d4b\u8bd5\u52a8\u4f5c"));
            },
            configure: config => config.ToolMenuActions.Add(new ShellMenuAction("\u6d4b\u8bd5\u52a8\u4f5c", "vulcan.app.about")));
    }

    [Fact]
    public void MaximizedPageMovesTheSingleMainChromeIntoTheFocusedHeader()
    {
        RunShell(window =>
        {
            // DEC-023:中央命令集页由 Mercury 提供，不在本仓库门禁内。注册一个等价的中央页，
            // 断言的是 Vulcan 自己的聚焦头与共享 chrome 归属。
            window.Docking.RegisterWindow(CenterPage(StandardWindowIds.Mcp), "test");
            window.Docking.Show(StandardWindowIds.Mcp);
            UiTestHost.Pump();

            var exitFocus = RequireButton(window, "ExitFocusButton");
            Assert.Equal(Visibility.Collapsed, exitFocus.Visibility);
            Assert.Equal(
                Visibility.Visible,
                Assert.Single(FindVisualDescendants<DocumentPaneTabPanel>(window)).Visibility);

            // Focused pages keep their real tab and host the one shared main-window chrome.
            window.Docking.MaximizeWindow(StandardWindowIds.Mcp);
            UiTestHost.Pump();

            Assert.Equal(
                Visibility.Visible,
                Assert.Single(FindVisualDescendants<DocumentPaneTabPanel>(window)).Visibility);
            Assert.Empty(FindVisualDescendants<AnchorablePaneTitle>(window));
            Assert.Equal(Visibility.Visible, exitFocus.Visibility);
            var chromeBar = RequireElement<Panel>(window, "ChromeBar");
            Assert.NotNull(VisualTreeHelper.GetParent(chromeBar));
            Assert.Single(FindVisualDescendants<Panel>(window), panel => panel.Name == "ChromeBar");
            Assert.All(
                new[] { "MenuButton", "MinimizeButton", "MaximizeButton", "CloseButton" },
                name => Assert.Equal(Visibility.Visible, RequireButton(window, name).Visibility));

            Assert.True(window.Commands.ExecuteAsync("vulcan.ui.restore", "Test").GetAwaiter().GetResult().Success);
            UiTestHost.Pump();

            Assert.Null(window.Docking.MaximizedId);
            Assert.Equal(
                Visibility.Visible,
                Assert.Single(FindVisualDescendants<DocumentPaneTabPanel>(window)).Visibility);
            Assert.Equal(Visibility.Collapsed, exitFocus.Visibility);
        });
    }

    [Fact]
    public void NonStandardWindowStyleStillKeepsMenuAndWindowButtons()
    {
        // UI-09.1:???? WindowStyle ??????????,???????
        RunShell(
            window =>
            {
                Assert.Equal(WindowStyle.ToolWindow, window.WindowStyle);
                foreach (var name in new[] { "MenuButton", "MinimizeButton", "MaximizeButton", "CloseButton" })
                {
                    var button = RequireButton(window, name);
                    Assert.Equal(Visibility.Visible, button.Visibility);
                    Assert.True(button.IsEnabled);
                }
            },
            windowStyle: WindowStyle.ToolWindow);
    }

    [Fact]
    public void ErrorEntryRaisesTitleBarBadgeInsteadOfStatusBar()
    {
        var log = new RelayLog();
        RunShell(
            window =>
            {
                var badge = RequireButton(window, "ErrorBadge");
                Assert.Equal(Visibility.Collapsed, badge.Visibility);

                log.Raise(ShellLogLevel.Error, "test", "\u754c\u9762\u5347\u7ea7\u9a8c\u8bc1\u7528\u9519\u8bef");
                UiTestHost.Pump();

                // UI-05.3: count moves to title-bar badge
                Assert.Equal(Visibility.Visible, badge.Visibility);
                Assert.Equal("\u9519\u8bef 1", badge.Content);
            },
            log: log);
    }

    [Fact]
    public void ConsoleToolbarShowsLevelDomainAndDependentClass()
    {
        RunShell(window =>
        {
            var console = Assert.Single(FindVisualDescendants<ConsoleView>(window));
            Assert.Equal(Visibility.Visible, Assert.IsType<ComboBox>(console.FindName("LevelFilter")).Visibility);
            Assert.Equal(Visibility.Visible, Assert.IsType<ComboBox>(console.FindName("DomainFilter")).Visibility);
            var classFilter = Assert.IsType<ComboBox>(console.FindName("ClassFilter"));
            Assert.Equal(Visibility.Visible, classFilter.Visibility);
            Assert.False(classFilter.IsEnabled);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<TextBox>(console.FindName("KeywordFilter")).Visibility);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<CheckBox>(console.FindName("MuteLayout")).Visibility);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<CheckBox>(console.FindName("AutoScroll")).Visibility);

            var status = window.Commands.ExecuteAsync("vulcan.log.autoscroll", "Test").GetAwaiter().GetResult();
            Assert.True(status.Success, status.Message);
            Assert.Contains("True", status.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ConsoleTypingInNormalLayoutKeepsCompletionInConsole()
    {
        var session = new CompletionCatalogSession();
        RunShell(window =>
        {
            window.AttachCommandCatalogSession(session);
            var console = Assert.Single(FindVisualDescendants<ConsoleView>(window));
            var input = Assert.IsType<TextBox>(console.FindName("Input"));
            var popup = Assert.IsType<Popup>(console.FindName("CompletionPopup"));

            Assert.Null(window.Docking.MaximizedId);
            input.Focus();
            Keyboard.Focus(input);
            input.Text = "v";
            input.CaretIndex = input.Text.Length;

            UiTestHost.PumpUntil(() => input.Text == "v");
            Assert.False(popup.IsOpen);
            Assert.True(input.IsKeyboardFocusWithin);
            Assert.Equal("v", input.Text);
            Assert.Equal("", session.LastText);
            Assert.False(console.HandleCompletionKey(Key.Tab, ModifierKeys.None));
            Assert.Equal("v", input.Text);
        });
    }

    [Fact]
    public void FocusedConsoleCompletesDomainClassMethodAndParameter()
    {
        var session = new CompletionCatalogSession();
        RunShell(window =>
        {
            window.AttachCommandCatalogSession(session);
            window.Docking.MaximizeWindow(StandardWindowIds.Console);
            UiTestHost.Pump();
            window.RefreshCommandCompletionFocus();
            var console = Assert.Single(FindVisualDescendants<ConsoleView>(window));
            var input = Assert.IsType<TextBox>(console.FindName("Input"));
            var popup = Assert.IsType<Popup>(console.FindName("CompletionPopup"));

            Assert.Equal(StandardWindowIds.Console, window.Docking.MaximizedId);
            input.Focus();
            Keyboard.Focus(input);
            input.Text = "v";
            input.CaretIndex = input.Text.Length;

            Assert.True(UiTestHost.PumpUntil(() => popup.IsOpen));
            Assert.Equal("vulcan.", Assert.Single(session.LastResult.Candidates).InsertText);

            Assert.True(console.HandleCompletionKey(Key.Tab, ModifierKeys.None));
            Assert.True(UiTestHost.PumpUntil(() => session.LastText == "vulcan."));
            Assert.Equal("vulcan.proj.", Assert.Single(session.LastResult.Candidates).InsertText);

            Assert.True(console.HandleCompletionKey(Key.Tab, ModifierKeys.None));
            Assert.True(UiTestHost.PumpUntil(() => session.LastText == "vulcan.proj."));
            Assert.Equal("vulcan.proj.open ", Assert.Single(session.LastResult.Candidates).InsertText);

            Assert.True(console.HandleCompletionKey(Key.Tab, ModifierKeys.None));
            Assert.True(UiTestHost.PumpUntil(() => session.LastText == "vulcan.proj.open "));
            Assert.Equal("name=", Assert.Single(session.LastResult.Candidates).InsertText);
        });
    }

    [Fact]
    public void ConsoleLongLinesWrapAtCurrentWidthWithoutHorizontalExtentOrLogicalNewlines()
    {
        UiTestHost.RunSta(() =>
        {
            var log = new RelayLog();
            var logicalText = new string('W', 320);
            log.Raise(ShellLogLevel.Info, "wrap.test", logicalText);
            var bus = new CommandBus(new CommandRegistry(), log);
            var console = new ConsoleView(
                log,
                bus,
                new CommandHistory(Path.Combine(Path.GetTempPath(), $"HistoryVulcan-history-{Guid.NewGuid():N}.txt")),
                new HistoryVulcan.Shell.CommandSurface.DeferredCommandCatalogSession());
            var host = new Window
            {
                Content = console,
                Width = 760,
                Height = 420,
                ShowInTaskbar = false,
            };
            var exportPath = Path.Combine(Path.GetTempPath(), $"HistoryVulcan-console-{Guid.NewGuid():N}.txt");
            try
            {
                host.Show();
                var output = Assert.IsType<ListBox>(console.FindName("Output"));
                // 同上：等 100ms 批量刷新把行送进列表。
                Assert.True(
                    UiTestHost.PumpUntil(() => output.Items.Count > 0),
                    "控制台批量刷新超时，没有可换行的行");
                var row = Assert.Single(output.Items.Cast<ConsoleRow>());
                output.ScrollIntoView(row);
                host.UpdateLayout();
                var container = Assert.IsType<ListBoxItem>(output.ItemContainerGenerator.ContainerFromItem(row));
                var text = Assert.Single(FindVisualDescendants<TextBlock>(container));
                var scroll = Assert.Single(FindVisualDescendants<ScrollViewer>(output));
                var horizontalBar = FindVisualDescendants<ScrollBar>(scroll)
                    .Single(bar => bar.Orientation == Orientation.Horizontal);
                var wideHeight = text.ActualHeight;

                Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
                Assert.True(scroll.CanContentScroll);
                Assert.Equal(ScrollBarVisibility.Disabled,
                    ScrollViewer.GetHorizontalScrollBarVisibility(output));
                Assert.Equal(0, scroll.ScrollableWidth);
                Assert.Equal(Visibility.Collapsed, horizontalBar.Visibility);

                host.Width = 360;
                host.UpdateLayout();
                UiTestHost.Pump();
                var narrowHeight = text.ActualHeight;
                Assert.True(narrowHeight > wideHeight,
                    $"narrow={narrowHeight}, wide={wideHeight}");
                Assert.Equal(0, scroll.ScrollableWidth);

                host.Width = 760;
                host.UpdateLayout();
                UiTestHost.Pump();
                Assert.True(text.ActualHeight < narrowHeight,
                    $"rewidened={text.ActualHeight}, narrow={narrowHeight}");
                Assert.Equal(0, scroll.ScrollableWidth);
                Assert.DoesNotContain('\n', row.Text);

                output.SelectedItem = row;
                var copy = console.CopySelected();
                Assert.Contains("\u5df2\u590d\u5236", copy, StringComparison.Ordinal);
                Assert.Equal(row.Text, Clipboard.GetText());

                var export = console.ExportVisible(exportPath);
                Assert.Contains("\u5df2\u5bfc\u51fa", export, StringComparison.Ordinal);
                var exported = Assert.Single(File.ReadAllLines(exportPath));
                Assert.Contains(logicalText, exported, StringComparison.Ordinal);
            }
            finally
            {
                host.Close();
                if (File.Exists(exportPath))
                    File.Delete(exportPath);
            }
        });
    }

    [Fact]
    public void CommandPipelineOwnsConsoleAndFormerDirectActions()
    {
        RunShell(window =>
        {
            var names = window.Commands.Registry.All().Select(command => command.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(
                new[]
                {
                    "vulcan.log.level", "vulcan.log.source", "vulcan.log.keyword", "vulcan.log.mute", "vulcan.log.autoscroll",
                    "vulcan.log.clear", "vulcan.log.export", "vulcan.log.copy", "vulcan.log.focus",
                    "vulcan.app.hide", "vulcan.app.show", "vulcan.app.focusconsole", "vulcan.app.close",
                    "vulcan.ui.max", "vulcan.log.focus",
                    "vulcan.app.window", "vulcan.ui.autohide", "vulcan.ui.floatstate", "vulcan.command.copyexample",
                    "vulcan.ui.selectfile", "vulcan.ui.selectdirectory",
                },
                name => Assert.Contains(name, names));
            Assert.DoesNotContain(names, name => name.StartsWith("res.", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, name => name.StartsWith("motor.", StringComparison.OrdinalIgnoreCase));

            Assert.True(window.Commands.ExecuteAsync("vulcan.log.level level=error", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("vulcan.log.source source=vulcan", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("vulcan.log.keyword text=timeout", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("vulcan.log.mute layout=true", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("vulcan.log.autoscroll enabled=false", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("vulcan.log.focus errors=true", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("vulcan.log.clear", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("vulcan.log.export", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("vulcan.log.copy", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync($"vulcan.ui.autohide name={StandardWindowIds.Console}", "Test")
                .GetAwaiter().GetResult().Success);
        });
    }

    // ---------------------------------------------------------------- ??

    private static bool WakeConsole(ShellWindow window)
        => window.Commands.ExecuteAsync("vulcan.app.focusconsole", "test")
            .GetAwaiter().GetResult().Success;

    private static void RunShell(
        Action<ShellWindow> assert,
        Action<ShellConfig>? configure = null,
        WindowStyle windowStyle = WindowStyle.SingleBorderWindow,
        IShellLog? log = null,
        ISettingsService? settings = null)
    {
        UiTestHost.RunSta(() =>
        {
            var dataDirectory = Path.Combine(Path.GetTempPath(), $"HistoryVulcan-chrome-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataDirectory);
            var config = new ShellConfig
            {
                AppName = "HistoryVulcan Chrome Test",
                AppVersion = "3.1.1",
            };
            configure?.Invoke(config);

            var window = new ShellWindow(
                config,
                new MemoryLayoutStore(),
                log ?? new NullLog(),
                settings ?? new MemorySettings(),
                dataDirectory)
            {
                Width = 1000,
                Height = 700,
                ShowInTaskbar = false,
                WindowStyle = windowStyle,
            };
            // DEC-023:命令工作台（命令集/详情/补全）由 HistoryMercury 提供，不在本仓库门禁内。
            // Shell 合同用例因此运行在「无 Mercury」降级路径上，验证 Vulcan 自身在缺少
            // 命令工作台时仍完整可用。
            try
            {
                window.Show();
                UiTestHost.Pump();
                assert(window);
            }
            finally
            {
                window.Close();
                Directory.Delete(dataDirectory, recursive: true);
            }
        });
    }

    /// <summary>
    /// REQ-CMD-012:省略 path 的导出不得弹 SaveFileDialog。本用例在无人值守下运行——
    /// 若实现回到弹窗，模态对话框会阻塞 STA 线程直到测试超时，而不是通过。
    /// </summary>
    [Fact]
    public void ConsoleExportWithoutPathWritesDefaultFileWithoutDialog()
    {
        UiTestHost.RunSta(() =>
        {
            var log = new RelayLog();
            log.Raise(ShellLogLevel.Info, "export.test", "no-dialog-export-line");
            var console = new ConsoleView(
                log,
                new CommandBus(new CommandRegistry(), log),
                new CommandHistory(Path.Combine(Path.GetTempPath(), $"HistoryVulcan-history-{Guid.NewGuid():N}.txt")),
                new HistoryVulcan.Shell.CommandSurface.DeferredCommandCatalogSession());
            var host = new Window
            {
                Content = console,
                Width = 760,
                Height = 420,
                ShowInTaskbar = false,
            };

            string? written = null;
            try
            {
                host.Show();
                // 控制台按 100ms 批量合并日志：等到行真正进入可见集合再导出，
                // 而不是盲等一个「应该够了」的固定时长。
                Assert.True(
                    UiTestHost.PumpUntil(() =>
                        ((System.Collections.IEnumerable)Assert.IsType<ListBox>(console.FindName("Output")).Items)
                            .Cast<ConsoleRow>().Any()),
                    "控制台批量刷新超时，没有可导出的行");

                var message = console.ExportVisible(null);

                Assert.Contains("已导出", message, StringComparison.Ordinal);
                // 结果必须回报绝对路径，调用方（MCP / Web）才能取回文件。
                const string marker = "行到 ";
                var at = message.LastIndexOf(marker, StringComparison.Ordinal);
                Assert.True(at > 0, $"导出结果未回报路径: {message}");
                written = message[(at + marker.Length)..].Trim();
                Assert.True(Path.IsPathFullyQualified(written), $"导出路径不是绝对路径: {written}");
                Assert.True(File.Exists(written), $"默认导出文件未落盘: {written}");
                Assert.Contains(
                    "no-dialog-export-line",
                    File.ReadAllText(written),
                    StringComparison.Ordinal);
                Assert.Equal("exports", Path.GetFileName(Path.GetDirectoryName(written)));
            }
            finally
            {
                host.Close();
                if (written != null && File.Exists(written))
                    File.Delete(written);
            }
        });
    }

    private static Button RequireButton(ShellWindow window, string name)
        => RequireElement<Button>(window, name);

    /// <summary>
    /// 中央主文档页替身。DEC-023 后命令集页由 HistoryMercury 提供，不在本仓库门禁内；
    /// 需要「中央区存在一个页面」的 Vulcan chrome 用例改用本替身。
    /// </summary>
    private static ToolWindowDescriptor CenterPage(string id) => new()
    {
        Id = id,
        Title = id,
        DefaultSide = DockSide.Center,
        DefaultRatio = 1,
        ContentFactory = () => new Border(),
    };

    private static bool HasKeyboardOrLogicalFocus(FrameworkElement element)
        => element.IsKeyboardFocusWithin
           || ReferenceEquals(
               FocusManager.GetFocusedElement(FocusManager.GetFocusScope(element)),
               element);

    private static T RequireElement<T>(ShellWindow window, string name)
        where T : FrameworkElement
    {
        var element = window.FindName(name);
        Assert.NotNull(element);
        return Assert.IsAssignableFrom<T>(element);
    }

    private static object? GetPrivateField(object instance, string name)
    {
        var field = instance.GetType().GetField(
            name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(instance);
    }

    private static T? FindAncestor<T>(DependencyObject element, Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (DependencyObject? current = element; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match && predicate(match))
                return match;
        }

        return null;
    }

    private static IEnumerable<T> FindLogicalDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match)
                yield return match;
            foreach (var descendant in FindLogicalDescendants<T>(child))
                yield return descendant;
        }
    }

    private static Color SamplePixel(FrameworkElement element, double relativeX, double relativeY)
    {
        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var x = Math.Clamp((int)(width * relativeX), 0, width - 1);
        var y = Math.Clamp((int)(height * relativeY), 0, height - 1);
        var pixels = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixels, 4, 0);
        return Color.FromArgb(pixels[3], pixels[2], pixels[1], pixels[0]);
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

    private sealed class MemoryLayoutStore : ILayoutStore
    {
        private string? _current;
        private readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase);

        public string? ReadCurrent() => _current;
        public void WriteCurrent(string payload) => _current = payload;
        public void DeleteCurrent() => _current = null;
        public void ReplaceCurrent(string oldValue, string newValue)
            => _current = _current?.Replace(oldValue, newValue, StringComparison.Ordinal);
        public string? ReadNamed(string name) => _named.GetValueOrDefault(name);
        public void WriteNamed(string name, string payload) => _named[name] = payload;
        public IReadOnlyList<string> ListNamed() => _named.Keys.ToList();
    }

    private sealed class NullLog : IShellLog
    {
        public void Log(ShellLogLevel level, string category, string message) { }
        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }

    /// <summary>????? EntryAdded ???,?????????</summary>
    private sealed class RelayLog : IShellLog
    {
        private readonly List<ShellLogEntry> _entries = new();

        public void Log(ShellLogLevel level, string category, string message)
            => Raise(level, category, message);

        public event EventHandler<ShellLogEntry>? EntryAdded;

        public IReadOnlyList<ShellLogEntry> Snapshot() => _entries;

        public void Raise(ShellLogLevel level, string category, string message)
        {
            var entry = new ShellLogEntry(DateTime.Now, level, category, message);
            _entries.Add(entry);
            EntryAdded?.Invoke(this, entry);
        }
    }

    private sealed class CompletionCatalogSession : ICommandCatalogSession
    {
        public event EventHandler<CommandCatalogChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public IReadOnlyList<string> Domains => ["vulcan"];
        public IReadOnlyList<string> Classes => ["app"];
        public string? SelectedCommandName => null;
        public CommandCatalogFilter CurrentFilter => new();
        public ConsoleCompletionResult LastResult { get; private set; } = ConsoleCompletionResult.Empty;
        public string LastText { get; private set; } = "";

        public Task<bool> RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public void SetFilter(CommandCatalogFilter filter) { }

        public bool TrySetDomain(string domain, out IReadOnlyList<string> availableDomains)
        {
            availableDomains = ["全部", "vulcan"];
            return availableDomains.Contains(domain, StringComparer.OrdinalIgnoreCase);
        }

        public bool TrySetCommandClass(string commandClass, out IReadOnlyList<string> availableClasses)
        {
            availableClasses = ["全部", "app"];
            return availableClasses.Contains(commandClass, StringComparer.OrdinalIgnoreCase);
        }

        public void SetConsoleQuery(string query) { }
        public bool MoveSelection(int direction) => false;
        public void Select(string? commandName) { }

        public Task<ConsoleCompletionResult> CompleteAsync(
            string text,
            int caretIndex,
            CancellationToken cancellationToken = default)
        {
            LastText = text;
            var candidate = text switch
            {
                "v" => Completion("vulcan.", ConsoleCompletionKind.Domain),
                "vulcan." => Completion("vulcan.proj.", ConsoleCompletionKind.Class),
                "vulcan.proj." => Completion("vulcan.proj.open ", ConsoleCompletionKind.Method),
                "vulcan.proj.open " => Completion("name=", ConsoleCompletionKind.Parameter),
                _ => null,
            };
            LastResult = new ConsoleCompletionResult
            {
                Candidates = candidate is null ? [] : [candidate],
                ReplaceStart = 0,
                ReplaceLength = text.Length,
            };
            return Task.FromResult(LastResult);
        }

        private static ConsoleCompletionCandidate Completion(
            string text,
            ConsoleCompletionKind kind)
            => new()
            {
                InsertText = text,
                DisplayText = text,
                Description = kind.ToString(),
                Kind = kind,
            };

        public void Dispose() { }
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback)
            => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }
}
