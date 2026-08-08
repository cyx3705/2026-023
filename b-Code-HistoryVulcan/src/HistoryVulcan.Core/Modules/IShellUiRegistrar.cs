using HistoryVulcan.Core.Docking;

namespace HistoryVulcan.Core.Modules;

/// <summary>模块向宿主注册内嵌界面的唯一门面。</summary>
public interface IShellUiRegistrar
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    bool IsUiThread { get; }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    void Invoke(Action action);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    IDisposable RegisterToolWindow(ToolWindowDescriptor descriptor, string owner);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    void UnregisterToolWindow(string id);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    void UnregisterOwner(string owner);
}
