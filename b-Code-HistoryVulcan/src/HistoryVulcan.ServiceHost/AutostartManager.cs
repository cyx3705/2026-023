using Microsoft.Win32;

namespace HistoryVulcan.ServiceHost;

/// <summary>用户级登录启动项；接口用于在测试中替换为内存实现。</summary>
public interface IAutostartManager
{
    bool IsEnabled(string name);

    void SetEnabled(string name, string executablePath, bool enabled);

    bool IsEnabled(string name, string executablePath, IReadOnlyList<string> arguments)
        => IsEnabled(name);

    void SetEnabled(
        string name,
        string executablePath,
        IReadOnlyList<string> arguments,
        bool enabled)
        => SetEnabled(name, executablePath, enabled);
}

public sealed class WindowsRunAutostartManager : IAutostartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsEnabled(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public bool IsEnabled(string name, string executablePath, IReadOnlyList<string> arguments)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(name) is string value
               && value.Equals(BuildCommand(executablePath, arguments), StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(string name, string executablePath, bool enabled)
        => SetEnabled(name, executablePath, [], enabled);

    public void SetEnabled(
        string name,
        string executablePath,
        IReadOnlyList<string> arguments,
        bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
            key.SetValue(name, BuildCommand(executablePath, arguments));
        else
            key.DeleteValue(name, throwOnMissingValue: false);
    }

    private static string BuildCommand(string executablePath, IReadOnlyList<string> arguments)
        => $"\"{executablePath}\"" + (arguments.Count == 0
            ? ""
            : " " + string.Join(' ', arguments.Select(Quote)));

    private static string Quote(string value)
        => value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? "\"" + value.Replace("\"", "\\\"") + "\""
            : value;
}
