namespace HistoryVulcan.Core.Storage;

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
