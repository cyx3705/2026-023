using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Input;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests
{

    public sealed class ModuleHostContextTests
    {
        [Fact]
        public async Task ContextAwareModuleReceivesHostServicesAndOwnsItsCommands()
        {
            var root = Path.Combine(Path.GetTempPath(), "HistoryVulcan.Tests", Guid.NewGuid().ToString("N"));
            var modulesDirectory = Path.Combine(root, "modules");
            var slotDirectory = Path.Combine(modulesDirectory, "context-fixture");
            var dataDirectory = Path.Combine(root, "data");
            Directory.CreateDirectory(slotDirectory);
            Directory.CreateDirectory(dataDirectory);

            var modulePath = Path.Combine(slotDirectory, "ContextFixture.dll");
            File.Copy(typeof(ContextFixtureModuleInfo).Assembly.Location, modulePath);

            var registry = new CommandRegistry();
            var log = new TestLog();
            var settings = new MemorySettings();
            settings.Set("fixture.value", "attached");
            var bus = new CommandBus(registry, log);
            var host = new ModuleHost(modulesDirectory, log)
            {
                EnableFileWatching = false,
                EnableUiModules = false,
            };

            try
            {
                host.Attach(registry, bus, settings, dataDirectory);
                host.Start();

                Assert.True(registry.TryGet("contextfixture.context-probe", out var direct));
                Assert.True(registry.TryGet("contextfixture.Probe", out var reflected));
                Assert.False(registry.TryGet("contextfixture.Attach", out _));
                Assert.Equal("contextfixture", registry.GetDomain(direct.Name));
                Assert.Equal("context", registry.GetCommandClass(direct.Name));
                Assert.Equal("contextfixture", registry.GetDomain(reflected.Name));
                Assert.Equal("probe", registry.GetCommandClass(reflected.Name));

                var result = await bus.ExecuteAsync("contextfixture.context-probe", "test");

                Assert.True(result.Success, result.Message);
                Assert.Equal($"{Path.GetFullPath(dataDirectory)}|attached", result.Message);
                Assert.Equal(2, Assert.Single(host.Modules).CommandCount);
            }
            finally
            {
                host.Dispose();
                Assert.False(registry.TryGet("contextfixture.context-probe", out _));
                Assert.False(registry.TryGet("contextfixture.Probe", out _));
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void DisabledModuleDoesNotAttachContextOrRegisterCommands()
        {
            var root = Path.Combine(Path.GetTempPath(), "HistoryVulcan.Tests", Guid.NewGuid().ToString("N"));
            var modulesDirectory = Path.Combine(root, "modules");
            var slotDirectory = Path.Combine(modulesDirectory, "context-fixture");
            Directory.CreateDirectory(slotDirectory);
            File.Copy(
                typeof(ContextFixtureModuleInfo).Assembly.Location,
                Path.Combine(slotDirectory, "ContextFixture.dll"));

            var previous = Environment.GetEnvironmentVariable(ContextFixtureModuleInfo.EnabledVariable);
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.EnabledVariable, "0");
            var registry = new CommandRegistry();
            var log = new TestLog();
            var settings = new MemorySettings();
            var bus = new CommandBus(registry, log);
            using var host = new ModuleHost(modulesDirectory, log)
            {
                EnableFileWatching = false,
                EnableUiModules = false,
            };

            try
            {
                host.Attach(registry, bus, settings, Path.Combine(root, "data"));
                host.Start();

                Assert.Empty(host.Modules);
                Assert.False(registry.TryGet("contextfixture.context-probe", out _));
                Assert.False(registry.TryGet("contextfixture.Probe", out _));
                Assert.False(registry.TryGet("contextfixture.Attach", out _));
            }
            finally
            {
                Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.EnabledVariable, previous);
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void RollbackSlotsAreIgnoredByModuleDiscovery()
        {
            var root = Path.Combine(Path.GetTempPath(), "HistoryVulcan.Tests", Guid.NewGuid().ToString("N"));
            var modulesDirectory = Path.Combine(root, "modules");
            var activeDirectory = Path.Combine(modulesDirectory, "context-fixture");
            var rollbackDirectory = Path.Combine(modulesDirectory, "context-fixture-rollback-20260808-000000");
            Directory.CreateDirectory(activeDirectory);
            Directory.CreateDirectory(rollbackDirectory);
            File.Copy(typeof(ContextFixtureModuleInfo).Assembly.Location,
                Path.Combine(activeDirectory, "ContextFixture.dll"));
            File.Copy(typeof(ContextFixtureModuleInfo).Assembly.Location,
                Path.Combine(rollbackDirectory, "ContextFixture.dll"));

            var registry = new CommandRegistry();
            var log = new TestLog();
            using var host = new ModuleHost(modulesDirectory, log)
            {
                EnableFileWatching = false,
                EnableUiModules = false,
            };

            try
            {
                host.Attach(registry);
                host.Start();

                var module = Assert.Single(host.Modules);
                Assert.Equal("contextfixture", module.ModuleName);
                Assert.Single(registry.All());
            }
            finally
            {
                host.Dispose();
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void ReloadReplacesOwnedGlobalShortcutsWithoutConflictOrLeak()
        {
            var root = Path.Combine(Path.GetTempPath(), "HistoryVulcan.Tests", Guid.NewGuid().ToString("N"));
            var modulesDirectory = Path.Combine(root, "modules");
            var slotDirectory = Path.Combine(modulesDirectory, "context-fixture");
            Directory.CreateDirectory(slotDirectory);
            File.Copy(typeof(ContextFixtureModuleInfo).Assembly.Location,
                Path.Combine(slotDirectory, "ContextFixture.dll"));

            var log = new TestLog();
            var shortcuts = new TestShortcutHost();
            var host = new ModuleHost(modulesDirectory, log)
            {
                EnableFileWatching = false,
                EnableUiModules = false,
                GlobalShortcuts = shortcuts,
            };

            try
            {
                host.Start();
                Assert.Single(shortcuts.Registrations);

                host.Reload();

                var registration = Assert.Single(shortcuts.Registrations);
                Assert.Equal("context-fixture", registration.Owner);
                Assert.Equal("reload-probe", registration.Id);
                Assert.DoesNotContain(log.Entries,
                    item => item.Category == "hotkey" && item.Level == ShellLogLevel.Warn);
            }
            finally
            {
                host.Dispose();
                Assert.Empty(shortcuts.Registrations);
                Directory.Delete(root, recursive: true);
            }
        }

        private sealed class MemorySettings : ISettingsService
        {
            private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

            public string? Get(string key) => _values.GetValueOrDefault(key);

            public int GetInt(string key, int fallback)
                => int.TryParse(Get(key), out var value) ? value : fallback;

            public void Set(string key, string value) => _values[key] = value;

            public IReadOnlyList<KeyValuePair<string, string>> All() => [.. _values];
        }

        private sealed class TestLog : IShellLog
        {
            public List<ShellLogEntry> Entries { get; } = [];

            public void Log(ShellLogLevel level, string category, string message)
                => Entries.Add(new ShellLogEntry(DateTime.Now, level, category, message));

            public event EventHandler<ShellLogEntry>? EntryAdded
            {
                add { }
                remove { }
            }

            public IReadOnlyList<ShellLogEntry> Snapshot() => Entries;
        }

        private sealed class TestShortcutHost : IGlobalShortcutHost
        {
            private readonly Dictionary<string, GlobalShortcutRegistrationInfo> _registrations =
                new(StringComparer.OrdinalIgnoreCase);

            public bool IsEnabled => true;
            public IReadOnlyList<GlobalShortcutRegistrationInfo> Registrations => _registrations.Values.ToArray();
            public void Start() { }
            public void Stop() { }

            public IDisposable Register(GlobalShortcutDescriptor descriptor, string owner)
            {
                if (_registrations.Values.Any(item =>
                        item.Strokes.Take(Math.Min(item.Strokes.Count, descriptor.Strokes.Count))
                            .SequenceEqual(descriptor.Strokes.Take(Math.Min(item.Strokes.Count, descriptor.Strokes.Count)))))
                    throw new InvalidOperationException($"快捷键冲突: {descriptor.Id}");
                var key = owner + "/" + descriptor.Id;
                _registrations.Add(key, new GlobalShortcutRegistrationInfo(
                    descriptor.Id, owner, descriptor.Strokes, descriptor.CommandText,
                    descriptor.MaxIntervalMilliseconds));
                return new ActionDisposable(() => _registrations.Remove(key));
            }

            public IGlobalShortcutRegistrar CreateOwnerRegistrar(string owner)
                => new OwnerRegistrar(this, owner);

            public void UnregisterOwner(string owner)
            {
                foreach (var key in _registrations.Keys
                             .Where(key => key.StartsWith(owner + "/", StringComparison.OrdinalIgnoreCase))
                             .ToArray())
                    _registrations.Remove(key);
            }

            public void Dispose() => _registrations.Clear();

            private sealed class OwnerRegistrar(TestShortcutHost host, string owner) : IGlobalShortcutRegistrar
            {
                public IDisposable Register(GlobalShortcutDescriptor descriptor) => host.Register(descriptor, owner);
                public void Dispose() => host.UnregisterOwner(owner);
            }

            private sealed class ActionDisposable(Action dispose) : IDisposable
            {
                public void Dispose() => dispose();
            }
        }
    }

    public sealed class ContextFixtureModuleInfo : BaseVariable.ModuleInfoBase
    {
        public const string EnabledVariable = "HISTORYVULCAN_CONTEXT_FIXTURE_ENABLED";

        public override string ModuleName => "contextfixture";

        public override Type MainClassType => typeof(ContextAwareFixture);

        public override bool Enabled
            => !string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "0", StringComparison.Ordinal);
    }

    public sealed class ContextAwareFixture : IModuleContextAware
    {
        public void Attach(IModuleContext context)
        {
            context.RegisterCommands(registry => registry.Register(new CommandDescriptor
            {
                Name = "contextfixture.context-probe",
                Domain = "spoofed-domain",
                CommandClass = "context",
                Summary = "Returns the injected host context values.",
                Readonly = true,
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(
                    $"{context.DataDirectory}|{context.Settings.Get("fixture.value")}")),
            }));
        }

        [ModuleCommand(CommandClass = "probe")]
        public string Probe() => "reflected";
    }

    public sealed class ContextShortcutFixture : IGlobalShortcutModule
    {
        public void RegisterShortcuts(IGlobalShortcutRegistrar registrar)
            => registrar.Register(new GlobalShortcutDescriptor(
                "reload-probe", [new GlobalShortcutStroke(0xBF), new GlobalShortcutStroke(0xBF)],
                "contextfixture.context-probe"));
    }

}

// 夹具此前在此内联定义 BaseVariable.ModuleInfoBase 的第六份副本。
// 契约的权威定义已进入 HistoryVulcan.Core(Modules/ModuleInfoBase.cs),
// 本测试直接消费它 —— 夹具与宿主装载逻辑校验的因此是同一个类型。
