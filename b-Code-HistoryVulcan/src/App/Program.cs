using System.Windows;
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

    [STAThread]
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

            return App.ExportCommandManual(args[exportIndex + 1]);
        }

        if (args.Any(argument => argument.Equals("--service", StringComparison.OrdinalIgnoreCase)))
        {
            var executable = Environment.ProcessPath
                             ?? throw new InvalidOperationException("无法确定 HistoryVulcan 可执行文件路径");
            return global::HistoryVulcan.ServiceHost.ServiceHost.Run(
                App.BuildServiceComposition(executable),
                executable,
                ["--service"]);
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
        return 0;
    }
}
