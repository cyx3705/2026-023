using HistoryVulcan.Core.Commands;
using HistoryVulcan.ServiceHost;
using HistoryVulcan.Services.Commands;
using HistoryVulcan.Services.Development;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// 6.1.1（DEC-073，REQ-HOST-090）：指令自描述就是说明书，宿主自己的指令也得照抄能跑。
/// 6.1.0 全量体检时宿主剩下的 4 处里，<c>vulcan.worktree.create</c> 的示例少了必填 <c>agent=</c>，照抄直接失败——
/// 这类错只有逐条解析示例才看得出来，所以这里按组合根同样的注册入口把宿主指令登记一遍逐条核对。
/// </summary>
public sealed class HostSelfDescriptionTests
{
    [Fact]
    public void HostCommandsWithParametersCarryRunnableExamples()
    {
        var registry = new CommandRegistry();
        var log = new NullLog();
        var bus = new CommandBus(registry, log);
        var settings = new MemorySettings();
        var composition = new ServiceComposition
        {
            ServiceName = "test",
            Registry = registry,
            Bus = bus,
            Log = log,
            Settings = settings,
        };
        ServiceCommands.RegisterAll(registry, composition, () => { }, "HistoryVulcan.exe");
        ServiceComposer.RegisterSettingCommands(registry, settings);
        CommandCatalogCommands.RegisterAll(registry);
        var development = new DevelopmentContext(bus, settings, Path.GetTempPath());
        WorktreeCommands.Register(registry, development);
        DevPipelineCommands.Register(registry, development);

        // 共享内置定义（vulcan.app.* 等）由前端与服务端各自 Bind，这里直接逐条 Bind 一份核对元数据。
        var descriptors = registry.All()
            .Concat(BuiltinCommandDefinitions.Names
                .Select(name => BuiltinCommandDefinitions.Bind(name, _ => Task.FromResult(CommandResult.Ok()))))
            .ToList();
        Assert.Contains(descriptors, item => item.Name == "vulcan.worktree.create");
        Assert.Contains(descriptors, item => item.Name == "vulcan.svc.autostart");

        var problems = descriptors
            .Where(item => item.Parameters.Count > 0)
            .SelectMany(Problems)
            .Distinct()
            .ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static IEnumerable<string> Problems(CommandDescriptor command)
    {
        if (string.IsNullOrWhiteSpace(command.Example))
        {
            yield return $"{command.Name}: 有参数却没有示例";
            yield break;
        }

        ParsedCommand parsed;
        try
        {
            parsed = CommandParser.Parse(command.Example);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            parsed = null!;
        }
        if (parsed == null)
        {
            yield return $"{command.Name}: 示例解析不了：{command.Example}";
            yield break;
        }
        if (!parsed.Name.Equals(command.Name, StringComparison.OrdinalIgnoreCase))
            yield return $"{command.Name}: 示例写的是另一条指令 {parsed.Name}";

        var declared = command.Parameters.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in parsed.Named.Keys.Where(name => !declared.Contains(name)))
            yield return $"{command.Name}: 示例用了未声明的参数 {name}=";

        var positional = command.Parameters.Where(item => item.Position.HasValue).ToList();
        if (parsed.Positionals.Count > positional.Count)
            yield return $"{command.Name}: 示例有多余的位置参数";

        foreach (var required in command.Parameters.Where(item => item.Required))
        {
            var byName = parsed.Named.ContainsKey(required.Name);
            var byPosition = required.Position is { } position && position < parsed.Positionals.Count;
            if (!byName && !byPosition)
                yield return $"{command.Name}: 示例缺少必填参数 {required.Name}=";
        }
    }
}
