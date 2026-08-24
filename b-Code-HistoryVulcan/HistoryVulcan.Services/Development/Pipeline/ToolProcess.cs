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
        var exit = Execute(fileName, arguments, workingDirectory, log, description, out var output, out var error);
        if (output.Length > 0)
            log.Write(output);
        if (error.Length > 0)
            log.Write(error);
        log.Flush();
        if (exit != 0)
            throw new InvalidOperationException($"{description} 失败，退出码 {exit}。");
    }

    public static int RunAllowingFailure(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TextWriter log,
        string description)
    {
        var exit = Execute(fileName, arguments, workingDirectory, log, description, out var output, out var error);
        log.Write(output);
        log.Write(error);
        log.Flush();
        return exit;
    }

    public static string Capture(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var exit = Execute(
            fileName, arguments, workingDirectory, TextWriter.Null, fileName, out var output, out var error);
        if (exit != 0)
            throw new InvalidOperationException($"{fileName} 失败，退出码 {exit}：{error}");
        return output.Trim();
    }

    /// <summary>
    /// 拉起一个工具进程，读干两条输出流，等它退出。
    /// </summary>
    /// <remarks>
    /// **两条流必须并发读，不能一条读完再读另一条。** 顺序读是这里此前的写法，也是
    /// .NET 上的经典死锁：子进程往 stderr 写满管道缓冲区（约 4KB）就阻塞，
    /// 而父进程还卡在 <c>StandardOutput.ReadToEnd()</c> 上等 stdout 收尾，两边永久互等。
    /// <c>dotnet build</c> 稍微多几条警告就能触发，而 PowerShell 管线退役之后，
    /// 这条路径是宿主唯一的发布通道——挂住就是整个发布挂住。
    ///
    /// 用 <c>OutputDataReceived</c>/<c>ErrorDataReceived</c> 异步读：两条流各有自己的
    /// 泵，谁先满谁先被抽干。<c>WaitForExit()</c> 的无参重载在异步读之后还会等两个
    /// 流到达 EOF，因此不需要额外同步。
    ///
    /// 超时是兜底而不是主要手段：正常的 <c>dotnet test</c> 可能跑几分钟，所以给得足够宽，
    /// 但绝不留「永远等下去」这个选项——发布卡死时使用者看到的只是一条没有回声的命令。
    /// </remarks>
    private static int Execute(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TextWriter log,
        string description,
        out string output,
        out string error)
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

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data != null)
                lock (stdout) stdout.AppendLine(args.Data);
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data != null)
                lock (stderr) stderr.AppendLine(args.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(Timeout))
        {
            TryKill(process, log, description);
            throw new TimeoutException(
                $"{description} 超过 {Timeout.TotalMinutes:0} 分钟未结束，已终止 {fileName}。");
        }

        lock (stdout)
            output = stdout.ToString();
        lock (stderr)
            error = stderr.ToString();
        return process.ExitCode;
    }

    /// <summary>单个工具进程的上限。宽到不会误杀正常的 dotnet test，窄到不会无声挂死。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    private static void TryKill(Process process, TextWriter log, string description)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            // 终止失败只记一笔：调用方要等的是那条 TimeoutException，
            // 不该被「收尸也失败了」顶替掉真正的死因。
            log.WriteLine($"[{description}] 终止超时进程失败: {ex.GetType().Name}: {ex.Message}");
            log.Flush();
        }
    }
}
