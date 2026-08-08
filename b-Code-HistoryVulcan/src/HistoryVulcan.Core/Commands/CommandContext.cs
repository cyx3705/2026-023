using System.Globalization;

namespace HistoryVulcan.Core.Commands;

/// <summary>
/// 指令执行上下文:总线校验后的参数取值 + 来源 + 进度上报通道。
/// </summary>
public sealed class CommandContext
{
    private readonly IReadOnlyDictionary<string, string> _values;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public CommandContext(
        CommandDescriptor descriptor,
        IReadOnlyDictionary<string, string> values,
        string source,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        Descriptor = descriptor;
        _values = values;
        Source = source;
        Progress = progress;
        Cancellation = cancellation;
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public CommandDescriptor Descriptor { get; }

    /// <summary>来源标签:UI / 手动 / 脚本:文件名 / layout / 派生应用自定义。</summary>
    public string Source { get; }

    /// <summary>长任务进度上报(§5.2 约束:全程不阻塞 UI);行文本直接进控制台。</summary>
    public IProgress<string>? Progress { get; }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public CancellationToken Cancellation { get; }

    /// <summary>参数是否被显式提供(区分默认值)。</summary>
    public bool Has(string name) => _values.ContainsKey(name);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public string? GetString(string name)
        => _values.TryGetValue(name, out var v) ? v : SpecDefault(name);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public string RequireString(string name)
        => GetString(name) ?? throw new InvalidOperationException($"缺少参数 {name}(总线校验应已拦截)");

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public int GetInt(string name, int fallback = 0)
        => int.TryParse(GetString(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public double GetDouble(string name, double fallback = 0)
        => double.TryParse(GetString(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public bool GetBool(string name, bool fallback = false)
    {
        var s = GetString(name);
        if (s == null)
            return fallback;
        return s.Equals("true", StringComparison.OrdinalIgnoreCase)
               || s.Equals("1", StringComparison.Ordinal)
               || s.Equals("yes", StringComparison.OrdinalIgnoreCase)
               || s.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private string? SpecDefault(string name)
        => Descriptor.Parameters.FirstOrDefault(
            p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Default;
}
