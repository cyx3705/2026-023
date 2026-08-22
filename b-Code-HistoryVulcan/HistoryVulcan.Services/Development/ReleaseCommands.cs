using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Storage;

namespace HistoryVulcan.Services.Development;

/// <summary>
/// 发布管线的命令面：把 <c>Publish-OneHistoryModule.ps1</c> 作为分离子进程拉起，
/// 让模块发布和其余能力一样原生暴露为 MCP 工具。
/// </summary>
/// <remarks>
/// 关键约束：运行状态只落在日志文件里，不放在本模块的内存里。
/// 门禁提交后对模块调用 <c>vulcan.module.install</c> 热重载（与测试、模块页按钮同一接口）。
/// 热重载会替换目标模块，不替换宿主。运行状态只落日志：start 立即返回 run 标识，
/// status/log 一律现场读日志目录，目标模块被换掉也不影响追踪。
///
/// 子进程自己把 stdout/stderr 重定向进日志（PowerShell 的 <c>*&gt;</c>），Diana 不做流泵送：
/// 泵送线程会随模块卸载而中断，日志就断在半截。退出码单独落一个纯 ASCII 的
/// <c>.exit</c> 文件，status 据此判断"仍在跑 / 成功 / 失败"，不依赖进程句柄。
/// </remarks>
internal static class ReleaseCommands
{
    private const string ExitFileSuffix = ".exit";
    /// <summary>
    /// 发布引擎所在的项目。随开发路线一同迁入宿主（4.6.0），此前是 2026-019-HistoryDiana。
    /// </summary>
    /// <remarks>
    /// 引擎（<c>Publish-OneHistoryModule.ps1</c> 与发布登记）必须与指令面同仓：
    /// 指令面在宿主而引擎留在模块，等于「宿主活着但发布跑不了」——
    /// 而这条路线搬进宿主的全部理由就是它不该依赖任何模块。
    /// </remarks>
    private const string PipelineProjectName = "2026-023-HistoryVulcan";

