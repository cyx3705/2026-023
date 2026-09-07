namespace DemoModule;

/// <summary>起步示例的固定身份；必须与 csproj Version 和 module.manifest.json 一致。</summary>
public static class Identity
{
    /// <summary>运行包与 manifest 使用的模块名。</summary>
    public const string Name = "DemoModule";

    /// <summary>运行包与 manifest 使用的版本。</summary>
    public const string Version = "1.0.0";
}

/// <summary>模块识别入口。命令全部由 <see cref="Module"/> 显式登记，不走反射全暴露。</summary>
public sealed class ModuleInfo : BaseVariable.ModuleInfoBase
{
    /// <inheritdoc />
    public override string ModuleName => Identity.Name;

    /// <inheritdoc />
    public override string Description => "HistoryVulcan 起步示例：显式登记命令的最小完整包。";

    /// <inheritdoc />
    public override string Author => "HistoryVulcan";

    /// <inheritdoc />
    public override string Version => Identity.Version;

    /// <inheritdoc />
    public override bool Open => false;

    /// <inheritdoc />
    public override Type? MainClassType => null;
}
