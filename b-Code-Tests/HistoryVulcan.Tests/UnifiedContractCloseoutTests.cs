using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.ServiceHost;
using HistoryVulcan.Services.Commands;
using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// 6.0.0 统一契约收口（REQ-HOST-084…088，DEC-071）：契约面白名单、载荷只交 JSON、来源盖章、
/// 宿主源码不认模块、日志只写。
/// </summary>
public sealed class UnifiedContractCloseoutTests
{
    // ------------------------------------------------------------ REQ-HOST-084 契约面白名单（G1 / G5）

    /// <summary>设计稿第四节的白名单。多出一个公开类型即失败：契约面只能按主版本扩。</summary>
    private static readonly string[] CoreContract =
    [
        "BaseVariable.ModuleInfoBase",
        "HistoryVulcan.Core.Commands.BuiltinCommandDefinitions",
        "HistoryVulcan.Core.Commands.CommandContext",
        "HistoryVulcan.Core.Commands.CommandDescriptor",
        "HistoryVulcan.Core.Commands.CommandLevel",
        "HistoryVulcan.Core.Commands.CommandParser",
        "HistoryVulcan.Core.Commands.CommandResult",
        "HistoryVulcan.Core.Commands.CommandSyntaxException",
        "HistoryVulcan.Core.Commands.ICommandBus",
        "HistoryVulcan.Core.Commands.ICommandRegistrar",
        "HistoryVulcan.Core.Commands.ModuleDomainNaming",
        "HistoryVulcan.Core.Commands.ParamType",
        "HistoryVulcan.Core.Commands.ParameterSpec",
        "HistoryVulcan.Core.Commands.ParsedCommand",
        "HistoryVulcan.Core.Logging.IModuleLog",
        "HistoryVulcan.Core.Logging.ModuleLogExtensions",
        "HistoryVulcan.Core.Logging.ShellLogLevel",
        "HistoryVulcan.Core.Modules.BusEvent",
        "HistoryVulcan.Core.Modules.HostRunMode",
        "HistoryVulcan.Core.Modules.IFrontend",
        "HistoryVulcan.Core.Modules.IModuleContext",
        "HistoryVulcan.Core.Modules.IModuleContextAware",
        "HistoryVulcan.Core.Modules.IModuleEnvironment",
        "HistoryVulcan.Core.Modules.ModuleCommandAttribute",
    ];

    [Fact]
    public void CorePublicSurfaceIsExactlyTheContractWhitelist()
    {
        var exported = typeof(ICommandBus).Assembly.GetExportedTypes()
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(CoreContract.Order(StringComparer.Ordinal), exported);
    }

    [Fact]
    public void HostImplementationAssembliesExportNothing()
    {
        Assert.Empty(typeof(ModuleHost).Assembly.GetExportedTypes());
        Assert.Empty(typeof(ServiceComposer).Assembly.GetExportedTypes());
    }

    [Fact]
    public void ModuleContextHandsOutOnlyNarrowInterfaces()
    {
        var context = typeof(IModuleContext);
        Assert.Equal(typeof(ICommandBus), context.GetProperty(nameof(IModuleContext.Bus))!.PropertyType);
        Assert.Equal(typeof(IModuleLog), context.GetProperty(nameof(IModuleContext.Log))!.PropertyType);
        Assert.Equal(
            typeof(Action<ICommandRegistrar>),
            context.GetMethod(nameof(IModuleContext.RegisterCommands))!.GetParameters().Single().ParameterType);
        Assert.False(typeof(CommandBus).IsPublic);
        Assert.False(typeof(CommandRegistry).IsPublic);
    }

    // ------------------------------------------------------------ REQ-HOST-085 载荷只交 JSON（G4）

    [Fact]
    public async Task CatalogDataIsJsonWithAGrowOnlyShape()
    {
        var registry = new CommandRegistry();
        var bus = new CommandBus(registry, new NullLog());
        CommandCatalogCommands.RegisterAll(registry);
        registry.Register(new CommandDescriptor
        {
            Name = "probe.thing.drop",
            Summary = "probe",
            Level = CommandLevel.Ask,
            Annotations = new Dictionary<string, string> { ["ui.page"] = "probe" },
            Parameters = [new ParameterSpec { Name = "id", Description = "id", Required = true, Position = 0 }],
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        }, "module:HistoryProbe");

        var listed = await bus.InvokeAsync("vulcan.command.list domain=probe", "test");
        var row = Assert.Single(Assert.IsType<JsonElement>(listed.Data).EnumerateArray());
        AssertHasFields(row,
            "commandName", "domain", "summary", "example", "parameterCount", "source", "sourceDetail",
            "dangerous", "requiresUiThread", "readonly", "hiddenReason", "commandClass", "method",
            "requiresConfirmation", "allowUnspecifiedParameters", "parameters", "annotations");
        var parameter = Assert.Single(row.GetProperty("parameters").EnumerateArray());
        AssertHasFields(parameter, "name", "type", "required", "default", "position", "allowedValues", "description");
        Assert.Equal("probe", row.GetProperty("annotations").GetProperty("ui.page").GetString());

        var shown = await bus.InvokeAsync("vulcan.command.show probe.thing.drop", "test");
        AssertHasFields(Assert.IsType<JsonElement>(shown.Data), "command", "parameters", "annotations");

        foreach (var text in new[]
                 {
                     "vulcan.command.domains", "vulcan.command.revision", "vulcan.command.suggest probe.thing",
                     "vulcan.command.validate probe.thing.drop", "vulcan.cli.list",
                 })
        {
            var result = await bus.InvokeAsync(text, "test");
            Assert.True(result.Success, $"{text}: {result.Message}");
            Assert.IsType<JsonElement>(result.Data);
        }
    }

