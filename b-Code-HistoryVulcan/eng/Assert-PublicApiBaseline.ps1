#requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projects = @(
    'HistoryVulcan.Core'
    'HistoryVulcan.Services'
    'HistoryVulcan.ServiceHost'
    'HistoryVulcan.Shell'
)

$componentRoot = Split-Path -Parent $PSScriptRoot
$violations = @()
$versionProps = Get-Content -LiteralPath (Join-Path $componentRoot 'VulcanVersion.props') -Raw -Encoding UTF8
$versionMatch = [regex]::Match($versionProps, '<VulcanVersion>(?<version>[^<]+)</VulcanVersion>')
if (-not $versionMatch.Success) {
    throw 'VulcanVersion.props does not contain VulcanVersion'
}
$version = $versionMatch.Groups['version'].Value
$approved = @{
    'HistoryVulcan.Core' = @()
    'HistoryVulcan.Services' = @()
    'HistoryVulcan.ServiceHost' = @()
    'HistoryVulcan.Shell' = @()
}

if ($version -eq '3.2.1') {
    $approved['HistoryVulcan.Core'] = @(
        'HistoryVulcan.Core.Commands.CommandDescriptor.CommandClass.get -> string?'
        'HistoryVulcan.Core.Commands.CommandDescriptor.CommandClass.init -> void'
        'HistoryVulcan.Core.Commands.CommandDescriptor.Domain.get -> string?'
        'HistoryVulcan.Core.Commands.CommandDescriptor.Domain.init -> void'
        'HistoryVulcan.Core.Commands.CommandRegistry.GetCommandClass(string! name) -> string!'
        'HistoryVulcan.Core.Commands.CommandRegistry.GetDomain(string! name) -> string!'
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.CommandClass.get -> string?'
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.CommandClass.init -> void'
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.Domain.get -> string?'
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.Domain.init -> void'
        'HistoryVulcan.Core.Modules.ModuleCommandAttribute.CommandClass.get -> string?'
        'HistoryVulcan.Core.Modules.ModuleCommandAttribute.CommandClass.init -> void'
    )
    $approved['HistoryVulcan.Shell'] = @(
        'HistoryVulcan.Shell.Mcp.CommandCatalogRow.CommandClass.get -> string!'
        'HistoryVulcan.Shell.Mcp.CommandCatalogRow.CommandClass.init -> void'
    )
}

