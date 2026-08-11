using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Extensibility.Panels;

namespace HistoryVulcan.Shell.Panels;

/// <summary>
/// 一个控制面板窗口的内容(§4.5):按声明构建控件栅格,
/// 按钮点击 = 收集同面板输入控件当前值 → 以 {控件id} 填入指令模板 →
/// 交指令总线执行 → 控制台回显(P-03,验收 6)。
/// </summary>
public sealed partial class PanelView : UserControl
{
    private readonly CommandBus _bus;
    private readonly IShellLog _log;
    private readonly Dictionary<string, Func<string>> _getters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Action<string>> _setters = new(StringComparer.OrdinalIgnoreCase);
    private PanelDefinition _def;

    public PanelView(PanelDefinition def, CommandBus bus, IShellLog log)
    {
        _bus = bus;
        _log = log;
        _def = def;
        // UI-01:底色由窗格卡片提供,面板自身不再画一块白
        Background = Brushes.Transparent;
        Rebuild(def);
    }

    public string PanelId => _def.Id;

    /// <summary>vulcan.ui.panelreload:按新声明原地重建内容(P-08)。</summary>
    public void Rebuild(PanelDefinition def)
    {
        _def = def;
        _getters.Clear();
        _setters.Clear();

        var grid = new Grid { Margin = new Thickness(10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 56 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var row = 0;
        foreach (var control in def.Controls)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            BuildControl(grid, row, control);
            row++;
        }

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = grid,
        };
    }

    /// <summary>vulcan.ui.panelset 反向驱动(P-07):程序向面板控件回写值。</summary>
    public bool TrySetValue(string controlId, string value)
    {
        if (!_setters.TryGetValue(controlId, out var setter))
            return false;
        setter(value);
        return true;
    }

    /// <summary>可反向驱动的控件 id 清单(vulcan.ui.panelset 报错提示用)。</summary>
    public IReadOnlyList<string> ControlIds => _setters.Keys.ToList();

    // ---------------------------------------------------------------- 控件构建(P-02)

