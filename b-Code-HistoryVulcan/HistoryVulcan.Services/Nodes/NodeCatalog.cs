using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.Services.Nodes;

internal sealed record NodeCommandInfo(
    string Name, string Domain, string CommandClass, string Summary, string? Example,
    ParameterSpec[] Parameters, bool Readonly, bool RequiresConfirmation,
    bool RequiresUiThread, bool AllowUnspecifiedParameters, Dictionary<string, string> Annotations,
    string Module, string InstanceId, string PackageHash, bool Frontend);

internal sealed record NodeSnapshot(
    string NodeId, string LogicalHostId, OwnershipDecision[] Ownership, NodeCommandInfo[] Commands,
    PackageArtifact[] Artifacts)
{
    public string HostVersion { get; init; } = "";
}

/// <summary>模块页上的一台设备。Id 是节点 GUID；未配对时本机用 local。不装载不是设备。</summary>
internal sealed record NodeDevice(string Id, string Label, string HostVersion, bool Connected, bool Self);

/// <summary>模块当前装在哪一台。OwnerNodeId 为空表示不装载。</summary>
internal sealed record ModulePlacementView(string Module, string? OwnerNodeId, bool Conflict);

/// <summary>已装载模块的场景。每台前端都列出同一份；工具仍在 ExecutionNodeId 执行。</summary>
internal sealed record ModuleSceneView(string Module, string ExecutionNodeId);

internal sealed record NodeCommandCall(
    string CallId, string Text, string Module, string InstanceId, string PackageHash,
    string? OwnershipHead, bool Quiet, bool Frontend = false);

internal sealed record NodeCallback(string CallId, string Message);

internal sealed record NodeExecutionResult(bool Success, string Message, System.Text.Json.JsonElement? Data);

internal sealed record NodeInstallation(string Module, string Version, string PackageHash, string NodeId,
    string InstanceId, bool Installed, bool Attached, int CommandCount, string? Error);
