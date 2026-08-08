using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;

namespace HistoryVulcan.Core.Modules;

/// <summary>Host-owned runtime services made available to an HistoryVulcan module.</summary>
public interface IModuleContext
{
    /// <summary>The single command bus owned by the current host process.</summary>
    CommandBus Bus { get; }

    /// <summary>The settings store owned by the current host process.</summary>
    ISettingsService Settings { get; }

    /// <summary>The log owned by the current host process.</summary>
    IShellLog Log { get; }

    /// <summary>The data root owned by the current host process.</summary>
    string DataDirectory { get; }

    /// <summary>
    /// Stages module commands for the current module snapshot. The supplied registry is isolated
    /// from the live host registry; the host commits and removes its contents with the module.
    /// </summary>
    void RegisterCommands(Action<CommandRegistry> configure);
}

/// <summary>Implemented by modules that consume host-owned runtime services.</summary>
public interface IModuleContextAware
{
    /// <summary>Attaches the current host context before commands and UI are activated.</summary>
    void Attach(IModuleContext context);
}
