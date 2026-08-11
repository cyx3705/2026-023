using System.Reflection;
using HistoryVulcan.Core;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Mcp;
using HistoryVulcan.Extensibility.Mcp;
using HistoryVulcan.Services;
using HistoryVulcan.ServiceHost;
using HistoryVulcan.Shell;

var smokeAssembly = Assembly.GetExecutingAssembly();
var smokeIdentity = AppIdentity.From(smokeAssembly);
var expected = smokeAssembly.GetName().Version
               ?? throw new InvalidOperationException("PackageSmoke assembly version is missing");
var assemblies = new[]
{
    typeof(CommandBus).Assembly,
    typeof(SettingsService).Assembly,
    typeof(ShellWindow).Assembly,
    typeof(ServiceComposition).Assembly,
};

foreach (var assembly in assemblies)
{
    var actual = assembly.GetName().Version;
    if (actual != expected)
        throw new InvalidOperationException($"{assembly.GetName().Name} version {actual}, expected {expected}");
}

var config = new ShellConfig
{
    AppName = "HistoryVulcanPackageSmoke",
    AppVersion = smokeIdentity.Version,
};

if (config.AppVersion != smokeIdentity.Version
    || config.EnableModules
    || config.EnableUiModules
    || config.EnableMcp
    || config.EnableRemoteManagementViews)
    throw new InvalidOperationException("ShellConfig package surface is not usable");

AppIdentity.Use(smokeAssembly);
var registry = new CommandRegistry();
registry.Register(new CommandDescriptor
{
    Name = "smoke.ping",
    Summary = "Package smoke command",
    Readonly = true,
    Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("pong")),
});
var manual = CommandManualGenerator.Render(registry, new CommandSchemaExporter(registry), "readonly");
if (!manual.StartsWith("# HistoryVulcanPackageSmoke 命令手册", StringComparison.Ordinal)
    || manual.Contains("HistoryJanus 命令手册", StringComparison.Ordinal)
    || manual.Contains("b-Office/", StringComparison.Ordinal))
{
    throw new InvalidOperationException("Command manual still leaks a derived application identity or path");
}

Console.WriteLine("PackageSmoke: PASS");
Console.WriteLine(string.Join(Environment.NewLine,
    assemblies.Select(assembly => $"{assembly.GetName().Name} {assembly.GetName().Version}")));
