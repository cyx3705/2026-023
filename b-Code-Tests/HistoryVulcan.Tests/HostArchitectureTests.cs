using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.ServiceHost;
using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class HostArchitectureTests
{
    [Fact]
    public void HostAssembliesContainNoGatewayOrRetiredDiscoveryImplementation()
    {
        var retired = new HashSet<string>(StringComparer.Ordinal)
        {
            "ZModuleDiscoverySource", "ShellRelayConfirmation", "McpExposurePolicy",
            "WebGateway", "McpGateway", "McpServer", "WebSocketServer",
        };
        foreach (var assembly in new[] { typeof(CommandBus).Assembly, typeof(ModuleHost).Assembly, typeof(ServiceComposer).Assembly })
        {
            using var stream = File.OpenRead(assembly.Location);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.TypeDefinitions)
                Assert.DoesNotContain(metadata.GetString(metadata.GetTypeDefinition(handle).Name), retired);
            foreach (var handle in metadata.TypeReferences)
            {
                var type = metadata.GetTypeReference(handle);
                var name = metadata.GetString(type.Name);
                var ns = metadata.GetString(type.Namespace);
                Assert.DoesNotContain(name, retired);
                Assert.False(ns.StartsWith("System.Net.WebSockets", StringComparison.Ordinal)
                    || ns.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                    || ns.StartsWith("ModelContextProtocol", StringComparison.Ordinal)
                    || ns == "System.Net" && name == "HttpListener"
                    || ns == "System.Net.Sockets" && name is "TcpListener" or "Socket",
                    $"Host must not implement a gateway: {ns}.{name}");
            }
        }
        Assert.Null(typeof(ModuleDiscoveryEntry).GetProperty("McpExposure"));
        foreach (var name in new[] { "ChangeDirectory", "ChangeDiscoveryRoots", "ReloadConfirmedSources" })
            Assert.Null(typeof(ModuleHost).GetMethod(name));
    }
}