    private void BuildControl(Grid grid, int row, PanelControl c)
    {
        FrameworkElement input;
        switch (c.Type.ToLowerInvariant())
        {
            case "button":
                {
                    var button = new Button
                    {
                        Content = c.Label ?? "执行",
                        Margin = new Thickness(0, 6, 0, 2),
                        Padding = new Thickness(10, 4, 10, 4),
                    };
                    if (c.Style == "danger")
                    {
                        // UI-07:令牌经资源引用延迟解析 —— 控件此刻尚未进入可视树,
                        // 直接 TryFindResource 取不到窗体资源。
                        button.SetResourceReference(ForegroundProperty, "Shell.Brush.Danger");
                    }

                    var command = c.Command;
                    button.Click += (_, _) => FireCommand(command);
                    Grid.SetRow(button, row);
                    Grid.SetColumn(button, 0);
                    Grid.SetColumnSpan(button, 2);
                    grid.Children.Add(button);
                    return;
                }

            case "label":
                {
                    var text = new TextBlock
                    {
                        Text = c.Default ?? "",
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    text.SetResourceReference(ForegroundProperty, "Shell.Brush.TextSecondary");
                    if (c.Id != null)
                    {
                        _getters[c.Id] = () => text.Text;
                        _setters[c.Id] = v => text.Text = v;
                    }

                    input = text;
                    break;
                }

            case "combo":
                {
                    var combo = new ComboBox { ItemsSource = c.Items ?? [] };
                    combo.SelectedItem = c.Default != null && (c.Items?.Contains(c.Default) ?? false)
                        ? c.Default
                        : c.Items?.FirstOrDefault();
                    if (c.Id != null)
                    {
                        _getters[c.Id] = () => combo.SelectedItem as string ?? "";
                        _setters[c.Id] = v => combo.SelectedItem =
                            (c.Items ?? []).FirstOrDefault(i => i.Equals(v, StringComparison.OrdinalIgnoreCase));
                    }

                    input = combo;
                    break;
                }

            case "check":
                {
                    var check = new CheckBox
                    {
                        IsChecked = c.Default is "true" or "1",
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    if (c.Id != null)
                    {
                        _getters[c.Id] = () => check.IsChecked == true ? "true" : "false";
                        _setters[c.Id] = v => check.IsChecked = v is "true" or "1";
                    }

                    input = check;
                    break;
                }

            case "slider":
                {
                    var slider = new Slider
                    {
                        Minimum = c.Min ?? 0,
                        Maximum = c.Max ?? 100,
                        TickFrequency = c.Step ?? 1,
                        IsSnapToTickEnabled = c.Step != null,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    if (double.TryParse(c.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv))
                        slider.Value = dv;

                    var valueText = new TextBlock
                    {
                        Width = 44,
                        TextAlignment = TextAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    valueText.SetResourceReference(ForegroundProperty, "Shell.Brush.TextSecondary");
                    valueText.Text = Format(slider.Value);
                    slider.ValueChanged += (_, _) => valueText.Text = Format(slider.Value);

                    var panel = new DockPanel();
                    DockPanel.SetDock(valueText, Dock.Right);
                    panel.Children.Add(valueText);
                    panel.Children.Add(slider);

                    if (c.Id != null)
                    {
                        _getters[c.Id] = () => Format(slider.Value);
                        _setters[c.Id] = v =>
                        {
                            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var nv))
                                slider.Value = nv;
                        };
                    }

                    input = panel;
                    break;
                }

            case "file":
            case "dir":
                {
                    var box = new TextBox { Text = c.Default ?? "", VerticalContentAlignment = VerticalAlignment.Center };
                    var browse = new Button { Content = "…", Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(4, 0, 0, 0) };
                    var isDir = c.Type.Equals("dir", StringComparison.OrdinalIgnoreCase);
                    browse.Click += async (_, _) =>
                    {
                        var command = isDir ? "vulcan.ui.selectdirectory" : "vulcan.ui.selectfile";
                        var result = await _bus.ExecuteAsync(command, "UI");
                        if (result.Success
                            && CommandResultData.TryRead<string>(result.Data, out var selected))
                            box.Text = selected;
                    };

                    var panel = new DockPanel();
                    DockPanel.SetDock(browse, Dock.Right);
                    panel.Children.Add(browse);
                    panel.Children.Add(box);

                    if (c.Id != null)
                    {
                        _getters[c.Id] = () => box.Text;
                        _setters[c.Id] = v => box.Text = v;
                    }

                    input = panel;
                    break;
                }

            case "number":
            case "text":
            default:
                {
                    var box = new TextBox { Text = c.Default ?? "", VerticalContentAlignment = VerticalAlignment.Center };
                    if (c.Id != null)
                    {
                        _getters[c.Id] = () => box.Text;
                        _setters[c.Id] = v => box.Text = v;
                    }

                    input = box;
                    break;
                }
        }

        var label = new TextBlock
        {
            Text = c.Label ?? c.Id ?? "",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        label.SetResourceReference(ForegroundProperty, "Shell.Brush.TextPrimary");
        label.SetValue(Grid.RowProperty, row);
        label.SetValue(Grid.ColumnProperty, 0);
        input.Margin = new Thickness(0, 3, 0, 3);
        input.SetValue(Grid.RowProperty, row);
        input.SetValue(Grid.ColumnProperty, 1);
        grid.Children.Add(label);
        grid.Children.Add(input);
    }

    private static string Format(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- 按钮 → 指令(P-03 / P-04)

    private void FireCommand(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            _log.Error("panel", $"面板 {_def.Id} 的按钮未配置 command");
            return;
        }

        // P-04 校验:必填与数值范围
        foreach (var c in _def.Controls)
        {
            if (c.Id == null || !_getters.TryGetValue(c.Id, out var getter))
                continue;
            var value = getter();

            if (c.Required && string.IsNullOrWhiteSpace(value))
            {
                _log.Error("panel", $"面板 {_def.Id}: {c.Label ?? c.Id} 为必填项");
                return;
            }

            if (c.Type.Equals("number", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                {
                    _log.Error("panel", $"面板 {_def.Id}: {c.Label ?? c.Id} 应为数值,实际 \"{value}\"");
                    return;
                }

                if (n < c.Min || n > c.Max)
                {
                    _log.Error("panel", $"面板 {_def.Id}: {c.Label ?? c.Id} 超出范围 [{c.Min}, {c.Max}]: {n}");
                    return;
                }
            }
        }

        // P-03:{控件id} 占位替换为当前值
        var unknown = new List<string>();
        var command = PlaceholderPattern().Replace(template, m =>
        {
            var id = m.Groups[1].Value;
            if (_getters.TryGetValue(id, out var getter))
                return getter();
            unknown.Add(id);
            return m.Value;
        });

        if (unknown.Count > 0)
        {
            _log.Error("panel", $"面板 {_def.Id}: 指令模板引用了不存在的控件 {{{string.Join("}, {", unknown)}}}");
            return;
        }

        _ = _bus.ExecuteAsync(command, "UI");
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex PlaceholderPattern();
}
