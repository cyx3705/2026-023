using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.Services.Nodes;

internal sealed partial class NodeRuntime
{
    internal async Task<CommandResult> VerifyDeliveryAsync(PackageDelivery delivery, CancellationToken cancellation)
    {
        if (!delivery.Success) return CommandResult.Fail("原交付未完成。");
        if (delivery.Target == "preload")
            return PreloadStore?.Inspect(delivery.Artifact.Hash) == delivery.Artifact
                ? CommandResult.Ok("预装载制品哈希已核对；不含运行效果审核。") : CommandResult.Fail("预装载制品不可获取。");
        if (Connected) await SynchronizeAsync(cancellation).ConfigureAwait(false);
        var state = _ownership.State(delivery.Artifact.Module);
        Modules.ModuleNupkg.TryRead(_cache.PathFor(delivery.Artifact.Hash), out var entry, out _, out _);
        if (!entry.FrontendRole && (state.Conflict || state.OwnerNodeId != delivery.Target))
            return CommandResult.Fail("归属已改变，请重新 submit 审核。");
        if (delivery.Target == _identity.NodeId)
        {
            var module = _modules.Modules.FirstOrDefault(item => item.ModuleName == delivery.Artifact.Module);
            return module is { Attached: true } && module.PackageHash == delivery.Artifact.Hash
                && module.InstanceId == delivery.Installation?.InstanceId
                ? CommandResult.Ok("同一制品、节点和实例已核对。") : CommandResult.Fail("目标运行实例已改变，请重新 submit 审核。");
        }
        if (!Connected) return CommandResult.Fail("目标节点离线，审核依据无法核对。");
        var verified = await RequestAsync("package.verify", delivery, cancellation).ConfigureAwait(false);
        return verified.GetBoolean() ? CommandResult.Ok("远端同一制品、节点和实例已核对。")
            : CommandResult.Fail("远端运行实例已改变，请重新 submit 审核。");
    }
}
