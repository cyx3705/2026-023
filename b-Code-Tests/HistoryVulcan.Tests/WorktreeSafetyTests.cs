using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Services.Development;
using HistoryVulcan.Services.Development.Pipeline;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>REQ-REL-003：在隔离真仓里验证 finish 不得回收主仓，Git 拒绝后不得越权删除。</summary>
public sealed class WorktreeSafetyTests
{
    [Theory]
    [InlineData("main")]
    [InlineData("normalized-main")]
    [InlineData("metadata")]
    [InlineData("child")]
    [InlineData("parent")]
    public async Task ProtectedPathsAreRejectedBeforeMerge(string kind)
    {
        using var repo = new Repository();
        var path = kind switch
        {
            "normalized-main" => Path.Combine(repo.Main, "..", Repository.Project, "."),
            "metadata" => Path.Combine(repo.Main, ".git"),
            "child" => Path.Combine(repo.Main, "child"),
            "parent" => Path.GetDirectoryName(repo.Main)!,
            _ => repo.Main,
        };
        Directory.CreateDirectory(path);
        var before = repo.Snapshot();
        var finalized = false;
        var result = await WorktreeCommands.MergeAsync(repo.Host, Repository.Project, path, default, () =>
        {
            finalized = true;
            return Task.FromResult(CommandResult.Ok("unexpected"));
        });
        Assert.False(result.Success, result.Message);
        Assert.Contains("主工作树", result.Message);
        Assert.False(finalized);
        Assert.Equal(before, repo.Snapshot());
    }

    [Fact]
    public async Task FinishRejectsMainBeforeCandidateReview()
    {
        using var repo = new Repository();
        var before = repo.Snapshot();
        var result = await repo.Bus.ExecuteAsync($"vulcan.dev.finish worktree=\"{repo.Main}\"", "Test");
        Assert.False(result.Success);
        Assert.Contains("主工作树", result.Message);
        Assert.Equal(before, repo.Snapshot());
    }

