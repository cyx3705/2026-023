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
