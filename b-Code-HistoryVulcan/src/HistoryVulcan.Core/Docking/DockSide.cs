namespace HistoryVulcan.Core.Docking;

/// <summary>
/// 工具窗口相对主程序窗体的停靠方位(§4.1 W-03 五种落点)。
/// </summary>
public enum DockSide
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Left = 0,
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Right = 1,
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Top = 2,
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Bottom = 3,
    /// <summary>并入目标标签组(vulcan.ui.dock pos=tab target=...)。</summary>
    Tab = 4,
    /// <summary>占据中央工作区；多个中央窗口组成标签组。</summary>
    Center = 5,
}
