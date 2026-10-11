# HistoryVulcan Host Distribution

The host snapshot remains a directory under `z-Publish/host/`. Module candidates are a single nupkg written by the pipeline (7.0.0, DEC-074), not a `dotnet pack` output and not a folder slot.

DEC-075 keeps the original module version in `module.manifest.json` and removes a leading `v` or `V` from the nuspec version. Payload entries are ordered and use a fixed ZIP timestamp. Module contents are published atomically under `ModuleContent/<module>/<package SHA256>` and reused without overwriting resources used by another host or CLI process.

Snapshots carry no consumer documentation (6.1.0, DEC-072): how to call a command is read from its registered self-description via `diana.docs.*`. See the [module development handbook](../b-Office/模块开发手册.md) for the integration contract and package requirements, and the [public API contract](../b-Office/current/冻结合同.md) for API change rules. Fixed freeze tags and per-version approval baselines are retired (DEC-076).
