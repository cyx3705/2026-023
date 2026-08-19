using System.Reflection;
using HistoryVulcan.ServiceHost;

namespace HistoryVulcan.App;

internal static class Program
{
    /// <summary>
    /// 无头导出命令手册的开关。
    ///
    /// 为什么不复用 <c>vulcan.command.manual</c>：那条命令带本地二次确认闸口，
    /// 无人值守的发布管线跑不了，而为它开一个"跳过确认"的口子会削弱确认语义本身。
    /// 命令手册是发布产物而非运行时操作，用显式的 CLI 入口更诚实。
    /// </summary>
    private const string ExportManualSwitch = "--export-command-manual";
    private const string RepairAutostartSwitch = "--repair-autostart";

    /// <summary>
    /// 供 <see cref="ServiceComposer"/> 确定 <c>AppIdentity</c> 的程序集。
    ///
    /// 4.0.0（REQ-A3）起服务组合根住在 ServiceHost，那边取不到 WPF 的 <c>App</c> 类型，
    /// 因此由入口点显式传入。A4 把服务拆成独立 exe 后，这里换成那个 exe 的程序集即可，
    /// 身份（数据根目录名、端口派生、服务名）随之保持一致。
    /// </summary>
    private static Assembly IdentityAssembly => typeof(Program).Assembly;

    private static int Main(string[] args)
    {
        var exportIndex = Array.FindIndex(
            args, argument => argument.Equals(ExportManualSwitch, StringComparison.OrdinalIgnoreCase));
        if (exportIndex >= 0)
        {
            if (exportIndex + 1 >= args.Length)
            {
                Console.Error.WriteLine($"{ExportManualSwitch} 需要一个输出路径参数。");
                return 2;
            }

            return ServiceComposer.ExportCommandManual(args[exportIndex + 1], IdentityAssembly);
        }

        if (args.Any(argument => argument.Equals(RepairAutostartSwitch, StringComparison.OrdinalIgnoreCase)))
        {
            var executable = Environment.ProcessPath
                             ?? throw new InvalidOperationException("无法确定 HistoryVulcan 可执行文件路径");
            return ServiceComposer.RepairAutostart(executable, IdentityAssembly);
        }

        // REQ-A8：宿主只剩服务这一个角色。
        //
        // 迁出前 `--service` 是「双角色 exe」的分支开关：不带它就起 WPF 前端。
        // 前端已整体成为 HistoryAurora.exe（REQ-A7），此处不再有第二条路径，
        // 因此该参数退化为**兼容开关**——自启动项、既有快捷方式和 `vulcan.svc.restart`
        // 都还带着它，静默忽略即可，报错只会让升级过程平白失败。
        var servicePath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("无法确定 HistoryVulcan 可执行文件路径");
        return global::HistoryVulcan.ServiceHost.ServiceHost.Run(
            ServiceComposer.Build(servicePath, IdentityAssembly),
            servicePath,
            ["--service"]);
    }
}
