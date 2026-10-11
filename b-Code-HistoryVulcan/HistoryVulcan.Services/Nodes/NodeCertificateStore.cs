using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace HistoryVulcan.Services.Nodes;

/// <summary>Private material belongs to the node account, including background accounts without a loaded HKCU hive.</summary>
internal static class NodeCertificateStore
{
    internal static X509Certificate2 Load(string stateRoot, string nodeId)
    {
        var directory = Path.Combine(stateRoot, "Private");
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var identities = new[] { WindowsIdentity.GetCurrent().User!,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) };
            foreach (var identity in identities)
                security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }
        else
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var path = Path.Combine(directory, "node.pfx");
        var fingerprint = NodeStateFile.Read(Path.Combine(directory, "certificate.json"), () =>
        {
            if (!File.Exists(path))
            {
                using var created = NodeTlsTransport.CreateCertificate(nodeId);
                var temporary = path + ".writing-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        file.Write(created.Export(X509ContentType.Pfx));
                        file.Flush(true);
                    }
                    File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            using var existing = NodeTlsTransport.ImportCertificate(File.ReadAllBytes(path));
            return NodeTlsTransport.Fingerprint(existing);
        });
        var certificate = NodeTlsTransport.ImportCertificate(File.ReadAllBytes(path));
        if (certificate.GetNameInfo(X509NameType.SimpleName, false) != nodeId || NodeTlsTransport.Fingerprint(certificate) != fingerprint)
        {
            certificate.Dispose();
            throw new InvalidDataException("节点私钥文件与已登记证书身份不一致，拒绝自动换证。");
        }
        return certificate;
    }
}
