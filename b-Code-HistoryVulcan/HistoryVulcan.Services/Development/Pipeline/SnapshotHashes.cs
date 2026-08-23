using System.Security.Cryptography;
using System.Text;

namespace HistoryVulcan.Services.Development.Pipeline;

internal static class SnapshotHashes
{
    public const string FileName = "SHA256SUMS";

    public static void Write(string root)
    {
        var files = EnumeratePayload(root);
        if (files.Count == 0)
            throw new InvalidOperationException($"快照没有可校验文件：{root}");

        var marker = DetectMarker(Path.Combine(root, FileName));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var lines = new List<string>(files.Count);
        foreach (var file in files)
        {
            var relative = file.Substring(prefix.Length).Replace('\\', '/');
            lines.Add($"{Hash(file)}{marker}{relative}");
        }

        File.WriteAllLines(Path.Combine(root, FileName), lines, new UTF8Encoding(false));
    }

    public static void Assert(string root, bool skipHistory)
    {
        var sumsPath = Path.Combine(root, FileName);
        if (!File.Exists(sumsPath))
            throw new InvalidOperationException($"缺少 {FileName}：{root}");

        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(sumsPath, new UTF8Encoding(false)))
        {
            if (line.Length == 0)
                continue;
            var match = System.Text.RegularExpressions.Regex.Match(
                line, @"^(?<hash>[0-9A-Fa-f]{64}) [ *](?<file>.+)$");
            if (!match.Success)
                throw new InvalidOperationException($"无效校验行：{line}");
            var key = match.Groups["file"].Value;
            if (!expected.TryAdd(key, match.Groups["hash"].Value.ToUpperInvariant()))
                throw new InvalidOperationException($"重复校验项：{key}");
        }

        var files = EnumeratePayload(root, skipHistory);
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (files.Count != expected.Count)
            throw new InvalidOperationException(
                $"{FileName} 未覆盖完整快照（{files.Count} 个文件 vs {expected.Count} 条）：{root}");

        foreach (var file in files)
        {
            var key = file.Substring(prefix.Length).Replace('\\', '/');
            var actual = Hash(file);
            if (!expected.TryGetValue(key, out var want) || !want.Equals(actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"校验不匹配：{key}");
        }
    }

    public static IReadOnlyList<string> EnumeratePayload(string root, bool skipHistory = false)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var history = Path.Combine(root, "history") + Path.DirectorySeparatorChar;
        var installer = Path.Combine(root, "installer") + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                if (path.EndsWith(FileName, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (skipHistory && path.StartsWith(history, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (path.StartsWith(installer, StringComparison.OrdinalIgnoreCase))
                    return false;
                return true;
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string DetectMarker(string sumsPath)
    {
        if (File.Exists(sumsPath))
        {
            foreach (var line in File.ReadAllLines(sumsPath, new UTF8Encoding(false)))
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^[0-9A-Fa-f]{64} \*"))
                    return " *";
            }
        }

        return "  ";
    }
}
