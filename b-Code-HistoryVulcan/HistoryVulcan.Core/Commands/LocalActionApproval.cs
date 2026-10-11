namespace HistoryVulcan.Core.Commands;

/// <summary>Approval from the current-user pipe handshake, never inferred from a source label.</summary>
internal sealed class LocalActionApproval : IDisposable
{
    private static readonly AsyncLocal<bool> Value = new();
    private readonly bool _previous = Value.Value;
    internal static bool Approved => Value.Value && NodeExecutionScope.Current == null;
    internal LocalActionApproval(bool approved) => Value.Value = approved;
    public void Dispose() => Value.Value = _previous;
}
