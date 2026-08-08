namespace HistoryVulcan.Services.Web;

/// <summary>Provides this HistoryVulcan public contract member.</summary>
public sealed record DeviceAuthenticationResult(
    bool Success,
    string? DeviceId,
    string? Subject,
    IReadOnlySet<string> Scopes,
    string? Failure = null)
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static DeviceAuthenticationResult Reject(string failure = "unauthorized")
        => new(false, null, null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), failure);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static DeviceAuthenticationResult Accept(
        string deviceId,
        string subject,
        IEnumerable<string> scopes)
        => new(true, deviceId, subject,
            new HashSet<string>(scopes, StringComparer.OrdinalIgnoreCase));
}

/// <summary>Provides this HistoryVulcan public contract member.</summary>
public interface IDeviceAuthenticationProvider
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    DeviceAuthenticationResult Authenticate(string deviceId, string token, string? remoteAddress);
}

/// <summary>Provides this HistoryVulcan public contract member.</summary>
public sealed record DevicePairingResult(
    bool Success,
    string? ServerId,
    string? DeviceId,
    string? AccessToken,
    IReadOnlySet<string> Scopes,
    string? Failure = null);

/// <summary>Provides this HistoryVulcan public contract member.</summary>
public interface IDevicePairingProvider
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    DevicePairingResult Pair(
        string code,
        string deviceId,
        string deviceName,
        string? remoteAddress);
}