    [Fact]
    public void ModuleListShapeKeepsItsFields()
    {
        var meta = new ModuleMeta("HistoryProbe", "d", "a", "1.0.0", false, "HistoryProbe.dll", 3, "slot", Ui: true)
        {
            DataDirectory = @"C:\data\HistoryProbe",
        };
        var element = BusJson.ToElement(new[] { meta })[0];
        AssertHasFields(element,
            "moduleName", "description", "author", "version", "open", "assemblyFile", "commandCount", "slot",
            "ui", "instanceId", "sourcePath", "manifestPath", "attachFailures", "dataDirectory", "attached");
    }

    [Fact]
    public async Task LogRecentHasTheSameShapeAsTheLogEvent()
    {
        var log = new BufferLog();
        var registry = new CommandRegistry();
        var bus = new CommandBus(registry, log);
        ServiceComposer.RegisterLogRecentCommand(registry, log);
        for (var i = 0; i < 5; i++)
            log.Info("probe", $"line {i}");
        log.Warn(BusEventHub.LogCategory, "hub diagnostics");

        var recent = await bus.InvokeAsync("vulcan.log.recent count=3", "test");
        var entries = Assert.IsType<JsonElement>(recent.Data).EnumerateArray().ToList();
        Assert.Equal(["line 3", "line 4"], entries.Select(entry => entry.GetProperty("message").GetString()));
        AssertHasFields(entries[0], "time", "level", "category", "message");
        Assert.Equal("info", entries[0].GetProperty("level").GetString());
    }

    private static void AssertHasFields(JsonElement element, params string[] names)
    {
        var present = element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        var missing = names.Where(name => !present.Contains(name)).ToList();
        Assert.True(missing.Count == 0, "契约字段缺失：" + string.Join(", ", missing));
    }

    // ------------------------------------------------------------ REQ-HOST-086 来源盖章

    [Fact]
    public async Task ModuleBusStampsTheCallerAndKeepsTheInnerLabel()
    {
        var registry = new CommandRegistry();
        var bus = new CommandBus(registry, new NullLog());
        registry.Register(new CommandDescriptor
        {
            Name = "probe.who",
            Summary = "echo source",
            Handler = CommandDescriptor.Sync(ctx => CommandResult.Ok(ctx.Source)),
        });

        ICommandBus moduleBus = new ModuleBus(bus, "HistoryProbe");
        Assert.Equal("module:HistoryProbe", (await moduleBus.ExecuteAsync("probe.who", "")).Message);
        Assert.Equal("module:HistoryProbe:UI", (await moduleBus.InvokeAsync("probe.who", "UI")).Message);
        Assert.Equal("", ModuleSource.Innermost("module:HistoryProbe"));
        Assert.Equal("mcp:client", ModuleSource.Innermost("module:A:module:B:mcp:client"));
        Assert.Equal("手动", ModuleSource.Innermost("手动"));
    }

    // ------------------------------------------------------------ REQ-HOST-087 宿主源码不认模块（G2 / G3）

    private static readonly Regex ModuleName = new(
        @"(?i)\b(History)?(Diana|Janus|Mercury|Minerva|Aurora|Portunus|Juno|Apollo|Strenua|Vesta)\b",
        RegexOptions.Compiled);

    [Fact]
    public void HostSourceNamesNoModuleOutsideCommentsAndExamples()
    {
        var root = Path.Combine(RepositoryPaths.Root(), "b-Code-HistoryVulcan");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (Regex.IsMatch(file, @"\\(bin|obj)\\"))
                continue;
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var code = lines[index].Trim();
                if (code.StartsWith("//", StringComparison.Ordinal) || !ModuleName.IsMatch(code))
                    continue;
                // 示例、参数说明与隐藏理由是写给人看的字符串，允许点名。
                if (code.Contains("Example =", StringComparison.Ordinal)
                    || code.Contains("例如", StringComparison.Ordinal)
                    || code.Contains("HiddenReason =", StringComparison.Ordinal))
                    continue;
                offenders.Add($"{Path.GetRelativePath(root, file)}:{index + 1}: {code}");
            }
        }

        Assert.True(offenders.Count == 0, "宿主源码点名了模块：\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void HostSourceHasNoPerModulePrefixChecks()
    {
        var root = Path.Combine(RepositoryPaths.Root(), "b-Code-HistoryVulcan");
        var pattern = new Regex(
            @"StartsWith\(\s*""(diana|janus|mercury|minerva|aurora|portunus|juno|apollo|strenua)\.",
            RegexOptions.IgnoreCase);
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Regex.IsMatch(file, @"\\(bin|obj)\\"))
            .Where(file => pattern.IsMatch(File.ReadAllText(file)))
            .ToList();
        Assert.Empty(offenders);
    }

    // ------------------------------------------------------------ REQ-HOST-088 日志只写

    [Fact]
    public void ModuleLogIsWriteOnly()
    {
        var members = typeof(IModuleLog).GetMembers(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal([nameof(IModuleLog.Log)], members.Select(member => member.Name));
        Assert.False(typeof(IShellLog).IsPublic);
    }

    /// <summary>带缓冲的日志：vulcan.log.recent 读的就是它的快照。</summary>
    private sealed class BufferLog : IShellLog
    {
        private readonly List<ShellLogEntry> _entries = [];

        public event EventHandler<ShellLogEntry>? EntryAdded;

        public void Log(ShellLogLevel level, string category, string message)
        {
            var entry = new ShellLogEntry(DateTime.Now, level, category, message);
            lock (_entries)
                _entries.Add(entry);
            EntryAdded?.Invoke(this, entry);
        }

        public IReadOnlyList<ShellLogEntry> Snapshot()
        {
            lock (_entries)
                return _entries.ToArray();
        }
    }
}
