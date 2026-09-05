using System.Text.Json;

namespace HistoryVulcan.Services.Development.Pipeline;

internal static class PublishLayout
{
    public static string PromoteVersioned(
        string stagingRoot,
        string publishRoot,
        ReleaseTarget target,
        string version,
        bool replaceCurrent = false,
        TextWriter? log = null)
    {
        var historyRoot = Path.Combine(publishRoot, "history");
        Directory.CreateDirectory(historyRoot);
        var incoming = Path.Combine(publishRoot, ".incoming-" + Guid.NewGuid().ToString("N"));
        try
        {
            return PromoteVersionedCore(
                stagingRoot, publishRoot, target, version, incoming, historyRoot, replaceCurrent, log);
        }
        finally
        {
            // 同 HostSnapshotBuilder：中转目录建在发布根里，抛异常时留下的就是一份完整
            // 副本，而发布根是纳入 git 的 z 快照。此前只有成功路径删得掉它。
            PublishTransaction.Cleanup(incoming, log);
        }
    }

    private static string PromoteVersionedCore(
        string stagingRoot,
        string publishRoot,
        ReleaseTarget target,
        string version,
        string incoming,
        string historyRoot,
        bool replaceCurrent,
        TextWriter? log)
    {
        SnapshotHashes.CopyDirectory(stagingRoot, incoming);
        ModuleSnapshotBuilder.AssertSnapshot(incoming, target, version);

        var destination = Path.Combine(publishRoot, $"{target.Name}-v{version}");
        if (!replaceCurrent)
            AssertImmutable(stagingRoot, destination);

        foreach (var current in Directory.GetDirectories(publishRoot, "History*-v*"))
            Archive(current, historyRoot);

        var snapshotManifest = Path.Combine(publishRoot, target.SnapshotManifest);
        if (File.Exists(snapshotManifest))
        {
            var legacy = Path.Combine(Path.GetTempPath(), "legacy-package-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(legacy);
            foreach (var item in Directory.GetFileSystemEntries(publishRoot))
            {
                var name = Path.GetFileName(item);
                if (name.Equals("history", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("History", StringComparison.OrdinalIgnoreCase) && name.Contains("-v")
                    || name.StartsWith(".incoming-"))
                {
                    continue;
                }

                var dest = Path.Combine(legacy, name);
                if (Directory.Exists(item))
                    SnapshotHashes.CopyDirectory(item, dest);
                else
                    File.Copy(item, dest, overwrite: true);
            }

            Archive(legacy, historyRoot);
            Directory.Delete(legacy, recursive: true);
        }

        var existing = Directory.GetFileSystemEntries(publishRoot)
            .Where(path => !Path.GetFileName(path).Equals("history", StringComparison.OrdinalIgnoreCase)
                && !path.Equals(incoming, StringComparison.OrdinalIgnoreCase)).ToList();
        PublishTransaction.Run(publishRoot, existing, [(incoming, destination)], log);

        return destination;
    }

    public static string PromoteFlatHost(string stagingRoot, string publishRoot, string version, TextWriter? log = null)
    {
        var historyRoot = Path.Combine(publishRoot, "history");
        Directory.CreateDirectory(historyRoot);

        var currentManifest = Path.Combine(publishRoot, "manifest.json");
        var currentSums = Path.Combine(publishRoot, SnapshotHashes.FileName);
        if (File.Exists(currentManifest) && File.Exists(currentSums))
        {
            var current = Path.Combine(Path.GetTempPath(), "host-current-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(current);
            foreach (var item in Directory.GetFileSystemEntries(publishRoot))
            {
                var name = Path.GetFileName(item);
                if (name.Equals("history", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(".incoming-")
                    || name.StartsWith("HistoryVulcan-v"))
                {
                    continue;
                }

                var dest = Path.Combine(current, name);
                if (Directory.Exists(item))
                    SnapshotHashes.CopyDirectory(item, dest);
                else
                    File.Copy(item, dest, overwrite: true);
            }

            Archive(current, historyRoot);
            Directory.Delete(current, recursive: true);
        }

        foreach (var legacy in Directory.Exists(publishRoot)
                     ? Directory.GetDirectories(publishRoot, "HistoryVulcan-v*")
                     : [])
        {
            Archive(legacy, historyRoot);
        }

        var existing = Directory.GetFileSystemEntries(publishRoot)
            .Where(path => !Path.GetFileName(path).Equals("history", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(path).StartsWith(".incoming-", StringComparison.OrdinalIgnoreCase)).ToList();
        var incoming = Directory.GetFileSystemEntries(stagingRoot)
            .Select(path => (path, Path.Combine(publishRoot, Path.GetFileName(path)))).ToList();
        PublishTransaction.Run(publishRoot, existing, incoming, log);

        return Path.GetFullPath(publishRoot);
    }

    private static void AssertImmutable(string source, string destination)
    {
        if (!Directory.Exists(destination))
            return;

        // 缺 SHA256SUMS 的目标目录是上一轮中断留下的残包。直接 ReadAllText 会抛
        // FileNotFoundException，把使用者引向一个与真实处境无关的栈。
        if (!TryReadSums(source, out var sourceSums) || !TryReadSums(destination, out var destinationSums))
        {
            throw new InvalidOperationException(
                $"历史包残缺（缺少 {SnapshotHashes.FileName}），无法判定是否可覆盖：{destination}");
        }

        if (!sourceSums.Equals(destinationSums, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"历史包已存在且内容不同。请升版本而不是覆盖：{destination}");
        }
    }

    private static void Archive(string package, string historyRoot)
    {
        var identity = ReadIdentity(package);
        var archive = Path.Combine(historyRoot, $"{identity.Name}-v{identity.Version}");
        if (Directory.Exists(archive))
        {
            if (TryReadSums(package, out var sourceSums)
                && TryReadSums(archive, out var destinationSums)
                && sourceSums.Equals(destinationSums, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // 秒级时间戳不足以保证唯一：PromoteVersioned 会在同一轮里连续归档多个包，
            // 同一秒内的两次归档算出同一个避让目录名，CopyDirectory 以 overwrite:true
            // 把两份不同内容的历史混在一起，而且没有任何人会察觉。
            var stamp = DateTime.Now.ToString(
                "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var baseName = $"{identity.Name}-v{identity.Version}-{stamp}";
            archive = Path.Combine(historyRoot, baseName);
            for (var ordinal = 2; Directory.Exists(archive); ordinal++)
                archive = Path.Combine(historyRoot, $"{baseName}-{ordinal}");
        }

        SnapshotHashes.CopyDirectory(package, archive);
    }

    private static bool TryReadSums(string root, out string sums)
    {
        var path = Path.Combine(root, SnapshotHashes.FileName);
        if (!File.Exists(path))
        {
            sums = "";
            return false;
        }

        sums = File.ReadAllText(path).Replace("\r\n", "\n");
        return true;
    }

    private static (string Name, string Version) ReadIdentity(string root)
    {
        foreach (var pair in new[] { ("module.manifest.json", "name"), ("manifest.json", "product") })
        {
            var path = Path.Combine(root, pair.Item1);
            if (!File.Exists(path))
                continue;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var name = document.RootElement.GetProperty(pair.Item2).GetString() ?? "";
            var version = document.RootElement.GetProperty("version").GetString() ?? "";
            // 预发布后缀必须放行：宿主自己的构建脚本一直接受 5.1.0-rc1 这种形状，
            // 而这里拒了它之后抛的却是「包身份清单缺失或无效」——清单明明是好的，
            // 使用者会照提示去找一个不存在的清单问题。
            if (name.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(
                    version, @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$"))
                return (name, version);
        }

        throw new InvalidOperationException($"包身份清单缺失或无效：{root}");
    }
}
