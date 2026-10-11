using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Services.Development;
using HistoryVulcan.Services.Development.Pipeline;
using HistoryVulcan.Services.Modules;
using HistoryVulcan.Services.Nodes;
using Xunit;

namespace HistoryVulcan.Tests;

[Collection(RuntimeModulePackageCollection.Name)]
public sealed class NodeDeliveryTests
{
    [Fact]
    public void CertificateIdentitySurvivesReloadWithoutUsingAUserRegistryHive()
    {
        using var temp = new TemporaryDirectory();
        var node = Guid.NewGuid().ToString("N");
        using var first = NodeCertificateStore.Load(temp.Path, node);
        using var again = NodeCertificateStore.Load(temp.Path, node);
        Assert.True(again.HasPrivateKey);
        Assert.Equal(NodeTlsTransport.Fingerprint(first), NodeTlsTransport.Fingerprint(again));
        Assert.Throws<InvalidDataException>(() => NodeCertificateStore.Load(temp.Path, Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task NodeCommandsRequireRealApprovalAndPersistExactDeliveryEvidence()
    {
        using var temp = new TemporaryDirectory();
        var log = new RecordingLog();
        var bus = new CommandBus(new CommandRegistry(), log);
        using var modules = new ModuleHost(new RuntimeModuleDiscoverySource(Path.Combine(temp.Path, "Modules")), log)
        { EnableFileWatching = false, ModuleDataRoot = Path.Combine(temp.Path, "ModuleData") };
        modules.Attach(bus.Registry, bus);
        await using var service = new NodeService(temp.Path, bus, modules);
        NodeCommands.Register(bus.Registry, bus, modules, service, temp.Path);
        service.Start();
        modules.Start();
        var path = RuntimeModulePackageTests.CreatePackage(temp.Path, "candidate", "contextfixture", "v1.0.0");
        var command = "vulcan.node.deliver path=" + CommandParser.QuoteArg(path);
        Assert.False((await bus.ExecuteAsync(command, "cli:runtime")).Success);
        using var approval = new LocalActionApproval(true);
        using (new NodeExecutionScope(_ => true))
            Assert.False((await bus.ExecuteAsync(command, "module:trusted:cli:runtime")).Success);
        var delivered = await bus.ExecuteAsync(command, "test");
        Assert.True(delivered.Success, delivered.Message);
        var receipt = Assert.IsType<NodeDeliveryRecord>(delivered.Data);
        Assert.Equal("submitted", receipt.Stage);
        Assert.True((await bus.InvokeAsync("vulcan.node.verify id=" + receipt.Id, "test")).Success);
        Assert.True(File.Exists(Path.Combine(temp.Path, "Nodes", "Deliveries", receipt.Id + ".json")));
        bus.Registry.Register(new CommandDescriptor
        {
            Name = "contextfixture.json",
            Summary = "JSON probe",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("complete", new { machine = "test" })),
        }, "module:contextfixture");
        var result = await bus.ExecuteAsync("vulcan.node.invoke command=contextfixture.json", "test");
        Assert.True(result.Success);
        Assert.Equal("test", JsonSerializer.SerializeToElement(result.Data).GetProperty("machine").GetString());
        Assert.Contains(log.Messages, message => message.Contains("command=[REDACTED]", StringComparison.Ordinal));
        modules.Reload();
        Assert.False((await bus.InvokeAsync("vulcan.node.verify id=" + receipt.Id, "test")).Success);
        var recovered = await bus.InvokeAsync("vulcan.node.deliver hash=" + receipt.Artifact.Hash, "test");
        Assert.True(recovered.Success, recovered.Message);
    }

    [Fact]
    public async Task SubmitReviewBindsCommitHashAndTargetAndRejectsChangedCandidate()
    {
        using var temp = new TemporaryDirectory();
        var repo = Path.Combine(temp.Path, "worktree");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-b", "main");
        Git(repo, "config", "user.name", "Node test");
        Git(repo, "config", "user.email", "node-test@example.invalid");
        var candidate = Path.Combine(repo, "z-Publish", PublishPackages.PackageDirectoryName("HistorySample", "v1.0.0"));
        Directory.CreateDirectory(candidate);
        var path = RuntimeModulePackageTests.CreatePackage(candidate, "HistorySample", "HistorySample", "v1.0.0");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "candidate");
        var cache = new PackageArtifactStore(Path.Combine(temp.Path, "cache"));
        var artifact = await cache.ImportAsync(path);
        var target = Guid.NewGuid().ToString("N");
        var delivery = new NodeDeliveryRecord(Guid.NewGuid().ToString("N"), artifact, target, "submitted",
            new PackageDelivery(artifact, target, [], true, false,
                new NodeInstallation(artifact.Module, artifact.Version, artifact.Hash, target, "instance", true, true, 1, null), null), null);
        var host = new DevelopmentContext(new CommandBus(new CommandRegistry(), new NullLog()), new MemorySettings(), temp.Path)
        {
            LiveHost = (command, _) =>
            {
                if (command.StartsWith("vulcan.node.deliver", StringComparison.Ordinal))
                {
                    Assert.Contains("target=" + target, command);
                    return Task.FromResult(CommandResult.Ok("delivered", delivery));
                }
                Assert.Equal("vulcan.node.verify id=" + delivery.Id, command);
                return Task.FromResult(CommandResult.Ok("same instance"));
            },
        };
        Assert.True((await PipelineDelivery.SubmitAsync(host, repo, "HistorySample", target, default)).Success);
        var reviewed = await PipelineDelivery.RequireReviewedAsync(host, repo, "HistorySample", default);
        Assert.Equal(artifact.Hash, reviewed.Delivery.Artifact.Hash);
        Assert.Equal(target, reviewed.Delivery.Target);
        RuntimeModulePackageTests.CreatePackage(candidate, "HistorySample", "HistorySample", "v1.0.0", marker: "same-version-new-content");
        await Assert.ThrowsAsync<InvalidOperationException>(() => PipelineDelivery.RequireReviewedAsync(host, repo, "HistorySample", default));
    }

    [Fact]
    public async Task NonFastForwardFinishDoesNotMergeOrRemoveTheWorktree()
    {
        using var temp = new TemporaryDirectory();
        var main = Path.Combine(temp.Path, "clio", "sample");
        var workRoot = Path.Combine(temp.Path, "work");
        var worktree = Path.Combine(workRoot, "sample", "review");
        Directory.CreateDirectory(main);
        Directory.CreateDirectory(Path.GetDirectoryName(worktree)!);
        Git(main, "init", "-b", "main");
        Git(main, "config", "user.name", "Node test");
        Git(main, "config", "user.email", "node-test@example.invalid");
        File.WriteAllText(Path.Combine(main, "base.txt"), "base");
        Git(main, "add", "."); Git(main, "commit", "-m", "base");
        Git(main, "worktree", "add", "-b", "review", worktree);
        File.WriteAllText(Path.Combine(main, "other.txt"), "other");
        Git(main, "add", "."); Git(main, "commit", "-m", "other");
        File.WriteAllText(Path.Combine(worktree, "review.txt"), "review");
        Git(worktree, "add", "."); Git(worktree, "commit", "-m", "review");
        var before = Git(main, "rev-parse", "HEAD");
        var settings = new MemorySettings();
        settings.Set(ProjectLibraryRoot.KeyLibraryRoot, Path.GetDirectoryName(main)!);
        settings.Set(WorktreeCommands.KeyWorktreeRoot, workRoot);
        var host = new DevelopmentContext(new CommandBus(new CommandRegistry(), new NullLog()), settings, temp.Path);
        var result = await WorktreeCommands.MergeAsync(host, "sample", "review", default);
        Assert.False(result.Success);
        Assert.Equal(before, Git(main, "rev-parse", "HEAD"));
        Assert.True(Directory.Exists(worktree));
        Assert.False(File.Exists(Path.Combine(main, ".git", "MERGE_HEAD")));
    }

    private static string Git(string directory, params string[] args) => ToolProcess.CaptureLines("git", args, directory);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HistoryVulcan.NodeDeliveryTests", Guid.NewGuid().ToString("N"));
        internal TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            foreach (var file in Directory.GetFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
    }
}
