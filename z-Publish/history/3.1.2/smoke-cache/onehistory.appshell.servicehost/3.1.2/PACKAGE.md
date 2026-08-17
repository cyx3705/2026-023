# OneHistory AppShell

AppShell is a .NET 8 desktop application framework for Windows. It provides a command bus,
WPF shell and docking UI, local services, module hosting, and an MCP gateway.

## Packages

- `OneHistory.AppShell.Core`: framework contracts and command/MCP metadata.
- `OneHistory.AppShell.Services`: settings, logging, file-backed state, modules, and MCP services.
- `OneHistory.AppShell.Shell`: the WPF application shell. Referencing this package brings in Core and Services.
- `OneHistory.AppShell.ServiceHost`: headless WPF service lifecycle, confirmation, and `svc.*` hosting.

## Install

```xml
<PackageReference Include="OneHistory.AppShell.Shell" Version="3.1.2" />
<PackageReference Include="OneHistory.AppShell.ServiceHost" Version="3.1.2" />
```

AppShell 3.1.x targets .NET 8. The Shell and ServiceHost packages require Windows and WPF. Packages in this
repository-local feed are for OneHistory-owned projects; no public distribution license is granted
by the package itself.

The current z-level release snapshot includes `AppShell.reuse.md` beside the feed directory and the
version-matched consumer contracts under `docs/`. Consumers and AI tools should start with the reuse
document and follow its links to the API/command, module/MCP, runtime-limit, and change-summary contracts.
Release evidence and maintenance documents remain outside the runtime packages. XML API documentation
stays beside each assembly under `lib/<TFM>/` for IntelliSense and precise API lookup.
