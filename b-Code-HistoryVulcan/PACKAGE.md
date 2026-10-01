# HistoryVulcan Host Distribution

The release pipeline produces the executable host, Console CLI, and Core, Services and ServiceHost assemblies. Projects do not produce NuGet packages.

Snapshots carry no consumer documentation (6.1.0, DEC-072): how to call a command is read from its registered self-description via `diana.docs.*`. See the [module development handbook](../b-Office/current/模块开发手册.md) for the integration contract and package requirements, and the [freeze contract](../b-Office/current/冻结合同.md) for the current API baseline.
