// 统一契约里的总线面（5.9.0 起，6.0.0 收口，DEC-070 / DEC-071）。
//
// 模块与宿主之间只有「执行指令、请求确认、登记指令」三件事。6.0.0 起上下文只给这两个窄接口，
// 总线与注册表的具体类收回宿主内部；目录、校验、补全一律走只读指令。

namespace HistoryVulcan.Core.Commands;

/// <summary>模块看得到的总线：执行指令与中途请求确认。目录、校验、补全一律走只读指令。</summary>
/// <remarks>
/// 模块经上下文拿到的总线由宿主给来源盖章：模块传入的 <c>source</c> 变成
/// <c>module:&lt;模块名&gt;:&lt;source&gt;</c>（空串时为 <c>module:&lt;模块名&gt;</c>）。
/// 处理器看到的 <see cref="CommandContext.Source"/> 就是盖章后的值。
/// </remarks>
public interface ICommandBus
{
    /// <summary>执行一条指令文本：回显、确认、结果进宿主日志，并发布 <c>vulcan.command.executed</c>。</summary>
    Task<CommandResult> ExecuteAsync(string text, string source, CancellationToken cancellation = default);

    /// <summary>
    /// 安静执行（6.0.0）：不回显、不记结果、不发布 <c>vulcan.command.executed</c>；确认闸口照旧。
    /// 用于取数这类「不是操作」的调用，例如界面刷新时读 <c>*.ui.data</c>。
    /// </summary>
    Task<CommandResult> InvokeAsync(string text, string source, CancellationToken cancellation = default);

    /// <summary>处理器执行中途请求确认；没有前端时宿主按拒绝处理。</summary>
    bool RequestConfirmation(string prompt);
}

/// <summary>模块看得到的登记口：只能加指令，不能枚举、删除或改写宿主表。</summary>
public interface ICommandRegistrar
{
    /// <summary>登记一条指令；来源由宿主按模块盖章。</summary>
    void Register(CommandDescriptor descriptor);
}
