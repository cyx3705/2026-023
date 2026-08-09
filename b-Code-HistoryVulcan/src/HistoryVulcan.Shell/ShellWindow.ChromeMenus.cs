using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Shell.Themes;

namespace HistoryVulcan.Shell;

public partial class ShellWindow
{
    // ---------------------------------------------------------------- 主题(UI-08)

    private const string ThemeSettingsKey = "ui.theme";
    private const string ThemeLight = "light";
    private const string ThemeDark = "dark";

    private static readonly Uri LightTokensUri =
        new("/HistoryVulcan.Shell;component/Themes/ShellTokens.xaml", UriKind.Relative);

    private static readonly Uri DarkTokensUri =
        new("/HistoryVulcan.Shell;component/Themes/ShellTokens.Dark.xaml", UriKind.Relative);

    /// <summary>
    /// 令牌字典整份替换:应用色与 AvalonDock 主题画刷键都在同一份令牌里,
    /// 因此不会出现「界面已深色、页签仍浅色」。三处都要换:
    /// 窗体(视图)、DockingManager(停靠区,压过 VS2013 主题)、应用级(浮动窗口是独立 Window)。
    /// </summary>
    private void ApplyFocusChrome()
    {
        var focused = _docking.MaximizedId != null;

        ExitFocusButton.Visibility = focused ? Visibility.Visible : Visibility.Collapsed;
        ExitFocusButton.ToolTip = focused ? "退出聚焦" : null;
        ChromeBar.Visibility = Visibility.Visible;

        // CaptionHeight 永久为零，避免隐藏命中区覆盖任意工具窗格顶部。
        // 文档页头显式拖动主窗口；工具页头由协调器显式拖出工具浮窗。
        if (WindowChrome.GetWindowChrome(this) is { } chrome)
            chrome.CaptionHeight = 0;

        ApplyPaneStyles(chromeless: focused);
        ScheduleChromeReserve();
        RefreshCommandCompletionFocus();
        _topBar.Refresh();
    }

    private string FindTitle(string id)
        => _docking.Descriptors
               .FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Title
           ?? id;

    private void OnExitFocusClick(object sender, RoutedEventArgs e)
        => _ = _bus.ExecuteAsync("vulcan.ui.restore", "UI");

    /// <summary>F11:在当前活动页的专注态与常规态之间切换。</summary>
    private void ToggleFocusMode()
    {
        if (_docking.MaximizedId != null)
        {
            _ = _bus.ExecuteAsync("vulcan.ui.restore", "UI");
            return;
        }

        var id = DockManager.Layout?.ActiveContent?.ContentId;
        if (!string.IsNullOrWhiteSpace(id))
            _ = _bus.ExecuteAsync($"vulcan.ui.max name={id}", "UI");
    }

    // ---------------------------------------------------------------- 键盘(UI-03.4 / UI-04.4)

    private void OnShellPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var alt = e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt;
        _altPressedAlone = alt && !_menu.IsOpen;

