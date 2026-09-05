using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryVulcan.ServiceHost;

/// <summary>Refuses requests until a module installs an interactive confirmation service.</summary>
internal sealed class DenyConfirmation(IShellLog log) : IConfirmationService
{
    public bool Confirm(string prompt)
    {
        log.Warn("confirm", "界面未装载，确认请求已拒绝。");
        return false;
    }
}