if ($version -eq '3.2.2') {
    $approved['HistoryVulcan.Core'] = @(
        'HistoryVulcan.Core.Commands.CommandDescriptor.CommandClass.get -> string?'
        'HistoryVulcan.Core.Commands.CommandDescriptor.CommandClass.init -> void'
        'HistoryVulcan.Core.Commands.CommandDescriptor.Domain.get -> string?'
        'HistoryVulcan.Core.Commands.CommandDescriptor.Domain.init -> void'
        'HistoryVulcan.Core.Commands.CommandRegistry.GetCommandClass(string! name) -> string!'
        'HistoryVulcan.Core.Commands.CommandRegistry.GetDomain(string! name) -> string!'
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.CommandClass.get -> string?'
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.CommandClass.init -> void'
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.Domain.get -> string?'
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.Domain.init -> void'
        'HistoryVulcan.Core.Modules.IActivatableToolContent'
        'HistoryVulcan.Core.Modules.IActivatableToolContent.ActivateContent() -> void'
        'HistoryVulcan.Core.Modules.ModuleCommandAttribute.CommandClass.get -> string?'
        'HistoryVulcan.Core.Modules.ModuleCommandAttribute.CommandClass.init -> void'
    )
    $approved['HistoryVulcan.Services'] = @(
        'const HistoryVulcan.Services.Modules.ZModuleDiscoverySource.ManifestFileName = "module.manifest.json" -> string!'
        'const HistoryVulcan.Services.Modules.ZModuleDiscoverySource.ManifestType = "HistoryVulcan.Module" -> string!'
        'HistoryVulcan.Services.Modules.IModuleDiscoverySource'
        'HistoryVulcan.Services.Modules.IModuleDiscoverySource.Discover() -> HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot!'
        'HistoryVulcan.Services.Modules.IModuleDiscoverySource.Roots.get -> System.Collections.Generic.IReadOnlyList<string!>!'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic.Code.get -> string!'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic.Code.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic.Message.get -> string!'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic.Message.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic.ModuleDiscoveryDiagnostic(string! Path, string! Code, string! Message) -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic.Path.get -> string!'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic.Path.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.ArtifactPath.get -> string!'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.ArtifactPath.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.DependencyPaths.get -> System.Collections.Generic.IReadOnlyList<string!>!'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.DependencyPaths.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.DocsPath.get -> string?'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.DocsPath.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.ManifestPath.get -> string!'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.ManifestPath.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.McpExposure.get -> string?'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.McpExposure.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.ModuleDiscoveryEntry(string! Name, string! Version, string! PackagePath, string! ManifestPath, string! ArtifactPath, bool Ui, string? DocsPath, System.Collections.Generic.IReadOnlyList<string!>! DependencyPaths, string? McpExposure) -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.Name.get -> string!'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.Name.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.PackagePath.get -> string!'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.PackagePath.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.Ui.get -> bool'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.Ui.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.Version.get -> string!'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.Version.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot'
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot.Diagnostics.get -> System.Collections.Generic.IReadOnlyList<HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic!>!'
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot.Diagnostics.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot.ModuleDiscoverySnapshot(System.Collections.Generic.IReadOnlyList<string!>! Roots, System.Collections.Generic.IReadOnlyList<HistoryVulcan.Services.Modules.ModuleDiscoveryEntry!>! Modules, System.Collections.Generic.IReadOnlyList<HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic!>! Diagnostics) -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot.Modules.get -> System.Collections.Generic.IReadOnlyList<HistoryVulcan.Services.Modules.ModuleDiscoveryEntry!>!'
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot.Modules.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot.Roots.get -> System.Collections.Generic.IReadOnlyList<string!>!'
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot.Roots.init -> void'
        'HistoryVulcan.Services.Modules.ModuleHost.ChangeDiscoveryRoots(System.Collections.Generic.IEnumerable<string!>! roots) -> void'
        'HistoryVulcan.Services.Modules.ModuleHost.DiscoveryDiagnostics.get -> System.Collections.Generic.IReadOnlyList<HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic!>!'
        'HistoryVulcan.Services.Modules.ModuleHost.DiscoveryRoots.get -> System.Collections.Generic.IReadOnlyList<string!>!'
        'HistoryVulcan.Services.Modules.ModuleHost.ModuleHost(HistoryVulcan.Services.Modules.IModuleDiscoverySource! discoverySource, HistoryVulcan.Core.Logging.IShellLog! log) -> void'
        'HistoryVulcan.Services.Modules.ModuleHost.ReloadConfirmedSources(System.Collections.Generic.IEnumerable<string!>! manifestPaths) -> void'
        'HistoryVulcan.Services.Modules.ModuleHost.RequireConfirmedSources.get -> bool'
        'HistoryVulcan.Services.Modules.ModuleHost.RequireConfirmedSources.set -> void'
        'HistoryVulcan.Services.Modules.ModuleMeta.ManifestPath.get -> string?'
        'HistoryVulcan.Services.Modules.ModuleMeta.ManifestPath.init -> void'
        'HistoryVulcan.Services.Modules.ModuleMeta.SourcePath.get -> string?'
        'HistoryVulcan.Services.Modules.ModuleMeta.SourcePath.init -> void'
        'HistoryVulcan.Services.Modules.ZModuleDiscoverySource'
        'HistoryVulcan.Services.Modules.ZModuleDiscoverySource.Discover() -> HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot!'
        'HistoryVulcan.Services.Modules.ZModuleDiscoverySource.Roots.get -> System.Collections.Generic.IReadOnlyList<string!>!'
        'HistoryVulcan.Services.Modules.ZModuleDiscoverySource.ZModuleDiscoverySource(System.Collections.Generic.IEnumerable<string!>! roots) -> void'
        'static HistoryVulcan.Services.Modules.ZModuleDiscoverySource.FindAutomaticRoot(string! startPath) -> string?'
    )
    $approved['HistoryVulcan.Shell'] = @(
        'const HistoryVulcan.Shell.Modules.ModuleCommands.KeyModuleRoots = "module.roots" -> string!'
        'HistoryVulcan.Shell.Console.ConsoleView.ActivateContent() -> void'
        'HistoryVulcan.Shell.Mcp.CommandCatalogRow.CommandClass.get -> string!'
        'HistoryVulcan.Shell.Mcp.CommandCatalogRow.CommandClass.init -> void'
        'HistoryVulcan.Shell.ShellConfig.ModuleDiscoveryRoots.get -> System.Collections.Generic.List<string!>!'
        'HistoryVulcan.Shell.ShellConfig.RequireConfirmedModuleSources.get -> bool'
        'HistoryVulcan.Shell.ShellConfig.RequireConfirmedModuleSources.set -> void'
    )
}

