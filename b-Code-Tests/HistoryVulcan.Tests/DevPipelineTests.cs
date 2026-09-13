using System.Diagnostics;
using System.Text;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services.Development;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class DevPipelineTests
{
    [Theory]
    [InlineData("HistoryVulcan")]
    [InlineData("historyvulcan")]
    [InlineData("2026-023-HistoryVulcan")]
    public void HostNamesAreRejectedFromTheModulePipeline(string name)
        => Assert.True(DevPipelineCommands.IsHostTarget(name));

    [Theory]
    [InlineData("HistoryJanus")]
    [InlineData("2026-020-HistoryJanus")]
    [InlineData("HistoryDiana")]
    public void ModuleNamesAreNotHostTargets(string name)
        => Assert.False(DevPipelineCommands.IsHostTarget(name));

    [Fact]
    public void StartSkipsLeftoverBranchAndUsesTheNextOrdinal()
    {
        var clio = Path.Combine(Path.GetTempPath(), "clio-" + Guid.NewGuid().ToString("N"));
        var workRoot = Path.Combine(Path.GetTempPath(), "aiwt-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(clio, "2026-020-HistoryJanus");
        Directory.CreateDirectory(project);
        try
        {
            Git(project, "init", "-b", "main");
            Git(project, "config", "user.email", "pipeline@test");
            Git(project, "config", "user.name", "pipeline");
            File.WriteAllText(Path.Combine(project, "README.md"), "x");
            Git(project, "add", "README.md");
            Git(project, "commit", "-m", "init");
            var head = Git(project, "rev-parse", "--short", "HEAD").Trim();
            Git(project, "branch", $"ai/2026-020-HistoryJanus/{head}-1-grok-pipeline-probe");

            var settings = new MemorySettings();
            settings.Set(ProjectLibraryRoot.KeyLibraryRoot, clio);
            settings.Set(WorktreeCommands.KeyWorktreeRoot, workRoot);

            var created = WorktreeCommands.Create(
                settings, "2026-020-HistoryJanus", "pipeline-probe", "grok", null, confirm: true);
            Assert.True(created.Success, created.Message);
            var expected = $"{head}-2-grok-pipeline-probe";
            Assert.Contains(expected, created.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(Path.Combine(workRoot, "2026-020-HistoryJanus", expected)));
        }
        finally
        {
            try { Git(project, "worktree", "prune"); } catch { }
            TryDelete(workRoot);
            TryDelete(clio);
        }
    }

    [Fact]
    public void FinishHintDoesNotBlockOtherAiOnADifferentLeftoverWorktree()
    {
        Assert.Contains("对话根不是那条工作区就可以 finish", DevPipelineCommands.MoveRootBeforeFinish, StringComparison.Ordinal);
        Assert.Contains("不要因为对话根在另一条 F 盘残留目录就停住", DevPipelineCommands.MoveRootBeforeFinish, StringComparison.Ordinal);
        Assert.DoesNotContain("其他 AI 本来就不在工作区里，可直接 finish", DevPipelineCommands.MoveRootBeforeFinish, StringComparison.Ordinal);
    }

    private static string Git(string workingDirectory, params string[] arguments)
        => HistoryVulcan.Services.Development.Pipeline.ToolProcess.Capture("git", arguments, workingDirectory);

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

}
