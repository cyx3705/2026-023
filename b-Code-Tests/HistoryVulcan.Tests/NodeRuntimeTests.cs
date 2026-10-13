using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Services.Modules;
using HistoryVulcan.Services.Nodes;
using Xunit;

namespace HistoryVulcan.Tests;

[Collection(RuntimeModulePackageCollection.Name)]
public sealed class NodeRuntimeTests
{
    [Fact]
    public async Task CacheDistinguishesSameVersionAndRejectsCorruptOrPartialContent()
    {
        using var temp = new TemporaryDirectory();
        var store = new PackageArtifactStore(Path.Combine(temp.Path, "cache"));
        var first = RuntimeModulePackageTests.CreatePackage(temp.Path, "first", "contextfixture", "v1.0.0", marker: "one");
        var second = RuntimeModulePackageTests.CreatePackage(temp.Path, "second", "contextfixture", "v1.0.0", marker: "two");
        var a = await store.ImportAsync(first);
        var b = await store.ImportAsync(second);
        Assert.NotEqual(a.Hash, b.Hash);
        Assert.Equal(a.Version, b.Version);
        Assert.Equal(a, await store.ImportAsync(first));
        await using var corrupt = new MemoryStream("broken"u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAsync(corrupt, a.Hash));
        Assert.Equal(2, Directory.GetFiles(store.Root).Length);
        Assert.Empty(new RuntimeModuleDiscoverySource(Path.Combine(temp.Path, "Modules")).Discover().Modules);
    }

    [Fact]
    public void ConcurrentOwnershipRemainsConflictUntilCausallyResolved()
    {
        using var temp = new TemporaryDirectory();
        var a = new ModuleOwnershipLedger(Path.Combine(temp.Path, "a"));
        var b = new ModuleOwnershipLedger(Path.Combine(temp.Path, "b"));
        var left = Guid.NewGuid().ToString("N");
        var right = Guid.NewGuid().ToString("N");
        a.Decide("sample", left, left, [], "initial");
        b.Merge(a.Read());
        a.Decide("sample", null, left, a.State("sample").Heads, "park");
        b.Decide("sample", right, right, b.State("sample").Heads, "takeover");
        a.Merge(b.Read());
        Assert.True(a.State("sample").Conflict);
        Assert.Null(a.State("sample").OwnerNodeId);
        Assert.Throws<InvalidOperationException>(() => a.Decide("sample", left, left, [], "stale"));
        a.Decide("sample", right, left, a.State("sample").Heads, "resolve");
        b.Merge(a.Read());
        Assert.False(b.State("sample").Conflict);
        Assert.Equal(right, b.State("sample").OwnerNodeId);
        var bad = a.Read()[0] with { OwnerNodeId = right };
        Assert.Throws<InvalidDataException>(() => b.Merge([bad]));
    }

    [Fact]
    public async Task BidirectionalTlsCallbacksDoNotBlockTheReaderAndWritesAreNotReplayed()
    {
        using var temp = new TemporaryDirectory();
        await using var pair = await Pair.Create(temp.Path);
        var calls = 0;
        NodeConnection? left = null;
        await using var right = new NodeConnection(pair.RightStream, async (operation, _, cancellation) =>
        {
            Interlocked.Increment(ref calls);
            if (operation == "hang") await Task.Delay(1000, cancellation);
            return JsonSerializer.SerializeToElement(true);
        });
        left = new NodeConnection(pair.LeftStream, (_, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(true)));
        await using (left)
        {
            Assert.True((await left.RequestAsync("write", new { }, TimeSpan.FromSeconds(3))).GetBoolean());
            Assert.True((await right.RequestAsync("reverse", new { }, TimeSpan.FromSeconds(3))).GetBoolean());
            var exception = await Assert.ThrowsAsync<IOException>(() => left.RequestAsync("hang", new { }, TimeSpan.FromMilliseconds(100)));
            Assert.Contains("未知", exception.Message);
            Assert.Equal(2, calls);
        }
    }

