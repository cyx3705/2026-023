using System.Security.Cryptography;
using System.Text.Json;
using HistoryVulcan.Services.Modules;

namespace HistoryVulcan.Services.Nodes;

internal sealed record OwnershipDecision(
    string Id, string Module, string? OwnerNodeId, string ActorNodeId, string[] Parents, string Reason);

internal sealed record OwnershipState(string Module, string? OwnerNodeId, string[] Heads, bool Conflict);

/// <summary>Causal ownership history. A single executing party wins on reconnect; two different loaders stay unresolved.</summary>
internal sealed class ModuleOwnershipLedger(string stateRoot)
{
    private string PathForState => Path.Combine(stateRoot, "ownership.json");

    internal OwnershipDecision[] Read()
    {
        var history = NodeStateFile.Read(PathForState, () => Array.Empty<OwnershipDecision>());
        Validate(history);
        return history;
    }

    internal OwnershipState State(string module) => State(Read(), module);

    internal OwnershipDecision Decide(string module, string? owner, string actor, string[] expectedHeads, string reason)
    {
        OwnershipDecision? created = null;
        NodeStateFile.Update(PathForState, () => Array.Empty<OwnershipDecision>(), history =>
        {
            Validate(history);
            var current = State(history, module);
            if (!current.Heads.Order(StringComparer.Ordinal).SequenceEqual(expectedHeads.Order(StringComparer.Ordinal)))
                throw new InvalidOperationException("装载归属已改变，请重新核对当前修订。");
            var decision = new OwnershipDecision("", module, owner, actor,
                current.Heads.Order(StringComparer.Ordinal).ToArray(), reason);
            created = decision with { Id = Identity(decision) };
            var updated = history.Append(created).ToArray();
            Validate(updated);
            return updated;
        });
        return created!;
    }

    internal void Merge(IEnumerable<OwnershipDecision> incoming)
    {
        var received = incoming.ToArray();
        NodeStateFile.Update(PathForState, () => Array.Empty<OwnershipDecision>(), history =>
        {
            var combined = history.Concat(received).ToArray();
            Validate(combined);
            return combined.DistinctBy(item => item.Id, StringComparer.Ordinal).ToArray();
        });
    }

    /// <summary>
/// Collapses a conflict when exactly one execution node was named.
/// Two different loaders are left untouched: neither computer is preferred.
/// </summary>
internal bool TryReconcileExecution(string module)
{
    var history = Read();
    var state = State(history, module);
    if (!state.Conflict || !TryChooseExecution(Heads(history, state), out var owner, out var actor))
        return false;
    Decide(module, owner, actor, state.Heads, "executed");
    return true;
}

internal static bool TryChooseExecution(IReadOnlyList<OwnershipDecision> heads, out string? owner, out string actor)
{
    owner = null;
    actor = "";
    var executing = heads.Select(item => item.OwnerNodeId).Where(item => item != null)
        .Distinct(StringComparer.Ordinal).ToArray();
    if (executing.Length > 1)
        return false;
    owner = executing.Length == 1 ? executing[0] : null;
    actor = owner ?? heads.Select(item => item.ActorNodeId).Order(StringComparer.Ordinal).First();
    return heads.Count > 1;
}

private static OwnershipDecision[] Heads(IEnumerable<OwnershipDecision> history, OwnershipState state)
{
    var indexed = history.Where(item => item.Module.Equals(state.Module, StringComparison.OrdinalIgnoreCase))
        .ToDictionary(item => item.Id, StringComparer.Ordinal);
    return state.Heads.Select(id => indexed[id]).ToArray();
}

internal static OwnershipState State(IEnumerable<OwnershipDecision> history, string module)
    {
        var decisions = history.Where(item => item.Module.Equals(module, StringComparison.OrdinalIgnoreCase)).ToArray();
        var parents = decisions.SelectMany(item => item.Parents).ToHashSet(StringComparer.Ordinal);
        var heads = decisions.Where(item => !parents.Contains(item.Id)).DistinctBy(item => item.Id).ToArray();
        return new OwnershipState(module, heads.Length == 1 ? heads[0].OwnerNodeId : null,
            heads.Select(item => item.Id).Order(StringComparer.Ordinal).ToArray(), heads.Length > 1);
    }

    private static string Identity(OwnershipDecision decision)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            decision with { Id = "" }, NodeStateFile.Json)));

    private static void Validate(OwnershipDecision[] decisions)
    {
        if (decisions.Length > 10000 || decisions.Any(item => item == null || item.Id == null || item.Module == null
            || item.Parents == null || item.Reason == null))
            throw new InvalidDataException("归属历史无效或超过容量。");
        var indexed = decisions.GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.First());
        foreach (var item in decisions)
        {
            if (!RuntimeModulePackageStore.IsSafeModuleName(item.Module) || item.Reason.Length is 0 or > 200)
                throw new InvalidDataException("归属决定的模块或原因无效。");
            NodeIdentity.ValidateId(item.ActorNodeId);
            if (item.OwnerNodeId != null)
                NodeIdentity.ValidateId(item.OwnerNodeId);
            if (!item.Id.Equals(Identity(item), StringComparison.Ordinal))
                throw new InvalidDataException("归属决定的内容与身份不一致。");
            if (item.Parents.Distinct(StringComparer.Ordinal).Count() != item.Parents.Length)
                throw new InvalidDataException("归属决定重复引用前序修订。");
            foreach (var parent in item.Parents)
            {
                if (!indexed.TryGetValue(parent, out var previous)
                    || !previous.Module.Equals(item.Module, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("归属决定缺少同模块的前序修订。");
            }
        }
        var remaining = indexed.Values.ToDictionary(item => item.Id, item => item.Parents.ToHashSet());
        while (remaining.Count > 0)
        {
            var roots = remaining.Where(item => item.Value.Count == 0).Select(item => item.Key).ToHashSet();
            if (roots.Count == 0)
                throw new InvalidDataException("归属历史存在循环。");
            foreach (var root in roots) remaining.Remove(root);
            foreach (var parents in remaining.Values) parents.ExceptWith(roots);
        }
    }
}