if ($version -eq '3.3.0') {
    $approved['HistoryVulcan.Core'] = @(
        '﻿#nullable enable',
        'HistoryVulcan.Core.Input.IGlobalShortcutHost',
        'HistoryVulcan.Core.Input.IGlobalShortcutHost.CreateOwnerRegistrar(string! owner) -> HistoryVulcan.Core.Input.IGlobalShortcutRegistrar!',
        'HistoryVulcan.Core.Input.IGlobalShortcutHost.IsEnabled.get -> bool',
        'HistoryVulcan.Core.Input.IGlobalShortcutHost.Register(HistoryVulcan.Core.Input.GlobalShortcutDescriptor! descriptor, string! owner) -> System.IDisposable!',
        'HistoryVulcan.Core.Input.IGlobalShortcutHost.Registrations.get -> System.Collections.Generic.IReadOnlyList<HistoryVulcan.Core.Input.GlobalShortcutRegistrationInfo!>!',
        'HistoryVulcan.Core.Input.IGlobalShortcutHost.Start() -> void',
        'HistoryVulcan.Core.Input.IGlobalShortcutHost.Stop() -> void',
        'HistoryVulcan.Core.Input.IGlobalShortcutHost.UnregisterOwner(string! owner) -> void',
        'HistoryVulcan.Core.Commands.CommandDescriptor.CommandClass.get -> string?',
        'HistoryVulcan.Core.Commands.CommandDescriptor.CommandClass.init -> void',
        'HistoryVulcan.Core.Commands.CommandDescriptor.Domain.get -> string?',
        'HistoryVulcan.Core.Commands.CommandDescriptor.Domain.init -> void',
        'HistoryVulcan.Core.Commands.CommandRegistry.GetCommandClass(string! name) -> string!',
        'HistoryVulcan.Core.Commands.CommandRegistry.GetDomain(string! name) -> string!',
        'static HistoryVulcan.Core.Commands.CommandRegistry.GetMethod(string! name) -> string!',
        'static HistoryVulcan.Core.Commands.CommandRegistry.LegacyClass(string! name) -> string!',
        'static HistoryVulcan.Core.Commands.CommandRegistry.LegacyDomain(string! name) -> string!',
        'static HistoryVulcan.Core.Commands.CommandRegistry.LegacyMethod(string! name) -> string!',
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.CommandClass.get -> string?',
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.CommandClass.init -> void',
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.Domain.get -> string?',
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.Domain.init -> void',
        'HistoryVulcan.Core.Modules.IActivatableToolContent',
        'HistoryVulcan.Core.Modules.IActivatableToolContent.ActivateContent() -> void',
        'HistoryVulcan.Core.Modules.ModuleCommandAttribute.CommandClass.get -> string?',
        'HistoryVulcan.Core.Modules.ModuleCommandAttribute.CommandClass.init -> void',
        'HistoryVulcan.Core.CommandSurface.CommandCatalogChangeKind',
        'HistoryVulcan.Core.CommandSurface.CommandCatalogChangeKind.Filter = 1 -> HistoryVulcan.Core.CommandSurface.CommandCatalogChangeKind',
        'HistoryVulcan.Core.CommandSurface.CommandCatalogChangeKind.Invalidated = 3 -> HistoryVulcan.Core.CommandSurface.CommandCatalogChangeKind',
        'HistoryVulcan.Core.CommandSurface.CommandCatalogChangeKind.Selection = 2 -> HistoryVulcan.Core.CommandSurface.CommandCatalogChangeKind',
        'HistoryVulcan.Core.CommandSurface.CommandCatalogChangeKind.Snapshot = 0 -> HistoryVulcan.Core.CommandSurface.CommandCatalogChangeKind',
        'HistoryVulcan.Core.CommandSurface.CommandCatalogChangedEventArgs',
        'HistoryVulcan.Core.CommandSurface.CommandCatalogChangedEventArgs.CommandCatalogChangedEventArgs(HistoryVulcan.Core.CommandSurface.CommandCatalogChangeKind kind) -> void',
        'HistoryVulcan.Core.CommandSurface.CommandCatalogChangedEventArgs.Kind.get -> HistoryVulcan.Core.CommandSurface.CommandCatalogChangeKind',
        'HistoryVulcan.Core.CommandSurface.CommandCatalogFilter',
        'HistoryVulcan.Core.CommandSurface.CommandCatalogFilter.CommandCatalogFilter(string! Query = "", string! Domain = "全部", string! CommandClass = "全部", int McpFilter = 0) -> void',
        'HistoryVulcan.Core.CommandSurface.CommandCatalogFilter.CommandClass.get -> string!',
        'HistoryVulcan.Core.CommandSurface.CommandCatalogFilter.CommandClass.init -> void',
        'HistoryVulcan.Core.CommandSurface.CommandCatalogFilter.Domain.get -> string!',
        'HistoryVulcan.Core.CommandSurface.CommandCatalogFilter.Domain.init -> void',
        'HistoryVulcan.Core.CommandSurface.CommandCatalogFilter.McpFilter.get -> int',
        'HistoryVulcan.Core.CommandSurface.CommandCatalogFilter.McpFilter.init -> void',
        'HistoryVulcan.Core.CommandSurface.CommandCatalogFilter.Query.get -> string!',
        'HistoryVulcan.Core.CommandSurface.CommandCatalogFilter.Query.init -> void',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionCandidate',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionCandidate.ConsoleCompletionCandidate() -> void',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionCandidate.Description.get -> string!',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionCandidate.Description.init -> void',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionCandidate.DisplayText.get -> string!',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionCandidate.DisplayText.init -> void',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionCandidate.InsertText.get -> string!',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionCandidate.InsertText.init -> void',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionCandidate.Kind.get -> HistoryVulcan.Core.CommandSurface.ConsoleCompletionKind',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionCandidate.Kind.init -> void',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionKind',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionKind.Command = 0 -> HistoryVulcan.Core.CommandSurface.ConsoleCompletionKind',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionKind.Parameter = 1 -> HistoryVulcan.Core.CommandSurface.ConsoleCompletionKind',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionKind.Value = 2 -> HistoryVulcan.Core.CommandSurface.ConsoleCompletionKind',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionResult',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionResult.Candidates.get -> System.Collections.Generic.IReadOnlyList<HistoryVulcan.Core.CommandSurface.ConsoleCompletionCandidate!>!',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionResult.Candidates.init -> void',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionResult.ConsoleCompletionResult() -> void',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionResult.HasCandidates.get -> bool',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionResult.ReplaceLength.get -> int',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionResult.ReplaceLength.init -> void',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionResult.ReplaceStart.get -> int',
        'HistoryVulcan.Core.CommandSurface.ConsoleCompletionResult.ReplaceStart.init -> void',
        'static HistoryVulcan.Core.CommandSurface.ConsoleCompletionResult.Empty.get -> HistoryVulcan.Core.CommandSurface.ConsoleCompletionResult!',
        'HistoryVulcan.Core.CommandSurface.ICommandCatalogSession',
        'HistoryVulcan.Core.CommandSurface.ICommandCatalogSession.Changed -> System.EventHandler<HistoryVulcan.Core.CommandSurface.CommandCatalogChangedEventArgs!>?',
        'HistoryVulcan.Core.CommandSurface.ICommandCatalogSession.Classes.get -> System.Collections.Generic.IReadOnlyList<string!>!',
        'HistoryVulcan.Core.CommandSurface.ICommandCatalogSession.CompleteAsync(string! text, int caretIndex, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<HistoryVulcan.Core.CommandSurface.ConsoleCompletionResult!>!',
        'HistoryVulcan.Core.CommandSurface.ICommandCatalogSession.CurrentFilter.get -> HistoryVulcan.Core.CommandSurface.CommandCatalogFilter!',
        'HistoryVulcan.Core.CommandSurface.ICommandCatalogSession.Domains.get -> System.Collections.Generic.IReadOnlyList<string!>!',
        'HistoryVulcan.Core.CommandSurface.ICommandCatalogSession.MoveSelection(int direction) -> bool',
        'HistoryVulcan.Core.CommandSurface.ICommandCatalogSession.RefreshAsync(bool force = false, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<bool>!',
        'HistoryVulcan.Core.CommandSurface.ICommandCatalogSession.Select(string? commandName) -> void',
        'HistoryVulcan.Core.CommandSurface.ICommandCatalogSession.SelectedCommandName.get -> string?',
        'HistoryVulcan.Core.CommandSurface.ICommandCatalogSession.SetConsoleQuery(string! query) -> void',
        'HistoryVulcan.Core.CommandSurface.ICommandCatalogSession.SetFilter(HistoryVulcan.Core.CommandSurface.CommandCatalogFilter! filter) -> void',
        'HistoryVulcan.Core.CommandSurface.ICommandCatalogSession.TrySetCommandClass(string! commandClass, out System.Collections.Generic.IReadOnlyList<string!>! availableClasses) -> bool',
        'HistoryVulcan.Core.CommandSurface.ICommandCatalogSession.TrySetDomain(string! domain, out System.Collections.Generic.IReadOnlyList<string!>! availableDomains) -> bool',
        'HistoryVulcan.Core.Modules.IShellCommandWorkbenchAware',
        'HistoryVulcan.Core.Modules.IShellCommandWorkbenchAware.CommandWorkbench.set -> void',
        'HistoryVulcan.Core.Modules.IShellCommandWorkbenchHost',
        'HistoryVulcan.Core.Modules.IShellCommandWorkbenchHost.AttachCommandCatalogSession(HistoryVulcan.Core.CommandSurface.ICommandCatalogSession! session) -> void',
        'HistoryVulcan.Core.Modules.IShellCommandWorkbenchHost.Bus.get -> HistoryVulcan.Core.Commands.CommandBus!',
        'HistoryVulcan.Core.Modules.IShellCommandWorkbenchHost.CommandSelection.get -> HistoryVulcan.Core.Commands.CommandSelectionState!',
        'HistoryVulcan.Core.Modules.IShellCommandWorkbenchHost.ConfigureCommandCompletionRouting(System.Func<bool>! isConsoleFocused, System.Action! showCommandCatalog) -> void',
        'HistoryVulcan.Core.Modules.IShellCommandWorkbenchHost.DataDirectory.get -> string!',
        'HistoryVulcan.Core.Modules.IShellCommandWorkbenchHost.Log.get -> HistoryVulcan.Core.Logging.IShellLog!',
        'HistoryVulcan.Core.Modules.IShellCommandWorkbenchHost.RefreshCommandCompletionFocus() -> void',
        'HistoryVulcan.Core.Modules.IShellCommandWorkbenchHost.Settings.get -> HistoryVulcan.Core.Storage.ISettingsService!'
    )
    $approved['HistoryVulcan.Services'] = @(
        'const HistoryVulcan.Services.Modules.ZModuleDiscoverySource.ManifestFileName = "module.manifest.json" -> string!',
        'const HistoryVulcan.Services.Modules.ZModuleDiscoverySource.ManifestType = "HistoryVulcan.Module" -> string!',
        'HistoryVulcan.Services.Modules.IModuleDiscoverySource',
        'HistoryVulcan.Services.Modules.IModuleDiscoverySource.Discover() -> HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot!',
        'HistoryVulcan.Services.Modules.IModuleDiscoverySource.Roots.get -> System.Collections.Generic.IReadOnlyList<string!>!',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic.Code.get -> string!',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic.Code.init -> void',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic.Message.get -> string!',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic.Message.init -> void',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic.ModuleDiscoveryDiagnostic(string! Path, string! Code, string! Message) -> void',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic.Path.get -> string!',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic.Path.init -> void',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.ArtifactPath.get -> string!',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.ArtifactPath.init -> void',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.DependencyPaths.get -> System.Collections.Generic.IReadOnlyList<string!>!',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.DependencyPaths.init -> void',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.DocsPath.get -> string?',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.DocsPath.init -> void',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.ManifestPath.get -> string!',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.ManifestPath.init -> void',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.McpExposure.get -> string?',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.McpExposure.init -> void',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.ModuleDiscoveryEntry(string! Name, string! Version, string! PackagePath, string! ManifestPath, string! ArtifactPath, bool Ui, string? DocsPath, System.Collections.Generic.IReadOnlyList<string!>! DependencyPaths, string? McpExposure) -> void',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.Name.get -> string!',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.Name.init -> void',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.PackagePath.get -> string!',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.PackagePath.init -> void',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.Ui.get -> bool',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.Ui.init -> void',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.Version.get -> string!',
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.Version.init -> void',
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot',
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot.Diagnostics.get -> System.Collections.Generic.IReadOnlyList<HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic!>!',
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot.Diagnostics.init -> void',
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot.ModuleDiscoverySnapshot(System.Collections.Generic.IReadOnlyList<string!>! Roots, System.Collections.Generic.IReadOnlyList<HistoryVulcan.Services.Modules.ModuleDiscoveryEntry!>! Modules, System.Collections.Generic.IReadOnlyList<HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic!>! Diagnostics) -> void',
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot.Modules.get -> System.Collections.Generic.IReadOnlyList<HistoryVulcan.Services.Modules.ModuleDiscoveryEntry!>!',
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot.Modules.init -> void',
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot.Roots.get -> System.Collections.Generic.IReadOnlyList<string!>!',
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot.Roots.init -> void',
        'HistoryVulcan.Services.Modules.ModuleHost.ChangeDiscoveryRoots(System.Collections.Generic.IEnumerable<string!>! roots) -> void',
        'HistoryVulcan.Services.Modules.ModuleHost.DiscoveryDiagnostics.get -> System.Collections.Generic.IReadOnlyList<HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic!>!',
        'HistoryVulcan.Services.Modules.ModuleHost.DiscoveryRoots.get -> System.Collections.Generic.IReadOnlyList<string!>!',
        'HistoryVulcan.Services.Modules.ModuleHost.ModuleHost(HistoryVulcan.Services.Modules.IModuleDiscoverySource! discoverySource, HistoryVulcan.Core.Logging.IShellLog! log) -> void',
        'HistoryVulcan.Services.Modules.ModuleHost.ReloadConfirmedSources(System.Collections.Generic.IEnumerable<string!>! manifestPaths) -> void',
        'HistoryVulcan.Services.Modules.ModuleHost.RequireConfirmedSources.get -> bool',
        'HistoryVulcan.Services.Modules.ModuleHost.RequireConfirmedSources.set -> void',
        'HistoryVulcan.Services.Modules.ModuleHost.CommandWorkbench.get -> HistoryVulcan.Core.Modules.IShellCommandWorkbenchHost?',
        'HistoryVulcan.Services.Modules.ModuleHost.CommandWorkbench.set -> void',
        'HistoryVulcan.Services.Modules.ModuleMeta.ManifestPath.get -> string?',
        'HistoryVulcan.Services.Modules.ModuleMeta.ManifestPath.init -> void',
        'HistoryVulcan.Services.Modules.ModuleMeta.SourcePath.get -> string?',
        'HistoryVulcan.Services.Modules.ModuleMeta.SourcePath.init -> void',
        'HistoryVulcan.Services.Modules.ZModuleDiscoverySource',
        'HistoryVulcan.Services.Modules.ZModuleDiscoverySource.Discover() -> HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot!',
        'HistoryVulcan.Services.Modules.ZModuleDiscoverySource.Roots.get -> System.Collections.Generic.IReadOnlyList<string!>!',
        'HistoryVulcan.Services.Modules.ZModuleDiscoverySource.ZModuleDiscoverySource(System.Collections.Generic.IEnumerable<string!>! roots) -> void',
        'static HistoryVulcan.Services.Modules.ZModuleDiscoverySource.FindAutomaticRoot(string! startPath) -> string?'
    )
    $approved['HistoryVulcan.ServiceHost'] = @(
    )
    $approved['HistoryVulcan.Shell'] = @(
        'const HistoryVulcan.Shell.Modules.ModuleCommands.KeyModuleRoots = "module.roots" -> string!',
        'HistoryVulcan.Shell.Console.ConsoleView.ActivateContent() -> void',
        'HistoryVulcan.Shell.Mcp.CommandCatalogRow.CommandClass.get -> string!',
        'HistoryVulcan.Shell.Mcp.CommandCatalogRow.CommandClass.init -> void',
        'HistoryVulcan.Shell.Mcp.CommandCatalogRow.Method.get -> string!',
        'HistoryVulcan.Shell.Mcp.CommandCatalogRow.Method.init -> void',
        'HistoryVulcan.Shell.ShellConfig.ModuleDiscoveryRoots.get -> System.Collections.Generic.List<string!>!',
        'HistoryVulcan.Shell.ShellConfig.RequireConfirmedModuleSources.get -> bool',
        'HistoryVulcan.Shell.ShellConfig.RequireConfirmedModuleSources.set -> void',
        'HistoryVulcan.Shell.ShellWindow.AttachCommandCatalogSession(HistoryVulcan.Core.CommandSurface.ICommandCatalogSession! session) -> void',
        'HistoryVulcan.Shell.ShellWindow.ConfigureCommandCompletionRouting(System.Func<bool>! isConsoleFocused, System.Action! showCommandCatalog) -> void',
        'HistoryVulcan.Shell.ShellWindow.RefreshCommandCompletionFocus() -> void'
    )
}


