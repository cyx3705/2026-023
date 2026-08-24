using HistoryVulcan.Core.Commands;
using HistoryVulcan.Services.Development.Pipeline;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// 5.1.0 冻结前审查修掉的几处，各钉一条。
/// </summary>
public sealed class PipelineHardeningTests
{
    /// <summary>
    /// 两条输出流都灌满时不许死锁。
    /// </summary>
    /// <remarks>
    /// 修复前 ToolProcess 先 StandardOutput.ReadToEnd() 再读 stderr：子进程把 stderr
    /// 写满管道缓冲区（约 4KB）后阻塞，父进程仍卡在读 stdout 上，两边永久互等，
    /// 而且没有超时。dotnet build 多几条警告就能触发，而 PowerShell 管线退役之后
    /// 这是宿主唯一的发布通道。
    ///
    /// 这里让子进程往两条流各写远超缓冲区的量：修复前必挂，修复后必须读全。
    /// </remarks>
    [Fact]
    public void LargeOutputOnBothStreamsDoesNotDeadlock()
    {
        const int lines = 4000;
        var script = $"for /L %i in (1,1,{lines}) do @(echo OUT-%i& echo ERR-%i 1>&2)";

        var captured = new StringWriter();
        var exit = ToolProcess.RunAllowingFailure(
            "cmd.exe", ["/c", script], Path.GetTempPath(), captured, "双流灌注");

        Assert.Equal(0, exit);
        var text = captured.ToString();
        Assert.Contains($"OUT-{lines}", text, StringComparison.Ordinal);
        Assert.Contains($"ERR-{lines}", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 校验清单只排除自己，不按后缀排除同名文件。
    /// </summary>
    /// <remarks>
    /// 修复前用 path.EndsWith("SHA256SUMS")，任意层级的同名文件都被剔出 payload——
    /// 那些字节既不参与写入也不参与校验，可以被任意篡改而 Assert 依然通过。
    /// </remarks>
    [Fact]
    public void NestedChecksumFilesStayInsideTheVerifiedPayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "sums-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        try
        {
            File.WriteAllText(Path.Combine(root, "payload.txt"), "a");
            var nested = Path.Combine(root, "nested", SnapshotHashes.FileName);
            File.WriteAllText(nested, "嵌套包自己的清单");

            SnapshotHashes.Write(root);
            SnapshotHashes.Assert(root, skipHistory: false);

            // 篡改嵌套清单：它在管辖范围内，必须被发现。
            File.WriteAllText(nested, "被改过了");
            Assert.Throws<InvalidOperationException>(
                () => SnapshotHashes.Assert(root, skipHistory: false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>预发布版本号是合法身份，不该被报成「清单缺失或无效」。</summary>
    [Fact]
    public void PrereleaseVersionsAreAcceptedAsPackageIdentity()
    {
        var staging = Path.Combine(Path.GetTempPath(), "pre-" + Guid.NewGuid().ToString("N"));
        var publish = Path.Combine(Path.GetTempPath(), "pub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(publish);
        try
        {
            File.WriteAllText(
                Path.Combine(publish, "module.manifest.json"),
                """{"name":"HistorySample","version":"1.2.3-rc1"}""");
            File.WriteAllText(Path.Combine(publish, "payload.txt"), "x");
            SnapshotHashes.Write(publish);

            // Archive 走 ReadIdentity；预发布号被拒时抛的是「包身份清单缺失或无效」。
            var historyRoot = Path.Combine(publish, "history");
            var archive = typeof(PublishLayout).GetMethod(
                "Archive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            archive.Invoke(null, [publish, historyRoot]);

            Assert.True(Directory.Exists(Path.Combine(historyRoot, "HistorySample-v1.2.3-rc1")));
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
            Directory.Delete(publish, recursive: true);
        }
    }

    /// <summary>
    /// 一条注册不上的指令只连累它自己，不连累整批。
    /// </summary>
    /// <remarks>
    /// CommandRegistry.Register 除重名（InvalidOperationException）外，还会因
    /// 「写了 ConfirmPrompt 却没升到 Ask 级」抛 ArgumentException。ModuleHost 的
    /// 注册循环此前只兜前者，后者会穿透整个 foreach——排在它后面的所有模块一条
    /// 指令都注册不上。这里锁住「两类异常都必须是可跳过的单条失败」。
    /// </remarks>
    [Fact]
    public void BothRejectionKindsAreSkippableSingleCommandFailures()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "sample.taken",
            Summary = "先占位",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        });

        // 重名 → InvalidOperationException
        Assert.Throws<InvalidOperationException>(() => registry.Register(new CommandDescriptor
        {
            Name = "sample.taken",
            Summary = "重名",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        }));

        // 文案与级别不一致 → ArgumentException
        Assert.Throws<ArgumentException>(() => registry.Register(new CommandDescriptor
        {
            Name = "sample.mismatch",
            Summary = "写了文案没升级别",
            ConfirmPrompt = _ => "确认？",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        }));

        // 两类都落在 ModuleHost 注册循环的 catch 过滤器里；漏掉任一类都会让
        // 一条坏指令吃掉它之后所有模块的指令。
        foreach (var thrown in new Exception[]
                 {
                     new InvalidOperationException("重名"),
                     new ArgumentException("级别不一致"),
                 })
        {
            Assert.True(thrown is InvalidOperationException or ArgumentException);
        }
    }

    /// <summary>
    /// 发布目标就是当前进程运行的目录时，第一步就拒绝。
    /// </summary>
    /// <remarks>
    /// 宿主自替换按 DEC-053 走手动操作。修复前没有任何前置检查：整轮还原、单元测试、
    /// 两道门禁和一次完整 publish 跑完几分钟，才在 promote 阶段撞上自己 exe 的文件锁，
    /// 抛的还是一句与处境无关的「文件被占用」。
    ///
    /// 顺带钉住前缀误判：z-Publish 与 z-Publish-old 是两个目录，不能因为字符串前缀
    /// 相同就把后者也拦下——那会把一个正当的发布目标变成永远发不出去的目标。
    /// </remarks>
    [Fact]
    public void ReleaseRefusesToReplaceTheDirectoryTheProcessRunsFrom()
    {
        var guard = typeof(ReleaseEngine).GetMethod(
            "AssertNotSelfReplacing",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var running = Environment.ProcessPath;
        Assert.False(string.IsNullOrEmpty(running));
        var ownDirectory = Path.GetDirectoryName(running)!;

        var refused = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => guard.Invoke(null, [ownDirectory, "HistoryVulcan"]));
        var reason = Assert.IsType<InvalidOperationException>(refused.InnerException);
        Assert.Contains("DEC-053", reason.Message, StringComparison.Ordinal);

        // 前缀相同但不是同一个目录，以及完全无关的目录：都必须放行。
        guard.Invoke(null, [ownDirectory + "-old", "HistoryVulcan"]);
        guard.Invoke(null, [Path.GetTempPath(), "HistorySample"]);
    }

    /// <summary>促级失败不许把中转目录留在 z 快照里。</summary>
    /// <remarks>
    /// 中转目录必须建在发布根内（同卷才能用 Move 促级），而发布根是纳入 git 的 z 快照。
    /// 修复前只有成功路径删得掉它：5.1.0 审查时 z-Publish 下有三个 .incoming-host-*
    /// 已经进了版本库，每个带一份完整的宿主副本。
    /// </remarks>
    [Fact]
    public void AbortedPromotionLeavesNoTransitDirectoryBehind()
    {
        var staging = Path.Combine(Path.GetTempPath(), "stage-" + Guid.NewGuid().ToString("N"));
        var publish = Path.Combine(Path.GetTempPath(), "pub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(publish);
        try
        {
            // 缺 module.manifest.json：AssertSnapshot 必然拒收，促级停在中转之后。
            File.WriteAllText(Path.Combine(staging, "payload.txt"), "x");

            var target = new ReleaseTarget(
                Name: "HistorySample",
                Kind: "module",
                ProjectDirectory: "",
                VersionProps: "",
                VersionProperty: "",
                SourceManifest: "",
                SnapshotManifest: "module.manifest.json",
                IdentityProperty: "name",
                CandidateDirectory: "z-Publish",
                FormalDirectory: "z-Publish",
                PackageDocuments: "",
                Package: null,
                Validation: [],
                TestProject: "");

            Assert.Throws<InvalidOperationException>(
                () => PublishLayout.PromoteVersioned(staging, publish, target, "1.0.0"));

            Assert.Empty(Directory.GetDirectories(publish, ".incoming-*"));
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
            Directory.Delete(publish, recursive: true);
        }
    }
}
