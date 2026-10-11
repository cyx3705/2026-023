using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Services.Nodes;
using HistoryVulcan.Services.Development.Pipeline;

namespace HistoryVulcan.Services.Development;

internal sealed record PipelineDeliveryRecord(string Commit, NodeDeliveryRecord Delivery, string Stage, object? Merge = null);

internal static class PipelineDelivery
{
    internal static string RecordPath(DevelopmentContext host, string worktree)
        => Path.Combine(host.DataDirectory, "release", "reviews", Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(worktree).TrimEnd('/', '\\').ToUpperInvariant()))) + ".json");

    internal static async Task<CommandResult> SubmitAsync(DevelopmentContext host, string worktree, string module,
        string? target, CancellationToken cancellation)
    {
        var package = PublishPackages.ResolveCurrent(worktree, module);
        var command = "vulcan.node.deliver path=" + CommandParser.QuoteArg(package)
            + (string.IsNullOrWhiteSpace(target) ? "" : " target=" + CommandParser.QuoteArg(target));
        var delivered = await ExecuteAsync(host, command, cancellation).ConfigureAwait(false);
        if (delivered.Data != null)
        {
            var node = JsonSerializer.SerializeToElement(delivered.Data, NodeStateFile.Json).Deserialize<NodeDeliveryRecord>(NodeStateFile.Json);
            if (node != null)
            {
                var commit = ToolProcess.CaptureLines("git", ["rev-parse", "HEAD"], worktree).Trim();
                var record = new PipelineDeliveryRecord(commit, node, delivered.Success ? "awaiting-review" : "delivery-incomplete");
                NodeStateFile.Update(RecordPath(host, worktree), () => record, _ => record);
            }
        }
        return delivered;
    }

    internal static async Task<PipelineDeliveryRecord> RequireReviewedAsync(DevelopmentContext host, string worktree,
        string module, CancellationToken cancellation)
    {
        var record = NodeStateFile.Read<PipelineDeliveryRecord>(RecordPath(host, worktree),
            () => throw new InvalidOperationException("缺少本轮交付审核记录，请重新 submit。"));
        var commit = ToolProcess.CaptureLines("git", ["rev-parse", "HEAD"], worktree).Trim();
        var package = PublishPackages.ResolveCurrent(worktree, module);
        if (!Modules.ModuleNupkg.TryResolveFile(package, out var packageFile, out var error))
            throw new InvalidDataException(error);
        if (record.Stage != "awaiting-review" || record.Commit != commit
            || Modules.ModuleNupkg.Hash(packageFile) != record.Delivery.Artifact.Hash)
            throw new InvalidOperationException("提交或候选与送审记录不一致，请重新 submit。");
        var verify = await ExecuteAsync(host, "vulcan.node.verify id=" + record.Delivery.Id, cancellation).ConfigureAwait(false);
        if (!verify.Success) throw new InvalidOperationException(verify.Message);
        return record;
    }

    internal static Task<CommandResult> ExecuteAsync(DevelopmentContext host, string command, CancellationToken cancellation)
        => host.LiveHost != null ? host.LiveHost(command, cancellation) : host.Bus.ExecuteAsync(command, "host:pipeline", cancellation);
}
