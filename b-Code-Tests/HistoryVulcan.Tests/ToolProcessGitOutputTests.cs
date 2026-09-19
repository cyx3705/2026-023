using HistoryVulcan.Services.Development.Pipeline;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// 管线摆给人看的 git 输出里，路径必须是原样的字。
///
/// git 默认 <c>core.quotepath=true</c>，会把路径里的非 ASCII 字节打成三位八进制码。
/// 本体系的项目名和文件名大量是中文，<c>vulcan.dev.submit</c> 的脏文件清单因此整片是
/// 数字串——那份清单存在的意义就是让人核对"这次带走了哪些文件"，看不懂等于没有。
/// 此前只有 <c>ToolProcess.Git</c> 自己拼了那两条 <c>-c</c>，清单走的却是
/// <see cref="ToolProcess.Capture"/>。因此这里钉的是**进程层**，不是某一个调用点。
/// </summary>
public sealed class ToolProcessGitOutputTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vulcan-quotepath-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// <see cref="ToolProcess.Capture"/>（脏文件清单走的这条）与
    /// <c>ToolProcess.Git</c>（其余管线走的那条）两条路都不做八进制转义。
    /// </summary>
    [Fact]
    public void EveryGitCallSitePrintsChinesePathsAsCharacters()
    {
        const string chinese = "技术合同.md";
        Directory.CreateDirectory(_root);
        Capture("init", "-b", "main");
        Capture("config", "user.email", "pipeline@test");
        Capture("config", "user.name", "pipeline");
        File.WriteAllText(Path.Combine(_root, chinese), "x");

        var captured = Capture("status", "--porcelain");
        Assert.Contains(chinese, captured, StringComparison.Ordinal);
        Assert.DoesNotContain("\\346", captured, StringComparison.Ordinal);

        var (output, error) = ToolProcess.Git(_root, "status", "--porcelain");
        Assert.Null(error);
        Assert.Contains(chinese, output, StringComparison.Ordinal);
        Assert.DoesNotContain("\\346", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 只给 git 补这两条。别的工具（dotnet、打包脚本）拿到 <c>-c core.quotepath=false</c>
    /// 会直接参数错误退出，所以这一层判断必须按可执行文件名收窄。
    /// </summary>
    [Fact]
    public void NonGitToolsKeepTheirArgumentsUntouched()
    {
        Directory.CreateDirectory(_root);
        var result = ToolProcess.Execute("cmd.exe", ["/c", "echo", "probe"], _root);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("probe", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("core.quotepath", result.Output, StringComparison.Ordinal);
    }

    private string Capture(params string[] arguments)
        => ToolProcess.Capture("git", arguments, _root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
