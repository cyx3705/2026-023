// 存储契约：宿主拥有的两个存储——扁平设置与界面布局。
//
// 两者都是「宿主持有、模块与派生应用共用」的读写口，实现都在
// HistoryVulcan.Services。分成两个不到 30 行的文件不携带任何信息。

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

/// <summary>
/// 布局文件存取抽象(F-01:布局采用停靠库序列化格式单独成文件)。
/// Shell 只负责序列化/反序列化,文件位置与读写由 Services 层实现。
/// </summary>
public interface ILayoutStore
{
    /// <summary>读取“当前布局”(启动恢复用);不存在返回 null。</summary>
    string? ReadCurrent();

    /// <summary>写入“当前布局”(退出自动保存用)。</summary>
    void WriteCurrent(string payload);

    /// <summary>删除“当前布局”(损坏回退时清理,N-06)。</summary>
    void DeleteCurrent();

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    string? ReadNamed(string name);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    void WriteNamed(string name, string payload);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    IReadOnlyList<string> ListNamed();
}
