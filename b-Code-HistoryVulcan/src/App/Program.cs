using System.Reflection;
using HistoryVulcan.ServiceHost;

namespace HistoryVulcan.App;

internal static class Program
{
    /// <summary>
    /// 供 <see cref="ServiceComposer"/> 确定 <c>AppIdentity</c> 的程序集。
    ///
    /// 4.0.0（REQ-A3）起服务组合根住在 ServiceHost，那边取不到 WPF 的 <c>App</c> 类型，
    /// 因此由入口点显式传入。A4 把服务拆成独立 exe 后，这里换成那个 exe 的程序集即可，
    /// 身份（数据根目录名、端口派生、服务名）随之保持一致。
    /// </summary>
    private static Assembly IdentityAssembly => typeof(Program).Assembly;

    /// <summary>
    /// 入口只做分派：参数判定在 <see cref="HostArgumentParser"/> 里，那是一个纯函数，
    /// 因此「不认识的参数不许起服务」这条能被单独测到，而不必靠拉起真实进程来验。
    /// </summary>
    private static int Main(string[] args)
    {
        var parsed = HostArgumentParser.Parse(args);
        switch (parsed.Action)
        {
            case HostAction.ExportManual:
                return ServiceComposer.ExportCommandManual(parsed.Value, IdentityAssembly);

            case HostAction.InstallModule:
                return InstallModule(parsed.Value);

            case HostAction.RunCommand:
                return CommandLineRunner.Run(parsed.Value, IdentityAssembly);

            case HostAction.RepairAutostart:
                return ServiceComposer.RepairAutostart(RequireExecutablePath(), IdentityAssembly);

            case HostAction.Error:
                Console.Error.WriteLine(parsed.Error);
                foreach (var line in HostArgumentParser.UsageLines)
                    Console.Error.WriteLine(line);
                return 2;

            default:
                var servicePath = RequireExecutablePath();
                return global::HistoryVulcan.ServiceHost.ServiceHost.Run(
                    ServiceComposer.Build(servicePath, IdentityAssembly),
                    servicePath,
                    [HostArgumentParser.LegacyServiceSwitch]);
        }
    }

    /// <summary>
    /// 离线安装：与 <see cref="ServiceComposer.Build"/> 取同一个模块槽，但**刻意不复用它**。
    ///
    /// Build 会装配整个宿主，而本开关存在的意义正是「装配路径坏掉时还能换包」——
    /// 让它经过 Build，就等于把恢复通道建在被恢复的东西上面。
    /// </summary>
    private static int InstallModule(string packagePath)
    {
        HistoryVulcan.Core.AppIdentity.Use(IdentityAssembly);
        var paths = new HistoryVulcan.Services.AppPaths(HistoryVulcan.Core.AppIdentity.Current.Name);
        var result = HistoryVulcan.Services.Modules.OfflineModuleInstall.Install(
            paths.ModulesDir, packagePath);

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
