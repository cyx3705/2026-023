using System.Text.Json;
using HistoryVulcan.Services.Modules;

namespace HistoryVulcan.Services.Nodes;

internal sealed record ModuleDataFile(string Path, byte[] Bytes);

internal sealed record ModuleDataShipment(string Module, string Head, bool Present, ModuleDataFile[] Files);

internal sealed record ModuleDataRequest(string Module, string Head);

internal sealed partial class NodeRuntime
{
    private const long MaximumModuleDataBytes = 32L * 1024 * 1024;

    private string MovedPath => Path.Combine(_stateRoot, "module-data-moved.json");

    private async Task CatchUpPlacementAsync(CancellationToken cancellation)
    {
        if (!Connected)
            return;
        foreach (var module in _ownership.Read().Select(item => item.Module).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
        {
            var state = _ownership.State(module);
            if (state.Conflict || state.Heads.Length != 1 || state.OwnerNodeId == null)
                continue;
            await MoveModuleDataAsync(module, state, cancellation).ConfigureAwait(false);
            if (state.OwnerNodeId == _identity.NodeId)
                await EnsureLocalInstalledAsync(module, state.Heads[0], cancellation).ConfigureAwait(false);
            else if (state.OwnerNodeId == _peerId && !_peerRunning.ContainsKey(module))
                await EnsurePeerInstalledAsync(module, state.Heads[0], cancellation).ConfigureAwait(false);
        }
    }

    private async Task MoveModuleDataAsync(string module, OwnershipState state, CancellationToken cancellation)
    {
        var head = _ownership.Read().Single(item => item.Id == state.Heads[0]);
        var source = DataSource(head);
        var key = module + "\n" + head.Id;
        if (source == null || Moved(key))
            return;
        if (source == _identity.NodeId && state.OwnerNodeId == _peerId)
        {
            var shipment = ReadShipment(module, head.Id);
            var accepted = (await RequestAsync("module.data.accept", shipment, cancellation).ConfigureAwait(false)).GetBoolean();
            if (!accepted)
                return;
            MarkMoved(key);
            if (shipment.Present)
                DeleteModuleData(module);
        }
        else if (source == _peerId && state.OwnerNodeId == _identity.NodeId)
        {
            var shipment = Decode<ModuleDataShipment>(await RequestAsync("module.data.offer",
                new ModuleDataRequest(module, head.Id), cancellation).ConfigureAwait(false));
            if (shipment.Module != module || shipment.Head != head.Id)
                throw new InvalidDataException("模块数据回执与装载修订不一致。");
            if (shipment.Present)
                ReplaceModuleData(shipment);
            MarkMoved(key);
            await RequestAsync("module.data.taken", new ModuleDataRequest(module, head.Id), cancellation).ConfigureAwait(false);
        }
    }

    private async Task EnsureLocalInstalledAsync(string module, string head, CancellationToken cancellation)
    {
        if (_bus.FrontendOwner?.Equals(module, StringComparison.OrdinalIgnoreCase) == true)
            return;
        if (_modules.Modules.Any(item => item.ModuleName.Equals(module, StringComparison.OrdinalIgnoreCase) && item.Attached))
            return;
        var artifact = await ArtifactForAsync(module, cancellation).ConfigureAwait(false);
        if (artifact != null)
            InstallLocal(artifact, head);
    }

    private async Task EnsurePeerInstalledAsync(string module, string head, CancellationToken cancellation)
    {
        var artifact = await ArtifactForAsync(module, cancellation).ConfigureAwait(false);
        if (artifact == null)
            return;
        var noted = await InstallOnPeerAsync(module, artifact, head, cancellation).ConfigureAwait(false);
        if (noted.Contains("未能", StringComparison.Ordinal) || noted.Contains("失败", StringComparison.Ordinal))
            throw new IOException(noted);
    }

    private async Task<PackageArtifact?> ArtifactForAsync(string module, CancellationToken cancellation)
    {
        var file = Path.Combine(_modules.ModulesDirectory, module + ModuleNupkg.Extension);
        if (File.Exists(file))
            return await _cache.ImportAsync(file, cancellation).ConfigureAwait(false);
        return Artifacts().FirstOrDefault(item => item.Module.Equals(module, StringComparison.OrdinalIgnoreCase)
            && File.Exists(_cache.PathFor(item.Hash)));
    }

    private Task<JsonElement> HandleModuleDataAsync(string operation, JsonElement payload, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        switch (operation)
        {
            case "module.data.accept":
                AcceptShipment(Decode<ModuleDataShipment>(payload));
                return Task.FromResult(Encode(true));
            case "module.data.offer":
                return Task.FromResult(Encode(OfferShipment(Decode<ModuleDataRequest>(payload))));
            case "module.data.taken":
                TakeShipment(Decode<ModuleDataRequest>(payload));
                return Task.FromResult(Encode(true));
            default:
                throw new InvalidOperationException("未知模块数据操作。");
        }
    }

    private void AcceptShipment(ModuleDataShipment shipment)
    {
        var state = _ownership.State(shipment.Module);
        var head = RequireHead(shipment.Module, shipment.Head, state);
        if (state.OwnerNodeId != _identity.NodeId || DataSource(head) != _peerId)
            throw new InvalidOperationException("只有执行节点接收上一个装载机的模块数据。");
        if (Moved(shipment.Module + "\n" + shipment.Head))
            return;
        if (shipment.Present)
            ReplaceModuleData(shipment);
        MarkMoved(shipment.Module + "\n" + shipment.Head);
    }

    private ModuleDataShipment OfferShipment(ModuleDataRequest request)
    {
        var state = _ownership.State(request.Module);
        var head = RequireHead(request.Module, request.Head, state);
        if (state.OwnerNodeId != _peerId || DataSource(head) != _identity.NodeId)
            throw new InvalidOperationException("模块数据只从原来的执行节点交出。");
        if (Moved(request.Module + "\n" + request.Head))
            return new ModuleDataShipment(request.Module, request.Head, false, []);
        return ReadShipment(request.Module, request.Head);
    }

    private void TakeShipment(ModuleDataRequest request)
    {
        var state = _ownership.State(request.Module);
        var head = RequireHead(request.Module, request.Head, state);
        if (state.OwnerNodeId != _peerId || DataSource(head) != _identity.NodeId)
            throw new InvalidOperationException("模块数据只从原来的执行节点交出。");
        MarkMoved(request.Module + "\n" + request.Head);
        DeleteModuleData(request.Module);
    }

    private OwnershipDecision RequireHead(string module, string head, OwnershipState state)
    {
        if (state.Conflict || state.Heads.Length != 1 || !string.Equals(state.Heads[0], head, StringComparison.Ordinal))
            throw new InvalidOperationException("模块装载修订已改变，未转移数据。");
        return _ownership.Read().Single(item => item.Id == head && item.Module.Equals(module, StringComparison.OrdinalIgnoreCase));
    }

    private string? DataSource(OwnershipDecision head)
    {
        var history = _ownership.Read().Where(item => item.Module.Equals(head.Module, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(head.Parents);
        string? found = null;
        while (pending.Count > 0)
        {
            var id = pending.Dequeue();
            if (!seen.Add(id) || !history.TryGetValue(id, out var decision))
                continue;
            if (decision.OwnerNodeId != null && !string.Equals(decision.OwnerNodeId, head.OwnerNodeId, StringComparison.Ordinal))
            {
                if (found != null && !string.Equals(found, decision.OwnerNodeId, StringComparison.Ordinal))
                    return null;
                found = decision.OwnerNodeId;
            }
            foreach (var parent in decision.Parents)
                pending.Enqueue(parent);
        }
        return found;
    }

    private void ReplaceModuleData(ModuleDataShipment shipment)
    {
        var root = ModuleDataDirectoryChecked(shipment.Module);
        var attached = _modules.Modules.Any(item =>
            item.ModuleName.Equals(shipment.Module, StringComparison.OrdinalIgnoreCase) && item.Attached);
        if (attached)
        {
            if (!_modules.CanRetire(shipment.Module))
                throw new InvalidOperationException("模块实例不能安全卸下，模块数据还留在原执行节点。");
            var unloaded = _modules.Unload(shipment.Module);
            if (!unloaded.Success)
                throw new InvalidOperationException(unloaded.Message);
        }
        WriteDirectory(root, shipment.Files);
        if (!attached)
            return;
        var file = Path.Combine(_modules.ModulesDirectory, shipment.Module + ModuleNupkg.Extension);
        if (!File.Exists(file))
            return;
        _suspended.TryRemove(shipment.Module, out _);
        var installed = _modules.InstallPackage(file);
        if (!installed.Success)
            throw new InvalidOperationException(installed.Message);
    }

    private ModuleDataShipment ReadShipment(string module, string head)
    {
        var root = ModuleDataDirectoryChecked(module);
        if (!Directory.Exists(root))
            return new ModuleDataShipment(module, head, false, []);
        var files = new List<ModuleDataFile>();
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = new DirectoryInfo(pending.Pop());
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;
            foreach (var child in directory.EnumerateDirectories())
            {
                if (!child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    pending.Push(child.FullName);
            }
            foreach (var file in directory.EnumerateFiles())
            {
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                total += file.Length;
                if (total > MaximumModuleDataBytes || files.Count >= 4096)
                    throw new InvalidDataException("模块数据超过可随装载转移的上限。");
                var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                SafeRelative(relative);
                files.Add(new ModuleDataFile(relative, File.ReadAllBytes(file.FullName)));
            }
        }
        return new ModuleDataShipment(module, head, files.Count > 0, files.ToArray());
    }

    private void DeleteModuleData(string module)
    {
        var root = ModuleDataDirectoryChecked(module);
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private string ModuleDataDirectoryChecked(string module)
    {
        if (!RuntimeModulePackageStore.IsSafeModuleName(module) || string.IsNullOrWhiteSpace(_modules.ModuleDataRoot))
            throw new InvalidDataException("模块数据目录无效。");
        var root = Path.GetFullPath(_modules.ModuleDataDirectory(module));
        var parent = Path.GetFullPath(_modules.ModuleDataRoot!);
        var prefix = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(root), module, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模块数据目录不在 ModuleData 下。");
        return root;
    }

    private static void WriteDirectory(string root, ModuleDataFile[] files)
    {
        var fullRoot = Path.GetFullPath(root);
        var staging = fullRoot + ".incoming-" + Guid.NewGuid().ToString("N");
        var backup = fullRoot + ".previous-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        var movedAside = false;
        try
        {
            foreach (var file in files)
            {
                var relative = SafeRelative(file.Path);
                var target = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
                var prefix = staging.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("模块数据路径无效。");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, file.Bytes);
            }
            if (Directory.Exists(fullRoot))
            {
                Directory.Move(fullRoot, backup);
                movedAside = true;
            }
            Directory.Move(staging, fullRoot);
            if (movedAside)
                Directory.Delete(backup, recursive: true);
        }
        catch
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            if (movedAside && !Directory.Exists(fullRoot) && Directory.Exists(backup))
                Directory.Move(backup, fullRoot);
            throw;
        }
    }

    private static string SafeRelative(string relative)
    {
        var normalized = relative.Replace('\\', '/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Contains(':'))
            throw new InvalidDataException("模块数据路径无效。");
        foreach (var part in normalized.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("模块数据路径无效。");
        }
        return normalized;
    }

    private bool Moved(string key)
        => NodeStateFile.Read(MovedPath, () => Array.Empty<string>()).Contains(key, StringComparer.Ordinal);

    private void MarkMoved(string key)
        => NodeStateFile.Update(MovedPath, () => Array.Empty<string>(),
            items => items.Contains(key, StringComparer.Ordinal) ? items : items.Append(key).ToArray());
}
