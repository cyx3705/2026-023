using System.Collections.Concurrent;
using System.Text.Json;
using HistoryVulcan.Services.Modules;

namespace HistoryVulcan.Services.Nodes;

internal sealed record PackageTransferStart(PackageArtifact Artifact);
internal sealed record PackageTransferChunk(string TransferId, long Offset, byte[] Bytes);
internal sealed record NodeInstallCall(string CallId, PackageArtifact Artifact, string OwnershipHead);
internal sealed record PackageDelivery(
    PackageArtifact Artifact, string Target, string[] PendingSync, bool Cached, bool Preloaded,
    NodeInstallation? Installation, string? Error)
{
    public bool Success => Error == null && (Target == "preload" ? Preloaded : Installation is { Installed: true, Attached: true, Error: null });
}

internal sealed partial class NodeRuntime
{
    private readonly ConcurrentDictionary<string, IncomingPackage> _incoming = new();
    private readonly SemaphoreSlim _delivery = new(1, 1);
    private readonly ConcurrentDictionary<string, NodeInstallCall> _installApprovals = new();
    private readonly object _transferGate = new();
    internal PackageArtifactStore? PreloadStore { get; set; }

    internal async Task<PackageDelivery> DeliverAsync(
        string path, string? target, Func<string, bool> approve, CancellationToken cancellation = default)
    {
        await _delivery.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var artifact = await _cache.ImportAsync(path, cancellation).ConfigureAwait(false);
            Remember(artifact);
            ModuleNupkg.TryRead(_cache.PathFor(artifact.Hash), out var entry, out _, out _);
            var frontend = entry.FrontendRole;
            var state = _ownership.State(artifact.Module);
            target = string.IsNullOrWhiteSpace(target) ? state.OwnerNodeId ?? _identity.NodeId : target;
            if (target == "local")
                target = _identity.NodeId;
            if (!frontend && state.Conflict)
                return new PackageDelivery(artifact, target, [], true, false, null, "装载归属存在冲突，请先明确归属。");
            if (target != "preload" && target != _identity.NodeId && target != _peerId)
                return new PackageDelivery(artifact, target, [], true, false, null, "目标节点未登记。");
            if (!frontend && target != "preload" && state.OwnerNodeId != null && state.OwnerNodeId != target)
                return new PackageDelivery(artifact, target, [], true, false, null, "更换运行节点属于迁移，不能通过发布直接修改归属。");
            var preloaded = false;
            if (PreloadStore != null)
            {
                await using var content = File.OpenRead(_cache.PathFor(artifact.Hash));
                await PreloadStore.ImportAsync(content, artifact.Hash, cancellation).ConfigureAwait(false);
                preloaded = true;
            }
            if (target == "preload" && !preloaded)
                return new PackageDelivery(artifact, target, [], true, false, null, "预装载区尚未配置。");

            var pending = new List<string>();
            if (Connected)
            {
                try { await PushPackageAsync(artifact, cancellation).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    pending.Add(_peerId);
                }
            }
            else if (HasPeer)
                pending.Add(_peerId);
            if (target == "preload")
                return new PackageDelivery(artifact, target, pending.ToArray(), true, true, null, null);
            if (target == _peerId && pending.Count > 0)
                return new PackageDelivery(artifact, target, pending.ToArray(), true, preloaded, null, "目标节点交付未完成；未改成本机部署。");
            if (!approve($"确认将 {artifact.Module} {artifact.Version} 装载到节点 {target}？"))
                return new PackageDelivery(artifact, target, pending.ToArray(), true, preloaded, null, "用户未批准目标安装。");
            if (!frontend && state.Heads.Length == 0)
            {
                _ownership.Decide(artifact.Module, target, _identity.NodeId, [], "initial-install");
                state = _ownership.State(artifact.Module);
            }
            if (Connected)
                await SynchronizeAsync(cancellation).ConfigureAwait(false);
            NodeInstallation installation;
            if (target == _identity.NodeId)
            {
                installation = InstallLocal(artifact, frontend ? "" : state.Heads.Single());
            }
            else
            {
                var id = Guid.NewGuid().ToString("N");
                var installCall = new NodeInstallCall(id, artifact, frontend ? "" : state.Heads.Single());
                _installApprovals[id] = installCall;
                try
                {
                    installation = Decode<NodeInstallation>(await RequestAsync("package.install",
                        installCall, cancellation).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    return new PackageDelivery(artifact, target, pending.ToArray(), true, preloaded, null,
                        "目标安装结果未知；可按同一制品重新核对。" + ex.Message);
                }
                finally { _installApprovals.TryRemove(id, out _); }
            }
            if (Connected)
                await SynchronizeAsync(cancellation).ConfigureAwait(false);
            return new PackageDelivery(artifact, target, pending.ToArray(), true, preloaded, installation, installation.Error);
        }
        finally { _delivery.Release(); }
    }

