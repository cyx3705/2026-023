using System.Diagnostics;
using System.Text;

namespace HistoryVulcan.Services.Development.Pipeline;

/// <summary>开发与发布共用的工具进程：并发读输出、限时等待，失败时保留诊断。</summary>
internal static class ToolProcess
{
    public static void Run(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        TextWriter log, string description)
    {
        var exit = RunAllowingFailure(fileName, arguments, workingDirectory, log, description);
        if (exit != 0)
            throw new InvalidOperationException($"{description} 失败，退出码 {exit}。");
    }

    public static int RunAllowingFailure(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        TextWriter log, string description)
    {
        log.WriteLine($"[{description}] {fileName} {string.Join(' ', arguments)}");
        log.Flush();
        var result = Execute(fileName, arguments, workingDirectory);
        log.Write(result.Output);
        log.Write(result.Error);
        log.Flush();
        return result.ExitCode;
    }

    public static string Capture(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var result = Execute(fileName, arguments, workingDirectory);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} 失败，退出码 {result.ExitCode}：{result.Error}");
        return result.Output.Trim();
    }

    internal static (string Output, string? Error) Git(string workingDirectory, params string[] arguments)
    {
        try
        {
            // 两条 -c 不在这里拼：它们对**每一条** git 都该生效，已统一由 Execute 补上。
            var result = Execute("git", arguments, workingDirectory, TimeSpan.FromMinutes(3));
            return (result.Output.Trim(), result.ExitCode == 0 ? null
                : string.IsNullOrWhiteSpace(result.Error) ? $"git 退出码 {result.ExitCode}" : result.Error.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                   or InvalidOperationException or IOException or TimeoutException)
        {
            return ("", ex.Message);
        }
    }

    internal static ToolResult Execute(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan? timeout = null)
    {
        if (Path.GetFileName(fileName).ToLowerInvariant() is "powershell" or "powershell.exe" or "pwsh" or "pwsh.exe")
            throw new InvalidOperationException($"开发管线禁止调用 {fileName}。");

        arguments = WithGitOutputSettings(fileName, arguments);

        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        start.Environment["LC_ALL"] = "C.UTF-8";
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"无法启动 {fileName}。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        var limit = timeout ?? TimeSpan.FromMinutes(30);
        try
        {
            // 超时覆盖进程退出和两条流到达 EOF；不能先阻塞读取再启动计时。
            Task.WhenAll(output, error, process.WaitForExitAsync()).WaitAsync(limit).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                throw new TimeoutException($"{fileName} 超时，终止进程失败: {ex.Message}", ex);
            }
            throw new TimeoutException($"{fileName} 超过 {limit.TotalSeconds:0.###} 秒未结束，已终止。");
        }

        return new ToolResult(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    /// <summary>
    /// 每一条 git 都按同一套输出设置跑。
    ///
    /// <c>core.quotepath</c> 默认开着，git 会把路径里的非 ASCII 字节打成 <c>\346\212\200</c>
    /// 这种三位八进制码。本体系的项目名和文件名大量是中文，于是 <c>vulcan.dev.submit</c>
    /// 摆给人看的脏文件清单整片是数字串——要核对"这次带走了哪些文件"时，那份清单等于没用。
    /// 此前只有 <see cref="Git"/> 自己拼了这两条 <c>-c</c>，而清单走的是
    /// <see cref="Capture"/>，绕过了它；补在进程层才不会再漏下一个调用点。
    ///
    /// 两条 <c>-c</c> 只影响 git **打印**的内容，不影响它做了什么。
    /// </summary>
    private static IReadOnlyList<string> WithGitOutputSettings(
        string fileName, IReadOnlyList<string> arguments)
        => Path.GetFileNameWithoutExtension(fileName)
            .Equals("git", StringComparison.OrdinalIgnoreCase)
            ? ["-c", "core.quotepath=false", "-c", "i18n.logOutputEncoding=utf-8", .. arguments]
            : arguments;
}

internal sealed record ToolResult(int ExitCode, string Output, string Error);
