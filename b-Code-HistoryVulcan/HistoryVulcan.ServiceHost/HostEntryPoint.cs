using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HistoryVulcan.Services;
using HistoryVulcan.Services.Modules;

namespace HistoryVulcan.ServiceHost;

/// <summary>GUI 与 Console 入口共用的参数分派器。</summary>
public static class HostEntryPoint
{
    public static int Run(string[] args, Assembly identityAssembly)
    {
        var parsed = HostArgumentParser.Parse(args);
        if (IsGuiWithoutConsole(identityAssembly, parsed.Action))
            return RunToReleaseLog(parsed, identityAssembly);

        return RunParsed(parsed, identityAssembly);
    }

    private static int RunParsed(HostArguments parsed, Assembly identityAssembly)
    {
        switch (parsed.Action)
        {
            case HostAction.ExportManual:
                return ServiceComposer.ExportCommandManual(parsed.Value, identityAssembly);
            case HostAction.InstallModule:
                return InstallModule(parsed.Value, identityAssembly);
            case HostAction.RunCommand:
                return CommandLineRunner.Run(parsed.Value, identityAssembly, parsed.Format);
            case HostAction.RunRuntime:
                return RuntimeCommandClient.Run(parsed.Value, identityAssembly, parsed.Format, parsed.Approve);
            case HostAction.Help:
                if (parsed.Format == HostOutputFormat.Json)
                    WriteJsonEnvelope(true, 0, "offline-composition", HostArgumentParser.UsageLines);
                else
                    foreach (var line in HostArgumentParser.UsageLines)
                        Console.WriteLine(line);
                return 0;
            case HostAction.Version:
                var version = HistoryVulcan.Core.AppIdentity.From(identityAssembly).Version;
                if (parsed.Format == HostOutputFormat.Json)
                    WriteJsonEnvelope(true, 0, "offline-composition", [version]);
                else
                    Console.WriteLine(version);
                return 0;
            case HostAction.RepairAutostart:
                return ServiceComposer.RepairAutostart(RequireExecutablePath(), identityAssembly);
            case HostAction.Error:
                if (parsed.Format == HostOutputFormat.Json)
                    WriteJsonEnvelope(false, 2, "offline-composition", [parsed.Error]);
                else
                {
                    Console.Error.WriteLine(parsed.Error);
                    foreach (var line in HostArgumentParser.UsageLines)
                        Console.Error.WriteLine(line);
                }
                return 2;
            default:
                var servicePath = RequireExecutablePath();
                return ServiceHost.Run(
                    ServiceComposer.Build(servicePath, identityAssembly),
                    servicePath,
                    [HostArgumentParser.LegacyServiceSwitch]);
        }
    }

    private static int RunToReleaseLog(HostArguments parsed, Assembly identityAssembly)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter(new StringBuilder());
        var error = new StringWriter(new StringBuilder());
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = RunParsed(parsed, identityAssembly);
            var paths = new AppPaths(HistoryVulcan.Core.AppIdentity.From(identityAssembly).Name);
            var releaseDirectory = Path.Combine(paths.Root, "release");
            Directory.CreateDirectory(releaseDirectory);
            var runId = Guid.NewGuid().ToString("N");
            var logPath = Path.Combine(releaseDirectory, $"{runId}.log");
            var contents = output.ToString();
            if (error.GetStringBuilder().Length > 0)
                contents += Environment.NewLine + error;
            contents += Environment.NewLine + $"logPath={logPath}{Environment.NewLine}exitCode={exitCode}{Environment.NewLine}";
            File.WriteAllText(logPath, contents, new UTF8Encoding(false));
            return exitCode;
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            output.Dispose();
            error.Dispose();
        }
    }

    private static bool IsGuiWithoutConsole(Assembly identityAssembly, HostAction action)
        => string.Equals(identityAssembly.GetName().Name, "HistoryVulcan", StringComparison.OrdinalIgnoreCase)
           && action != HostAction.RunService
           && OperatingSystem.IsWindows()
           && GetConsoleWindow() == IntPtr.Zero;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    private static void WriteJsonEnvelope(bool success, int exitCode, string target, IReadOnlyList<string> diagnostics)
        => Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
            new CliResultEnvelope(Guid.NewGuid().ToString("N"), success, exitCode, target,
                null, null, null, null, diagnostics),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

    private static int InstallModule(string packagePath, Assembly identityAssembly)
    {
        HistoryVulcan.Core.AppIdentity.Use(identityAssembly);
        var paths = new AppPaths(HistoryVulcan.Core.AppIdentity.Current.Name);
        var result = OfflineModuleInstall.Install(paths.ModulesDir, packagePath);
        if (result.ExitCode == 0)
            Console.WriteLine(result.Message);
        else
            Console.Error.WriteLine(result.Message);
        return result.ExitCode;
    }

    private static string RequireExecutablePath()
        => Environment.ProcessPath
           ?? throw new InvalidOperationException("无法确定 HistoryVulcan 可执行文件路径");
}
