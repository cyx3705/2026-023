using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HistoryVulcan.Services.Web;

/// <summary>Provides this HistoryVulcan public contract member.</summary>
public enum ShellConnectionState
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Disconnected,
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Connecting,
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    PairingRequired,
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Ready,
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Rejected,
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    VersionMismatch,
}

/// <summary>Shell HTTP/WS 共享的端点、设备凭据与证书固定配置。</summary>
public sealed record ShellEndpointProfile(
    Uri BaseUri,
    string DeviceId,
    Func<string?>? AccessTokenProvider = null,
    string? CertificateFingerprint = null,
    TimeSpan? ConnectTimeout = null,
    string? ServerId = null)
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public TimeSpan EffectiveConnectTimeout => ConnectTimeout ?? TimeSpan.FromSeconds(5);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static string NormalizeFingerprint(string? value)
        => string.Concat((value ?? "").Where(Uri.IsHexDigit)).ToUpperInvariant();

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public bool ValidateCertificate(X509Certificate2? certificate)
    {
        var expected = NormalizeFingerprint(CertificateFingerprint);
        if (expected.Length == 0)
            return BaseUri.IsLoopback;
        if (certificate == null)
            return false;
        var actual = Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256));
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(expected),
            System.Text.Encoding.ASCII.GetBytes(actual));
    }
}
