namespace BaseVariable;

/// <summary>
/// 模块契约的本地副本 —— 这是全体系中刻意保留的唯一一份副本。
///
/// 权威定义在 HistoryVulcan.Core 的 Modules/ModuleInfoBase.cs。引用 Core 的模块
/// (Janus / Mercury / Minerva / Diana)一律直接使用那一份:同时保留自带副本会造成
/// CS0433 类型二义性。
///
/// 本示例模块存在的意义正是证明另一条路径依然成立:宿主的识别逻辑
/// (<c>ModuleHost.IsModuleInfo</c>)按基类全名 "BaseVariable.ModuleInfoBase" 比对,
/// 与程序集无关,因此一个**不引用任何宿主程序集**的 DLL(见 DemoModule.csproj:
/// 无任何 PackageReference / ProjectReference)把契约类编译进自身,同样能被装载。
///
/// 修改本文件时必须与 Core 中的权威定义保持一致。
/// </summary>
public abstract class ModuleInfoBase
{
    /// <summary>模块名称(默认取程序集名);同时是指令域名。</summary>
    public virtual string ModuleName => GetType().Assembly.GetName().Name ?? "UnknownModule";

    /// <summary>模块描述</summary>
    public virtual string Description => "";

    /// <summary>作者</summary>
    public virtual string Author => "";

    /// <summary>版本号</summary>
    public virtual string Version => "v1.0.0";

    /// <summary>true = 全暴露:程序集内所有公共类的公共方法都托管;false = 精准暴露 MainClassType。</summary>
    public virtual bool Open => false;

    /// <summary>精准暴露模式下要托管的核心业务类;为 null 则不注册任何指令。</summary>
    public virtual Type? MainClassType => null;

    /// <summary>是否启用该模块(false 时整个模块不注册)。</summary>
    public virtual bool Enabled => true;
}
