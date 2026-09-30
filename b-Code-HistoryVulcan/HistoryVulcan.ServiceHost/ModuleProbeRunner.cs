using System.Reflection;
using System.Text.Json;
using HistoryVulcan.Core;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Services;

namespace HistoryVulcan.ServiceHost;

/// <summary>
/// <c>HistoryVulcan.Cli.exe --probe &lt;包目录&gt; [--cli &lt;指令&gt;]</c>（5.9.0，DEC-070）。
/// </summary>
/// <remarks>
/// <para>
/// 模块测试以前直接 <c>new ModuleHost(...)</c>，于是宿主实现程序集 Services 的 143 条公开面都成了模块的编译期依赖，
/// 5.3.0、5.4.0 两次改宿主内部都把模块测试打得编不过。现在测试只调已发布宿主的命令行：
/// 宿主怎么装包是宿主的事，模块只看装载结果与指令结果。
/// </para>
/// <para>
/// 包复制到临时运行区后装载（manifest 的 dependsOn 从正式运行区一并复制），数据目录也在临时目录，
/// 不碰正式运行区与正式数据。<c>--cli</c> 给出的指令不受命令行白名单限制——被测的正是模块自己的指令。
/// 退出码：0 接上且指令成功；1 没接上或指令失败；2 用法错误。
/// </para>
/// </remarks>
internal static class ModuleProbeRunner
{
    private const string Source = "cli:probe";

    public static int Run(string packagePath, string command, Assembly identityAssembly, HostOutputFormat format)
    {
        var package = Path.GetFullPath(packagePath);
        var manifestPath = Path.Combine(package, "module.manifest.json");
        if (!File.Exists(manifestPath))
            return Usage($"{package} 不是模块包：缺少 module.manifest.json。", format);

        string name;
        string[] dependsOn;
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            name = manifest.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            dependsOn = manifest.RootElement.TryGetProperty("dependsOn", out var deps) && deps.ValueKind == JsonValueKind.Array
                ? deps.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToArray()
                : [];
        }
        catch (JsonException ex)
        {
            return Usage($"module.manifest.json 不是合法 JSON：{ex.Message}", format);
        }

        if (name.Length == 0)
            return Usage("module.manifest.json 缺少 name。", format);

        var root = Path.Combine(Path.GetTempPath(), "vulcan-probe-" + Guid.NewGuid().ToString("N"));
        var modulesDirectory = Path.Combine(root, "Modules");
        var notes = new List<string>();
        ServiceComposition? composition = null;
        try
        {
            CopyDirectory(package, Path.Combine(modulesDirectory, name));
            var runtime = new AppPaths(AppIdentity.From(identityAssembly).Name, createBusinessDirectories: false).ModulesDir;
            foreach (var dependency in dependsOn)
            {
                var source = Path.Combine(runtime, dependency);
                if (Directory.Exists(source))
                    CopyDirectory(source, Path.Combine(modulesDirectory, dependency));
                else
                    notes.Add($"依赖 {dependency} 不在正式运行区，未复制。");
            }

            composition = ServiceComposer.Build(
                Environment.ProcessPath ?? identityAssembly.Location,
                identityAssembly,
                new ServiceComposer.ProbeRoots(modulesDirectory, Path.Combine(root, "ModuleData")));
            var modules = composition.Modules!;
            modules.EnableFileWatching = false;
            modules.Start();

            var meta = modules.Modules.FirstOrDefault(item =>
                item.ModuleName.Equals(name, StringComparison.OrdinalIgnoreCase));
            notes.AddRange(modules.DiscoveryDiagnostics.Select(item => $"{item.Code}: {item.Message}"));
            var attached = meta is { Attached: true };

            CommandResult? result = null;
            if (attached && command.Length > 0)
                result = composition.Bus.ExecuteAsync(command, Source).GetAwaiter().GetResult();

            var exitCode = !attached ? 1 : result is { Success: false } ? 1 : 0;
            var module = new ProbeModule(
                name,
                meta?.Version ?? "",
                attached,
                meta?.CommandCount ?? 0,
                meta?.AttachFailures ?? ["宿主没有装上这个包（见 diagnostics）"],
                meta?.DataDirectory ?? "");
            Print(format, exitCode, module, notes, result, identityAssembly);
            return exitCode;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Usage($"测试装载失败：{ex.Message}", format, exitCode: 1);
        }
        finally
        {
            composition?.Dispose();
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 刚卸下的程序集可能还没放开文件；临时目录留给系统清理。
            }
        }
    }

    private static void Print(
        HostOutputFormat format, int exitCode, ProbeModule module, List<string> notes, CommandResult? result,
        Assembly identityAssembly)
    {
        if (format == HostOutputFormat.Json)
        {
            var data = new
            {
                module,
                result = result == null ? null : new { success = result.Success, message = result.Message, data = result.Data },
            };
            Console.WriteLine(JsonSerializer.Serialize(
                new CliResultEnvelope(Guid.NewGuid().ToString("N"), exitCode == 0, exitCode, "probe",
                    null, null, null, null, notes, data),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
            return;
        }

        Console.WriteLine($"executionTarget=probe hostVersion={AppIdentity.From(identityAssembly).Version}");
        Console.WriteLine(module.Attached
            ? $"已接上：{module.Name} {module.Version}，{module.CommandCount} 条指令"
            : $"没接上：{module.Name}：{string.Join("；", module.AttachFailures)}");
        foreach (var note in notes)
            Console.WriteLine("  " + note);
        if (result != null)
            Console.WriteLine(result.Message);
    }

    private sealed record ProbeModule(
        string Name, string Version, bool Attached, int CommandCount, IReadOnlyList<string> AttachFailures, string DataDirectory);

    private static int Usage(string message, HostOutputFormat format, int exitCode = 2)
    {
        if (format == HostOutputFormat.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new CliResultEnvelope(Guid.NewGuid().ToString("N"), false, exitCode, "probe", null, null, null, null, [message]),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        else
        {
            Console.Error.WriteLine(message);
        }

        return exitCode;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
