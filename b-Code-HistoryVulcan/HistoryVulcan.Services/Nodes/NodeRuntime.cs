using System.Collections.Concurrent;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Services.Modules;

namespace HistoryVulcan.Services.Nodes;

/// <summary>Two-node catalog and command routing. Only the local bus can execute a module handler.</summary>
internal sealed partial class NodeRuntime : IAsyncDisposable
{
    private readonly CommandBus _bus;
    private readonly ModuleHost _modules;
    private readonly NodeIdentity _identity;
    private readonly string _peerId;
    private readonly ModuleOwnershipLedger _ownership;
    private readonly PackageArtifactStore _cache;
    private readonly ConcurrentDictionary<string, NodeCommandInfo> _remote = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Func<string, bool>> _confirmations = new();
    private NodeConnection? _connection;
    private readonly object _catalogGate = new();
    private readonly string _stateRoot;
    private volatile bool _ready;
    private NodeCommandInfo[] _frontends = [];
    private PackageArtifact[] _peerArtifacts = [];
    private Dictionary<string, string> _peerRunning = new(StringComparer.OrdinalIgnoreCase);
    private string _peerHostVersion = "";
    private long _session;

    internal static string LocalHostVersion { get; } =
        typeof(NodeRuntime).Assembly.GetName().Version is { } version ? version.ToString(3) : "";

    internal NodeRuntime(CommandBus bus, ModuleHost modules, NodeIdentity identity, string peerId, string stateRoot, string cacheRoot)
    {
        NodeIdentity.ValidateId(identity.NodeId);
        NodeIdentity.ValidateId(identity.LogicalHostId);
        NodeIdentity.ValidateId(peerId);
        _bus = bus;
        _modules = modules;
        _identity = identity;
        _peerId = peerId;
        _stateRoot = stateRoot;
        _ownership = new ModuleOwnershipLedger(stateRoot);
        _cache = new PackageArtifactStore(cacheRoot);
        modules.CanLoad = CanLoad;
        modules.RequireDeclaredFrontendRole = true;
        bus.NodeRoute = name => _remote.ContainsKey(name);
        bus.NodeExecutor = ExecuteRemoteAsync;
        bus.Events.Published += Published;
        _eventPump = Task.Run(SendEventsAsync);
        bus.NodeLocalAllowed = name =>
        {
            var source = bus.Registry.GetSource(name);
            if (!source.StartsWith("module:", StringComparison.Ordinal)) return true;
            var module = source[7..];
            if (bus.FrontendOwner?.Equals(module, StringComparison.OrdinalIgnoreCase) == true) return true;
            var state = _ownership.State(module);
            return !_suspended.ContainsKey(module) && !state.Conflict && state.OwnerNodeId == identity.NodeId;
        };
    }

    internal NodeIdentity Identity => _identity;
    internal ModuleOwnershipLedger Ownership => _ownership;
    internal PackageArtifactStore Cache => _cache;
    internal bool Connected => _ready && _connection?.Connected == true;
    internal bool HasPeer => _peerId != _identity.NodeId;
    internal NodeCommandInfo[] RemoteCommands => _remote.Values.ToArray();
    internal PackageArtifact[] KnownArtifacts => Artifacts();

    internal IReadOnlyList<NodeDevice> Devices()
    {
        var devices = new List<NodeDevice> { new(_identity.NodeId, "本机", LocalHostVersion, true, true) };
        if (HasPeer)
            devices.Add(new NodeDevice(_peerId, "对端", _peerHostVersion, Connected, false));
        return devices;
    }
    internal string[] PendingPackages => HasPeer ? Artifacts().Where(item => !_peerArtifacts.Contains(item)).Select(item => item.Hash).ToArray() : [];

