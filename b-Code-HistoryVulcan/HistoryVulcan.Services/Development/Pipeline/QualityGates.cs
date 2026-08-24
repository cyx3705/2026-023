using System.Text.RegularExpressions;

namespace HistoryVulcan.Services.Development.Pipeline;

internal static class QualityGates
{
    /// <summary>单个生产文件的行数上限。超限即发布失败，不是警告。</summary>
    private const int HotspotLimit = 1000;

    private static readonly Regex Suppression = new(
        @"NoWarn|SuppressMessage|#pragma\s+warning\s+disable",
        RegexOptions.CultureInvariant);

    public static void AssertSourceQuality(string projectRoot, TextWriter log)
    {
        var violations = new List<string>();
        var active = Path.Combine(projectRoot, "b-Code-HistoryVulcan");
        if (!Directory.Exists(active))
            throw new InvalidOperationException("找不到 b-Code-HistoryVulcan。");

        // 一趟遍历读完，两件事共用同一份文本。
        //
        // 此前是两趟：第一趟 ReadAllText 全部 .cs/.csproj/.props/.targets 查抑制标记，
        // 第二趟再 ReadAllLines 全部 .cs/.xaml 数行数。两趟之间没有任何依赖，
        // 却让每次发布都把整棵源码树完整读两遍、解码两遍。
        var hotspots = new List<(string Path, int Lines)>();
        foreach (var file in Directory.EnumerateFiles(active, "*.*", SearchOption.AllDirectories))
        {
            if (IsGenerated(file))
                continue;
            var extension = Path.GetExtension(file);
            var checkSuppression = extension is ".cs" or ".csproj" or ".props" or ".targets";
            var checkSize = extension is ".cs" or ".xaml";
            if (!checkSuppression && !checkSize)
                continue;

            var text = File.ReadAllText(file);
            if (checkSuppression
                && !file.EndsWith("QualityGates.cs", StringComparison.OrdinalIgnoreCase)
                && Suppression.IsMatch(text))
            {
                violations.Add($"抑制标记：{file}");
            }

            if (checkSize)
            {
                var lines = text.AsSpan().Count('\n') + (text.Length > 0 && !text.EndsWith('\n') ? 1 : 0);
                if (lines > HotspotLimit)
                    hotspots.Add((file, lines));
            }
        }

        hotspots.Sort((left, right) => right.Lines.CompareTo(left.Lines));

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

        // 超限是**失败**，不是警告。
        //
        // 此前这里把每个热点写成「警告：热点 …」，紧接着又因为 hotspots.Count > 0 抛异常
        // 终止整轮发布；而抛出的那句话里不含任何文件名——文件名只在前面那几行「警告」里。
        // 使用者读到「警告」以为可以继续，实际发布已经停了，且拿不到该改哪个文件。
        foreach (var hotspot in hotspots)
            log.WriteLine($"错误：热点 {hotspot.Path}（{hotspot.Lines} 行）；按职责拆分。");

        if (hotspots.Count > 0)
        {
            violations.Add(
                $"生产文件超过 {HotspotLimit} 行质量上限：" + Environment.NewLine + "  "
                + string.Join(
                    Environment.NewLine + "  ",
                    hotspots.Select(item => $"{item.Path}（{item.Lines} 行）")));
        }

        if (violations.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, violations));

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
