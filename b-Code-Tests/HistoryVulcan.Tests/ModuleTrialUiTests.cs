using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// 候选模块界面的试用装载(后台 → 前端中继)。
///
/// 回归背景:模块命令住在无窗 <c>--service</c> 后台进程,那里没有 IShellUiRegistrar,
/// 于是 <c>ui=true</c> 一律被拒。界面只能由前端宿主创建,后台经前端命令代理中继过去;
/// 本组用例锁住两侧的行为——后台拒绝时给出可执行的提示,前端能装能卸且不污染正式快照。
/// </summary>
public sealed class ModuleTrialUiTests
{
    [Fact]
    public void ServiceShapedHostRejectsTrialUiWithAnActionableMessage()
    {
        using var fixture = TrialPackage.Create(ui: true);
        using var host = new ModuleHost(fixture.ModulesDirectory, new TestLog())
        {
            EnableFileWatching = false,
            EnableUiModules = false,
        };

        var result = host.LoadTrialUi(fixture.PackagePath, "trial");

        Assert.False(result.Success);
        Assert.Contains("IShellUiRegistrar", result.Message, StringComparison.Ordinal);
        Assert.Contains("前端", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FrontendShapedHostCreatesAndUnloadsTrialUiOutsideTheModuleSnapshot()
    {
        using var fixture = TrialPackage.Create(ui: true);
        var registry = new CommandRegistry();
        var shellUi = new RecordingShellUi();
        using var host = CreateFrontendHost(fixture, shellUi, out var log);
        host.Attach(registry, new CommandBus(registry, log), new MemorySettings(), fixture.DataDirectory);

        var loaded = host.LoadTrialUi(fixture.PackagePath, "trial");

        Assert.True(loaded.Success, loaded.Message);
        Assert.Equal(new[] { "trial" }, shellUi.RegisteredOwners);
        // 试用界面不进正式快照:vulcan.module.list 与命令表都不该因此变化。
        Assert.Empty(host.Modules);
        Assert.Empty(registry.All());

        var reloaded = host.LoadTrialUi(fixture.PackagePath, "trial");
        Assert.False(reloaded.Success);
        Assert.Contains("别名已占用", reloaded.Message, StringComparison.Ordinal);

        var unloaded = host.UnloadTrialUi("trial");

        Assert.True(unloaded.Success, unloaded.Message);
        Assert.Equal(new[] { TrialUiFixtureModule.ToolWindowId }, shellUi.UnregisteredWindows);
        Assert.Equal(new[] { "trial" }, shellUi.UnregisteredOwners);
        Assert.False(host.UnloadTrialUi("trial").Success);
    }

    [Fact]
    public void DisposeTearsDownTrialUiThatIsNotInTheModuleSnapshot()
    {
        using var fixture = TrialPackage.Create(ui: true);
        var shellUi = new RecordingShellUi();
        var host = CreateFrontendHost(fixture, shellUi, out _);

        try
        {
            Assert.True(host.LoadTrialUi(fixture.PackagePath, "trial").Success);
        }
        finally
        {
            host.Dispose();
        }

        Assert.Equal(new[] { TrialUiFixtureModule.ToolWindowId }, shellUi.UnregisteredWindows);
        Assert.Equal(new[] { "trial" }, shellUi.UnregisteredOwners);
    }

    [Fact]
    public void TrialAliasMayNotShadowALoadedModuleOwner()
    {
        using var fixture = TrialPackage.Create(ui: true);
        var registry = new CommandRegistry();
        using var host = CreateFrontendHost(fixture, new RecordingShellUi(), out var log);
        host.Attach(registry, new CommandBus(registry, log), new MemorySettings(), fixture.DataDirectory);
        fixture.InstallAsLoadedModule();
        host.Start();

        var loaded = Assert.Single(host.Modules);
        var result = host.LoadTrialUi(fixture.PackagePath, loaded.ModuleName);

        Assert.False(result.Success);
        Assert.Contains("与已装载模块同名", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnloadFormalModuleFreesWindowIdsSoTrialUiCanLoad()
    {
        using var fixture = TrialPackage.Create(ui: true);
        var registry = new CommandRegistry();
        var shellUi = new RecordingShellUi();
        using var host = CreateFrontendHost(fixture, shellUi, out var log);
        host.EnableCommands = true;
        host.Attach(registry, new CommandBus(registry, log), new MemorySettings(), fixture.DataDirectory);
        fixture.InstallAsLoadedUiModule();
        host.Start();

        var loaded = Assert.Single(host.Modules);
        Assert.Equal(TrialPackage.ModuleName, loaded.ModuleName);
        Assert.Contains(TrialUiFixtureModule.ToolWindowId, shellUi.RegisteredWindowIds);

        var blocked = host.LoadTrialUi(fixture.PackagePath, "janus-trial");
        Assert.False(blocked.Success);
        Assert.Contains("工具窗口 Id 冲突", blocked.Message, StringComparison.Ordinal);

        var unloaded = host.Unload(loaded.ModuleName);
        Assert.True(unloaded.Success, unloaded.Message);
        Assert.Empty(host.Modules);
        Assert.Contains(TrialUiFixtureModule.ToolWindowId, shellUi.UnregisteredWindows);
        Assert.Contains("context-fixture", shellUi.UnregisteredOwners);

        var trial = host.LoadTrialUi(fixture.PackagePath, "janus-trial");
        Assert.True(trial.Success, trial.Message);
        Assert.Empty(host.Modules);
        Assert.Equal(2, shellUi.RegisteredWindowIds.Count);
        Assert.Contains(TrialUiFixtureModule.ToolWindowId, shellUi.RegisteredWindowIds);
    }

    [Fact]
    public void TrialUiRefusesPackagesThatDeclareNoUi()
    {
        using var fixture = TrialPackage.Create(ui: false);
        using var host = CreateFrontendHost(fixture, new RecordingShellUi(), out _);

        var result = host.LoadTrialUi(fixture.PackagePath, "trial");

        Assert.False(result.Success);
        Assert.Contains("ui=false", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TrialUiRefusesDirectoriesWithoutAValidManifest()
    {
        using var fixture = TrialPackage.Create(ui: true);
        using var host = CreateFrontendHost(fixture, new RecordingShellUi(), out _);

        var result = host.LoadTrialUi(fixture.ModulesDirectory, "trial");

        Assert.False(result.Success);
        Assert.Contains("module.manifest.json", result.Message, StringComparison.Ordinal);
    }

    private static ModuleHost CreateFrontendHost(
        TrialPackage fixture,
        RecordingShellUi shellUi,
        out TestLog log)
    {
        log = new TestLog();
        return new ModuleHost(fixture.ModulesDirectory, log)
        {
            EnableFileWatching = false,
            EnableCommands = false,
            EnableUiModules = true,
            ShellUi = shellUi,
            // 前端在 UI 线程上装配;本测试不跨线程，占位上下文足以满足宿主的前端形态判定。
            UiContext = new SynchronizationContext(),
        };
    }

    /// <summary>一个可试用的 z 快照:测试程序集配上清单,直接当候选包用。</summary>
    private sealed class TrialPackage : IDisposable
    {
        /// <summary>候选包清单声明的模块名。</summary>
        public const string ModuleName = "contextfixture";

        private readonly string _root;

        private TrialPackage(string root, string modulesDirectory, string packagePath, string dataDirectory)
        {
            _root = root;
            ModulesDirectory = modulesDirectory;
            PackagePath = packagePath;
            DataDirectory = dataDirectory;
        }

        public string ModulesDirectory { get; }

        public string PackagePath { get; }

        public string DataDirectory { get; }

        public static TrialPackage Create(bool ui)
        {
            var root = Path.Combine(Path.GetTempPath(), "HistoryVulcan.Tests", Guid.NewGuid().ToString("N"));
            var modulesDirectory = Path.Combine(root, "modules");
            var packagePath = Path.Combine(root, "candidate", "z-HistoryFixture");
            var dataDirectory = Path.Combine(root, "data");
            Directory.CreateDirectory(modulesDirectory);
            Directory.CreateDirectory(packagePath);
            Directory.CreateDirectory(dataDirectory);

            File.Copy(
                typeof(TrialUiFixtureModule).Assembly.Location,
                Path.Combine(packagePath, "TrialUiFixture.dll"));
            File.WriteAllText(Path.Combine(packagePath, "module.manifest.json"),
                $$"""
                {
                  "schemaVersion": 1,
                  "type": "HistoryVulcan.Module",
                  "name": "{{ModuleName}}",
                  "version": "v1.0.0",
                  "artifact": "TrialUiFixture.dll",
                  "ui": {{(ui ? "true" : "false")}}
                }
                """);

            return new TrialPackage(root, modulesDirectory, packagePath, dataDirectory);
        }

        /// <summary>把同一份夹具也放进模块目录,让宿主的正式快照里出现同名 owner。</summary>
        public void InstallAsLoadedModule()
        {
            var slot = Path.Combine(ModulesDirectory, "context-fixture");
            Directory.CreateDirectory(slot);
            File.Copy(
                typeof(TrialUiFixtureModule).Assembly.Location,
                Path.Combine(slot, "ContextFixture.dll"));
        }

        /// <summary>正式装载且声明 ui=true，使 Start 会创建与试用夹具相同 Id 的工具窗口。</summary>
        public void InstallAsLoadedUiModule()
        {
            InstallAsLoadedModule();
            File.WriteAllText(
                Path.Combine(ModulesDirectory, "context-fixture", "module.manifest.json"),
                $$"""
                {
                  "schemaVersion": 1,
                  "type": "HistoryVulcan.Module",
                  "name": "{{ModuleName}}",
                  "version": "v1.0.0",
                  "artifact": "ContextFixture.dll",
                  "ui": true
                }
                """);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// 记录宿主收到的注册与注销。试用程序集在独立 ALC 里,夹具自己的静态字段与
    /// 宿主这一份不是同一个;跨上下文可观察的只有从默认上下文传进去的这个注册器。
    /// </summary>
    private sealed class RecordingShellUi : IShellUiRegistrar
    {
        public List<string> RegisteredOwners { get; } = [];

        public List<string> RegisteredWindowIds { get; } = [];

        public List<string> UnregisteredWindows { get; } = [];

        public List<string> UnregisteredOwners { get; } = [];

        private readonly HashSet<string> _windowIds = new(StringComparer.OrdinalIgnoreCase);

        public bool IsUiThread => true;

        public void Invoke(Action action) => action();

        public IDisposable RegisterToolWindow(ToolWindowDescriptor descriptor, string owner)
        {
            if (!_windowIds.Add(descriptor.Id))
                throw new InvalidOperationException($"工具窗口 Id 冲突: {descriptor.Id}(禁止静默覆盖,§5.3)");
            RegisteredWindowIds.Add(descriptor.Id);
            RegisteredOwners.Add(owner);
            return new Registration();
        }

        public void UnregisterToolWindow(string id)
        {
            _windowIds.Remove(id);
            UnregisteredWindows.Add(id);
        }

        public void UnregisterOwner(string owner) => UnregisteredOwners.Add(owner);

        private sealed class Registration : IDisposable
        {
            public void Dispose()
            {
            }
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
}

/// <summary>
/// 试用界面夹具:创建时向宿主注册器登记一个工具窗口,销毁时注销同一个 id。
/// 它是本测试程序集里唯一的 <see cref="IUiModule"/>,其余用例一律 EnableUiModules=false,
/// 因此不会被别的模块装载用例牵连。
/// </summary>
public sealed class TrialUiFixtureModule : IUiModule, IShellUiAware
{
    /// <summary>夹具登记的工具窗口 id;用例据此确认销毁确实走到了。</summary>
    public const string ToolWindowId = "trialui.fixture";

    private IShellUiRegistrar? _shellUi;

    /// <inheritdoc />
    public IShellUiRegistrar ShellUi { set => _shellUi = value; }

    /// <inheritdoc />
    public void CreateUi()
        => _shellUi?.RegisterToolWindow(
            new ToolWindowDescriptor { Id = ToolWindowId, Title = "试用夹具" },
            "trial");

    /// <inheritdoc />
    public void DestroyUi() => _shellUi?.UnregisterToolWindow(ToolWindowId);
}
