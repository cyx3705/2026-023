using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services.Development;
using HistoryVulcan.Services.Development.Pipeline;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// 5.7.0 开发管线易用性（REQ-HOST-072…076）：参数收敛、工作区内不再要 allowDirty、
/// 热装附着核对、回执按 agent 与格式修整、主树改动警告、start 给出可复制命令。
/// </summary>
public sealed class DevPipelineUsabilityTests
{
    private const string Project = "2026-020-HistoryJanus";

    [Fact]
    public void SubmitAndFinishRequireOnlyTheWorktree()
    {
        var registry = new CommandRegistry();
        var bus = new CommandBus(registry, new NullLog());
        DevPipelineCommands.Register(registry, new DevelopmentContext(bus, new MemorySettings(), Path.GetTempPath()));

        foreach (var name in new[] { "vulcan.dev.submit", "vulcan.dev.finish" })
        {
            Assert.True(registry.TryGet(name, out var descriptor));
            var required = descriptor!.Parameters.Where(parameter => parameter.Required).Select(parameter => parameter.Name);
            Assert.Equal(["worktree"], required);
        }
    }

    [Theory]
    [InlineData("fb0e1c6-1-claude-kind-exclude-multistate-box", "claude", "kind-exclude-multistate-box")]
    [InlineData("abc1234-12-grok-fix", "grok", "fix")]
    public void WorktreeNameSplitsIntoAgentAndSlug(string name, string agent, string slug)
    {
        Assert.True(DevPipelineCommands.TryParseWorktreeName(name, out var parsedAgent, out var parsedSlug));
        Assert.Equal(agent, parsedAgent);
        Assert.Equal(slug, parsedSlug);
    }

    [Theory]
    [InlineData("")]
    [InlineData("custom-worktree")]
    [InlineData("abc-x-claude-fix")]
    public void UnconventionalWorktreeNamesAreNotParsed(string name)
        => Assert.False(DevPipelineCommands.TryParseWorktreeName(name, out _, out _));

    [Theory]
    [InlineData("grok", true)]
    [InlineData("Cursor", true)]
    [InlineData("claude", false)]
    [InlineData("codex", false)]
    [InlineData(null, false)]
    public void MoveRootHintsAreOnlyForAgentsThatMoveTheirRoot(string? agent, bool expected)
        => Assert.Equal(expected, DevPipelineCommands.MovesConversationRoot(agent));

