using System.Text.Json;

namespace HistoryVulcan.Services.Nodes;

/// <summary>Atomic local state shared by the service and offline CLI. Invalid state is never reset silently.</summary>
internal static class NodeStateFile
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static T Read<T>(string path, Func<T> initial)
        => Update(path, initial, state => state);

    internal static T Update<T>(string path, Func<T> initial, Func<T, T> change)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var lease = Lock(path + ".lock");
        var exists = File.Exists(path);
        var before = exists ? File.ReadAllBytes(path) : null;
        var state = before == null ? initial() : JsonSerializer.Deserialize<T>(before, Json)
            ?? throw new InvalidDataException("节点状态为空，拒绝重新初始化。");
        var updated = change(state);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(updated, Json);
        if (before != null && before.AsSpan().SequenceEqual(bytes))
            return updated;
        var temporary = path + ".writing-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            return updated;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static FileStream Lock(string path)
    {
        var deadline = Environment.TickCount64 + 10000;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(20);
            }
        }
    }
}

internal sealed record NodeIdentity(string NodeId, string LogicalHostId)
{
    internal static NodeIdentity Load(string stateRoot)
    {
        var identity = NodeStateFile.Read(Path.Combine(stateRoot, "identity.json"), () =>
        {
            var id = Guid.NewGuid().ToString("N");
            return new NodeIdentity(id, id);
        });
        ValidateId(identity.NodeId);
        ValidateId(identity.LogicalHostId);
        return identity;
    }

    internal static void ValidateId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new InvalidDataException("节点或逻辑宿主身份必须是 32 位 GUID。");
    }
}
