namespace HistoryVulcan.Services.Modules;

/// <summary>Provides this HistoryVulcan public contract member.</summary>
public sealed record ModuleMeta(
    string ModuleName, string Description, string Author, string Version,
    bool Open, string AssemblyFile, int CommandCount, string Slot = "", bool Ui = false)
{
    /// <summary>当前加载快照中的唯一模块实例标识；重载后变化。</summary>
    public string InstanceId { get; init; } = "";
    /// <summary>Absolute runtime package path.</summary>
    public string? SourcePath { get; init; }

    /// <summary>Absolute runtime manifest path.</summary>
    public string? ManifestPath { get; init; }

    /// <summary>
    /// 上下文注入失败的原因；非空表示本模块**没有接上宿主**，它的指令一条都不会到位。
    /// </summary>
    public IReadOnlyList<string> AttachFailures { get; init; } = [];

    /// <summary>本模块是否真正接上了宿主。</summary>
    public bool Attached => AttachFailures.Count == 0;
}
