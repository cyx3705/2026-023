using System.Text.Json;

namespace HistoryVulcan.Services.Development.Pipeline;

internal static class PublishLayout
{
    public static string PromoteVersioned(
        string stagingRoot,
        string publishRoot,
        ReleaseTarget target,
        string version)
    {
        var packageName = $"{target.Name}-v{version}";
        var historyRoot = Path.Combine(publishRoot, "history");
        Directory.CreateDirectory(historyRoot);
        var incoming = Path.Combine(publishRoot, ".incoming-" + Guid.NewGuid().ToString("N"));
        SnapshotHashes.CopyDirectory(stagingRoot, incoming);
        ModuleSnapshotBuilder.AssertSnapshot(incoming, target, version);

        var destination = Path.Combine(publishRoot, packageName);
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

        foreach (var current in Directory.GetDirectories(publishRoot, "History*-v*"))
            Directory.Delete(current, recursive: true);
        foreach (var item in Directory.GetFileSystemEntries(publishRoot))
        {
            var name = Path.GetFileName(item);
            if (name.Equals("history", StringComparison.OrdinalIgnoreCase)
                || name.Equals(Path.GetFileName(incoming), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Directory.Exists(item))
                Directory.Delete(item, recursive: true);
            else
                File.Delete(item);
        }

        Relocate(incoming, destination);
        return destination;
    }

    public static string PromoteFlatHost(string stagingRoot, string publishRoot, string version)
    {
        var historyRoot = Path.Combine(publishRoot, "history");
        Directory.CreateDirectory(historyRoot);
        var backup = Path.Combine(Path.GetTempPath(), "host-previous-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);

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

        var movedExisting = new List<string>();
        var movedIncoming = new List<string>();
        try
        {
            Directory.CreateDirectory(publishRoot);
            foreach (var item in Directory.GetFileSystemEntries(publishRoot))
            {
                var name = Path.GetFileName(item);
                if (name.Equals("history", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(".incoming-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var dest = Path.Combine(backup, name);
                Relocate(item, dest);
                movedExisting.Add(name);
            }

            foreach (var item in Directory.GetFileSystemEntries(stagingRoot))
            {
                var name = Path.GetFileName(item);
                var dest = Path.Combine(publishRoot, name);
                Relocate(item, dest);
                movedIncoming.Add(name);
            }
        }
        catch
        {
            foreach (var name in movedIncoming)
            {
                var path = Path.Combine(publishRoot, name);
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                else if (File.Exists(path))
                    File.Delete(path);
            }

            foreach (var name in movedExisting)
            {
                var path = Path.Combine(backup, name);
                var dest = Path.Combine(publishRoot, name);
                Relocate(path, dest);
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(backup))
                Directory.Delete(backup, recursive: true);
        }

        return Path.GetFullPath(publishRoot);
    }

    private static void Relocate(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
            if (SameVolume(source, destination))
                Directory.Move(source, destination);
            else
            {
                SnapshotHashes.CopyDirectory(source, destination);
                Directory.Delete(source, recursive: true);
            }

            return;
        }

        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        if (SameVolume(source, destination))
            File.Move(source, destination, overwrite: true);
        else
        {
            File.Copy(source, destination, overwrite: true);
            File.Delete(source);
        }
    }

    private static bool SameVolume(string left, string right)
        => string.Equals(
            Path.GetPathRoot(Path.GetFullPath(left)),
            Path.GetPathRoot(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static void AssertImmutable(string source, string destination)
    {
        if (!Directory.Exists(destination))
            return;
        var sourceSums = File.ReadAllText(Path.Combine(source, SnapshotHashes.FileName)).Replace("\r\n", "\n");
        var destinationSums = File.ReadAllText(Path.Combine(destination, SnapshotHashes.FileName)).Replace("\r\n", "\n");
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
            var sourceSums = File.ReadAllText(Path.Combine(package, SnapshotHashes.FileName)).Replace("\r\n", "\n");
            var destinationSums = File.ReadAllText(Path.Combine(archive, SnapshotHashes.FileName)).Replace("\r\n", "\n");
            if (sourceSums.Equals(destinationSums, StringComparison.OrdinalIgnoreCase))
                return;
            archive = Path.Combine(historyRoot, $"{identity.Name}-v{identity.Version}-{DateTime.Now:yyyyMMdd-HHmmss}");
        }

        SnapshotHashes.CopyDirectory(package, archive);
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
            if (name.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+$"))
                return (name, version);
        }

        throw new InvalidOperationException($"包身份清单缺失或无效：{root}");
    }
}
