using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// 两级（运行 / 询问）与提示语的分工。
/// </summary>
/// <remarks>
/// 4.8.0 之前「问不问」由三个成员共同表达：<c>Dangerous</c>、<c>ConfirmPrompt</c>
/// 与合成的 <c>IsDangerous</c>。三份表达意味着不同消费方可以对同一条指令给出不同
/// 答案，这里把收敛后的唯一权威钉住。
/// </remarks>
public sealed class CommandLevelGateTests
{
    /// <summary>
    /// <c>ConfirmPrompt</c> 上的 null 有两种含义，**总线必须分得清**。
    /// </summary>
    /// <remarks>
    /// 「没有 ConfirmPrompt」是没写文案，总线补一句缺省的照问；
    /// 「ConfirmPrompt 调用后返回 null」是这次不用问，直接放行。
    ///
    /// 混同的后果不对称，所以两个方向都要验：
    /// 当成「都要问」，<c>janus.github.identity</c> 在 <c>apply=false</c> 的只读那次
    /// 也弹框；当成「都不问」，写了级别却没写文案的指令闸口整个消失。
    /// </remarks>
    [Fact]
    public async Task AMissingPromptStillAsksButAPromptReturningNullDoesNot()
    {
        var registry = new CommandRegistry();
        var executed = new List<string>();
        var prompts = new List<string>();

        registry.Register(new CommandDescriptor
        {
            Name = "sample.nofile",
            Summary = "没写文案，仍然要问",
            Level = CommandLevel.Ask,
            Handler = CommandDescriptor.Sync(_ =>
            {
                executed.Add("nofile");
                return CommandResult.Ok("ran");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "sample.conditional",
            Summary = "只在 apply=true 时问",
            Level = CommandLevel.Ask,
            Parameters = [new ParameterSpec { Name = "apply", Description = "apply", Type = ParamType.Bool }],
            ConfirmPrompt = context => context.GetBool("apply") ? "确认写入？" : null,
            Handler = CommandDescriptor.Sync(_ =>
            {
                executed.Add("conditional");
                return CommandResult.Ok("ran");
            }),
        });

        var bus = new CommandBus(registry, new NullLog())
        {
            Confirmation = new RecordingConfirmation(prompts),
        };

        // 没写文案：总线补缺省提示，闸口照样存在。
        Assert.True((await bus.ExecuteAsync("sample.nofile", "Test")).Success);
        Assert.Single(prompts);
        Assert.Contains("sample.nofile", prompts[0], StringComparison.Ordinal);

        // 文案返回 null：这次豁免，不问。
        Assert.True((await bus.ExecuteAsync("sample.conditional apply=false", "Test")).Success);
        Assert.Single(prompts);

        // 文案返回文本：照问。
        Assert.True((await bus.ExecuteAsync("sample.conditional apply=true", "Test")).Success);
        Assert.Equal(2, prompts.Count);
        Assert.Equal("确认写入？", prompts[1]);

        Assert.Equal(["nofile", "conditional", "conditional"], executed);
    }

    /// <summary>级别为「运行」时，即便挂着文案也一律不问——但那种组合根本注册不进去。</summary>
    [Fact]
    public void APromptWithoutTheAskLevelIsRefusedAtRegistration()
    {
        var registry = new CommandRegistry();

        var error = Assert.Throws<ArgumentException>(() => registry.Register(new CommandDescriptor
        {
            Name = "sample.mismatch",
            Summary = "写了文案却没升级别",
            ConfirmPrompt = _ => "确认？",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        }));

        // 拒绝理由必须说破因果，否则作者只会把文案删掉了事——
        // 而真正该做的是把级别升上去。
        Assert.Contains(nameof(CommandLevel.Ask), error.Message, StringComparison.Ordinal);
        Assert.Contains("级别才决定", error.Message, StringComparison.Ordinal);
    }

    /// <summary>没有确认通道时，「询问」级一律拒绝执行，而不是放行。</summary>
    [Fact]
    public async Task WithoutAConfirmationChannelTheAskLevelRefusesInsteadOfRunning()
    {
        var registry = new CommandRegistry();
        var executed = false;
        registry.Register(new CommandDescriptor
        {
            Name = "sample.ask",
            Summary = "ask",
            Level = CommandLevel.Ask,
            Handler = CommandDescriptor.Sync(_ =>
            {
                executed = true;
                return CommandResult.Ok();
            }),
        });

        var result = await new CommandBus(registry, new NullLog()).ExecuteAsync("sample.ask", "Test");

        Assert.False(result.Success);
        Assert.False(executed);
    }

    private sealed class RecordingConfirmation(List<string> prompts) : IConfirmationService
    {
        public bool Confirm(string prompt)
        {
            prompts.Add(prompt);
            return true;
        }
    }
}
