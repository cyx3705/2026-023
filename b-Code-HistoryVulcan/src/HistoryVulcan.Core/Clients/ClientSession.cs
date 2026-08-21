namespace HistoryVulcan.Core.Clients;

/// <summary>
/// 接入命令总线的客户端种类。
///
/// 本族类型此前住在 Core.Mcp，但它并非 MCP 协议形态：种类覆盖 Mcp / Shell / Web 三类
/// 客户端，且承载 AuthSubject、Scopes、IsLoopback 等安全身份，MCP 审计与 Web 限流共同
/// 依赖它。冻结前正名到 Core.Clients——名字要说实话，而冻结之后就再也改不了了。
/// </summary>
public enum ClientKind
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Mcp,
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Shell,
}

/// <summary>一次客户端会话的稳定身份。</summary>
public sealed record ClientSession(
    string Id,
    ClientKind Kind,
    string Name,
    string ProtocolVersion,
    DateTimeOffset ConnectedAt)
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public string? RemoteAddress { get; init; }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public string? DeviceId { get; init; }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public string? AuthSubject { get; init; }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public IReadOnlySet<string> Scopes { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public bool IsLoopback { get; init; }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static ClientSession Create(
        ClientKind kind,
        string name,
        string protocolVersion = "2025-03-26",
        string? id = null,
        string? remoteAddress = null,
        string? deviceId = null,
        string? authSubject = null,
        IEnumerable<string>? scopes = null,
        bool isLoopback = false)
        => new(
            id ?? Guid.NewGuid().ToString("N"),
            kind,
            string.IsNullOrWhiteSpace(name) ? "client" : name.Trim(),
            protocolVersion,
            DateTimeOffset.UtcNow)
        {
            RemoteAddress = remoteAddress,
            DeviceId = deviceId,
            AuthSubject = authSubject,
            Scopes = new HashSet<string>(scopes ?? [], StringComparer.OrdinalIgnoreCase),
            IsLoopback = isLoopback,
        };
}
