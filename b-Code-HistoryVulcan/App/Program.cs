using System.Reflection;
using HistoryVulcan.ServiceHost;

namespace HistoryVulcan.App;

internal static class Program
{
    /// <summary>
    /// 供 <see cref="ServiceComposer"/> 确定 <c>AppIdentity</c> 的程序集。
    ///
    /// 4.0.0起服务组合根住在 ServiceHost，那边取不到 WPF 的 <c>App</c> 类型，
    /// 因此由入口点显式传入。A4 把服务拆成独立 exe 后，这里换成那个 exe 的程序集即可，
    /// 身份（数据根目录名、端口派生、服务名）随之保持一致。
    /// </summary>
    private static Assembly IdentityAssembly => typeof(Program).Assembly;

    /// <summary>
    /// 入口只做分派：参数判定在 <see cref="HostArgumentParser"/> 里，那是一个纯函数，
    /// 因此「不认识的参数不许起服务」这条能被单独测到，而不必靠拉起真实进程来验。
    /// </summary>
    private static int Main(string[] args)
        => HostEntryPoint.Run(args, IdentityAssembly);
}
