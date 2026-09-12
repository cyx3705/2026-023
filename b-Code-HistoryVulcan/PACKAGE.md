# HistoryVulcan Host Distribution

HistoryVulcan 5.3.0 ships an executable host, a Console CLI and the Core, Services and ServiceHost contract assemblies.
The freeze tag remains v5.1.2. This version removes obsolete public APIs as recorded in Unshipped and the approved 5.3.0 baseline.

The host owns module registration and lifecycle, the command bus, local CLI, and development/release pipelines.
Aurora owns the desktop UI; Portunus owns Web/MCP gateways. Existing generic integration delegates remain supported.

Consumer documentation is edited under b-Office/package and generated into z-Publish/docs by the release pipeline.
Do not edit generated snapshots. Obtain cross-project contracts through Diana's published catalog and channels.
See the [module API](../b-Office/package/模块API.md) for migration and package requirements.
