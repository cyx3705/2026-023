using System.Diagnostics;
using System.Text;

namespace HistoryVulcan.Services.Development.Pipeline;

/// <summary>
/// 管线只拉起 git 与 dotnet。PowerShell 不再是发布引擎。
/// </summary>
internal static class ToolProcess
{
    private static readonly HashSet<string> Forbidden =
        new(StringComparer.OrdinalIgnoreCase) { "powershell", "powershell.exe", "pwsh", "pwsh.exe" };

    public static void Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TextWriter log,
        string description)
    {
        if (Forbidden.Contains(Path.GetFileName(fileName)))
            throw new InvalidOperationException($"开发管线禁止调用 {fileName}。{description}");

        log.WriteLine($"[{description}] {fileName} {string.Join(' ', arguments)}");
        log.Flush();

        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"{description} 无法启动 {fileName}。");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (output.Length > 0)
            log.Write(output);
        if (error.Length > 0)
            log.Write(error);
        log.Flush();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{description} 失败，退出码 {process.ExitCode}。");
    }

    public static int RunAllowingFailure(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TextWriter log,
        string description)
    {
        if (Forbidden.Contains(Path.GetFileName(fileName)))
            throw new InvalidOperationException($"开发管线禁止调用 {fileName}。{description}");

        log.WriteLine($"[{description}] {fileName} {string.Join(' ', arguments)}");
        log.Flush();
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"{description} 无法启动 {fileName}。");
        log.Write(process.StandardOutput.ReadToEnd());
        log.Write(process.StandardError.ReadToEnd());
        process.WaitForExit();
        log.Flush();
        return process.ExitCode;
    }

    public static string Capture(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        if (Forbidden.Contains(Path.GetFileName(fileName)))
            throw new InvalidOperationException($"开发管线禁止调用 {fileName}。");

        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"无法启动 {fileName}。");
        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} 失败，退出码 {process.ExitCode}：{error}");
        return output;
    }
}
