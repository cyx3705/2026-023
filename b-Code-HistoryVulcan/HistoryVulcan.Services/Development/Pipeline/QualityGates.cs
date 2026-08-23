using System.Text.RegularExpressions;

namespace HistoryVulcan.Services.Development.Pipeline;

internal static class QualityGates
{
    private static readonly Regex Suppression = new(
        @"NoWarn|SuppressMessage|#pragma\s+warning\s+disable",
        RegexOptions.CultureInvariant);

    public static void AssertSourceQuality(string projectRoot, TextWriter log)
    {
        var violations = new List<string>();
        var active = Path.Combine(projectRoot, "b-Code-HistoryVulcan");
        if (!Directory.Exists(active))
            throw new InvalidOperationException("找不到 b-Code-HistoryVulcan。");

        foreach (var file in Directory.EnumerateFiles(active, "*.*", SearchOption.AllDirectories))
        {
            if (IsGenerated(file) || file.EndsWith("QualityGates.cs", StringComparison.OrdinalIgnoreCase))
                continue;
            var extension = Path.GetExtension(file);
            if (extension is not (".cs" or ".csproj" or ".props" or ".targets"))
                continue;
            var text = File.ReadAllText(file);
            if (Suppression.IsMatch(text))
                violations.Add($"抑制标记：{file}");
        }

        var candidate = Path.Combine(projectRoot, "z-Publish");
        if (Directory.Exists(candidate))
        {
            var hostExe = Path.Combine(candidate, "host", "HistoryVulcan.exe");
            if (!File.Exists(hostExe))
            {
                violations.Add(
                    "候选快照不是扁平结构：缺少 z-Publish\\host\\HistoryVulcan.exe。");
            }

            foreach (var stray in Directory.GetDirectories(candidate, "HistoryVulcan-v*"))
            {
                violations.Add($"候选根出现版本化目录：{Path.GetFileName(stray)}。当前候选应扁平放在 z-Publish\\。");
            }
        }

        var hotspots = Directory.EnumerateFiles(active, "*.*", SearchOption.AllDirectories)
            .Where(file => !IsGenerated(file) && Path.GetExtension(file) is ".cs" or ".xaml")
            .Select(file => (Path: file, Lines: File.ReadAllLines(file).Length))
            .Where(item => item.Lines > 1000)
            .OrderByDescending(item => item.Lines)
            .ToList();
        foreach (var hotspot in hotspots)
            log.WriteLine($"警告：热点 {hotspot.Path}（{hotspot.Lines} 行）；按职责拆分。");

        if (violations.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, violations));
        if (hotspots.Count > 0)
            throw new InvalidOperationException("生产文件超过 1000 行质量上限。");

        log.WriteLine("质量门禁通过：抑制标记 0；无超限热点。");
    }

    public static void AssertPublicApiBaseline(string projectRoot, string version, TextWriter log)
    {
        var projects = new[] { "HistoryVulcan.Core", "HistoryVulcan.Services", "HistoryVulcan.ServiceHost" };
        var component = Path.Combine(projectRoot, "b-Code-HistoryVulcan");
        var baselineDir = Path.Combine(projectRoot, "b-Code-Eng", "public-api-baselines", version);
        if (!Directory.Exists(baselineDir))
            throw new InvalidOperationException($"缺少 {version} 的已批准 Unshipped 基线目录：{baselineDir}");

        var violations = new List<string>();
        foreach (var project in projects)
        {
            var approved = ReadApi(Path.Combine(baselineDir, project + ".Unshipped.txt"));
            var actualPath = Path.Combine(component, project, "PublicAPI.Unshipped.txt");
            if (!File.Exists(actualPath))
            {
                violations.Add($"{project}：缺少 PublicAPI.Unshipped.txt");
                continue;
            }

            var actual = ReadApi(actualPath);
            if (!approved.SequenceEqual(actual, StringComparer.Ordinal))
            {
                violations.Add($"{project}：Unshipped API 与已批准的 {version} 基线不一致。");
            }
        }

        if (violations.Count > 0)
            throw new InvalidOperationException("公开 API 冻结门禁失败：\n  " + string.Join("\n  ", violations));

        log.WriteLine($"公开 API 基线门禁通过：{projects.Length} 份 Unshipped 与已批准的 {version} 基线一致。");
    }

    private static IReadOnlyList<string> ReadApi(string path)
        => File.ReadAllLines(path)
            .Select(line => line.Trim().TrimStart('\uFEFF'))
            .Where(line => line.Length > 0 && line != "#nullable enable")
            .ToList();

    private static bool IsGenerated(string path)
        => Regex.IsMatch(path, @"\\(bin|obj|artifacts|history|b-Publish|z-Publish)\\", RegexOptions.IgnoreCase);
}
