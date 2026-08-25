using System.Reflection;
using HistoryVulcan.ServiceHost;

namespace HistoryVulcan.Cli;

internal static class Program
{
    private static Assembly IdentityAssembly => typeof(Program).Assembly;

    private static int Main(string[] args)
        => HostEntryPoint.Run(args, IdentityAssembly);
}
