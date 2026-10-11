namespace HistoryVulcan.Core.Commands;

/// <summary>Authenticated request context, carried through nested module calls independently of source labels.</summary>
internal sealed class NodeExecutionScope : IDisposable
{
    private static readonly AsyncLocal<NodeExecutionScope?> Ambient = new();
    private readonly NodeExecutionScope? _previous;
    internal static NodeExecutionScope? Current => Ambient.Value;
    internal Func<string, bool> Confirm { get; }
    internal Action<string>? Progress { get; }

    internal NodeExecutionScope(Func<string, bool> confirm, Action<string>? progress = null)
    {
        _previous = Ambient.Value;
        Confirm = confirm;
        Progress = progress;
        Ambient.Value = this;
    }

    public void Dispose() => Ambient.Value = _previous;
}
