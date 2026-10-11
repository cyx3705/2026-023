using System.Security.Cryptography;
using System.Text.Json;
using HistoryVulcan.Services.Modules;

namespace HistoryVulcan.Services.Nodes;

internal sealed record OwnershipDecision(
    string Id, string Module, string? OwnerNodeId, string ActorNodeId, string[] Parents, string Reason);

internal sealed record OwnershipState(string Module, string? OwnerNodeId, string[] Heads, bool Conflict);

/// <summary>Causal ownership history. Concurrent decisions remain a visible conflict until explicitly resolved.</summary>
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
