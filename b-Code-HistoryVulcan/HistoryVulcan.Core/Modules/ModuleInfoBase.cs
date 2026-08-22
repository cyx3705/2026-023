namespace BaseVariable;

/// <summary>
/// 模块识别契约(MD-02):宿主按基类全名 "BaseVariable.ModuleInfoBase" 做鸭子类型识别。
///
/// 本类型是该契约的权威定义。此前它以逐字副本形式存在于六处(Janus / Mercury / Minerva /
/// Diana 各一份、样例一份、宿主测试夹具内联一份),而这些模块本就已经引用 HistoryVulcan.Core,
/// 副本当初"避免依赖宿主程序集"的理由早已不成立;副本之间也已开始漂移(注释与默认值
/// 各不相同,编译器无从校验)。
///
/// 命名空间保持 BaseVariable 而非 HistoryVulcan.Core.Modules:宿主的识别逻辑
/// (<c>ModuleHost.IsModuleInfo</c>)按全名比对基类链,改名会使既有已编译模块无法装载。
///
/// 装载不要求模块引用本程序集:识别只看基类全名,因此把契约类直接编译进模块自身
/// (见 b-Code-Samples/ModuleInfoBase.cs 的零依赖示范)同样有效。引用 Core 的模块
/// 必须直接使用本类型:同时保留自带副本会造成 CS0433 类型二义性。
/// </summary>
public abstract class ModuleInfoBase
{
    /// <summary>模块名称(默认取程序集名);同时是指令域名。</summary>
    public virtual string ModuleName => GetType().Assembly.GetName().Name ?? "UnknownModule";

    /// <summary>模块描述。</summary>
    public virtual string Description => "";

    /// <summary>作者。</summary>
    public virtual string Author => "";

    /// <summary>版本号。</summary>
    public virtual string Version => "v1.0.0";

    /// <summary>true = 全暴露:程序集内所有公共类的公共方法都托管;false = 精准暴露 MainClassType。</summary>
    public virtual bool Open => false;

    /// <summary>精准暴露模式下要托管的核心业务类;为 null 则不注册任何指令。</summary>
    public virtual Type? MainClassType => null;

    /// <summary>是否启用该模块(false 时整个模块不注册)。</summary>
    public virtual bool Enabled => true;
}