    public static void Register(CommandRegistry registry, DevelopmentContext host)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(host);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.release.modules",
            Domain = "vulcan",
            CommandClass = "release",
            Summary = "列出可发布的模块和宿主及其项目目录",
            Example = "vulcan.release.modules",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => Modules(host.Settings)),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.release.status",
            Domain = "vulcan",
            CommandClass = "release",
            Summary = "查看发布运行状态：仍在跑 / 成功 / 失败，附日志末尾",
            Example = "vulcan.release.status",
            Parameters =
            [
                Text("run", "run 标识，省略时取最近一次", position: 0),
                Int("lines", "附带的日志末尾行数，范围 1~200", "20"),
            ],
            Readonly = true,
            Handler = CommandDescriptor.Sync(context => Status(
                host,
                context.GetString("run"),
                context.GetInt("lines", 20))),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.release.log",
            Domain = "vulcan",
            CommandClass = "release",
            Summary = "读取某次发布运行的日志末尾",
            Example = "vulcan.release.log run=20260815-090000-HistoryMercury lines=80",
            Parameters =
            [
                Text("run", "run 标识，省略时取最近一次", position: 0),
                Int("lines", "返回的末尾行数，范围 1~500", "80"),
            ],
            Readonly = true,
            Handler = CommandDescriptor.Sync(context => Log(
                host,
                context.GetString("run"),
                context.GetInt("lines", 80))),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.release.cycle",
            Domain = "vulcan",
            CommandClass = "release",
            Summary = "跑通门禁、写入版本化候选并提交；模块候选严格替换到 Vulcan 运行区",
            Example = "vulcan.release.cycle name=HistoryJanus msg=fix-layout worktree=abc-1-codex-fix",
            Parameters =
            [
                Text("name", "已登记的模块名，见 vulcan.release.modules", required: true, position: 0),
                Text("msg", "提交说明", required: true, position: 1),
                Text("worktree", "AI 工作区目录名或绝对路径；省略则在主树正式促级后提交"),
            ],
            Handler = async context => await CycleAsync(
                host,
                context.RequireString("name"),
                context.RequireString("msg"),
                context.GetString("worktree"),
                context.Progress,
                context.Cancellation).ConfigureAwait(false),
        });
    }

    private static CommandResult Modules(ISettingsService settings)
    {
        var registryPath = RegistryPath(settings);
        if (!File.Exists(registryPath))
            return CommandResult.Fail($"找不到发布登记表：{registryPath}");

        List<(string Name, string Kind, string Project)> rows = [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(registryPath));
            if (document.RootElement.TryGetProperty("modules", out var modules))
            {
                foreach (var module in modules.EnumerateArray())
                {
                    rows.Add((
                        module.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                        module.TryGetProperty("kind", out var kind) ? kind.GetString() ?? "" : "",
                        module.TryGetProperty("projectDirectory", out var dir) ? dir.GetString() ?? "" : ""));
                }
            }
        }
        catch (JsonException ex)
        {
            return CommandResult.Fail($"发布登记表解析失败：{ex.Message}");
        }

        rows = rows.Where(row => row.Name.Length > 0).ToList();
        if (!rows.Any(row => row.Name.Equals("HistoryVulcan", StringComparison.OrdinalIgnoreCase)))
            rows.Add(("HistoryVulcan", "host", "2026-023-HistoryVulcan"));
        rows = rows.OrderBy(row => row.Name, StringComparer.Ordinal).ToList();
        var text = new StringBuilder($"已登记发布目标: {rows.Count} 个");
        foreach (var row in rows)
            text.Append($"\n  {row.Name,-18} {row.Kind,-8} {row.Project}");
        return CommandResult.Ok(text.ToString(), rows.Select(row => new { row.Name, row.Kind, row.Project }).ToList());
    }

    private static CommandResult Start(
        DevelopmentContext host,
        string name,
        bool publish,
        string? worktree,
        string? commitRoot = null,
        string? commitMessage = null)
    {
        var moduleName = name.Trim();
        var registryPath = RegistryPath(host.Settings);
        if (!File.Exists(registryPath))
            return CommandResult.Fail($"找不到发布登记表：{registryPath}");

        bool known;
        var moduleProject = moduleName;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(registryPath));
            known = false;
            if (document.RootElement.TryGetProperty("modules", out var modules))
            {
                foreach (var module in modules.EnumerateArray())
                {
                    if (!module.TryGetProperty("name", out var value)
                        || !string.Equals(value.GetString(), moduleName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    known = true;
                    if (module.TryGetProperty("projectDirectory", out var dir) && dir.GetString() is { Length: > 0 } project)
                        moduleProject = project;
                    break;
                }
            }
        }
        catch (JsonException ex)
        {
            return CommandResult.Fail($"发布登记表解析失败：{ex.Message}");
        }

        // 宿主是管线里的内置特例，不在登记表里，但确实可发布。
        if (!known && !moduleName.Equals("HistoryVulcan", StringComparison.OrdinalIgnoreCase))
            return CommandResult.Fail($"{moduleName} 不在发布登记表里，见 vulcan.release.modules。");

        // 工作区不写正式 Clio z：-Publish 只从主树来。无 -Publish 时脚本把候选写入该工作树自己的 z-*。
        string? projectRootOverride = null;
        if (!string.IsNullOrWhiteSpace(worktree))
        {
            if (publish)
                return CommandResult.Fail("worktree 与 publish=true 互斥：正式促级只能从主树构建。");
            projectRootOverride = Path.IsPathFullyQualified(worktree.Trim())
                ? worktree.Trim()
                : Path.Combine(WorktreeCommands.ResolveRootPublic(host.Settings), moduleProject, worktree.Trim());
            if (!Directory.Exists(projectRootOverride))
                return CommandResult.Fail($"工作区不存在：{projectRootOverride}");
        }

        var scriptPath = Path.Combine(PipelineProjectRoot(host.Settings), PipelineDirectory, "Publish-OneHistoryModule.ps1");
        if (!File.Exists(scriptPath))
            return CommandResult.Fail($"找不到发布管线脚本：{scriptPath}");

        var logDirectory = LogDirectory(host);
        Directory.CreateDirectory(logDirectory);
        var run = $"{DateTime.Now:yyyyMMdd-HHmmss}-{moduleName}";
        var logPath = Path.Combine(logDirectory, run + ".log");
        // cycle 一返回 run 就会去读这份日志；子进程的重定向还没创建文件时，
        // 旧逻辑会立刻报「找不到运行记录」。先占位，等管线往里追加。
        File.WriteAllText(logPath, "", new UTF8Encoding(false));

        // 子进程自己写日志：Diana 被管线热重载时，泵送线程会断，日志就断在半截。
        // 必须用嵌套的 powershell.exe 调起管线，不能在本会话里 `& 脚本`：
        // 管线内部会 exit，那会直接终结整个会话，后面的收尾语句一行都跑不到，
        // 实测表现为子进程已退出却没有留下退出码文件。
        // 日志用 UTF-8 流式追加，不用 `*>`：控制台默认 GBK，中文 dotnet 输出会烂码。
        var inner = new StringBuilder();
        inner.Append("chcp 65001 | Out-Null; ");
        inner.Append("[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false; ");
        inner.Append($"& {Quote(scriptPath)} -Module {Quote(moduleName)}");
        if (publish)
            inner.Append(" -Publish");
        if (projectRootOverride != null)
            inner.Append($" -SourceWorktree {Quote(projectRootOverride)}");
        inner.Append("; if (-not $?) { exit 1 }; if ($null -eq $LASTEXITCODE) { exit 0 }; exit $LASTEXITCODE");

        var command = new StringBuilder();
        command.Append("chcp 65001 | Out-Null; ");
        command.Append("[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false; ");
        command.Append("$OutputEncoding = [Console]::OutputEncoding; ");
        command.Append("$__utf8 = New-Object System.Text.UTF8Encoding $false; ");
        command.Append($"$__writer = New-Object System.IO.StreamWriter({Quote(logPath)}, $true, $__utf8); ");
        command.Append("$__writer.AutoFlush = $true; ");
        command.Append("$__pipe = 1; ");
        command.Append("try { ");
        command.Append("& powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ");
        command.Append(Quote(inner.ToString()));
        command.Append(" 2>&1 | ForEach-Object { $__writer.WriteLine([string]$_) }; ");
        command.Append("$__pipe = $LASTEXITCODE; if ($null -eq $__pipe) { $__pipe = 1 } ");
        command.Append("} finally { $__writer.Close() }; ");
        // 退出码单独落一个纯 ASCII 文件，不往日志里追加。
        if (!string.IsNullOrWhiteSpace(commitRoot) && !string.IsNullOrWhiteSpace(commitMessage))
        {
            var msgPath = logPath + ".msg";
            File.WriteAllText(msgPath, commitMessage.Trim() + Environment.NewLine, new UTF8Encoding(false));
            command.Append("if ($__pipe -eq 0) { ");
            command.Append($"git -C {Quote(commitRoot)} add -A; ");
            command.Append($"git -C {Quote(commitRoot)} diff --cached --quiet; ");
            command.Append("if ($LASTEXITCODE -ne 0) { ");
            command.Append($"git -C {Quote(commitRoot)} -c i18n.commitEncoding=utf-8 commit -F {Quote(msgPath)}; ");
            command.Append("$__pipe = $LASTEXITCODE ");
            command.Append("} else { Write-Host 'Nothing to commit after pipeline.' } ");
            command.Append("}; ");
        }

        command.Append($"Set-Content -Path {Quote(logPath + ExitFileSuffix)} -Value $__pipe -Encoding ascii");

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = PipelineProjectRoot(host.Settings),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command.ToString());

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
                return CommandResult.Fail("发布管线子进程启动失败。");

            var mode = publish
                ? "构建 + 门禁 + 正式促级"
                : projectRootOverride != null
                    ? "构建 + 门禁 + 写入工作区 z"
                    : "只构建候选并跑门禁";
            if (!string.IsNullOrWhiteSpace(commitRoot))
                mode += " + 提交";
            return CommandResult.Ok(
                $"已拉起 {moduleName} 的发布管线（{mode}），run={run}，pid={process.Id}\n"
                + $"日志: {logPath}\n"
                + "管线在独立进程中运行，用 vulcan.release.status 查看进度；目标模块热重载时状态仍从日志读取。",
                new { Run = run, Module = moduleName, Publish = publish, Pid = process.Id, Log = logPath });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return CommandResult.Fail($"发布管线子进程启动失败：{ex.Message}");
        }
    }

    private static CommandResult Status(DevelopmentContext host, string? run, int lines)
    {
        var (logPath, error) = ResolveRun(host, run);
        if (error != null)
            return CommandResult.Fail(error);

        var tail = ReadTail(logPath!, Math.Clamp(lines, 1, 200), out var exitCode, out var finished);
        var runId = Path.GetFileNameWithoutExtension(logPath!);
        var state = !finished
            ? "仍在运行"
            : exitCode == 0 ? "成功" : $"失败（退出码 {exitCode}）";

        var text = new StringBuilder($"[{runId}] {state}");
        text.Append($"\n日志: {logPath}");
        text.Append($"\n--- 末尾 {tail.Count} 行 ---");
        foreach (var line in tail)
            text.Append('\n').Append(line);

        return CommandResult.Ok(text.ToString(), new
        {
            Run = runId,
            Finished = finished,
            ExitCode = exitCode,
            Success = finished && exitCode == 0,
            Log = logPath,
        });
    }

    private static CommandResult Log(DevelopmentContext host, string? run, int lines)
    {
        var (logPath, error) = ResolveRun(host, run);
        if (error != null)
            return CommandResult.Fail(error);

        var tail = ReadTail(logPath!, Math.Clamp(lines, 1, 500), out _, out _);
        var text = new StringBuilder($"[{Path.GetFileNameWithoutExtension(logPath!)}] 末尾 {tail.Count} 行");
        foreach (var line in tail)
            text.Append('\n').Append(line);
        return CommandResult.Ok(text.ToString(), new { Log = logPath, Lines = tail });
    }

    private static (string? LogPath, string? Error) ResolveRun(DevelopmentContext host, string? run)
    {
        var logDirectory = LogDirectory(host);
        if (!Directory.Exists(logDirectory))
            return (null, "还没有任何发布运行记录。");

        if (!string.IsNullOrWhiteSpace(run))
        {
            var named = Path.Combine(logDirectory, run.Trim() + ".log");
            return File.Exists(named) ? (named, null) : (null, $"找不到运行记录 {run.Trim()}。");
        }

        var newest = new DirectoryInfo(logDirectory).GetFiles("*.log")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();
        return newest == null ? (null, "还没有任何发布运行记录。") : (newest.FullName, null);
    }

    /// <summary>读日志末尾；允许被子进程同时写入，因此以共享模式打开。</summary>
    private static List<string> ReadTail(string path, int lines, out int exitCode, out bool finished)
    {
        exitCode = 0;
        finished = false;
        var all = new List<string>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
                all.Add(line);
        }
        catch (IOException)
        {
            return ["(日志暂时不可读，管线正在写入)"];
        }

        var exitPath = path + ExitFileSuffix;
        if (File.Exists(exitPath))
        {
            try
            {
                finished = int.TryParse(File.ReadAllText(exitPath).Trim(), out var parsed);
                exitCode = finished ? parsed : 0;
            }
            catch (IOException)
            {
            }
        }

        return all.Count <= lines ? all : all.GetRange(all.Count - lines, lines);
    }

    /// <summary>引擎脚本与发布登记在项目里的位置。</summary>
    private const string PipelineDirectory = "b-Code-HistoryVulcan/eng/pipeline";

    private static string PipelineProjectRoot(ISettingsService settings)
        => Path.Combine(ProjectLibraryRoot.Resolve(settings), PipelineProjectName);

    private static string RegistryPath(ISettingsService settings)
        => Path.Combine(PipelineProjectRoot(settings), PipelineDirectory, "module-publish.manifest.json");

    private static string LogDirectory(DevelopmentContext host)
        => Path.Combine(host.DataDirectory, "release");

    private static async Task<CommandResult> CycleAsync(
        DevelopmentContext host,
        string name,
        string message,
        string? worktree,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        var moduleName = name.Trim();
        var commitMessage = message.Trim();
        if (commitMessage.Length == 0)
            return CommandResult.Fail("msg 不能为空。");

        if (!TryResolveModule(host.Settings, moduleName, out var module))
            return CommandResult.Fail($"{moduleName} 不在发布登记表里，见 vulcan.release.modules。");

        var mainProject = Path.Combine(ProjectLibraryRoot.Resolve(host.Settings), module.ProjectDirectory);
        var isWorktree = !string.IsNullOrWhiteSpace(worktree);
        string repoRoot;
        if (isWorktree)
        {
            repoRoot = WorktreeCommands.ResolveWorktreePath(
                host.Settings, module.ProjectDirectory, worktree!.Trim());
            if (!Directory.Exists(repoRoot))
                return CommandResult.Fail($"工作区不存在：{repoRoot}");
        }
        else
        {
            repoRoot = mainProject;
            if (!Directory.Exists(repoRoot))
                return CommandResult.Fail($"项目主树不存在：{repoRoot}");
        }

        var started = Start(
            host,
            moduleName,
            publish: !isWorktree,
            worktree: isWorktree ? repoRoot : null,
            commitRoot: repoRoot,
            commitMessage: commitMessage);
        if (!started.Success)
            return started;

        var run = ReadRunId(started);
        if (string.IsNullOrWhiteSpace(run))
            return CommandResult.Fail("管线已拉起，但没有返回 run 标识。\n" + started.Message);

        progress?.Report($"已拉起 {run}，等待门禁和提交结束…");
        // 管线子进程已经独立在跑。MCP/CLI 客户端超时不得取消等待和热重载，
        // 否则会出现「日志成功、运行区仍是旧包」。
        var (finished, exitCode, tail) = await WaitForRunAsync(host, run, progress, CancellationToken.None)
            .ConfigureAwait(false);
        if (!finished)
            return CommandResult.Fail($"cycle 等待结束：{tail}\nrun={run}");
        if (exitCode != 0)
        {
            return CommandResult.Fail(
                $"cycle 失败（退出码 {exitCode}）。提交未执行或未成功。\nrun={run}\n--- 日志末尾 ---\n{tail}");
        }

        var text = new StringBuilder($"cycle 完成：{moduleName} 已门禁通过并提交。\n仓库: {repoRoot}\nrun={run}");
        if (!string.Equals(module.Kind, "module", StringComparison.OrdinalIgnoreCase))
        {
            text.Append(isWorktree
                ? "\n宿主候选不热重载模块槽，可继续在工作区开发或 vulcan.worktree.merge。"
                : "\n主树 z-Publish 宿主候选已通过门禁；重启切换由宿主部署步骤完成。");
            if (isWorktree)
                text.Append("\n若对话根已在工作区内（grok 切过根），合并前先迁出。");
            return CommandResult.Ok(text.ToString(), new
            {
                Module = moduleName,
                Worktree = isWorktree ? repoRoot : null,
                Run = run,
                Committed = true,
            });
        }

        progress?.Report("提交完成，正在调用 Vulcan 热重载当前候选…");
        var reload = await ModulePackageHotReload.InstallCurrentAsync(
            host, repoRoot, moduleName, "host:vulcan.release.cycle", CancellationToken.None).ConfigureAwait(false);
        text.Append('\n').Append(reload.Success
            ? reload.Message
            : "候选已提交，但 Vulcan 热重载未成功（不自动回滚）：" + reload.Message);
        text.Append("\n测试不通过时，从主树候选或 z-Publish/history 再调同一热重载接口，不会自动恢复。");
        if (isWorktree)
        {
            text.Append("\n可继续在此工作区开发，或 vulcan.worktree.merge 并回主线。");
            text.Append("\n若对话根已在工作区内（grok 切过根），合并前先迁到宿主主树或该模块 Clio 主树再 merge；合并会删工作区目录。其他 AI 对话不在工作区里，可直接 merge。");
        }

        return reload.Success
            ? CommandResult.Ok(text.ToString(), new
            {
                Module = moduleName,
                Worktree = isWorktree ? repoRoot : null,
                Run = run,
                Committed = true,
            })
            : CommandResult.Fail(text.ToString());
    }

    private static async Task<(bool Finished, int ExitCode, string Tail)> WaitForRunAsync(
        DevelopmentContext host,
        string run,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow.AddMinutes(20);
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var (logPath, error) = ResolveRun(host, run);
            if (error != null)
            {
                if (error.Contains("找不到运行记录", StringComparison.Ordinal)
                    && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(250, cancellation).ConfigureAwait(false);
                    continue;
                }

                return (false, -1, error);
            }

            var tail = ReadTail(logPath!, 40, out var exitCode, out var finished);
            if (finished)
                return (true, exitCode, string.Join('\n', tail));
            if (DateTime.UtcNow >= deadline)
                return (false, -1, "等待超时（20 分钟）。管线可能仍在跑，用 vulcan.release.status 查看。\n" + string.Join('\n', tail));

            progress?.Report($"[{run}] 仍在运行…");
            await Task.Delay(2000, cancellation).ConfigureAwait(false);
        }
    }

    private static string? ReadRunId(CommandResult started)
    {
        var run = started.Data?.GetType().GetProperty("Run")?.GetValue(started.Data) as string;
        if (!string.IsNullOrWhiteSpace(run))
            return run;
        var match = Regex.Match(started.Message, @"run=([A-Za-z0-9._-]+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    internal static bool TryResolveModule(ISettingsService settings, string name, out ReleaseModule module)
    {
        module = default!;
        if (name.Equals("HistoryVulcan", StringComparison.OrdinalIgnoreCase))
        {
            module = new ReleaseModule("HistoryVulcan", "host", "2026-023-HistoryVulcan", "z-Publish");
            return true;
        }

        var registryPath = RegistryPath(settings);
        if (!File.Exists(registryPath))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(registryPath));
            if (!document.RootElement.TryGetProperty("modules", out var modules))
                return false;
            foreach (var entry in modules.EnumerateArray())
            {
                if (!entry.TryGetProperty("name", out var value)
                    || !string.Equals(value.GetString(), name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var project = entry.TryGetProperty("projectDirectory", out var dir) ? dir.GetString() ?? "" : "";
                var formal = entry.TryGetProperty("formalDirectory", out var formalDir) ? formalDir.GetString() ?? "" : "";
                var kind = entry.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() ?? "module" : "module";
                if (project.Length == 0 || formal.Length == 0)
                    return false;
                module = new ReleaseModule(name, kind, project, formal);
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    internal static bool TryResolveModuleByProject(ISettingsService settings, string projectDirectory, out ReleaseModule module)
    {
        module = default!;
        if (projectDirectory.Equals("2026-023-HistoryVulcan", StringComparison.OrdinalIgnoreCase))
        {
            module = new ReleaseModule("HistoryVulcan", "host", "2026-023-HistoryVulcan", "z-Publish");
            return true;
        }

        var registryPath = RegistryPath(settings);
        if (!File.Exists(registryPath))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(registryPath));
            if (!document.RootElement.TryGetProperty("modules", out var modules))
                return false;
            foreach (var entry in modules.EnumerateArray())
            {
                if (!entry.TryGetProperty("projectDirectory", out var dir)
                    || !string.Equals(dir.GetString(), projectDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var moduleName = entry.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                var formal = entry.TryGetProperty("formalDirectory", out var formalDir) ? formalDir.GetString() ?? "" : "";
                var kind = entry.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() ?? "module" : "module";
                if (moduleName.Length == 0 || formal.Length == 0)
                    return false;
                module = new ReleaseModule(moduleName, kind, projectDirectory, formal);
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    internal sealed record ReleaseModule(string Name, string Kind, string ProjectDirectory, string FormalDirectory);

    /// <summary>PowerShell 单引号字符串：内部单引号翻倍转义。</summary>
    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static ParameterSpec Text(string name, string description, bool required = false, int? position = null)
        => new() { Name = name, Description = description, Required = required, Position = position };

    private static ParameterSpec Bool(string name, string description, string defaultValue)
        => new()
        {
            Name = name,
            Description = description,
            Type = ParamType.Bool,
            Default = defaultValue,
            AllowedValues = ["true", "false"],
        };

    private static ParameterSpec Int(string name, string description, string defaultValue)
        => new() { Name = name, Description = description, Type = ParamType.Int, Default = defaultValue };
}