        if (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.None)
        {
            OpenShellMenu();
            e.Handled = true;
        }
        else if (e.Key == Key.F11 && Keyboard.Modifiers == ModifierKeys.None)
        {
            ToggleFocusMode();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _docking.MaximizedId != null && !IsTextInputFocused())
        {
            _ = _bus.ExecuteAsync("vulcan.ui.restore", "UI");
            e.Handled = true;
        }
    }

    private void OnShellPreviewKeyUp(object sender, KeyEventArgs e)
    {
        // Alt 单独按下抬起才呼出菜单;与其他键组合(Alt+Tab、Alt+F4、助记符)一律放行
        if (_altPressedAlone && e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt)
        {
            _altPressedAlone = false;
            OpenShellMenu();
            e.Handled = true;
        }
    }

    /// <summary>Esc 退出专注不得抢走控制台等输入控件的 Esc(UI-04.4)。</summary>
    private static bool IsTextInputFocused()
        => Keyboard.FocusedElement is TextBoxBase or ComboBox or PasswordBox;

    private static object? FindInDictionary(ResourceDictionary dict, object key)
    {
        if (dict.Contains(key))
            return dict[key];
        foreach (var merged in dict.MergedDictionaries)
        {
            if (FindInDictionary(merged, key) is { } found)
                return found;
        }

        return null;
    }

    // ---------------------------------------------------------------- 菜单(S-01,3.1 折叠为顶栏弹出层 UI-03)

    private void OnMenuButtonClick(object sender, RoutedEventArgs e) => OpenShellMenu();

    private void OpenShellMenu()
    {
        if (_menu.Items.Count == 0)
            return;
        _menu.PlacementTarget = MenuButton;
        _menu.Placement = PlacementMode.Bottom;
        _menu.IsOpen = true;
    }

    private void BuildMenus()
    {
        var rebuilt = new List<object>();

        // 文件
        var file = new MenuItem { Header = "文件(_F)" };
        file.Items.Add(Item("退出(_X)", "vulcan.app.quit"));
        rebuilt.Add(file);

        // 编辑(预留)
        var edit = new MenuItem { Header = "编辑(_E)", IsEnabled = false };
        rebuilt.Add(edit);

        // 视图:全部窗口开关 + 重置布局(W-02)
        var view = new MenuItem { Header = "视图(_V)" };
        foreach (var d in _docking.Descriptors)
        {
            var sub = new MenuItem { Header = d.Title };
            sub.Items.Add(Item("显示", $"vulcan.ui.show name={d.Id}"));
            sub.Items.Add(Item("隐藏", $"vulcan.ui.hide name={d.Id}"));
            sub.Items.Add(Item("浮动", $"vulcan.ui.float name={d.Id}"));
            sub.Items.Add(Item("复位到默认位置", $"vulcan.ui.reset name={d.Id}"));
            view.Items.Add(sub);
        }

        view.Items.Add(new Separator());
        var restore = Item("退出窗口最大化", "vulcan.ui.restore");
        restore.IsEnabled = _docking.MaximizedId != null;
        view.Items.Add(restore);
        view.Items.Add(Item("重置默认布局", "vulcan.ui.layoutreset"));
        // UI-08:主题切换(S-02,同样是发指令)
        view.Items.Add(new Separator());
        view.Items.Add(_theme == ThemeDark
            ? Item("切换到浅色模式", $"vulcan.app.theme mode={ThemeLight}")
            : Item("切换到深色模式", $"vulcan.app.theme mode={ThemeDark}"));
        rebuilt.Add(view);

        // 工具
        var tools = new MenuItem { Header = "工具(_T)" };
        tools.Items.Add(Item("打开数据目录", "vulcan.app.opendata"));
        if (_config.ToolMenuActions.Count > 0)
            tools.Items.Add(new Separator());
        foreach (var action in _config.ToolMenuActions)
            tools.Items.Add(Item(action.Header, action.CommandText));
        rebuilt.Add(tools);

        // 帮助:指令手册 = help 的图形化版本(S-01)
        var help = new MenuItem { Header = "帮助(_H)" };
        var manual = new MenuItem { Header = "指令手册(_M)" };
        var helpError = _bus.Validate("vulcan.command.help");
        if (helpError != null && !_menusInitialized)
            throw InvalidMenuCommand("vulcan.command.help", helpError);
        manual.IsEnabled = helpError == null;
        manual.ToolTip = helpError == null ? null : $"指令当前不可用: {helpError}";
        manual.Click += async (_, _) =>
        {
            await _bus.ExecuteAsync("vulcan.log.focus", "UI");
            await _bus.ExecuteAsync("vulcan.command.help", "UI");
        };
        help.Items.Add(manual);
        help.Items.Add(Item("关于(_A)", "vulcan.app.about"));
        rebuilt.Add(help);

        _menu.Items.Clear();
        foreach (var item in rebuilt)
            _menu.Items.Add(item);
        _menusInitialized = true;
    }

    /// <summary>菜单项点击同样是发指令(S-02):统一经总线分发、回显、留痕。</summary>
    private MenuItem Item(string header, string commandText)
    {
        var mi = new MenuItem { Header = header };
        var validationError = _bus.Validate(commandText);
        if (validationError != null)
        {
            if (!_menusInitialized)
                throw InvalidMenuCommand(commandText, validationError);
            mi.IsEnabled = false;
            mi.ToolTip = $"指令当前不可用: {validationError}";
            return mi;
        }
        mi.Click += (_, _) => _ = _bus.ExecuteAsync(commandText, "UI");
        return mi;
    }

    private static InvalidOperationException InvalidMenuCommand(string commandText, string validationError)
        => new($"菜单引用了无效指令 [{commandText}]: {validationError}");

    /// <summary>
    /// UI-05.4:原状态栏右侧的布局名。3.1 取消独立标题栏后没有常驻文本位,
    /// 改挂菜单按钮提示 —— 需要时一悬停就能看到,不占任何常驻像素。
    /// </summary>
    private void UpdateLayoutIndicator()
        => MenuButton.ToolTip = $"菜单 (Alt) · 布局 {_docking.CurrentLayoutName}";
}
