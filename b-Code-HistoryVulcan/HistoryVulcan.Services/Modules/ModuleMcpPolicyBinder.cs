using HistoryVulcan.Core.Mcp;

namespace HistoryVulcan.Services.Modules;

/// <summary>
/// 把模块宿主的解析器接到进程级 <see cref="McpExposurePolicy"/> 上,并负责还原。
///
/// 从 <see cref="ModuleHost"/> 抽出:这段逻辑管的既不是模块装载也不是命令注册,而是
/// **一对进程级静态属性的借用与归还**——它有自己独立的生命周期(绑定一次、退出时还原),
/// 却曾与快照构建、注册表换血挤在同一个类型里,还带着四个只为它存在的字段
/// (_mcpPolicyBound、_previous* 两个、以及两个缓存的解析器委托)。
///
/// 解析器读的是宿主的实时状态,因此这里只持有委托、不复制数据:一次热重载改变了暴露
/// 结果,不需要重建 MCP 网关。
///
/// 还原时用引用相等判断:只有当前仍是自己装上去的那一对委托才归还,避免把后来者的
/// 绑定覆盖掉——多个宿主实例并存(例如烟测)时这一点是必需的。
/// </summary>
internal sealed class ModuleMcpPolicyBinder
{
    private readonly Func<string, string?> _moduleOfCommand;
    private readonly Func<string, string?> _moduleExposure;
    private Func<string, string?>? _previousModuleOfCommand;
    private Func<string, string?>? _previousModuleExposure;
    private bool _bound;

    internal ModuleMcpPolicyBinder(
        Func<string, string?> moduleOfCommand,
        Func<string, string?> moduleExposure)
    {
        _moduleOfCommand = moduleOfCommand;
        _moduleExposure = moduleExposure;
    }

    /// <summary>接管进程级策略;重复调用只在首次记录被覆盖的前值。</summary>
    internal void Bind()
    {
        if (!_bound)
        {
            _previousModuleOfCommand = McpExposurePolicy.ModuleOfCommand;
            _previousModuleExposure = McpExposurePolicy.ModuleExposure;
            _bound = true;
        }

        McpExposurePolicy.ModuleOfCommand = _moduleOfCommand;
        McpExposurePolicy.ModuleExposure = _moduleExposure;
    }

    /// <summary>归还进程级策略;只有当前仍是本实例装上去的委托才还原。</summary>
    internal void Unbind()
    {
        if (!_bound)
            return;

        if (ReferenceEquals(McpExposurePolicy.ModuleOfCommand, _moduleOfCommand))
            McpExposurePolicy.ModuleOfCommand = _previousModuleOfCommand;
        if (ReferenceEquals(McpExposurePolicy.ModuleExposure, _moduleExposure))
            McpExposurePolicy.ModuleExposure = _previousModuleExposure;
        _bound = false;
    }
}
