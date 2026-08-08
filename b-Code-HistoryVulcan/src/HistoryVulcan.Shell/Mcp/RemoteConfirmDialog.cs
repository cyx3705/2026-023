using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace HistoryVulcan.Shell.Mcp;

/// <summary>
/// 宿主确认中继对话框(V2.2 CX-02):MCP 危险指令请求 → 宿主端弹框由人裁决,带倒计时。
/// 独立于总线 Confirmation(即使 --yes 生效也弹真实框),满足 CX-03。
/// 返回 true=允许 / false=拒绝 / null=超时(按拒绝处理)。
/// </summary>
internal static class RemoteConfirmDialog
{
    public static bool? Ask(Window owner, string prompt, int timeoutSeconds)
    {
        return owner.Dispatcher.Invoke(() =>
        {
            bool? result = null;
            var remaining = Math.Max(5, timeoutSeconds);

            var countdown = new TextBlock
            {
                Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(0, 12, 0, 0),
                Text = $"{remaining} 秒内未操作将自动拒绝",
            };

            var message = new TextBlock
            {
                Text = prompt,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460,
            };

            var yes = new Button { Content = "允许执行", Width = 96, Margin = new Thickness(0, 0, 8, 0), IsDefault = false };
            var no = new Button { Content = "拒绝", Width = 96, IsCancel = true, IsDefault = true };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
                Children = { yes, no },
            };

            var win = new Window
            {
                Title = "MCP 远程请求 · 需要确认",
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow,
                Content = new StackPanel
                {
                    Margin = new Thickness(20),
                    Children = { message, countdown, buttons },
                },
            };

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) =>
            {
                remaining--;
                if (remaining <= 0)
                {
                    timer.Stop();
                    result = null; // 超时
                    win.Close();
                    return;
                }

                countdown.Text = $"{remaining} 秒内未操作将自动拒绝";
            };

            yes.Click += (_, _) => { result = true; win.Close(); };
            no.Click += (_, _) => { result = false; win.Close(); };
            win.Closed += (_, _) => timer.Stop();

            timer.Start();
            win.ShowDialog();
            return result;
        });
    }
}
