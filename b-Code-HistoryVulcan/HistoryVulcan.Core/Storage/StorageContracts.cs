// 存储契约：宿主内部持有的扁平设置。
//
// 5.0 起布局存取不再作为 Core 契约；界面布局由认领 ui.* 指令的模块自己解释。
// ISettingsService 仍供宿主自己与开发总线使用，不再注入给模块。

namespace HistoryVulcan.Core.Storage;

/// <summary>
/// 应用设置读写(F-01/F-03):扁平键值对,vulcan.app.set / vulcan.app.get 指令与派生应用共用。
/// </summary>
public interface ISettingsService
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    string? Get(string key);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    int GetInt(string key, int fallback);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    void Set(string key, string value);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    IReadOnlyList<KeyValuePair<string, string>> All();
}

