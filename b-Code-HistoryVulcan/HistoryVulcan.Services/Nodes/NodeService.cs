using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Services.Modules;

namespace HistoryVulcan.Services.Nodes;

/// <summary>Owns a single authenticated two-node session. Offline compositions never start networking.</summary>
internal sealed class NodeService : IAsyncDisposable
{
    private readonly string _root;
    private readonly CommandBus _bus;
    private readonly ModuleHost _modules;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private X509Certificate2? _certificate;
    internal NodeRuntime? Runtime { get; private set; }
    internal NodeIdentity Identity { get; private set; }
    internal string? LastError { get; private set; }
    internal bool Running { get; private set; }
    private string OptionsPath => Path.Combine(_root, "options.json");

    internal NodeService(string dataRoot, CommandBus bus, ModuleHost modules)
    {
        _root = Path.Combine(dataRoot, "Nodes");
        _bus = bus;
        _modules = modules;
        Identity = NodeIdentity.Load(_root);
    }

    internal object PairingIdentity()
    {
        _certificate ??= NodeCertificateStore.Load(_root, Identity.NodeId);
        return new { Identity.NodeId, Identity.LogicalHostId, certificateHash = NodeTlsTransport.Fingerprint(_certificate) };
    }

    internal void Configure(NodeOptions options)
    {
        if (Runtime != null) throw new InvalidOperationException("节点会话已配置运行，修改配对需另行安排维护。");
        NodeIdentity.ValidateId(options.LogicalHostId);
        NodeIdentity.ValidateId(options.Peer.NodeId);
        PackageArtifactStore.ValidateHash(options.Peer.CertificateHash);
        if (options.Peer.NodeId == Identity.NodeId || !IPAddress.TryParse(options.ListenAddress, out _)
            || options.ListenPort is < 0 or > 65535 || options.Peer.Port is < 1 or > 65535)
            throw new InvalidDataException("配对地址或节点身份无效。");
        if (_modules.Modules.Count > 0)
            throw new InvalidOperationException("配对前需迁移既有模块并声明前端角色；当前节点仍有已装载模块，未自动分配归属。");
        var ownership = new ModuleOwnershipLedger(_root);
        if (ownership.Read().Length > 0 && Identity.LogicalHostId != options.LogicalHostId)
            throw new InvalidOperationException("已有归属历史，不能更换逻辑宿主。");
        Identity = Identity with { LogicalHostId = options.LogicalHostId };
        NodeStateFile.Update(Path.Combine(_root, "identity.json"), () => Identity, _ => Identity);
        NodeStateFile.Update(OptionsPath, () => options, _ => options);
        if (Running) StartRuntime();
    }

    internal void Start()
    {
        Running = true;
        StartRuntime();
    }

    private void StartRuntime()
    {
        if (Runtime != null || !File.Exists(OptionsPath)) return;
        var options = NodeStateFile.Read<NodeOptions>(OptionsPath, () => throw new InvalidDataException("未配置配对。"));
        if (options.LogicalHostId != Identity.LogicalHostId) throw new InvalidDataException("节点配置与逻辑身份不一致。");
        _certificate ??= NodeCertificateStore.Load(_root, Identity.NodeId);
        Runtime = new NodeRuntime(_bus, _modules, Identity, options.Peer.NodeId, _root, Path.Combine(_root, "Cache"));
        if (!string.IsNullOrWhiteSpace(options.PreloadDirectory))
        {
            var path = Path.GetFullPath(options.PreloadDirectory);
            var modulesPath = Path.GetFullPath(_modules.ModulesDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if ((path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).StartsWith(modulesPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("预装载区不能位于 Modules 发现目录内。");
            Runtime.PreloadStore = new PackageArtifactStore(path);
        }
        using (ExecutionContext.SuppressFlow())
            _loop = Task.Run(() => RunAsync(options));
    }

    private async Task RunAsync(NodeOptions options)
    {
        TcpListener? listener = null;
        try
        {
            if (options.ListenPort != 0)
            {
                listener = new TcpListener(IPAddress.Parse(options.ListenAddress), options.ListenPort);
                listener.Start(4);
            }
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    System.Net.Security.SslStream stream;
                    if (listener != null)
                    {
                        var client = await listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                        handshake.CancelAfter(TimeSpan.FromSeconds(10));
                        stream = await NodeTlsTransport.AcceptAsync(client, options.Peer, _certificate!, handshake.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        handshake.CancelAfter(TimeSpan.FromSeconds(10));
                        stream = await NodeTlsTransport.ConnectAsync(options.Peer, _certificate!, handshake.Token).ConfigureAwait(false);
                    }
                    await using var connection = Runtime!.Attach(stream);
                    await Runtime.SynchronizeAsync(_stop.Token).ConfigureAwait(false);
                    LastError = null;
                    while (connection.Connected && !_stop.IsCancellationRequested)
                    {
                        await Runtime.SynchronizePackagesAsync(_stop.Token).ConfigureAwait(false);
                        await Task.Delay(TimeSpan.FromSeconds(3), _stop.Token).ConfigureAwait(false);
                        await Runtime.SynchronizeAsync(_stop.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (!_stop.IsCancellationRequested)
                {
                    LastError = "节点连接或同步失败：" + ex.GetType().Name;
                    _bus.Log.Warn("node", LastError);
                    await Task.Delay(TimeSpan.FromSeconds(3), _stop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastError = "节点监听失败：" + ex.GetType().Name; }
        finally { listener?.Stop(); }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop != null) await _loop.ConfigureAwait(false);
        if (Runtime != null) await Runtime.DisposeAsync().ConfigureAwait(false);
        _certificate?.Dispose();
    }
}
