namespace HistoryVulcan.ServiceHost;

/// <summary>CLI 的稳定机器可读结果合同。</summary>
internal sealed record CliResultEnvelope(
    string RunId,
    bool Success,
    int ExitCode,
    string ExecutionTarget,
    string? CandidatePath,
    string? InstalledPath,
    object? RuntimeAck,
    string? LogPath,
    IReadOnlyList<string> Diagnostics,
    object? Data = null);
