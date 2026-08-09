using HistoryVulcan.Core.Commands;
using HistoryVulcan.Services.Modules;
using HistoryVulcan.Shell.Mcp;

namespace HistoryVulcan.Shell.Views;

public sealed record ModuleCommandInfo(
    string Name,
    string Summary,
    string Example,
    string Source,
    string? SourceDetail);

public sealed class ModuleCatalogSnapshot
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ModuleCommandInfo>> _commandsByModule;

    private ModuleCatalogSnapshot(
        IReadOnlyList<ModuleMeta> modules,
        IReadOnlyList<ModuleCommandInfo> commands,
        IReadOnlyDictionary<string, IReadOnlyList<ModuleCommandInfo>> commandsByModule)
    {
        Modules = modules;
        Commands = commands;
        _commandsByModule = commandsByModule;
    }

    public IReadOnlyList<ModuleMeta> Modules { get; }

    public IReadOnlyList<ModuleCommandInfo> Commands { get; }

    internal static ModuleCatalogSnapshot FromModules(IReadOnlyList<ModuleMeta> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var copy = modules.ToList();
        var names = new HashSet<string>(copy.Select(module => module.ModuleName), StringComparer.OrdinalIgnoreCase);
        if (names.Count != copy.Count || copy.Any(module => string.IsNullOrWhiteSpace(module.ModuleName)))
            throw new InvalidOperationException("模块目录包含空名称或重复名称");
        return new ModuleCatalogSnapshot(copy, [], new Dictionary<string, IReadOnlyList<ModuleCommandInfo>>(
            StringComparer.OrdinalIgnoreCase));
    }

    public IReadOnlyList<ModuleCommandInfo> CommandsFor(string moduleName)
        => _commandsByModule.GetValueOrDefault(moduleName) ?? [];

    public static bool TryCreate(
        IReadOnlyList<ModuleMeta> modules,
        IEnumerable<ModuleCommandInfo> commands,
        out ModuleCatalogSnapshot? snapshot,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(commands);

        var moduleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules)
        {
            if (string.IsNullOrWhiteSpace(module.ModuleName) || !moduleNames.Add(module.ModuleName))
            {
                snapshot = null;
                error = $"模块目录包含空名称或重复名称: {module.ModuleName}";
                return false;
            }
        }

        var moduleCommands = commands
            .Where(command => command.Source.Equals("module", StringComparison.OrdinalIgnoreCase)
                              && !string.IsNullOrWhiteSpace(command.SourceDetail))
            .OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var duplicate = moduleCommands
            .GroupBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
        {
            snapshot = null;
            error = $"命令目录包含重复模块指令: {duplicate.Key}";
            return false;
        }

        var grouped = moduleCommands
            .GroupBy(command => command.SourceDetail!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ModuleCommandInfo>)group.ToList(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules)
        {
            var actual = grouped.GetValueOrDefault(module.ModuleName)?.Count ?? 0;
            if (actual != module.CommandCount)
            {
                snapshot = null;
                error = $"模块目录在刷新期间发生变化: {module.ModuleName} 摘要={module.CommandCount},目录={actual}";
                return false;
            }
        }

        var unknownModule = grouped.Keys.FirstOrDefault(name => !moduleNames.Contains(name));
        if (unknownModule != null)
        {
            snapshot = null;
            error = $"命令目录包含未出现在模块清单中的来源: {unknownModule}";
            return false;
        }

        snapshot = new ModuleCatalogSnapshot(modules.ToList(), moduleCommands, grouped);
        error = "";
        return true;
    }
}

public sealed record ModuleCatalogLoadResult(
    bool Success,
    string Message,
    ModuleCatalogSnapshot? Snapshot);

public static class ModuleCatalogReader
{
    /// <summary>
    /// 只读取模块清单供模块管理页使用。模块页不依赖 vulcan.command.list，避免命令目录修订期间
    /// 因计数短暂不一致而隐藏已经成功装载的模块。
    /// </summary>
    internal static async Task<ModuleCatalogLoadResult> LoadModulesAsync(
        CommandBus bus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bus);

        var moduleResult = await bus.ExecuteAsync("vulcan.module.list", "UI", cancellationToken);
        if (!moduleResult.Success)
            return new(false, $"模块清单加载失败: {FirstLine(moduleResult.Message)}", null);
        if (!CommandResultData.TryRead<IReadOnlyList<ModuleMeta>>(moduleResult.Data, out var modules))
            return new(false, "模块清单返回了无法识别的数据", null);

        try
        {
            return new(true, moduleResult.Message, ModuleCatalogSnapshot.FromModules(modules));
        }
        catch (InvalidOperationException ex)
        {
            return new(false, ex.Message, null);
        }
    }

    public static async Task<ModuleCatalogLoadResult> LoadAsync(
        CommandBus bus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bus);

        var moduleResult = await bus.ExecuteAsync("vulcan.module.list", "UI", cancellationToken);
        if (!moduleResult.Success)
            return new(false, $"模块清单加载失败: {FirstLine(moduleResult.Message)}", null);
        if (!CommandResultData.TryRead<IReadOnlyList<ModuleMeta>>(moduleResult.Data, out var modules))
            return new(false, "模块清单返回了无法识别的数据", null);

        IReadOnlyList<ModuleCommandInfo> commands;
        if (bus.RemoteExecutor == null && !bus.Registry.TryGet("vulcan.command.list", out _))
        {
            commands = ReadLocalCommands(bus.Registry);
        }
        else
        {
            var commandResult = await bus.ExecuteAsync("vulcan.command.list", "UI", cancellationToken);
            if (!commandResult.Success)
                return new(false, $"命令目录加载失败: {FirstLine(commandResult.Message)}", null);
            if (!CommandResultData.TryRead<IReadOnlyList<CommandCatalogRow>>(commandResult.Data, out var rows))
                return new(false, "命令目录返回了无法识别的数据", null);
            commands = rows.Select(row => new ModuleCommandInfo(
                row.CommandName,
                row.Summary,
                row.Example ?? "",
                row.Source,
                row.SourceDetail)).ToList();
        }

        if (!ModuleCatalogSnapshot.TryCreate(modules, commands, out var snapshot, out var error))
            return new(false, error, null);
        return new(true, moduleResult.Message, snapshot);
    }

    private static IReadOnlyList<ModuleCommandInfo> ReadLocalCommands(CommandRegistry registry)
        => registry.All().Select(descriptor =>
        {
            var rawSource = registry.GetSource(descriptor.Name);
            var module = rawSource.StartsWith("module:", StringComparison.OrdinalIgnoreCase);
            return new ModuleCommandInfo(
                descriptor.Name,
                descriptor.Summary,
                descriptor.Example ?? "",
                module ? "module" : rawSource,
                module ? rawSource["module:".Length..] : null);
        }).ToList();

    private static string FirstLine(string value)
        => value.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? value;
}