    [Fact]
    public async Task ForeignAndUnregisteredWorktreesArePreserved()
    {
        using var repo = new Repository();
        using var foreign = new Repository();
        var foreignTree = foreign.AddWorktree("foreign");
        var unregistered = Path.Combine(repo.Root, "unregistered");
        Directory.CreateDirectory(unregistered);
        File.Copy(Path.Combine(repo.Worktree, ".git"), Path.Combine(unregistered, ".git"));
        File.WriteAllText(Path.Combine(unregistered, "sentinel.txt"), "preserve");
        var before = repo.Snapshot();
        var foreignBefore = foreign.Snapshot();
        foreach (var target in new[] { foreign.Main, foreignTree, unregistered, Path.Combine(repo.Worktree, "child") })
        {
            Directory.CreateDirectory(target);
            var result = await WorktreeCommands.MergeAsync(repo.Host, Repository.Project, target, default);
            Assert.False(result.Success, result.Message);
            Assert.True(Directory.Exists(target));
        }
        Assert.Equal(before, repo.Snapshot());
        Assert.Equal(foreignBefore, foreign.Snapshot());
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(unregistered, "sentinel.txt")));
    }

    [Fact]
    public async Task RegisteredPathWithForeignGitPointerIsPreserved()
    {
        using var repo = new Repository();
        using var foreign = new Repository();
        var pointer = File.ReadAllText(Path.Combine(foreign.Worktree, ".git"));
        var marker = Path.Combine(repo.Worktree, ".git");
        // Windows 下 Git 将 .git 标为 Hidden，造假夹具需先显式去掉该属性。
        File.SetAttributes(marker, FileAttributes.Normal);
        File.WriteAllText(marker, pointer);
        var before = repo.Snapshot();
        var foreignBefore = foreign.Snapshot();
        var result = await WorktreeCommands.MergeAsync(repo.Host, Repository.Project, repo.Worktree, default);
        Assert.False(result.Success);
        Assert.Contains("归属不匹配", result.Message);
        Assert.Equal(before, repo.Snapshot());
        Assert.Equal(foreignBefore, foreign.Snapshot());
        Assert.Equal(pointer, File.ReadAllText(Path.Combine(repo.Worktree, ".git")));
    }

    [Fact]
    public async Task LockedWorktreeIsRejectedBeforeMerge()
    {
        using var repo = new Repository();
        repo.CommitWorktree();
        Repository.Git(repo.Main, "worktree", "lock", "--reason", "keep", repo.Worktree);
        var before = repo.Snapshot();
        var result = await WorktreeCommands.MergeAsync(repo.Host, Repository.Project, repo.Worktree, default);
        Assert.False(result.Success);
        Assert.Contains("锁定", result.Message);
        Assert.Equal(before, repo.Snapshot());
        Assert.True(File.Exists(Path.Combine(repo.Worktree, ".git")));
    }

    [Fact]
    public async Task DirtyWorktreeIsPreservedBeforeMerge()
    {
        using var repo = new Repository();
        repo.CommitWorktree();
        File.WriteAllText(Path.Combine(repo.Worktree, "uncommitted.txt"), "preserve");
        var before = repo.Snapshot();
        var result = await WorktreeCommands.MergeAsync(repo.Host, Repository.Project, repo.Worktree, default);
        Assert.False(result.Success);
        Assert.Contains("未提交", result.Message);
        Assert.Equal(before, repo.Snapshot());
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(repo.Worktree, "uncommitted.txt")));
    }

    [Fact]
    public async Task GitRemovalFailurePreservesFilesAndSkipsFinalization()
    {
        using var repo = new Repository();
        repo.CommitWorktree();
        // Git 完成快进后才产生新文件：真正走到 git worktree remove 的拒绝分支。
        var lateFile = Path.Combine(repo.Worktree, "late.txt");
        var hooks = Path.Combine(repo.Main, ".git", "hooks");
        Repository.Git(repo.Main, "config", "core.hooksPath", hooks.Replace('\\', '/'));
        File.WriteAllText(Path.Combine(hooks, "post-merge"),
            "#!/bin/sh\nprintf '%s' 'preserve me' > '" + lateFile.Replace('\\', '/').Replace("'", "'\"'\"'") + "'\n");
        var finalized = false;
        var result = await WorktreeCommands.MergeAsync(repo.Host, Repository.Project, repo.Worktree, default, () =>
        {
            finalized = true;
            return Task.FromResult(CommandResult.Ok("unexpected"));
        });
        Assert.False(result.Success);
        Assert.Contains("Git 拒绝回收", result.Message);
        Assert.False(finalized);
        Assert.Equal("preserve me", File.ReadAllText(lateFile));
        Assert.True(File.Exists(Path.Combine(repo.Worktree, ".git")));
        Assert.Contains(repo.Worktree.Replace('\\', '/'), Repository.Git(repo.Main, "worktree", "list", "--porcelain"));
        Assert.Equal(Repository.Git(repo.Worktree, "rev-parse", "HEAD"), Repository.Git(repo.Main, "rev-parse", "HEAD"));
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.True(data.GetProperty("Merged").GetBoolean());
        Assert.False(data.GetProperty("WorktreeRemoved").GetBoolean());
        Assert.False(data.GetProperty("DeliveryVerified").GetBoolean());
        Assert.True(Directory.Exists(Path.Combine(repo.Main, ".git")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredLinkedWorktreeMergesAndRemovesOnlyTheExactPath(bool useName)
    {
        using var repo = new Repository();
        repo.CommitWorktree();
        var neighbor = repo.AddWorktree("neighbor");
        var branch = Repository.Git(repo.Worktree, "branch", "--show-current");
        var finalized = false;
        var result = await WorktreeCommands.MergeAsync(repo.Host, Repository.Project,
            useName ? Path.GetFileName(repo.Worktree) : repo.Worktree, default, () =>
            {
                Assert.False(Directory.Exists(repo.Worktree));
                finalized = true;
                return Task.FromResult(CommandResult.Ok("verified"));
            });
        Assert.True(result.Success, result.Message);
        Assert.True(finalized);
        Assert.False(Directory.Exists(repo.Worktree));
        Assert.Equal("change", File.ReadAllText(Path.Combine(repo.Main, "change.txt")));
        Assert.True(File.Exists(Path.Combine(neighbor, ".git")));
        Assert.True(Directory.Exists(Path.Combine(repo.Main, ".git")));
        Assert.Equal(branch, Repository.Git(repo.Main, "branch", "--list", branch));
    }

    [Fact]
    public async Task DuplicateBasenamesRequireAnExactPath()
    {
        using var repo = new Repository();
        var duplicate = Path.Combine(repo.Root, "elsewhere", Path.GetFileName(repo.Worktree));
        Directory.CreateDirectory(Path.GetDirectoryName(duplicate)!);
        Repository.Git(repo.Main, "worktree", "add", "-b", "duplicate", duplicate);
        // 即使配置根下有同名目录，也不能借 fallback 绕过登记名歧义。
        repo.Host.Settings.Set(WorktreeCommands.KeyWorktreeRoot, Path.Combine(repo.Root, "linked"));
        var before = repo.Snapshot();
        var finish = await repo.Bus.ExecuteAsync($"vulcan.dev.finish worktree=\"{Path.GetFileName(repo.Worktree)}\"", "Test");
        Assert.False(finish.Success);
        Assert.Contains("未唯一匹配", finish.Message);
        var ambiguous = await WorktreeCommands.MergeAsync(repo.Host, Repository.Project, Path.GetFileName(repo.Worktree), default);
        Assert.False(ambiguous.Success);
        Assert.Equal(before, repo.Snapshot());
        var result = await WorktreeCommands.MergeAsync(repo.Host, Repository.Project, duplicate, default,
            () => Task.FromResult(CommandResult.Ok("verified")));
        Assert.True(result.Success, result.Message);
        Assert.False(Directory.Exists(duplicate));
        Assert.True(File.Exists(Path.Combine(repo.Worktree, ".git")));
    }

    [Fact]
    public void DirectoryJunctionIsRejectedWithoutTouchingItsTarget()
    {
        using var repo = new Repository();
        var alias = Path.Combine(repo.Root, "alias");
        ToolProcess.Capture("cmd.exe", ["/d", "/c", "mklink", "/J", alias, repo.Worktree], repo.Root);
        try
        {
            Assert.False(WorktreeCommands.TryValidateLinkedWorktree(repo.Main, alias, out var error));
            Assert.Contains("链接", error);
            Assert.True(File.Exists(Path.Combine(repo.Worktree, ".git")));
        }
        finally
        {
            Directory.Delete(alias);
        }
    }

    private sealed class Repository : IDisposable
    {
        internal const string Project = "2026-999-HistorySample";
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "VulcanWorktreeSafety", Guid.NewGuid().ToString("N"));
        internal string Main { get; }
        internal string Worktree { get; }
        internal CommandBus Bus { get; }
        internal DevelopmentContext Host { get; }

        internal Repository()
        {
            Main = Path.Combine(Root, "clio", Project);
            Directory.CreateDirectory(Main);
            Git(Main, "init", "-b", "main");
            Git(Main, "config", "user.name", "Safety test");
            Git(Main, "config", "user.email", "safety@example.invalid");
            Git(Main, "config", "commit.gpgSign", "false");
            File.WriteAllText(Path.Combine(Main, "sentinel.txt"), "main must survive");
            File.WriteAllText(Path.Combine(Main, "project.manifest.json"), """
                { "schemaVersion": 1, "project": { "name": "HistorySample" },
                  "publish": { "versionProps": "Version.props", "versionProperty": "Version",
                    "sourceManifest": "module.manifest.json",
                    "package": { "project": "Module.csproj", "files": ["HistorySample.dll"] },
                    "validation": [{ "tool": "dotnet.exe", "arguments": ["test"] }] } }
                """);
            Git(Main, "add", ".");
            Git(Main, "commit", "-m", "base");
            Worktree = AddWorktree("工作 区");
            var settings = new MemorySettings();
            settings.Set(ProjectLibraryRoot.KeyLibraryRoot, Path.GetDirectoryName(Main)!);
            settings.Set(WorktreeCommands.KeyWorktreeRoot, Path.Combine(Root, "configured-empty-root"));
            var registry = new CommandRegistry();
            Bus = new CommandBus(registry, new NullLog());
            Host = new DevelopmentContext(Bus, settings, Path.Combine(Root, "data"));
            DevPipelineCommands.Register(registry, Host);
        }

        internal string AddWorktree(string name)
        {
            var path = Path.Combine(Root, "linked", Project, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Git(Main, "worktree", "add", "-b", "review-" + Guid.NewGuid().ToString("N"), path);
            return path;
        }

        internal void CommitWorktree()
        {
            File.WriteAllText(Path.Combine(Worktree, "change.txt"), "change");
            Git(Worktree, "add", ".");
            Git(Worktree, "commit", "-m", "change");
        }

        internal string Snapshot() => string.Join("\n", Git(Main, "rev-parse", "HEAD"),
            Git(Main, "worktree", "list", "--porcelain"), File.ReadAllText(Path.Combine(Main, ".git", "config")),
            File.ReadAllText(Path.Combine(Main, "sentinel.txt")));
        internal static string Git(string directory, params string[] args) => ToolProcess.Capture("git", args, directory);

        public void Dispose()
        {
            foreach (var file in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
    }
}
