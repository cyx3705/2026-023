using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.Services.Modules;

/// <summary>
/// 宿主总线的模块视图（6.0.0，DEC-071）：只有执行、安静执行与中途确认三件事，来源由宿主盖章。
/// </summary>
internal sealed class ModuleBus(CommandBus bus, string owner) : ICommandBus
{
    public Task<CommandResult> ExecuteAsync(string text, string source, CancellationToken cancellation = default)
        => bus.ExecuteAsync(text, ModuleSource.Stamp(owner, source), cancellation);

    public Task<CommandResult> InvokeAsync(string text, string source, CancellationToken cancellation = default)
        => bus.InvokeAsync(text, ModuleSource.Stamp(owner, source), cancellation);

    public bool RequestConfirmation(string prompt) => bus.RequestConfirmation(prompt);
}