# Version-approved baselines live in public-api-baselines\<version>\. Before 3.3.2 this lookup was
# hard-coded to '3.3.0', so every later version silently fell back to the stale in-script 3.2.x lists
# and the gate could never pass. Resolve by the actual VulcanVersion instead (DEC-023).
$baselineDir = Join-Path $PSScriptRoot "public-api-baselines\$version"
if (-not (Test-Path -LiteralPath $baselineDir -PathType Container)) {
    throw "Missing approved Unshipped baseline directory for $version : $baselineDir"
}

foreach ($project in $projects) {
    $baselinePath = Join-Path $baselineDir "$project.Unshipped.txt"
    if (-not (Test-Path -LiteralPath $baselinePath -PathType Leaf)) {
        throw "Missing approved Unshipped baseline: $baselinePath"
    }
    $approved[$project] = @(
        [System.IO.File]::ReadAllLines($baselinePath, [System.Text.UTF8Encoding]::new($false)) |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ -ne '' }
    )
}

foreach ($project in $projects) {
    $path = Join-Path $componentRoot "src\$project\PublicAPI.Unshipped.txt"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $violations += "$project : PublicAPI.Unshipped.txt missing"
        continue
    }

    $entries = @(
        [System.IO.File]::ReadAllLines($path, [System.Text.UTF8Encoding]::new($false)) |
            ForEach-Object { $_.Trim().TrimStart([char]0xFEFF) } |
            Where-Object { $_ -ne '' -and $_ -ne '#nullable enable' }
    )

    $difference = @(Compare-Object -ReferenceObject @($approved[$project]) -DifferenceObject $entries)
    if ($difference.Count -ne 0) {
        $violations += "$project : unshipped API differs from approved $version baseline -> $($difference -join '; ')"
    }
}

if ($violations.Count -ne 0) {
    Write-Host 'Public API freeze gate failed:' -ForegroundColor Red
    $violations | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'Public API changes must exactly match the version-approved Unshipped baseline.'
    exit 1
}

Write-Host "Public API baseline gate passed: $($projects.Count) package Unshipped files match the approved $version baseline."
