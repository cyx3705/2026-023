using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Services.Modules;

namespace HistoryVulcan.Services.Nodes;

internal sealed record NodeDeliveryRecord(string Id, PackageArtifact Artifact, string? Target, string Stage,
    PackageDelivery? Receipt, string? Error);

internal static class NodeCommands
{
    internal static void Register(CommandRegistry registry, CommandBus bus, ModuleHost modules, NodeService service, string dataRoot)
    {
        var records = Path.Combine(dataRoot, "Nodes", "Deliveries");
        var cache = new PackageArtifactStore(Path.Combine(dataRoot, "Nodes", "Cache"));
        Add("identity", "显示节点身份及用于双向配对的证书 SHA-256", [], true,
            _ => Task.FromResult(CommandResult.Ok("节点配对身份", service.PairingIdentity())));
        Add("status", "显示连接、归属历史、远端目录和事件缺口", [], true,
            _ => Task.FromResult(CommandResult.Ok("节点状态", new
            {
                service.Identity,
                service.Running,
                service.LastError,
                enabled = service.Runtime != null,
                connected = service.Runtime?.Connected ?? false,
                ownership = service.Runtime?.Ownership.Read() ?? [],
                commands = service.Runtime?.RemoteCommands ?? [],
                eventGaps = service.Runtime?.EventGaps ?? 0,
                artifacts = service.Runtime?.KnownArtifacts ?? [],
                pendingPackages = service.Runtime?.PendingPackages ?? [],
            })));
        Add("configure", "登记双向证书配对；监听端填写 listenPort，连接端填 0；只在本机配置", [
            Text("group", "逻辑宿主 GUID", true), Text("peer", "对端节点 GUID", true),
            Text("fingerprint", "对端证书 SHA-256", true), Text("host", "对端地址", true),
            Text("port", "对端端口", true), Text("listen", "本机监听地址，默认 127.0.0.1"),
            Text("listenPort", "本机监听端口，0 表示主动连接"), Text("preload", "预装载共享目录，可省略")], false,
            context =>
            {
                service.Configure(new NodeOptions(context.RequireString("group"), context.GetString("listen") ?? "127.0.0.1",
                    context.GetInt("listenPort"), new NodePeer(context.RequireString("peer"), context.RequireString("fingerprint"),
                        context.RequireString("host"), context.GetInt("port")), context.GetString("preload")));
                return Task.FromResult(CommandResult.Ok("配对配置已保存；活宿主立即连接，离线 CLI 在下次服务启动时连接。"));
            });
        Add("own", "明确分配、迁移、离线接管或解决归属冲突；数据留在原地", [
            Text("module", "模块身份", true), Text("target", "节点 GUID、local 或 preload", true),
            Text("heads", "status 中该模块的当前头修订，以逗号分隔；初次用 none", true),
            Text("mode", "assign/migrate/takeover/resolve", true), Text("data", "必须为 keep-local", true)], false,
            async context => CommandResult.Ok("归属已记录；按明确制品执行 deliver 才会装载。",
                await RequireRuntime().ChangeOwnershipAsync(context.RequireString("module"), context.RequireString("target"),
                    context.RequireString("heads") == "none" ? [] : context.RequireString("heads").Split(','),
                    context.RequireString("mode"), context.RequireString("data"), _ => true, context.Cancellation).ConfigureAwait(false)));
        Add("frontend", "执行指定节点前端的命令，确认回发起端，UI 在线程所属节点执行", [
            Text("node", "节点 GUID 或 local", true), Text("command", "完整前端指令", true)], false,
            context => RequireRuntime().ExecuteFrontendAsync(context.RequireString("node"), context.RequireString("command"),
                false, context.Cancellation));
        Add("invoke", "经活宿主总线调用已登记的可见模块命令；按归属路由", [Text("command", "完整模块指令", true)], false,
            context =>
            {
                var command = context.RequireString("command");
                if (!service.Running) return Task.FromResult(CommandResult.Fail("模块调用须连接活宿主。"));
                var request = CommandRequest.Create(command, registry);
                var source = registry.GetSource(request.Name);
                if (request.Descriptor is not { HiddenReason: null }
                    || (!source.StartsWith("module:", StringComparison.Ordinal) && !source.StartsWith("node:", StringComparison.Ordinal)))
                    return Task.FromResult(CommandResult.Fail("只允许已登记的可见模块命令。"));
                return bus.ExecuteAsync(command, "cli:node", context.Cancellation);
            });
        Add("deliver", "交付确切 nupkg 至归属节点或预装载区，保留阶段回执", [
            Text("path", "nupkg 或单包目录，与 hash 二选一"), Text("hash", "已缓存或预装载制品 SHA-256，用于恢复；与 path 二选一"),
            Text("target", "节点 GUID、local 或 preload；默认沿用归属")], false,
            async context =>
            {
                if (!service.Running) return CommandResult.Fail("交付须通过 --runtime 连接活宿主。");
                if (context.Has("path") == context.Has("hash")) return CommandResult.Fail("path 与 hash 必须且只能指定一个。");
                PackageArtifact artifact;
                if (context.GetString("hash") is { } hash)
                {
                    if (File.Exists(cache.PathFor(hash))) artifact = cache.Inspect(hash);
                    else if (service.Runtime?.PreloadStore is { } preload)
                    {
                        await using var input = File.OpenRead(preload.PathFor(hash));
                        artifact = await cache.ImportAsync(input, hash, context.Cancellation).ConfigureAwait(false);
                    }
                    else return CommandResult.Fail("缓存中没有该制品，且未配置预装载区。");
                }
                else artifact = await cache.ImportAsync(context.RequireString("path"), context.Cancellation).ConfigureAwait(false);
                var record = new NodeDeliveryRecord(Guid.NewGuid().ToString("N"), artifact, context.GetString("target"), "cached", null, null);
                Save(record);
                try
                {
                    PackageDelivery delivered;
                    if (service.Runtime is { } runtime)
                        delivered = await runtime.DeliverAsync(cache.PathFor(artifact.Hash), record.Target, _ => true, context.Cancellation).ConfigureAwait(false);
                    else
                    {
                        if (record.Target != null && record.Target != "local" && record.Target != service.Identity.NodeId)
                            throw new InvalidOperationException("尚未配对；不能交付远端或预装载区。");
                        var current = modules.Modules.FirstOrDefault(item => item.ModuleName == artifact.Module);
                        var installed = current is { Attached: true } && current.PackageHash == artifact.Hash
                            ? CommandResult.Ok("同一制品已附着") : modules.InstallPackage(cache.PathFor(artifact.Hash));
                        var state = modules.Modules.FirstOrDefault(item => item.ModuleName == artifact.Module);
                        var attached = installed.Success && state is { Attached: true } && state.PackageHash == artifact.Hash && state.InstanceId.Length > 0;
                        var installation = new NodeInstallation(artifact.Module, artifact.Version, artifact.Hash, service.Identity.NodeId,
                            state?.InstanceId ?? "", installed.Success, attached, state?.CommandCount ?? 0, attached ? null : installed.Message);
                        delivered = new PackageDelivery(artifact, service.Identity.NodeId, [], true, false, installation, installation.Error);
                    }
                    record = record with { Target = delivered.Target, Stage = delivered.Success ? "submitted" : "delivery-incomplete", Receipt = delivered, Error = delivered.Error };
                    Save(record);
                    return new CommandResult
                    {
                        Success = delivered.Success,
                        Message = delivered.Success
                        ? "制品已交付并核对，等待审核。" : "交付未完成，可按回执中的制品哈希恢复。",
                        Data = record
                    };
                }
                catch (Exception ex)
                {
                    record = record with { Stage = "delivery-incomplete", Error = ex.GetType().Name + ": " + ex.Message };
                    Save(record);
                    return new CommandResult { Success = false, Message = record.Error, Data = record };
                }
            });
        Add("receipt", "读取持久交付回执", [Text("id", "交付回执 GUID", true)], true,
            context => Task.FromResult(CommandResult.Ok("交付记录", Read(context.RequireString("id")))));
        Add("verify", "核对已审核的制品、目标、归属及运行实例，变化时要求重新 submit", [Text("id", "交付回执 GUID", true)], true,
            context => Verify(Read(context.RequireString("id")), context.Cancellation));
        Add("devices", "列出本机和对端，以及每个模块当前装在哪一台", [], true,
            _ => Task.FromResult(CommandResult.Ok("设备", DeviceCatalog())));
        Add("refresh", "按本机最新的宿主版本和模块包对齐已连接设备；命令在执行它的那台机器上生效", [], false,
            async context => CommandResult.Ok(service.Runtime == null
                ? RefreshLocalPackages()
                : await service.Runtime.RefreshDevicesAsync(context.Cancellation).ConfigureAwait(false)),
            confirm: false);
        Add("place", "把模块装到选定设备，或改为不装载", [
            Text("module", "模块名", true),
            Text("target", "local、对端节点 GUID，或 none 表示不装载", true)], false,
            async context =>
            {
                try
                {
                    return CommandResult.Ok(await PlaceAsync(context.RequireString("module"), context.RequireString("target"), context.Cancellation)
                        .ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }, confirm: false);
        Add("finalize", "finish 合并后将同一交付记录标为已收口；不选择新制品", [Text("id", "交付回执 GUID", true)], false,
            async context =>
            {
                var record = Read(context.RequireString("id"));
                var verified = await Verify(record, context.Cancellation).ConfigureAwait(false);
                if (!verified.Success) return verified;
                Save(record with { Stage = "finished" });
                return CommandResult.Ok("同一交付制品已收口。", record with { Stage = "finished" });
            });

        NodeRuntime RequireRuntime() => service.Runtime ?? throw new InvalidOperationException("逻辑宿主尚未配置或不在服务进程中。");
        NodeDevice[] LocalOnly() => [new NodeDevice("local", "本机", NodeRuntime.LocalHostVersion, true, true)];
        JsonElement DeviceCatalog()
        {
            var runtime = service.Runtime;
            var devices = runtime?.Devices() ?? LocalOnly();
            var placements = runtime == null
                ? Array.Empty<ModulePlacementView>()
                : runtime.Ownership.Read()
                    .Select(item => item.Module)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(module =>
                    {
                        var state = runtime.Ownership.State(module);
                        return new ModulePlacementView(state.Module, state.OwnerNodeId, state.Conflict);
                    })
                    .ToArray();
            return BusJson.ToElement(new { devices, placements });
        }
        string RefreshLocalPackages()
        {
            var notes = new List<string>();
            if (!Directory.Exists(modules.ModulesDirectory))
                return "没有模块目录。";
            foreach (var file in Directory.EnumerateFiles(modules.ModulesDirectory, "*" + ModuleNupkg.Extension))
            {
                if (RuntimeModuleDiscoverySource.IsTransientPackageDirectory(Path.GetFileName(file)))
                    continue;
                if (!ModuleNupkg.TryRead(file, out var entry, out _, out _))
                    continue;
                var loaded = modules.Modules.FirstOrDefault(item =>
                    item.ModuleName.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
                if (loaded is not { Attached: true })
                    continue;
                var hash = ModuleNupkg.Hash(file);
                if (string.Equals(loaded.PackageHash, hash, StringComparison.OrdinalIgnoreCase))
                    continue;
                var replaced = modules.InstallPackage(file);
                notes.Add(replaced.Success
                    ? $"{entry.Name} 已按本机最新包装上 {entry.Version}。"
                    : $"{entry.Name} 未能按本机最新包装上：{replaced.Message}");
            }

            return notes.Count == 0 ? "设备与模块已与本机最新版本一致。" : string.Join("\n", notes);
        }
        async Task<string> PlaceAsync(string module, string target, CancellationToken cancellation)
        {
            if (bus.FrontendOwner?.Equals(module, StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidOperationException("前端按节点定位，不在装载区改归属。");
            if (service.Runtime == null)
            {
                if (target is "none" or "preload")
                {
                    var unloaded = modules.Unload(module);
                    if (!unloaded.Success) throw new InvalidOperationException(unloaded.Message);
                    return module + " 已改为不装载。包还在，运行实例已撤下。";
                }

                if (target != "local")
                    throw new InvalidOperationException("尚未配对，只能装到本机或不装载。");
                var file = Path.Combine(modules.ModulesDirectory, module + ModuleNupkg.Extension);
                if (!File.Exists(file))
                    throw new InvalidOperationException("运行区里没有这个模块的包。");
                var installed = modules.InstallPackage(file);
                if (!installed.Success) throw new InvalidOperationException(installed.Message);
                return installed.Message;
            }

            return await service.Runtime.PlaceAsync(module, target, cancellation).ConfigureAwait(false);
        }
        void Save(NodeDeliveryRecord record)
        {
            NodeStateFile.Update(Path.Combine(records, record.Id + ".json"), () => record, _ => record);
            if (record.Receipt?.Preloaded == true && service.Runtime?.PreloadStore is { } preload)
            {
                if (preload.Inspect(record.Artifact.Hash) != record.Artifact) throw new InvalidDataException("预装载区制品不匹配。");
                NodeStateFile.Update(Path.Combine(preload.Root, "index", service.Identity.NodeId, record.Id + ".json"), () => record, _ => record);
            }
        }
        NodeDeliveryRecord Read(string id)
        {
            NodeIdentity.ValidateId(id);
            return NodeStateFile.Read<NodeDeliveryRecord>(Path.Combine(records, id + ".json"), () => throw new InvalidDataException("交付记录不存在。"));
        }
        async Task<CommandResult> Verify(NodeDeliveryRecord record, CancellationToken cancellation)
        {
            if (record.Receipt?.Success != true) return CommandResult.Fail("原交付没有完成，不能作为审核依据。");
            if (service.Runtime is { } runtime)
                return await runtime.VerifyDeliveryAsync(record.Receipt, cancellation).ConfigureAwait(false);
            if (!service.Running) return CommandResult.Fail("必须核对活宿主。");
            var current = modules.Modules.FirstOrDefault(item => item.ModuleName == record.Artifact.Module);
            return current is { Attached: true } && current.PackageHash == record.Artifact.Hash
                && current.InstanceId == record.Receipt.Installation?.InstanceId
                ? CommandResult.Ok("同一目标实例、制品已核对。", record)
                : CommandResult.Fail("运行制品或实例已改变，请重新 submit 审核。");
        }
        void Add(string action, string summary, ParameterSpec[] parameters, bool readOnly, Func<CommandContext, Task<CommandResult>> handler, bool confirm = true)
        {
            registry.Register(new CommandDescriptor
            {
                Name = "vulcan.node." + action,
                Domain = "vulcan",
                CommandClass = "node",
                Summary = summary,
                HiddenReason = "节点管理只经本机 CLI 或本机总线，不提供通用远端管理入口。",
                Example = action == "deliver" ? "vulcan.node.deliver path=Example.nupkg target=local"
                    : "vulcan.node." + action + string.Concat(parameters.Where(item => item.Required).Select(item => " " + item.Name + "=" + ExampleValue(item.Name))),
                Parameters = parameters,
                Readonly = readOnly,
                Annotations = new Dictionary<string, string> { ["maskedInputParameters"] = "command" },
                Handler = context =>
                {
                    if (NodeExecutionScope.Current != null) return Task.FromResult(CommandResult.Fail("远端调用不得改写本机节点管理状态。"));
                    if (!readOnly && confirm && !LocalActionApproval.Approved && !bus.RequestConfirmation(summary))
                        return Task.FromResult(CommandResult.Fail("节点操作未获本机动作批准。"));
                    return handler(context);
                },
            }, "framework:node");
        }
    }

    private static ParameterSpec Text(string name, string description, bool required = false)
        => new() { Name = name, Description = description, Required = required };

    private static string ExampleValue(string name) => name switch
    {
        "heads" => "none",
        "mode" => "assign",
        "data" => "keep-local",
        "port" => "47880",
        "host" => "192.0.2.1",
        "path" => "Example.nupkg",
        "command" => "example.info",
        "module" => "Example",
        "target" or "node" => "local",
        "fingerprint" => new string('A', 64),
        _ => new string('1', 32),
    };
}