    private NodeInstallation InstallLocal(PackageArtifact artifact, string ownershipHead)
    {
        var state = _ownership.State(artifact.Module);
        var cached = _cache.Inspect(artifact.Hash);
        if (cached != artifact)
            throw new InvalidDataException("交付制品元数据与缓存不一致。");
        ModuleNupkg.TryRead(_cache.PathFor(artifact.Hash), out var entry, out _, out _);
        if (!entry.FrontendRole && (state.Conflict || state.OwnerNodeId != _identity.NodeId || !state.Heads.Contains(ownershipHead)))
            return new NodeInstallation(artifact.Module, artifact.Version, artifact.Hash, _identity.NodeId,
                "", false, false, 0, "目标归属已改变，拒绝安装。");
        // 迁入前对端会先撤下实例，撤下期间禁止装载。归属已经核对为本节点后，这次安装就是落点。
        _suspended.TryRemove(artifact.Module, out _);
        var current = _modules.Modules.FirstOrDefault(item => item.ModuleName == artifact.Module);
        if (current is { Attached: true } && current.PackageHash == artifact.Hash)
            return new NodeInstallation(artifact.Module, artifact.Version, artifact.Hash, _identity.NodeId,
                current.InstanceId, true, true, current.CommandCount, null);
        var result = _modules.InstallPackage(_cache.PathFor(artifact.Hash));
        var module = _modules.Modules.FirstOrDefault(item => item.ModuleName.Equals(artifact.Module, StringComparison.OrdinalIgnoreCase));
        var attached = result.Success && module is { Attached: true } && module.Version == artifact.Version
            && module.PackageHash == artifact.Hash && module.InstanceId.Length > 0;
        return new NodeInstallation(artifact.Module, artifact.Version, artifact.Hash, _identity.NodeId,
            module?.InstanceId ?? "", result.Success, attached, module?.CommandCount ?? 0,
            attached ? null : result.Success ? "安装已返回，但未证实目标实例附着。" : result.Message);
    }