    [Fact]
    public void WorktreeProjectIsInferredFromNameOrAbsolutePath()
    {
        using var library = FakeLibrary.Create();
        var created = library.Start("claude", "probe");

        Assert.True(DevPipelineCommands.TryResolveWorktreeProject(
            library.Settings, created.Name, out var byName, out var pathByName, out var error), error);
        Assert.Equal(Project, byName);
        Assert.Equal(created.Path, pathByName, ignoreCase: true);

        Assert.True(DevPipelineCommands.TryResolveWorktreeProject(
            library.Settings, created.Path, out var byPath, out _, out error), error);
        Assert.Equal(Project, byPath);

        Assert.False(DevPipelineCommands.TryResolveWorktreeProject(
            library.Settings, "no-such-worktree", out _, out _, out error));
        Assert.Contains("找不到工作区", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitNameMustMatchTheWorktreeProject()
    {
        using var library = FakeLibrary.Create();
        var created = library.Start("claude", "probe");

        Assert.True(DevPipelineCommands.TryResolveTarget(
            library.Settings, null, created.Name, out var inferred, out _, out var error), error);
        Assert.Equal("HistoryJanus", inferred.Name);

        Assert.True(DevPipelineCommands.TryResolveTarget(
            library.Settings, "HistoryJanus", created.Name, out _, out _, out error), error);

        Assert.False(DevPipelineCommands.TryResolveTarget(
            library.Settings, "HistoryMinerva", created.Name, out _, out _, out error));
        Assert.Contains("name=HistoryMinerva", error, StringComparison.Ordinal);
        Assert.Contains("HistoryJanus", error, StringComparison.Ordinal);

        Assert.False(DevPipelineCommands.TryResolveTarget(
            library.Settings, "HistoryVulcan", created.Name, out _, out _, out error));
        Assert.Equal(DevPipelineCommands.HostRejected, error);
    }

    [Fact]
    public async Task DryRunSubmitNeedsNoAllowDirtyAndReportsCleanly()
    {
        using var library = FakeLibrary.Create();
        var created = library.Start("claude", "probe");
        File.AppendAllText(Path.Combine(created.Path, "README.md"), "changed");
        File.WriteAllText(Path.Combine(created.Path, "added.txt"), "new");

        var result = await library.Bus.ExecuteAsync(
            $"vulcan.dev.submit worktree={created.Name} dryRun=true", "Test");

        Assert.True(result.Success, result.Message);
        // 首行的状态列空格不能被裁掉；清单与后一句之间要换行。
        Assert.Contains("脏树文件:\n M README.md\n?? added.txt\n不会写日志", result.Message, StringComparison.Ordinal);
        Assert.Contains("dryRun：未登记审核", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("已登记宿主审核", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("迁根", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureLinesKeepsTheLeadingStatusColumn()
    {
        using var library = FakeLibrary.Create();
        File.AppendAllText(Path.Combine(library.ProjectPath, "README.md"), "changed");

        Assert.Equal(" M README.md", ToolProcess.CaptureLines("git", ["status", "--porcelain"], library.ProjectPath));
        Assert.Equal("M README.md", ToolProcess.Capture("git", ["status", "--porcelain"], library.ProjectPath));
    }

    [Fact]
    public void MainTreeChangesAreWarnedButFormalSnapshotIsIgnored()
    {
        using var library = FakeLibrary.Create();
        Assert.Equal("", DevPipelineCommands.MainTreeWarning(library.ProjectPath, "z-Publish"));

        File.AppendAllText(Path.Combine(library.ProjectPath, "z-Publish", "x.txt"), "candidate");
        Assert.Equal("", DevPipelineCommands.MainTreeWarning(library.ProjectPath, "z-Publish"));

        File.AppendAllText(Path.Combine(library.ProjectPath, "README.md"), "changed");
        var warning = DevPipelineCommands.MainTreeWarning(library.ProjectPath, "z-Publish");
        Assert.Contains("1 个未提交文件", warning, StringComparison.Ordinal);
        Assert.Contains("README.md", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartGivesCopyableCommandsAndOnlyGrokGetsTheMoveRootHint()
    {
        using var library = FakeLibrary.Create();

        var claude = await library.Bus.ExecuteAsync($"vulcan.dev.start project={Project} slug=probe agent=claude", "Test");
        Assert.True(claude.Success, claude.Message);
        Assert.DoesNotContain("迁根", claude.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("grok 按手册", claude.Message, StringComparison.Ordinal);
        Assert.Contains("--cli \"vulcan.dev.submit worktree=", claude.Message, StringComparison.Ordinal);
        Assert.Contains("--cli \"vulcan.dev.finish worktree=", claude.Message, StringComparison.Ordinal);

        var grok = await library.Bus.ExecuteAsync(
            $"vulcan.dev.start project={Project} slug=other agent=grok confirm=true", "Test");
        Assert.True(grok.Success, grok.Message);
        Assert.Contains(DevPipelineCommands.MoveRootHint, grok.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HotReloadFailsWhenTheModuleDidNotAttach()
    {
        var failures = new[] { "InvalidOperationException: 模块 HistorySample 重复暂存指令: sample.a" };
        var result = await InstallWithModuleList(
            [new { moduleName = "HistorySample", version = "1.0.0", commandCount = 0, attached = false, attachFailures = failures }]);

        Assert.False(result.Success);
        Assert.Contains("attached=false", result.Message, StringComparison.Ordinal);
        Assert.Contains("重复暂存指令", result.Message, StringComparison.Ordinal);
        Assert.Contains("[module.discovery]", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HotReloadFailsWhenTheOldVersionIsStillLoaded()
    {
        var result = await InstallWithModuleList(
            [new { moduleName = "HistorySample", version = "0.9.0", commandCount = 3, attached = true, attachFailures = Array.Empty<string>() }]);

        Assert.False(result.Success);
        Assert.Contains("不是候选版本 1.0.0", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HotReloadReportsAttachedModuleAndCommandCount()
    {
        var result = await InstallWithModuleList(
            [new { moduleName = "HistorySample", version = "1.0.0", commandCount = 7, attached = true, attachFailures = Array.Empty<string>() }]);

        Assert.True(result.Success, result.Message);
        Assert.Contains("已核对：HistorySample 1.0.0 已接上宿主，7 条指令", result.Message, StringComparison.Ordinal);
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.True(data.GetProperty("Attached").GetBoolean());
        Assert.Equal(7, data.GetProperty("CommandCount").GetInt32());
    }

    [Fact]
    public void AttachStateReadsInProcessModuleMetadataToo()
    {
        var modules = new[]
        {
            new HistoryVulcan.Services.Modules.ModuleMeta("HistorySample", "", "", "1.0.0", true, "a.dll", 4),
        };

        Assert.True(ModuleAttachState.TryFind(modules, "historysample", out var state));
        Assert.NotNull(state);
        Assert.True(state!.Attached);
        Assert.Equal(4, state.CommandCount);

        Assert.True(ModuleAttachState.TryFind(modules, "HistoryOther", out var missing));
        Assert.Null(missing);
        Assert.False(ModuleAttachState.TryFind(null, "HistorySample", out _));
    }

    private static async Task<CommandResult> InstallWithModuleList(object[] modules)
    {
        var package = Path.Combine(Path.GetTempPath(), "pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(package);
        File.WriteAllText(
            Path.Combine(package, "module.manifest.json"),
            """{"schemaVersion":1,"type":"HistoryVulcan.Module","name":"HistorySample","version":"1.0.0","artifact":"HistorySample.dll"}""");
        try
        {
            var listed = JsonSerializer.SerializeToElement(modules);
            var context = new DevelopmentContext(
                new CommandBus(new CommandRegistry(), new NullLog()), new MemorySettings(), package)
            {
                LiveHost = (command, _) => Task.FromResult(command.StartsWith("vulcan.module.list", StringComparison.Ordinal)
                    ? CommandResult.Ok("模块列表", listed)
                    : CommandResult.Ok("已安装并重载 HistorySample 1.0.0")),
            };
            return await ModulePackageHotReload.InstallAsync(context, package, "test", CancellationToken.None);
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    /// <summary>
    /// 一个临时项目库：宿主目录里放真实的发布登记表，另有一个带 z-Publish 的 Janus 仓。
    /// </summary>
    private sealed class FakeLibrary : IDisposable
    {
        private FakeLibrary(string clio, string workRoot)
        {
            Clio = clio;
            WorkRoot = workRoot;
            ProjectPath = Path.Combine(clio, Project);
            Settings = new MemorySettings();
            Settings.Set(ProjectLibraryRoot.KeyLibraryRoot, clio);
            Settings.Set(WorktreeCommands.KeyWorktreeRoot, workRoot);
            var registry = new CommandRegistry();
            Bus = new CommandBus(registry, new NullLog());
            DevPipelineCommands.Register(registry, new DevelopmentContext(Bus, Settings, workRoot));
        }

        public string Clio { get; }
        public string WorkRoot { get; }
        public string ProjectPath { get; }
        public MemorySettings Settings { get; }
        public CommandBus Bus { get; }

        public static FakeLibrary Create()
        {
            var clio = Path.Combine(Path.GetTempPath(), "clio-" + Guid.NewGuid().ToString("N"));
            var workRoot = Path.Combine(Path.GetTempPath(), "aiwt-" + Guid.NewGuid().ToString("N"));
            var library = new FakeLibrary(clio, workRoot);

            var pipeline = Path.Combine(clio, "2026-023-HistoryVulcan", "b-Code-Eng", "pipeline");
            Directory.CreateDirectory(pipeline);
            File.Copy(
                Path.Combine(RepositoryPaths.Root(), "b-Code-Eng", "pipeline", "module-publish.manifest.json"),
                Path.Combine(pipeline, "module-publish.manifest.json"));

            Directory.CreateDirectory(Path.Combine(library.ProjectPath, "z-Publish"));
            Git(library.ProjectPath, "init", "-b", "main");
            Git(library.ProjectPath, "config", "user.email", "pipeline@test");
            Git(library.ProjectPath, "config", "user.name", "pipeline");
            File.WriteAllText(Path.Combine(library.ProjectPath, "README.md"), "x");
            File.WriteAllText(Path.Combine(library.ProjectPath, "z-Publish", "x.txt"), "x");
            Git(library.ProjectPath, "add", ".");
            Git(library.ProjectPath, "commit", "-m", "init");
            return library;
        }

        public (string Name, string Path) Start(string agent, string slug)
        {
            var created = WorktreeCommands.Create(Settings, Project, slug, agent, null, confirm: true);
            Assert.True(created.Success, created.Message);
            var data = JsonSerializer.SerializeToElement(created.Data);
            return (data.GetProperty("Name").GetString()!, data.GetProperty("Path").GetString()!);
        }

        public void Dispose()
        {
            try { Git(ProjectPath, "worktree", "prune"); } catch (InvalidOperationException) { }
            TryDelete(WorkRoot);
            TryDelete(Clio);
        }

        private static void Git(string workingDirectory, params string[] arguments)
            => ToolProcess.Capture("git", arguments, workingDirectory);

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