    internal NodeConnection Attach(Stream authenticatedStream)
    {
        if (_connection?.Connected == true)
            throw new InvalidOperationException("节点已有连接。");
        _ready = false;
        var session = Interlocked.Increment(ref _session);
        while (_events.Reader.TryRead(out _)) { }
        _receivedSequence = 0;
        ClearTransfers();
        var connection = new NodeConnection(authenticatedStream, (operation, payload, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (session != _session) throw new IOException("节点会话已替换。");
            return HandleAsync(operation, payload, cancellation);
        });
        _connection = connection;
        return connection;
    }

    internal async Task SynchronizeAsync(CancellationToken cancellation = default)
    {
        var snapshot = Decode<NodeSnapshot>(await RequestAsync("snapshot", Snapshot(), cancellation).ConfigureAwait(false));
        ApplySnapshot(snapshot);
        _ready = true;
    }

    private bool CanLoad(ModuleDiscoveryEntry entry)
    {
        if (entry.FrontendRole)
            return true;
        var state = _ownership.State(entry.Name);
        return !_suspended.ContainsKey(entry.Name) && !state.Conflict && state.OwnerNodeId == _identity.NodeId;
    }

    private NodeSnapshot Snapshot()
    {
        var commands = new List<NodeCommandInfo>();
        foreach (var descriptor in _bus.Registry.All())
        {
            var source = _bus.Registry.GetSource(descriptor.Name);
            if (!source.StartsWith("module:", StringComparison.Ordinal) || descriptor.HiddenReason != null)
                continue;
            var moduleName = source["module:".Length..];
            var module = _modules.Modules.FirstOrDefault(item => item.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase));
            if (module is not { Attached: true })
                continue;
            var frontend = _bus.FrontendOwner?.Equals(moduleName, StringComparison.OrdinalIgnoreCase) == true;
            var state = _ownership.State(moduleName);
            if (!frontend && (state.Conflict || state.OwnerNodeId != _identity.NodeId))
                continue;
            commands.Add(new NodeCommandInfo(descriptor.Name, _bus.Registry.GetDomain(descriptor.Name),
                _bus.Registry.GetCommandClass(descriptor.Name), descriptor.Summary, descriptor.Example,
                descriptor.Parameters.ToArray(), descriptor.Readonly, descriptor.Level == CommandLevel.Ask,
                descriptor.RequiresUiThread, descriptor.AllowUnspecifiedParameters,
                new Dictionary<string, string>(descriptor.Annotations), module.ModuleName, module.InstanceId,
                module.PackageHash, frontend));
        }
        return new NodeSnapshot(_identity.NodeId, _identity.LogicalHostId, _ownership.Read(), commands.ToArray(), Artifacts())
        {
            HostVersion = LocalHostVersion,
        };
    }

    private void ApplySnapshot(NodeSnapshot snapshot)
    {
        lock (_catalogGate) ApplySnapshotCore(snapshot);
    }

    private void ApplySnapshotCore(NodeSnapshot snapshot)
    {
        if (snapshot.NodeId != _peerId || snapshot.LogicalHostId != _identity.LogicalHostId)
            throw new InvalidDataException("对端身份或逻辑宿主不匹配。");
        if (snapshot.Ownership.Any(item => (item.ActorNodeId != _peerId && item.ActorNodeId != _identity.NodeId)
            || (item.OwnerNodeId != null && item.OwnerNodeId != _peerId && item.OwnerNodeId != _identity.NodeId)))
            throw new InvalidDataException("归属记录包含未登记的节点。");
        _ownership.Merge(snapshot.Ownership);
        _frontends = snapshot.Commands.Where(item => item.Frontend).ToArray();
        _peerArtifacts = snapshot.Artifacts;
        _peerHostVersion = snapshot.HostVersion ?? "";
        _peerRunning = snapshot.Commands
            .GroupBy(item => item.Module, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().PackageHash, StringComparer.OrdinalIgnoreCase);
        foreach (var module in _modules.Modules)
        {
            if (_bus.FrontendOwner?.Equals(module.ModuleName, StringComparison.OrdinalIgnoreCase) == true)
                continue;
            var state = _ownership.State(module.ModuleName);
            if (state.Conflict || state.OwnerNodeId != _identity.NodeId)
            {
                StopForOwnership(module.ModuleName);
            }
        }

        var valid = snapshot.Commands.Where(item => !item.Frontend &&
            _ownership.State(item.Module) is { Conflict: false } state && state.OwnerNodeId == _peerId)
            .ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var name in _remote.Keys)
        {
            if (!valid.ContainsKey(name))
            {
                if (_bus.Registry.GetSource(name) == "node:" + _peerId)
                    _bus.Registry.Unregister(name);
                _remote.TryRemove(name, out _);
            }
        }
        foreach (var (name, info) in valid)
        {
            if (_remote.TryGetValue(name, out var previous) && previous.InstanceId == info.InstanceId
                && previous.PackageHash == info.PackageHash) continue;
            if (_bus.Registry.TryGet(name, out _) && _bus.Registry.GetSource(name) != "node:" + _peerId)
                throw new InvalidOperationException("节点命令与本机目录冲突：" + name);
            if (_bus.Registry.GetSource(name) == "node:" + _peerId)
                _bus.Registry.Unregister(name);
            _remote[name] = info;
            _bus.Registry.Register(new CommandDescriptor
            {
                Name = name,
                Domain = info.Domain,
                CommandClass = info.CommandClass,
                Summary = info.Summary,
                Example = info.Example,
                Parameters = info.Parameters,
                Readonly = info.Readonly,
                Level = info.RequiresConfirmation ? CommandLevel.Ask : CommandLevel.Run,
                RequiresUiThread = info.RequiresUiThread,
                AllowUnspecifiedParameters = info.AllowUnspecifiedParameters,
                Annotations = new Dictionary<string, string>(info.Annotations) { ["executionNode"] = _peerId },
                Handler = CommandDescriptor.Sync(_ => CommandResult.Fail("远端命令必须经过节点路由。")),
            }, "node:" + _peerId);
        }
    }

    private async Task<CommandResult> ExecuteRemoteAsync(string text, string source, bool quiet, CancellationToken cancellation)
    {
        var request = CommandRequest.Create(text, _bus.Registry);
        if (request.SyntaxError != null)
            return CommandResult.Fail(request.SyntaxError);
        if (!_remote.TryGetValue(request.Name, out var command))
            return CommandResult.Fail("远端目录已改变。");
        if (!Connected)
            return CommandResult.Fail("模块所属节点离线；未在本机装载替代实例。");
        var ownership = _ownership.State(command.Module);
        if (ownership.Conflict || ownership.OwnerNodeId != _peerId)
            return CommandResult.Fail("模块归属已改变或存在冲突，请刷新目录。");
        var id = Guid.NewGuid().ToString("N");
        var origin = NodeExecutionScope.Current;
        _confirmations[id] = prompt => origin != null ? origin.Confirm(prompt) : _bus.RequestConfirmation(prompt);
        try
        {
            var result = Decode<NodeExecutionResult>(await RequestAsync("execute", new NodeCommandCall(
                id, text, command.Module, command.InstanceId, command.PackageHash,
                ownership.Heads.Single(), quiet), cancellation).ConfigureAwait(false));
            return new CommandResult { Success = result.Success, Message = result.Message, Data = result.Data };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return new CommandResult
            {
                Success = false,
                Message = ex.Message,
                Data = new { nodeId = _peerId, outcome = "unknown", replayed = false }
            };
        }
        finally
        {
            _confirmations.TryRemove(id, out _);
        }
    }

    private async Task<JsonElement> HandleAsync(string operation, JsonElement payload, CancellationToken cancellation)
    {
        if (operation != "snapshot" && !_ready)
            throw new InvalidOperationException("逻辑宿主握手尚未完成。");
        switch (operation)
        {
            case "event":
                ReceiveEvent(Decode<NodeEvent>(payload));
                return Encode(new { });
            case "snapshot":
                ApplySnapshot(Decode<NodeSnapshot>(payload));
                _ready = true;
                return Encode(Snapshot());
            case "execute":
                return Encode(await ExecuteLocalAsync(Decode<NodeCommandCall>(payload), cancellation).ConfigureAwait(false));
            case "confirm":
                var callback = Decode<NodeCallback>(payload);
                return Encode(_confirmations.TryGetValue(callback.CallId, out var confirm) && confirm(callback.Message));
            case "progress":
                var progress = Decode<NodeCallback>(payload);
                if (_confirmations.ContainsKey(progress.CallId))
                    _bus.Log.Info("cmd:progress:node", progress.Message);
                return Encode(new { });
            default:
                if (operation.StartsWith("ownership.", StringComparison.Ordinal))
                    return await HandleOwnershipAsync(operation, payload, cancellation).ConfigureAwait(false);
                return await HandlePackageAsync(operation, payload, cancellation).ConfigureAwait(false);
        }
    }

    private async Task<NodeExecutionResult> ExecuteLocalAsync(NodeCommandCall call, CancellationToken cancellation)
    {
        var request = CommandRequest.Create(call.Text, _bus.Registry);
        var state = _ownership.State(call.Module);
        var module = _modules.Modules.FirstOrDefault(item => item.ModuleName == call.Module);
        if (request.SyntaxError != null || request.Descriptor is not { HiddenReason: null }
            || _bus.Registry.GetSource(request.Name) != "module:" + call.Module
            || module is not { Attached: true } || module.InstanceId != call.InstanceId || module.PackageHash != call.PackageHash
            || (call.Frontend
                ? _bus.FrontendOwner != call.Module
                : state.Conflict || state.OwnerNodeId != _identity.NodeId || !state.Heads.Contains(call.OwnershipHead)))
            return new NodeExecutionResult(false, "目标归属、实例或命令目录已改变，未执行。", null);
        using var scope = new NodeExecutionScope(prompt =>
        {
            try { return RequestAsync("confirm", new NodeCallback(call.CallId, request.Mask(prompt)), cancellation).GetAwaiter().GetResult().GetBoolean(); }
            catch (Exception) { return false; }
        }, line =>
        {
            try { RequestAsync("progress", new NodeCallback(call.CallId, line), cancellation).GetAwaiter().GetResult(); }
            catch (Exception) { /* A disconnected progress channel cannot replay the command. */ }
        });
        var result = call.Quiet
            ? await _bus.InvokeAsync(call.Text, "node:" + _peerId, cancellation).ConfigureAwait(false)
            : await _bus.ExecuteAsync(call.Text, "node:" + _peerId, cancellation).ConfigureAwait(false);
        result = request.RedactResult(result);
        try
        {
            return new NodeExecutionResult(result.Success, result.Message, result.Data == null ? null : Encode(result.Data));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return new NodeExecutionResult(false, "命令已执行，但结果包含无法跨节点传输的对象。", Encode(new { outcome = "executed-result-unavailable" }));
        }
    }

    private Task<JsonElement> RequestAsync(string operation, object payload, CancellationToken cancellation)
        => _connection?.RequestAsync(operation, payload, TimeSpan.FromSeconds(60), cancellation)
            ?? throw new IOException("节点未连接。");

    private static T Decode<T>(JsonElement value) => value.Deserialize<T>(NodeStateFile.Json)
        ?? throw new InvalidDataException("节点载荷为空。");
    private static JsonElement Encode(object value) => JsonSerializer.SerializeToElement(value, NodeStateFile.Json);

    public async ValueTask DisposeAsync()
    {
        _bus.Events.Published -= Published;
        _eventStop.Cancel();
        if (_eventPump != null) await _eventPump.ConfigureAwait(false);
        if (_connection != null)
            await _connection.DisposeAsync().ConfigureAwait(false);
        _bus.NodeRoute = null;
        _bus.NodeExecutor = null;
        _bus.NodeLocalAllowed = null;
        ClearTransfers();
    }
}