    private async Task PushPackageAsync(PackageArtifact artifact, CancellationToken cancellation)
    {
        if ((await RequestAsync("package.has", artifact, cancellation).ConfigureAwait(false)).GetBoolean()) return;
        var transferId = (await RequestAsync("package.begin", new PackageTransferStart(artifact), cancellation)
            .ConfigureAwait(false)).GetString()!;
        await using var stream = File.OpenRead(_cache.PathFor(artifact.Hash));
        var buffer = new byte[256 * 1024];
        long offset = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            await RequestAsync("package.chunk", new PackageTransferChunk(transferId, offset, buffer[..read]), cancellation)
                .ConfigureAwait(false);
            offset += read;
        }
        var received = Decode<PackageArtifact>(await RequestAsync("package.complete", transferId, cancellation).ConfigureAwait(false));
        if (received != artifact)
            throw new InvalidDataException("对端包接收回执与制品不一致。");
    }

    private async Task<JsonElement> HandlePackageAsync(string operation, JsonElement payload, CancellationToken cancellation)
    {
        switch (operation)
        {
            case "package.verify":
                var delivery = Decode<PackageDelivery>(payload);
                if (delivery.Target != _identity.NodeId) return Encode(false);
                var module = _modules.Modules.FirstOrDefault(item => item.ModuleName == delivery.Artifact.Module);
                var state = _ownership.State(delivery.Artifact.Module);
                return Encode(module is { Attached: true } && module.PackageHash == delivery.Artifact.Hash
                    && module.InstanceId == delivery.Installation?.InstanceId
                    && (_bus.FrontendOwner == delivery.Artifact.Module || (!state.Conflict && state.OwnerNodeId == _identity.NodeId)));
            case "package.has":
                var expected = Decode<PackageArtifact>(payload);
                return Encode(File.Exists(_cache.PathFor(expected.Hash)) && _cache.Inspect(expected.Hash) == expected);
            case "approve.install":
                var requested = Decode<NodeInstallCall>(payload);
                return Encode(_installApprovals.TryGetValue(requested.CallId, out var approvedCall) && approvedCall == requested);
            case "package.begin":
                var artifact = Decode<PackageTransferStart>(payload).Artifact;
                PackageArtifactStore.ValidateHash(artifact.Hash);
                if (artifact.Length is <= 0 or > PackageArtifactStore.MaximumPackageBytes)
                    throw new InvalidDataException("包大小无效或并发传输过多。");
                Directory.CreateDirectory(_cache.Root);
                var id = Guid.NewGuid().ToString("N");
                var temporary = Path.Combine(_cache.Root, ".transfer-" + id);
                lock (_transferGate)
                {
                    foreach (var stale in _incoming.Where(item => Environment.TickCount64 - item.Value.Created > 120000).Select(item => item.Key))
                        if (_incoming.TryRemove(stale, out var abandoned)) RemoveTransfer(abandoned);
                    if (_incoming.Count >= 4) throw new InvalidDataException("并发传输过多。");
                    _incoming[id] = new IncomingPackage(artifact, temporary,
                        new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None), Environment.TickCount64);
                }
                return Encode(id);
            case "package.chunk":
                var chunk = Decode<PackageTransferChunk>(payload);
                if (!_incoming.TryGetValue(chunk.TransferId, out var receiving))
                    throw new InvalidDataException("传输会话不存在。");
                lock (receiving)
                {
                    if (chunk.Offset != receiving.Stream.Position || chunk.Bytes.Length > 256 * 1024
                        || chunk.Offset + chunk.Bytes.Length > receiving.Artifact.Length)
                        throw new InvalidDataException("包分段顺序或长度不正确。");
                    receiving.Stream.Write(chunk.Bytes);
                }
                return Encode(new { });
            case "package.complete":
                if (!_incoming.TryRemove(payload.GetString()!, out var incoming))
                    throw new InvalidDataException("传输会话不存在。");
                try
                {
                    // Removal makes further chunks invalid; the lock waits for an in-flight chunk.
                    lock (incoming)
                    {
                        if (incoming.Stream.Length != incoming.Artifact.Length)
                            throw new InvalidDataException("包未完整接收。");
                        incoming.Stream.Dispose();
                    }
                    await using var completed = File.OpenRead(incoming.Path);
                    var saved = await _cache.ImportAsync(completed, incoming.Artifact.Hash, cancellation).ConfigureAwait(false);
                    if (saved != incoming.Artifact)
                        throw new InvalidDataException("包元数据与制品记录不一致。");
                    Remember(saved);
                    return Encode(saved);
                }
                finally
                {
                    RemoveTransfer(incoming);
                }
            case "package.install":
                {
                    var call = Decode<NodeInstallCall>(payload);
                    var approved = await RequestAsync("approve.install", call, cancellation).ConfigureAwait(false);
                    if (!approved.GetBoolean())
                        throw new InvalidOperationException("跨节点安装未获批准。");
                    using var remoteInstall = new HistoryVulcan.Core.Commands.NodeExecutionScope(_ => false);
                    return Encode(InstallLocal(call.Artifact, call.OwnershipHead));
                }
            default:
                throw new InvalidOperationException("不支持的节点操作。");
        }
    }

    private sealed record IncomingPackage(PackageArtifact Artifact, string Path, FileStream Stream, long Created);

    private static void RemoveTransfer(IncomingPackage transfer)
    {
        lock (transfer)
        {
            transfer.Stream.Dispose();
            File.Delete(transfer.Path);
        }
    }

    private void ClearTransfers()
    {
        lock (_transferGate)
            foreach (var id in _incoming.Keys)
                if (_incoming.TryRemove(id, out var transfer)) RemoveTransfer(transfer);
    }

    private PackageArtifact[] Artifacts() => NodeStateFile.Read(Path.Combine(_stateRoot, "artifacts.json"), () => Array.Empty<PackageArtifact>());

    private void Remember(PackageArtifact artifact) => NodeStateFile.Update(Path.Combine(_stateRoot, "artifacts.json"),
        () => Array.Empty<PackageArtifact>(), artifacts => artifacts.Append(artifact).DistinctBy(item => item.Hash).ToArray());

    internal async Task<string> RefreshDevicesAsync(CancellationToken cancellation)
    {
        var notes = new List<string>();
        if (Connected)
        {
            await SynchronizeAsync(cancellation).ConfigureAwait(false);
            if (_peerHostVersion.Length > 0 && !string.Equals(_peerHostVersion, LocalHostVersion, StringComparison.Ordinal))
                notes.Add($"对端宿主 {_peerHostVersion} 与本机 {LocalHostVersion} 不一致，模块按本机最新包对齐。");
        }
        else if (HasPeer)
            notes.Add("对端未连接。本机模块已按本机目录里的最新包核对。");

        if (!Directory.Exists(_modules.ModulesDirectory))
            return notes.Count == 0 ? "没有模块目录。" : string.Join("\n", notes);

        foreach (var file in Directory.EnumerateFiles(_modules.ModulesDirectory, "*" + ModuleNupkg.Extension))
        {
            if (RuntimeModuleDiscoverySource.IsTransientPackageDirectory(Path.GetFileName(file)))
                continue;
            if (!ModuleNupkg.TryRead(file, out var entry, out _, out _))
                continue;
            var artifact = await _cache.ImportAsync(file, cancellation).ConfigureAwait(false);
            var loaded = _modules.Modules.FirstOrDefault(item =>
                item.ModuleName.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
            var state = _ownership.State(entry.Name);
            if (loaded is { Attached: true }
                && !string.Equals(loaded.PackageHash, artifact.Hash, StringComparison.OrdinalIgnoreCase))
            {
                if (entry.FrontendRole || (state.OwnerNodeId == _identity.NodeId && state.Heads.Length == 1))
                    notes.Add(DescribeInstall(entry.Name, InstallLocal(artifact, entry.FrontendRole ? "" : state.Heads.Single())));
                else
                {
                    var replaced = _modules.InstallPackage(_cache.PathFor(artifact.Hash));
                    notes.Add(replaced.Success
                        ? $"{entry.Name} 已按本机最新包装上 {artifact.Version}。"
                        : $"{entry.Name} 未能按本机最新包装上：{replaced.Message}");
                }
            }

            if (!Connected)
                continue;
            var peerRunning = _peerRunning.TryGetValue(entry.Name, out var running) ? running : "";
            var peerHasPackage = _peerArtifacts.Any(item =>
                item.Module.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Hash, artifact.Hash, StringComparison.OrdinalIgnoreCase));
            if (peerRunning.Length > 0
                && !string.Equals(peerRunning, artifact.Hash, StringComparison.OrdinalIgnoreCase)
                && (entry.FrontendRole || (state.OwnerNodeId == _peerId && state.Heads.Length == 1)))
            {
                notes.Add(await InstallOnPeerAsync(entry.Name, artifact, entry.FrontendRole ? "" : state.Heads[0], cancellation)
                    .ConfigureAwait(false));
            }
            else if (!peerHasPackage)
            {
                try
                {
                    await PushPackageAsync(artifact, cancellation).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    notes.Add($"对端还没有 {entry.Name} 的本机包：{ex.Message}");
                }
            }
        }

        if (Connected)
            await SynchronizeAsync(cancellation).ConfigureAwait(false);
        return notes.Count == 0 ? "设备与模块已与本机最新版本一致。" : string.Join("\n", notes);
    }

    internal async Task<string> PlaceAsync(string module, string target, CancellationToken cancellation)
    {
        if (target == "local")
            target = _identity.NodeId;
        if (target is "none" or "preload")
            target = "preload";
        if (target != "preload" && target != _identity.NodeId && target != _peerId)
            throw new InvalidOperationException("目标节点未登记。");
        if (_bus.FrontendOwner?.Equals(module, StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidOperationException("前端按节点定位，不在装载区改归属。");
        if (Connected)
            await SynchronizeAsync(cancellation).ConfigureAwait(false);
        var state = _ownership.State(module);
        var desired = target == "preload" ? null : target;
        if (state.Conflict || state.OwnerNodeId != desired)
        {
            var mode = state.Conflict ? "resolve" : state.Heads.Length == 0 ? "assign" : "migrate";
            await ChangeOwnershipAsync(module, target, state.Heads, mode, "follow-load", _ => true, cancellation)
                .ConfigureAwait(false);
        }

        if (target == "preload")
            return module + " 已改为不装载。包还在，运行实例已撤下。";
        if (target == _peerId && !Connected)
            return module + " 的装载已由本次执行改到对端。对端起来后同步；若对端还装着，会卸下。";
        var file = Path.Combine(_modules.ModulesDirectory, module + ModuleNupkg.Extension);
        if (!File.Exists(file))
            return module + " 的归属已改到该设备，本机没有可交付的包。";
        var delivered = await DeliverAsync(file, target, _ => true, cancellation).ConfigureAwait(false);
        if (!delivered.Success)
            throw new InvalidOperationException(delivered.Error ?? "装载未完成。");
        return $"{module} {delivered.Artifact.Version} 已装到所选设备。";
    }

    private async Task<string> InstallOnPeerAsync(string module, PackageArtifact artifact, string ownershipHead, CancellationToken cancellation)
    {
        try
        {
            await PushPackageAsync(artifact, cancellation).ConfigureAwait(false);
            var id = Guid.NewGuid().ToString("N");
            var call = new NodeInstallCall(id, artifact, ownershipHead);
            _installApprovals[id] = call;
            try
            {
                var installation = Decode<NodeInstallation>(await RequestAsync("package.install", call, cancellation).ConfigureAwait(false));
                return DescribeInstall("对端 " + module, installation);
            }
            finally
            {
                _installApprovals.TryRemove(id, out _);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return $"对端 {module} 对齐失败：{ex.Message}";
        }
    }

    private static string DescribeInstall(string name, NodeInstallation installation)
        => installation.Error == null
            ? $"{name} 已按本机最新包装上 {installation.Version}。"
            : $"{name} 未能按本机最新包装上：{installation.Error}";

    internal async Task SynchronizePackagesAsync(CancellationToken cancellation)
    {
        foreach (var artifact in Artifacts())
            if (!_peerArtifacts.Any(item => item == artifact))
                await PushPackageAsync(artifact, cancellation).ConfigureAwait(false);
    }
}
