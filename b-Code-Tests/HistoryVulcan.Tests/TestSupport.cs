using System.Collections.Concurrent;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;

namespace HistoryVulcan.Tests;

internal sealed class MemorySettings : ISettingsService
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    public string? Get(string key) => _values.GetValueOrDefault(key);
    public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
    public void Set(string key, string value) => _values[key] = value;
    public IReadOnlyList<KeyValuePair<string, string>> All() => [.. _values];
}

internal sealed class NullLog : IShellLog
{
    public void Log(ShellLogLevel level, string category, string message) { }
    public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
    public IReadOnlyList<ShellLogEntry> Snapshot() => [];
}

internal sealed class RecordingLog : IShellLog
{
    private readonly ConcurrentQueue<ShellLogEntry> _entries = new();
    public IReadOnlyList<ShellLogEntry> Entries => _entries.ToArray();
    public IReadOnlyList<string> Messages => _entries.Select(entry => entry.Message).ToArray();
    public void Log(ShellLogLevel level, string category, string message)
        => _entries.Enqueue(new ShellLogEntry(DateTime.UtcNow, level, category, message));
    public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
    public IReadOnlyList<ShellLogEntry> Snapshot() => Entries;
    internal void Clear() => _entries.Clear();
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HistoryVulcan.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }
    public string Path { get; }
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
