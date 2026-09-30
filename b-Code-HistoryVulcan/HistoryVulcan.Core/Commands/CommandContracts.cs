// 统一契约里的总线面（5.9.0，DEC-070）。
//
// 模块与宿主之间只有「执行指令、请求确认、登记指令」三件事。这里把它们写成窄接口：
// 5.9.0 期间 IModuleContext.Bus / RegisterCommands 仍给具体类（CommandBus / CommandRegistry 已实现这两个接口），
// 模块可以先把字段类型改成接口；6.0.0 起上下文只给接口，具体类收回宿主内部。

namespace HistoryVulcan.Core.Commands;

/// <summary>模块看得到的总线：执行指令与中途请求确认。目录、校验、补全一律走只读指令。</summary>
public interface ICommandBus
{
    /// <summary>执行一条指令文本。</summary>
    Task<CommandResult> ExecuteAsync(string text, string source, CancellationToken cancellation = default);

    /// <summary>处理器执行中途请求确认；没有前端时宿主按拒绝处理。</summary>
    bool RequestConfirmation(string prompt);
}

/// <summary>模块看得到的登记口：只能加指令，不能枚举、删除或改写宿主表。</summary>
public interface ICommandRegistrar
{
    /// <summary>登记一条指令；来源由宿主按模块盖章。</summary>
    void Register(CommandDescriptor descriptor);
}