    [Fact]
    public async Task TlsRejectsUnpairedCertificate()
    {
        using var a = NodeTlsTransport.CreateCertificate(Guid.NewGuid().ToString("N"));
        using var b = NodeTlsTransport.CreateCertificate(Guid.NewGuid().ToString("N"));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var client = NodeTlsTransport.ConnectAsync(new NodePeer(b.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false),
            new string('0', 64), "127.0.0.1", port), a, deadline.Token);
        using var accepted = await listener.AcceptTcpClientAsync(deadline.Token);
        var server = NodeTlsTransport.AcceptAsync(accepted, new NodePeer(a.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false),
            NodeTlsTransport.Fingerprint(a), "127.0.0.1", port), b, deadline.Token);
        await Assert.ThrowsAnyAsync<Exception>(async () => await client);
        try { await using var ignored = await server; } catch (Exception) { }
    }

    [Fact]
    public async Task RemoteDeliveryRoutesCommandsConfirmationAndEventsWithoutLoadingTheCache()
    {
        using var temp = new TemporaryDirectory();
        await using var pair = await Pair.Create(temp.Path);
        await using var a = new TestNode(Path.Combine(temp.Path, "a"), pair.A, pair.B);
        await using var b = new TestNode(Path.Combine(temp.Path, "b"), pair.B, pair.A);
        a.Runtime.Attach(pair.LeftStream);
        b.Runtime.Attach(pair.RightStream);
        await a.Runtime.SynchronizeAsync();
        var package = RuntimeModulePackageTests.CreatePackage(temp.Path, "candidate", "contextfixture", "v1.0.0");
        var delivery = await a.Runtime.DeliverAsync(package, pair.B.NodeId, _ => true);
        Assert.True(delivery.Success, delivery.Error);
        Assert.Empty(a.Modules.Modules);
        Assert.True(Assert.Single(b.Modules.Modules).Attached);
        Assert.Equal(delivery.Artifact.Hash, Assert.Single(b.Modules.Modules).PackageHash);
        Assert.True((await a.Bus.ExecuteAsync("contextfixture.context-probe", "test")).Success);

        var confirmed = 0;
        a.Bus.Confirmation = new TestConfirmation(_ => { confirmed++; return true; });
        b.Bus.Registry.Register(new CommandDescriptor
        {
            Name = "contextfixture.confirm",
            Level = CommandLevel.Ask,
            Summary = "confirm",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(b.Bus.RequestConfirmation("nested") ? "approved" : "denied")),
        }, "module:contextfixture");
        // The instance has acquired a command after installation; refresh the advertised metadata.
        await a.Runtime.SynchronizeAsync();
        var asked = await a.Bus.InvokeAsync("contextfixture.confirm", "test").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(asked.Success, asked.Message);
        Assert.Equal("approved", asked.Message);
        Assert.Equal(2, confirmed);

        var evt = new TaskCompletionSource<HistoryVulcan.Core.Modules.BusEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = a.Bus.Events.Subscribe("contextfixture.changed", "test", item => evt.TrySetResult(item));
        Assert.True((await a.Bus.InvokeAsync("contextfixture.context-publish topic=contextfixture.changed", "test")).Success);
        var received = await evt.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("node:" + pair.B.NodeId, received.Source);
        Assert.True((await a.Runtime.VerifyDeliveryAsync(delivery, default)).Success);
        var repeat = await a.Runtime.DeliverAsync(package, pair.B.NodeId, _ => true);
        Assert.Equal(delivery.Installation!.InstanceId, repeat.Installation!.InstanceId);
    }

    [Fact]
    public async Task PlaceOnThePeerInstallsAfterThePeerStopsThePreviousInstance()
    {
        using var temp = new TemporaryDirectory();
        await using var pair = await Pair.Create(temp.Path);
        await using var a = new TestNode(Path.Combine(temp.Path, "a"), pair.A, pair.B);
        await using var b = new TestNode(Path.Combine(temp.Path, "b"), pair.B, pair.A);
        a.Runtime.Attach(pair.LeftStream);
        b.Runtime.Attach(pair.RightStream);
        await a.Runtime.SynchronizeAsync();
        var package = RuntimeModulePackageTests.CreatePackage(temp.Path, "candidate", "contextfixture", "v1.0.0");
        var installed = await a.Runtime.DeliverAsync(package, pair.A.NodeId, _ => true);
        Assert.True(installed.Success, installed.Error);
        Assert.True(Assert.Single(a.Modules.Modules).Attached);

        var moved = await a.Runtime.PlaceAsync("contextfixture", pair.B.NodeId, CancellationToken.None);

        Assert.Contains("已装到所选设备", moved, StringComparison.Ordinal);
        Assert.Empty(a.Modules.Modules);
        Assert.True(Assert.Single(b.Modules.Modules).Attached);
        Assert.Equal(pair.B.NodeId, a.Runtime.Ownership.State("contextfixture").OwnerNodeId);
    }

    [Fact]
    public void ExecutionReconcileFollowsTheExecutingPartyAndDoesNotPreferAComputer()
    {
        using var temp = new TemporaryDirectory();
        var left = Guid.NewGuid().ToString("N");
        var right = Guid.NewGuid().ToString("N");
        var parked = new ModuleOwnershipLedger(Path.Combine(temp.Path, "parked"));
        var loaded = new ModuleOwnershipLedger(Path.Combine(temp.Path, "loaded"));
        parked.Decide("sample", left, left, [], "initial");
        loaded.Merge(parked.Read());
        parked.Decide("sample", null, left, parked.State("sample").Heads, "park");
        loaded.Decide("sample", right, right, loaded.State("sample").Heads, "takeover");
        parked.Merge(loaded.Read());
        Assert.True(parked.TryReconcileExecution("sample"));
        Assert.Equal(right, parked.State("sample").OwnerNodeId);
        loaded.Merge(parked.Read());
        Assert.False(loaded.State("sample").Conflict);
        Assert.Equal(right, loaded.State("sample").OwnerNodeId);

        var again = new ModuleOwnershipLedger(Path.Combine(temp.Path, "again"));
        var other = new ModuleOwnershipLedger(Path.Combine(temp.Path, "other"));
        again.Decide("sample", left, left, [], "initial");
        other.Merge(again.Read());
        again.Decide("sample", left, left, again.State("sample").Heads, "stay");
        other.Decide("sample", right, right, other.State("sample").Heads, "take");
        again.Merge(other.Read());
        Assert.False(again.TryReconcileExecution("sample"));
        Assert.True(again.State("sample").Conflict);
        Assert.Null(again.State("sample").OwnerNodeId);
    }

    [Fact]
    public async Task OfflinePlaceFollowsTheExecutorAndMovesModuleDataWithoutTouchingTheProjectLibrary()
    {
        using var temp = new TemporaryDirectory();
        await using var pair = await Pair.Create(temp.Path);
        await using var a = new TestNode(Path.Combine(temp.Path, "a"), pair.A, pair.B);
        await using var b = new TestNode(Path.Combine(temp.Path, "b"), pair.B, pair.A);
        var package = RuntimeModulePackageTests.CreatePackage(temp.Path, "candidate", "contextfixture", "v1.0.0");
        var installed = await a.Runtime.DeliverAsync(package, "local", _ => true);
        Assert.True(installed.Success, installed.Error);
        var library = Path.Combine(temp.Path, "library", "sample", "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(library)!);
        File.WriteAllText(library, "project-library");
        var data = Path.Combine(temp.Path, "a", "ModuleData", "contextfixture", "state.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(data)!);
        File.WriteAllText(data, "module-state");

        var moved = await a.Runtime.PlaceAsync("contextfixture", pair.B.NodeId, CancellationToken.None);

        Assert.Contains("对端起来后同步", moved, StringComparison.Ordinal);
        Assert.Empty(a.Modules.Modules);
        Assert.Equal(pair.B.NodeId, a.Runtime.Ownership.State("contextfixture").OwnerNodeId);
        Assert.Equal("module-state", File.ReadAllText(data));
        Assert.Equal("project-library", File.ReadAllText(library));
        Assert.Equal(pair.B.NodeId, Assert.Single(a.Runtime.Scenes()).ExecutionNodeId);

        a.Runtime.Attach(pair.LeftStream);
        b.Runtime.Attach(pair.RightStream);
        await a.Runtime.SynchronizeAsync();

        Assert.Empty(a.Modules.Modules);
        Assert.True(Assert.Single(b.Modules.Modules).Attached);
        Assert.Equal("module-state", File.ReadAllText(Path.Combine(temp.Path, "b", "ModuleData", "contextfixture", "state.txt")));
        Assert.False(File.Exists(data));
        Assert.Equal("project-library", File.ReadAllText(library));
        Assert.False(File.Exists(Path.Combine(temp.Path, "b", "library", "sample", "keep.txt")));
        Assert.Equal(pair.B.NodeId, Assert.Single(a.Runtime.Scenes()).ExecutionNodeId);
        Assert.Equal(a.Runtime.Scenes(), b.Runtime.Scenes());
        Assert.True((await a.Bus.InvokeAsync("contextfixture.context-probe", "test")).Success);
    }

    [Fact]
    public async Task ContradictoryOfflineLoadsDoNotPreferAComputer()
    {
        using var temp = new TemporaryDirectory();
        await using var pair = await Pair.Create(temp.Path);
        await using var a = new TestNode(Path.Combine(temp.Path, "a"), pair.A, pair.B);
        await using var b = new TestNode(Path.Combine(temp.Path, "b"), pair.B, pair.A);
        var package = RuntimeModulePackageTests.CreatePackage(temp.Path, "candidate", "contextfixture", "v1.0.0");
        var installed = await a.Runtime.DeliverAsync(package, "local", _ => true);
        Assert.True(installed.Success, installed.Error);
        b.Runtime.Ownership.Merge(a.Runtime.Ownership.Read());
        var installedPackage = Assert.Single(Directory.GetFiles(a.Modules.ModulesDirectory, "*.nupkg"));
        Directory.CreateDirectory(b.Modules.ModulesDirectory);
        File.Copy(installedPackage, Path.Combine(b.Modules.ModulesDirectory, Path.GetFileName(installedPackage)));

        Assert.Contains("不装载", await a.Runtime.PlaceAsync("contextfixture", "none", CancellationToken.None), StringComparison.Ordinal);
        Assert.Contains("已装到所选设备", await a.Runtime.PlaceAsync("contextfixture", "local", CancellationToken.None), StringComparison.Ordinal);
        Assert.Contains("已装到所选设备", await b.Runtime.PlaceAsync("contextfixture", "local", CancellationToken.None), StringComparison.Ordinal);
        Assert.Single(a.Modules.Modules);
        Assert.Single(b.Modules.Modules);

        a.Runtime.Attach(pair.LeftStream);
        b.Runtime.Attach(pair.RightStream);
        await a.Runtime.SynchronizeAsync();

        Assert.Empty(a.Modules.Modules);
        Assert.Empty(b.Modules.Modules);
        Assert.True(a.Runtime.Ownership.State("contextfixture").Conflict);
        Assert.True(b.Runtime.Ownership.State("contextfixture").Conflict);
        Assert.Empty(a.Runtime.Scenes());
        Assert.Empty(b.Runtime.Scenes());
    }

    [Fact]
    public async Task PreloadSuccessCannotMaskFailedRuntimeDeliveryAndOfflineDoesNotMoveOwnership()
    {
        using var temp = new TemporaryDirectory();
        var aId = new NodeIdentity(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        var bId = new NodeIdentity(Guid.NewGuid().ToString("N"), aId.LogicalHostId);
        await using var a = new TestNode(temp.Path, aId, bId);
        a.Runtime.PreloadStore = new PackageArtifactStore(Path.Combine(temp.Path, "preload"));
        var package = RuntimeModulePackageTests.CreatePackage(temp.Path, "candidate", "contextfixture", "v1.0.0");
        a.Runtime.Ownership.Decide("contextfixture", bId.NodeId, aId.NodeId, [], "initial");
        var failed = await a.Runtime.DeliverAsync(package, null, _ => true);
        Assert.False(failed.Success);
        Assert.True(failed.Preloaded);
        Assert.Equal(bId.NodeId, failed.Target);
        Assert.Empty(a.Modules.Modules);
        var stored = await a.Runtime.DeliverAsync(package, "preload", _ => true);
        Assert.True(stored.Success);
        Assert.Null(stored.Installation);
        Assert.Equal(bId.NodeId, a.Runtime.Ownership.State("contextfixture").OwnerNodeId);
    }

    [Fact]
    public async Task ReconnectRetiresOldOwnerAfterExplicitOfflineTakeover()
    {
        using var temp = new TemporaryDirectory();
        await using var pair = await Pair.Create(temp.Path);
        await using var a = new TestNode(Path.Combine(temp.Path, "a"), pair.A, pair.B);
        await using var b = new TestNode(Path.Combine(temp.Path, "b"), pair.B, pair.A);
        var package = RuntimeModulePackageTests.CreatePackage(temp.Path, "candidate", "contextfixture", "v1.0.0");
        var original = await a.Runtime.DeliverAsync(package, "local", _ => true);
        Assert.True(original.Success, original.Error);
        b.Runtime.Ownership.Merge(a.Runtime.Ownership.Read());
        await b.Runtime.ChangeOwnershipAsync("contextfixture", "local", b.Runtime.Ownership.State("contextfixture").Heads,
            "takeover", "follow-load", _ => true, default);
        var takeover = await b.Runtime.DeliverAsync(package, "local", _ => true);
        Assert.True(takeover.Success, takeover.Error);
        Assert.Single(a.Modules.Modules);
        a.Runtime.Attach(pair.LeftStream);
        b.Runtime.Attach(pair.RightStream);
        await a.Runtime.SynchronizeAsync();
        Assert.Empty(a.Modules.Modules);
        Assert.Single(b.Modules.Modules);
        Assert.True((await a.Bus.InvokeAsync("contextfixture.context-probe", "test")).Success);
    }

    [Fact]
    public async Task RefreshReplacesARunningLocalPackageWithTheModulesDirectory()
    {
        using var temp = new TemporaryDirectory();
        var self = new NodeIdentity(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        var peer = new NodeIdentity(Guid.NewGuid().ToString("N"), self.LogicalHostId);
        await using var node = new TestNode(temp.Path, self, peer);
        var previous = Environment.GetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable);
        try
        {
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v1.0.0");
            var original = RuntimeModulePackageTests.CreatePackage(temp.Path, "candidate", "contextfixture", "v1.0.0", marker: "one");
            var installed = await node.Runtime.DeliverAsync(original, "local", _ => true);
            Assert.True(installed.Success, installed.Error);
            var before = node.Modules.Modules.Single();
            Assert.Equal("v1.0.0", before.Version);

            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v1.0.1");
            var newer = RuntimeModulePackageTests.CreatePackage(temp.Path, "newer", "contextfixture", "v1.0.1", marker: "two");
            File.Copy(newer, Path.Combine(node.Modules.ModulesDirectory, "contextfixture.nupkg"), overwrite: true);
            var message = await node.Runtime.RefreshDevicesAsync(CancellationToken.None);

            Assert.Contains("v1.0.1", message, StringComparison.Ordinal);
            var after = node.Modules.Modules.Single();
            Assert.Equal("v1.0.1", after.Version);
            Assert.NotEqual(before.PackageHash, after.PackageHash);
            Assert.Contains(node.Runtime.Devices(), device => device.Self && device.Id == self.NodeId && device.Label == "本机");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, previous);
        }
    }

    [Fact]
    public async Task AuthenticatedContextSurvivesUiDispatchAndCannotBecomeLocalApproval()
    {
        var bus = new CommandBus(new CommandRegistry(), new NullLog());
        bus.UiContext = new UnflowedContext();
        bus.Registry.Register(new CommandDescriptor
        {
            Name = "sample.inspect",
            RequiresUiThread = true,
            Summary = "inspect",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(NodeExecutionScope.Current != null && !LocalActionApproval.Approved ? "remote" : "wrong")),
        });
        using var approval = new LocalActionApproval(true);
        using var scope = new NodeExecutionScope(_ => false);
        var result = await bus.InvokeAsync("sample.inspect", "module:sample:local");
        Assert.Equal("remote", result.Message);
    }

    private sealed class UnflowedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => ThreadPool.UnsafeQueueUserWorkItem(_ => callback(state), null);
    }
    private sealed class TestConfirmation(Func<string, bool> callback) : IConfirmationService
    {
        public bool Confirm(string prompt) => callback(prompt);
    }
    private sealed class TestNode : IAsyncDisposable
    {
        internal CommandBus Bus { get; } = new(new CommandRegistry(), new NullLog());
        internal ModuleHost Modules { get; }
        internal NodeRuntime Runtime { get; }
        internal TestNode(string root, NodeIdentity identity, NodeIdentity peer)
        {
            Modules = new ModuleHost(new RuntimeModuleDiscoverySource(Path.Combine(root, "Modules")), new NullLog())
            { EnableFileWatching = false, ModuleDataRoot = Path.Combine(root, "ModuleData") };
            Modules.Attach(Bus.Registry, Bus);
            Runtime = new NodeRuntime(Bus, Modules, identity, peer.NodeId, Path.Combine(root, "Nodes"), Path.Combine(root, "Cache"));
            Modules.Start();
        }
        public async ValueTask DisposeAsync() { await Runtime.DisposeAsync(); Modules.Dispose(); }
    }
    private sealed class Pair : IAsyncDisposable
    {
        internal required NodeIdentity A { get; init; }
        internal required NodeIdentity B { get; init; }
        internal required Stream LeftStream { get; init; }
        internal required Stream RightStream { get; init; }
        internal static async Task<Pair> Create(string root)
        {
            var group = Guid.NewGuid().ToString("N");
            var a = new NodeIdentity(Guid.NewGuid().ToString("N"), group);
            var b = new NodeIdentity(Guid.NewGuid().ToString("N"), group);
            using var ca = NodeTlsTransport.CreateCertificate(a.NodeId);
            using var cb = NodeTlsTransport.CreateCertificate(b.NodeId);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var connecting = NodeTlsTransport.ConnectAsync(new NodePeer(b.NodeId, NodeTlsTransport.Fingerprint(cb), "127.0.0.1", port), ca, deadline.Token);
            var accepted = await listener.AcceptTcpClientAsync(deadline.Token);
            var right = await NodeTlsTransport.AcceptAsync(accepted, new NodePeer(a.NodeId, NodeTlsTransport.Fingerprint(ca), "127.0.0.1", port), cb, deadline.Token);
            return new Pair { A = a, B = b, LeftStream = await connecting, RightStream = right };
        }
        public async ValueTask DisposeAsync() { await LeftStream.DisposeAsync(); await RightStream.DisposeAsync(); }
    }
}
