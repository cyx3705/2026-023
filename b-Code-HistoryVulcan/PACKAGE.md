# OneHistory HistoryVulcan

HistoryVulcan is a .NET 8 desktop application framework for Windows. It provides a command bus,
WPF shell and docking UI, local services, module hosting, and an MCP gateway.

## Packages

- `OneHistory.HistoryVulcan.Core`: framework contracts and command/MCP metadata.
- `OneHistory.HistoryVulcan.Services`: settings, logging, file-backed state, modules, and MCP services.
- `OneHistory.HistoryVulcan.Shell`: the WPF application shell. Referencing this package brings in Core and Services.
- `OneHistory.HistoryVulcan.ServiceHost`: headless WPF service lifecycle, confirmation, and `svc.*` hosting.

## Install

```xml
<PackageReference Include="OneHistory.HistoryVulcan.Shell" Version="3.3.0" />
<PackageReference Include="OneHistory.HistoryVulcan.ServiceHost" Version="3.3.0" />
```

The references above describe the 3.3.0 source contract. The supported delivery is the formal
`z-HistoryVulcan/host` application snapshot; NuGet generation remains a compatibility-only workflow.
HistoryVulcan targets .NET 8.
The Shell and ServiceHost packages require Windows and WPF. Packages in this
repository-local feed are for OneHistory-owned projects; no public distribution license is granted
by the package itself.

The current z-level release snapshot includes `HistoryVulcan.reuse.md` beside the feed directory and the
version-matched consumer contracts under `docs/`. Consumers and AI tools should start with the reuse
document and follow its links to the API/command, module/MCP, runtime-limit, and change-summary contracts.
Release evidence and maintenance documents remain outside the runtime packages. XML API documentation
stays beside each assembly under `lib/<TFM>/` for IntelliSense and precise API lookup.
