using System.Collections.Concurrent;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.ServiceHost;
using HistoryVulcan.Services.Commands;
using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// 5.9.0 统一契约第一段（REQ-HOST-080…083，DEC-070）：总线事件、模块运行环境、目录指令、测试装载。
/// </summary>
[Collection(RuntimeModulePackageCollection.Name)]
public sealed class UnifiedContractTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    // ------------------------------------------------------------ REQ-HOST-080 总线事件

    [Fact]
    public void SubscribersReceiveEventsInPublishOrder()
    {
        var hub = new BusEventHub(new NullLog());
        var exact = new BlockingCollection<BusEvent>();
        var prefix = new BlockingCollection<BusEvent>();
        using var a = hub.Subscribe("vulcan.log.entry", "host", exact.Add);
        using var b = hub.Subscribe("vulcan.*", "host", prefix.Add);

        for (var i = 0; i < 50; i++)
            hub.Publish("vulcan.log.entry", "host", new { index = i });
        hub.Publish("vulcan.catalog.changed", "host", new { revision = 7 });

        for (var i = 0; i < 50; i++)
        {
            Assert.True(exact.TryTake(out var evt, Wait));
            Assert.Equal(i, evt!.Payload.GetProperty("index").GetInt32());
        }

        var topics = Enumerable.Range(0, 51).Select(_ => prefix.TryTake(out var evt, Wait) ? evt!.Topic : "").ToList();
        Assert.Equal(50, topics.Count(topic => topic == "vulcan.log.entry"));
        Assert.Equal("vulcan.catalog.changed", topics[^1]);
        Assert.False(exact.TryTake(out _, TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void HandlerExceptionIsLoggedAndDoesNotStopDelivery()
    {
        var log = new RecordingLog();
        var hub = new BusEventHub(log);
        var received = new BlockingCollection<int>();
        using var subscription = hub.Subscribe("probe.thing", "HistoryProbe", evt =>
        {
            var value = evt.Payload.GetProperty("value").GetInt32();
            if (value == 1)
                throw new InvalidOperationException("boom");
            received.Add(value);
        });

        hub.Publish("probe.thing", "module:HistoryProbe", new { value = 1 });
        hub.Publish("probe.thing", "module:HistoryProbe", new { value = 2 });

        Assert.True(received.TryTake(out var second, Wait));
        Assert.Equal(2, second);
        Assert.Contains(log.Entries, entry => entry.Category == BusEventHub.LogCategory && entry.Message.Contains("boom"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("vulcan")]
    [InlineData("Vulcan.Log")]
    [InlineData("vulcan..log")]
    [InlineData("vulcan.log entry")]
    public void InvalidTopicsAreRejected(string topic)
    {
        var hub = new BusEventHub(new NullLog());
        Assert.Throws<ArgumentException>(() => hub.Publish(topic, "host", null));
        Assert.Throws<ArgumentException>(() => hub.Subscribe(topic, "host", _ => { }));
    }

    [Fact]
    public void DisposeAndRemoveOwnerStopDelivery()
    {
        var hub = new BusEventHub(new NullLog());
        var received = new BlockingCollection<string>();
        var disposed = hub.Subscribe("probe.a", "HistoryProbe", evt => received.Add("disposed"));
        hub.Subscribe("probe.a", "HistoryProbe", evt => received.Add("owned"));
        hub.Subscribe("probe.a", "HistoryOther", evt => received.Add("other"));

        disposed.Dispose();
        Assert.Equal(1, hub.RemoveOwner("historyprobe"));
        hub.Publish("probe.a", "host", null);

        Assert.True(received.TryTake(out var only, Wait));
        Assert.Equal("other", only);
        Assert.False(received.TryTake(out _, TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task HostPublishesCatalogCommandAndLogEvents()
    {
        var log = new EventingLog();
        var registry = new CommandRegistry();
        var bus = new CommandBus(registry, log);
        registry.Register(new CommandDescriptor
        {
            Name = "probe.key.set",
            Summary = "probe",
            AllowUnspecifiedParameters = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("done\nsecond line")),
        });
        using var publisher = new HostEventPublisher(registry, bus, log, modules: null, debounce: TimeSpan.FromHours(1));
        var events = new BlockingCollection<BusEvent>();
        using var subscription = bus.Events.Subscribe("vulcan.*", "test", events.Add);

        publisher.Flush();
        Assert.True(events.TryTake(out var catalog, Wait));
        Assert.Equal(HostEventPublisher.CatalogChanged, catalog!.Topic);
        Assert.Equal(registry.Revision, catalog.Payload.GetProperty("revision").GetInt64());
        publisher.Flush();
        Assert.False(events.TryTake(out _, TimeSpan.FromMilliseconds(100)));

        await bus.ExecuteAsync("probe.key.set value=secret-value", "test");
        var executed = Drain(events).First(evt => evt.Topic == HostEventPublisher.CommandExecuted);
        Assert.Equal("probe.key.set", executed.Payload.GetProperty("name").GetString());
        Assert.Equal("done", executed.Payload.GetProperty("summary").GetString());
        Assert.DoesNotContain("secret-value", executed.Payload.GetRawText(), StringComparison.Ordinal);

        log.Log(ShellLogLevel.Info, "probe", "hello");
        log.Log(ShellLogLevel.Warn, BusEventHub.LogCategory, "hub diagnostics");
        var logged = Drain(events).Where(evt => evt.Topic == HostEventPublisher.LogEntry).ToList();
        Assert.Contains(logged, evt => evt.Payload.GetProperty("message").GetString() == "hello");
        Assert.DoesNotContain(logged, evt => evt.Payload.GetProperty("category").GetString() == BusEventHub.LogCategory);
    }

    private static List<BusEvent> Drain(BlockingCollection<BusEvent> events)
    {
        var list = new List<BusEvent>();
        while (events.TryTake(out var evt, TimeSpan.FromMilliseconds(300)))
            list.Add(evt);
        return list;
    }

    /// <summary>会真正触发 EntryAdded 的日志；RecordingLog 不触发。</summary>
    private sealed class EventingLog : IShellLog
    {
        public event EventHandler<ShellLogEntry>? EntryAdded;

        public void Log(ShellLogLevel level, string category, string message)
            => EntryAdded?.Invoke(this, new ShellLogEntry(DateTime.Now, level, category, message));

        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }

    // ------------------------------------------------------------ REQ-HOST-081 模块运行环境

    [Fact]
    public async Task ModuleEnvironmentGivesADataDirectoryOutsideThePackageSlot()
    {
        using var temp = new TemporaryDirectory();
        var (host, bus) = StartFixture(temp.Path, HostRunMode.Probe);
        using (host)
        {
            var env = await bus.ExecuteAsync("contextfixture.context-env", "test");
            Assert.True(env.Success, env.Message);
            var parts = env.Message.Split('|');
            Assert.Equal("contextfixture", parts[0]);
            Assert.Equal(Path.Combine(temp.Path, "ModuleData", "contextfixture"), parts[1]);
            Assert.True(Directory.Exists(parts[1]));
            Assert.False(parts[1].StartsWith(Path.Combine(temp.Path, "modules"), StringComparison.OrdinalIgnoreCase));
            Assert.Equal(nameof(HostRunMode.Probe), parts[2]);
            Assert.Equal(parts[1], Assert.Single(host.Modules).DataDirectory);
        }
    }

    [Fact]
    public async Task ModuleCanPublishOnlyUnderItsOwnDomainAndLosesSubscriptionsOnUnload()
    {
        using var temp = new TemporaryDirectory();
        var (host, bus) = StartFixture(temp.Path, HostRunMode.Service);
        using (host)
        {
            var events = new BlockingCollection<BusEvent>();
            using var watch = bus.Events.Subscribe("contextfixture.*", "test", events.Add);

            Assert.True((await bus.ExecuteAsync("contextfixture.context-publish contextfixture.ping", "test")).Success);
            Assert.True(events.TryTake(out var evt, Wait));
            Assert.Equal("module:contextfixture", evt!.Source);
            Assert.Equal("fixture", evt.Payload.GetProperty("from").GetString());

            var spoof = await bus.ExecuteAsync("contextfixture.context-publish vulcan.catalog.changed", "test");
            Assert.False(spoof.Success);
            Assert.Contains("只能发布自己指令域", spoof.Message, StringComparison.Ordinal);

            var file = (await bus.ExecuteAsync("contextfixture.context-subscribe vulcan.probe.tick", "test")).Message;
            bus.Events.Publish("vulcan.probe.tick", "host", new { n = 1 });
            Assert.True(SpinWait.SpinUntil(() => File.Exists(file) && File.ReadAllText(file).Contains("\"n\":1"), Wait));

            Assert.True(host.Unload("contextfixture").Success);
            File.Delete(file);
            bus.Events.Publish("vulcan.probe.tick", "host", new { n = 2 });
            Thread.Sleep(200);
            Assert.False(File.Exists(file));
        }
    }

    [Fact]
    public void UninstallKeepsTheDataDirectoryUnlessPurged()
    {
        using var temp = new TemporaryDirectory();
        var modules = Path.Combine(temp.Path, "modules");
        var registry = new CommandRegistry();
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(modules), new NullLog())
        {
            EnableFileWatching = false,
            ModuleDataRoot = Path.Combine(temp.Path, "ModuleData"),
        };
        host.Attach(registry, new CommandBus(registry, new NullLog()));
        var data = host.ModuleDataDirectory("contextfixture");

        RuntimeModulePackageTests.CreatePackage(modules, "contextfixture", "contextfixture", "v1.0.0");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "state.txt"), "keep me");
        var kept = host.Uninstall("contextfixture");
        Assert.True(kept.Success, kept.Message);
        Assert.Contains("数据目录已保留", kept.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(data, "state.txt")));

        RuntimeModulePackageTests.CreatePackage(modules, "contextfixture", "contextfixture", "v1.0.0");
        var purged = host.Uninstall("contextfixture", purge: true);
        Assert.True(purged.Success, purged.Message);
        Assert.False(Directory.Exists(data));
    }

    // ------------------------------------------------------------ REQ-HOST-082 目录指令与宿主信息

    [Fact]
    public async Task CatalogQueriesAreBusCommandsInsteadOfRegistryInternals()
    {
        var registry = new CommandRegistry();
        var bus = new CommandBus(registry, new NullLog());
        CommandCatalogCommands.RegisterAll(registry);
        registry.Register(new CommandDescriptor
        {
            Name = "probe.thing.drop",
            Summary = "probe",
            Level = CommandLevel.Ask,
            ConfirmPrompt = _ => "确定？",
            Parameters = [new ParameterSpec { Name = "id", Description = "id", Required = true, Position = 0 }],
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        });

        var ok = await bus.ExecuteAsync("vulcan.command.validate \"probe.thing.drop 1\"", "test");
        Assert.True(JsonSerializer.SerializeToElement(ok.Data).GetProperty("ok").GetBoolean(), ok.Message);
        var missing = await bus.ExecuteAsync("vulcan.command.validate probe.thing.drop", "test");
        Assert.False(JsonSerializer.SerializeToElement(missing.Data).GetProperty("ok").GetBoolean());
        var unknown = await bus.ExecuteAsync("vulcan.command.validate probe.thing.dorp", "test");
        Assert.Contains("未知指令", unknown.Message, StringComparison.Ordinal);

        var suggest = await bus.ExecuteAsync("vulcan.command.suggest probe.thing.dorp", "test");
        Assert.Contains("probe.thing.drop", Assert.IsAssignableFrom<IReadOnlyList<string>>(suggest.Data));

        var before = registry.Revision;
        var revision = await bus.ExecuteAsync("vulcan.command.revision", "test");
        Assert.Equal(before, JsonSerializer.SerializeToElement(revision.Data).GetProperty("revision").GetInt64());
        Assert.True(registry.Unregister("probe.thing.drop"));
        Assert.Equal(before + 1, registry.Revision);

        foreach (var name in new[] { "vulcan.command.validate", "vulcan.command.suggest", "vulcan.command.revision" })
        {
            Assert.True(registry.TryGet(name, out var descriptor));
            Assert.True(descriptor.Readonly);
            Assert.False(string.IsNullOrEmpty(descriptor.HiddenReason));
        }
    }

    [Fact]
    public async Task CatalogRowsSayWhetherConfirmationIsNeeded()
    {
        var registry = new CommandRegistry();
        var bus = new CommandBus(registry, new NullLog());
        CommandCatalogCommands.RegisterAll(registry);
        registry.Register(new CommandDescriptor
        {
            Name = "probe.thing.drop",
            Summary = "probe",
            Level = CommandLevel.Ask,
            ConfirmPrompt = _ => "确定？",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        });

        var listed = await bus.ExecuteAsync("vulcan.command.list domain=probe", "test");
        var row = Assert.Single(Assert.IsAssignableFrom<IEnumerable<CommandCatalogRow>>(listed.Data));
        Assert.True(row.RequiresConfirmation);
        Assert.True(JsonSerializer.SerializeToElement(row, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .GetProperty("requiresConfirmation").GetBoolean());
    }

    [Fact]
    public void HostInfoIsOnTheCommandLineSurface()
        => Assert.Contains("vulcan.host.info", CliExposurePolicy.ExposedCommands);

    [Theory]
    [InlineData("module:HistoryDiana", true)]
    [InlineData("diana.host.observe", true)]
    [InlineData("janus.proj.list", false)]
    [InlineData("mcp:client", false)]
    public void ModuleSourcesAreTrustedByPrefixNotByModuleName(string source, bool trusted)
        => Assert.Equal(trusted, ServiceComposer.IsLocalModuleMutationSource(source));

    // ------------------------------------------------------------ REQ-HOST-083 测试装载

    [Fact]
    public void ProbeSwitchTakesAPackageAndAnOptionalCommand()
    {
        var parsed = HostArgumentParser.Parse(["--probe", @"C:\pkg\HistoryX-v1.0.0", "--cli", "x.hello.echo", "text=hi", "--format", "json"]);
        Assert.Equal(HostAction.Probe, parsed.Action);
        Assert.Equal(@"C:\pkg\HistoryX-v1.0.0", parsed.Value);
        Assert.Equal("x.hello.echo text=hi", parsed.ProbeCommand);
        Assert.Equal(HostOutputFormat.Json, parsed.Format);

        var bare = HostArgumentParser.Parse(["--probe", @"C:\pkg"]);
        Assert.Equal(HostAction.Probe, bare.Action);
        Assert.Equal("", bare.ProbeCommand);

        Assert.Equal(HostAction.Error, HostArgumentParser.Parse(["--probe"]).Action);
        Assert.Equal(HostAction.Error, HostArgumentParser.Parse(["--probe", "--cli", "x"]).Action);
    }

    private static (ModuleHost Host, CommandBus Bus) StartFixture(string root, HostRunMode mode)
    {
        var modules = Path.Combine(root, "modules");
        RuntimeModulePackageTests.CreatePackage(modules, "contextfixture", "contextfixture", "v1.0.0");
        var registry = new CommandRegistry();
        var bus = new CommandBus(registry, new NullLog());
        var host = new ModuleHost(new RuntimeModuleDiscoverySource(modules), new NullLog())
        {
            EnableFileWatching = false,
            ModuleDataRoot = Path.Combine(root, "ModuleData"),
            RunMode = mode,
        };
        host.Attach(registry, bus);
        host.Start();
        Assert.True(Assert.Single(host.Modules).Attached);
        return (host, bus);
    }
}
