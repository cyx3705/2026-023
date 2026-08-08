namespace HistoryVulcan.Core.Commands;

/// <summary>命令执行位置。服务化宿主可把窗口命令中继给已连接的前端。</summary>
public enum CommandExecutionSite
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Local,
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Frontend,
}
