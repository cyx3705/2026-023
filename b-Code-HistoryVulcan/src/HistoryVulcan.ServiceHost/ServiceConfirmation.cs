using System.Windows;
using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.ServiceHost;

/// <summary>由服务进程自己的 Dispatcher 显示的人工确认通道。</summary>
public sealed class ServiceConfirmation : IConfirmationService
{
    public bool Confirm(string prompt)
        => Application.Current.Dispatcher.Invoke(() =>
            MessageBox.Show(
                prompt,
                "服务请求确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No,
                MessageBoxOptions.DefaultDesktopOnly) == MessageBoxResult.Yes);

    public bool? ConfirmRemote(string client, string prompt, int timeoutSeconds)
        => Confirm(prompt);
}
