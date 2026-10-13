using System.Collections.Concurrent;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Services.Modules;

namespace HistoryVulcan.Services.Nodes;

internal sealed record OwnershipChange(string Id, string Module, string? Target, string[] Heads, string Mode, string DataPolicy);

internal sealed partial class NodeRuntime
{
    private readonly ConcurrentDictionary<string, OwnershipChange> _ownershipApprovals = new();
    private readonly ConcurrentDictionary<string, bool> _suspended = new(StringComparer.OrdinalIgnoreCase);

    internal async Task<OwnershipState> ChangeOwnershipAsync(string module, string? target, string[] heads,
        string mode, string dataPolicy, Func<string, bool> approve, CancellationToken cancellation)
    {
        if (target == "local") target = _identity.NodeId;
        if (target == "preload") target = null;
        if (target != null && target != _identity.NodeId && target != _peerId)
            throw new InvalidOperationException("目标节点未登记。");
        if (mode is not ("assign" or "migrate" or "takeover" or "resolve") || dataPolicy != "follow-load")
            throw new InvalidOperationException("必须指定有效模式及 data=follow-load。模块数据随装载转到执行节点，项目库不动。");
        if (Connected) await SynchronizeAsync(cancellation).ConfigureAwait(false);
        var before = _ownership.State(module);
        if (!before.Heads.SequenceEqual(heads.Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("归属修订已改变，请重新读取节点状态并审核。");
        if (before.Conflict && mode != "resolve") throw new InvalidOperationException("并发归属必须显式 resolve。");
        if (mode == "assign" && before.Heads.Length > 0) throw new InvalidOperationException("已有归属，不能首次分配。");
        if (mode == "takeover" && (Connected || target != _identity.NodeId))
            throw new InvalidOperationException("takeover 只用于对端离线时显式接管到本机。");
        var warning = HasPeer && !Connected ? "对端当前不在线，这次执行在对端起来后同步。" : "";
        if (!approve($"{warning}确认 {module} 改由 {target ?? "不装载"} 执行？模块数据跟着走，项目库不动。"))
            throw new InvalidOperationException("归属变更未获批准。");
        var change = new OwnershipChange(Guid.NewGuid().ToString("N"), module, target, before.Heads, mode, dataPolicy);
        _ownershipApprovals[change.Id] = change;
        try
        {
            StopForOwnership(module);
            if (Connected && mode != "takeover")
                await RequestAsync("ownership.stop", change, cancellation).ConfigureAwait(false);
            _ownership.Decide(module, target, _identity.NodeId, before.Heads, mode);
            _suspended.TryRemove(module, out _);
            if (Connected) await SynchronizeAsync(cancellation).ConfigureAwait(false);
            return _ownership.State(module);
        }
        finally { _ownershipApprovals.TryRemove(change.Id, out _); }
    }

    private void StopForOwnership(string module)
    {
        if (_bus.FrontendOwner?.Equals(module, StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidOperationException("前端按节点定位，不参与普通模块归属迁移。");
        _suspended[module] = true;
        var loaded = _modules.Modules.FirstOrDefault(item => item.ModuleName.Equals(module, StringComparison.OrdinalIgnoreCase));
        if (loaded == null) return;
        if (!_modules.CanRetire(module))
            throw new InvalidOperationException("模块实例不能安全撤下，节点已禁止其总线执行；归属不迁移。需要另行安排宿主维护。");
        var result = _modules.Unload(module);
        if (!result.Success) throw new InvalidOperationException(result.Message);
    }

    private async Task<JsonElement> HandleOwnershipAsync(string operation, JsonElement payload, CancellationToken cancellation)
    {
        var change = Decode<OwnershipChange>(payload);
        if (operation == "ownership.approve")
            return Encode(_ownershipApprovals.TryGetValue(change.Id, out var approved)
                && JsonSerializer.Serialize(approved) == JsonSerializer.Serialize(change));
        if (operation != "ownership.stop") throw new InvalidOperationException("未知归属操作。");
        if (!(await RequestAsync("ownership.approve", change, cancellation).ConfigureAwait(false)).GetBoolean())
            throw new InvalidOperationException("归属操作未获发起端批准。");
        if (!_ownership.State(change.Module).Heads.SequenceEqual(change.Heads))
            throw new InvalidOperationException("归属修订已改变。");
        StopForOwnership(change.Module);
        return Encode(new { stopped = true });
    }

    internal async Task<CommandResult> ExecuteFrontendAsync(string node, string text, bool quiet, CancellationToken cancellation)
    {
        if (node == "local" || node == _identity.NodeId)
        {
            var local = CommandRequest.Create(text, _bus.Registry);
            if (_bus.FrontendOwner == null || _bus.Registry.GetSource(local.Name) != "module:" + _bus.FrontendOwner)
                return CommandResult.Fail("目标不是本节点前端命令。");
            return quiet ? await _bus.InvokeAsync(text, "node:frontend", cancellation).ConfigureAwait(false)
                : await _bus.ExecuteAsync(text, "node:frontend", cancellation).ConfigureAwait(false);
        }
        if (node != _peerId || !Connected) return CommandResult.Fail("目标前端节点未连接。");
        var name = CommandParser.Parse(text).Name;
        var command = _frontends.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (command == null) return CommandResult.Fail("目标节点未登记该前端命令。");
        var id = Guid.NewGuid().ToString("N");
        var origin = NodeExecutionScope.Current;
        _confirmations[id] = prompt => origin != null ? origin.Confirm(prompt) : _bus.RequestConfirmation(prompt);
        try
        {
            var result = Decode<NodeExecutionResult>(await RequestAsync("execute", new NodeCommandCall(id, text,
                command.Module, command.InstanceId, command.PackageHash, null, quiet, true), cancellation).ConfigureAwait(false));
            return new CommandResult { Success = result.Success, Message = result.Message, Data = result.Data };
        }
        finally { _confirmations.TryRemove(id, out _); }
    }
}
