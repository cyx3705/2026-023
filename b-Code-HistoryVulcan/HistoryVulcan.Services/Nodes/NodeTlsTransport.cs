using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HistoryVulcan.Services.Nodes;

internal sealed record NodePeer(string NodeId, string CertificateHash, string Host, int Port);

internal sealed record NodeOptions(string LogicalHostId, string ListenAddress, int ListenPort, NodePeer Peer, string? PreloadDirectory = null);

/// <summary>TLS 1.2/1.3 direct transport with explicit mutual certificate pinning.</summary>
internal static class NodeTlsTransport
{
    internal static string Fingerprint(X509Certificate certificate)
        => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    internal static X509Certificate2 CreateCertificate(string nodeId)
    {
        NodeIdentity.ValidateId(nodeId);
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=" + nodeId, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
        // Windows Schannel requires an imported key container; the default import is removed on disposal.
        return ImportCertificate(created.Export(X509ContentType.Pfx), exportable: true);
    }

    internal static X509Certificate2 ImportCertificate(byte[] bytes, bool exportable = false)
    {
        var flags = exportable ? X509KeyStorageFlags.Exportable : X509KeyStorageFlags.DefaultKeySet;
        try { return new X509Certificate2(bytes, (string?)null, flags | X509KeyStorageFlags.UserKeySet); }
        catch (CryptographicException) when (OperatingSystem.IsWindows())
        {
            // Background logons may have no user key container. This creates a temporary machine key
            // owned by the importing account; it does not add a certificate to any trust store.
            return new X509Certificate2(bytes, (string?)null, flags | X509KeyStorageFlags.MachineKeySet);
        }
    }

    internal static async Task<SslStream> ConnectAsync(
        NodePeer peer, X509Certificate2 certificate, CancellationToken cancellation)
    {
        ValidatePeer(peer);
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(peer.Host, peer.Port, cancellation).ConfigureAwait(false);
            var stream = Secure(client.GetStream(), peer);
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = peer.NodeId,
                ClientCertificates = new X509CertificateCollection { certificate },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, cancellation).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal static async Task<SslStream> AcceptAsync(
        TcpClient client, NodePeer peer, X509Certificate2 certificate, CancellationToken cancellation)
    {
        ValidatePeer(peer);
        var stream = Secure(client.GetStream(), peer);
        try
        {
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, cancellation).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
            throw;
        }
    }

    private static SslStream Secure(Stream stream, NodePeer peer)
        => new(stream, leaveInnerStreamOpen: false, (_, certificate, _, _) =>
        {
            if (certificate == null)
                return false;
            using var remote = new X509Certificate2(certificate);
            return Fingerprint(remote).Equals(peer.CertificateHash, StringComparison.OrdinalIgnoreCase)
                && remote.GetNameInfo(X509NameType.SimpleName, forIssuer: false) == peer.NodeId
                && remote.NotBefore.ToUniversalTime() <= DateTime.UtcNow
                && remote.NotAfter.ToUniversalTime() > DateTime.UtcNow;
        });

    private static void ValidatePeer(NodePeer peer)
    {
        NodeIdentity.ValidateId(peer.NodeId);
        PackageArtifactStore.ValidateHash(peer.CertificateHash);
        if (string.IsNullOrWhiteSpace(peer.Host) || peer.Port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
            throw new InvalidDataException("节点地址或端口无效。");
    }
}
