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

    private static string Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 git");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} 失败: {error}");
        return output;
    }

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

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => [.. _values];
    }
}
