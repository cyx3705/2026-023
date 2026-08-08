using System.Windows;
using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.Shell;

/// <summary>
/// 二次确认的模态对话框实现(§5.2 拦截器;手输指令路径的危险操作闸口)。
/// 编组到 UI 线程弹窗,任意线程可调用。
/// </summary>
public sealed class MessageBoxConfirmation : IConfirmationService
{
    private readonly Window _owner;

    public MessageBoxConfirmation(Window owner) => _owner = owner;

    public bool Confirm(string prompt)
        => _owner.Dispatcher.Invoke(() =>
            MessageBox.Show(_owner, prompt, "需要确认",
                MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes);
}
