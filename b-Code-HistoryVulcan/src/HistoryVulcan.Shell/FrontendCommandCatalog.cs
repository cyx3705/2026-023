using HistoryVulcan.Core.Commands;
using HistoryVulcan.Shell.Panels;

namespace HistoryVulcan.Shell;

/// <summary>
/// 服务端前端代理的框架目录。描述符由真实 <see cref="BuiltinCommands.Register"/>
/// 装配结果投影，名称分类只在此保留一份。
/// </summary>
public static class FrontendCommandCatalog
{
    public const string Source = "framework:frontend";

    private static readonly Lazy<CatalogSnapshot> FrameworkSnapshot =
        new(CreateFrameworkSnapshot, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<CommandDescriptor> FrameworkSourceDescriptors => FrameworkSnapshot.Value.Frontend;

    /// <summary>Actual desktop registrations for metadata shared with a service host.</summary>
    public static IReadOnlyList<CommandDescriptor> SharedBuiltinSourceDescriptors =>
        FrameworkSnapshot.Value.SharedBuiltins;

    public static IReadOnlyList<CommandDescriptor> CreateFrameworkProxies()
        => FrameworkSnapshot.Value.Frontend.Select(CreateProxy).ToList();

    public static CommandDescriptor CreateProxy(CommandDescriptor source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return FrontendCommandCapability.From(source, Source).CreateProxy();
    }

    private static CatalogSnapshot CreateFrameworkSnapshot()
    {
        var registry = new CommandRegistry();
        BuiltinCommands.Register(registry, new ShellCommandServices
        {
            Window = null!,
            Docking = null!,
            Console = null!,
            History = null!,
            Settings = null!,
            Log = null!,
            Bus = null!,
            DataDirectory = "",
            Panels = new PanelManager(),
        });

        var all = registry.All();
        var frontend = all
            .Where(descriptor => registry.GetSource(descriptor.Name)
                .Equals(Source, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var sharedBuiltins = all
            .Where(descriptor => BuiltinCommandDefinitions.Contains(descriptor.Name))
            .ToList();
        return new CatalogSnapshot(frontend, sharedBuiltins);
    }

    private sealed record CatalogSnapshot(
        IReadOnlyList<CommandDescriptor> Frontend,
        IReadOnlyList<CommandDescriptor> SharedBuiltins);
}
