namespace HistoryVulcan.Services.Development.Pipeline;

/// <summary>发布前从 MSBuild 查询模块的真实目标框架和输出目录。</summary>
internal static class MsBuildOutputResolver
{
    public static (string TargetFramework, string TargetDirectory) Resolve(
        string projectRoot,
        PackageLayout package,
        TextWriter log)
    {
        var project = Path.Combine(projectRoot, package.Project);
        var properties = ReadProperties(projectRoot, project, []);
        var frameworks = Split(properties.GetValueOrDefault("TargetFrameworks"));
        if (frameworks.Count == 0 && properties.GetValueOrDefault("TargetFramework") is { Length: > 0 } framework)
            frameworks.Add(framework);
        if (frameworks.Count == 0)
            throw new InvalidOperationException($"项目未声明 TargetFramework 或 TargetFrameworks：{project}");

        var configured = package.PublishTargetFramework?.Trim();
        var selected = frameworks.Count switch
        {
            1 when string.IsNullOrWhiteSpace(configured) => frameworks[0],
            1 when configured!.Equals(frameworks[0], StringComparison.OrdinalIgnoreCase) => frameworks[0],
            1 => throw new InvalidOperationException(
                $"发布 TFM 不一致：项目声明={frameworks[0]}；登记声明={configured}；实际输出=未查询。"),
            _ when string.IsNullOrWhiteSpace(configured) => throw new InvalidOperationException(
                $"项目声明多个 TFM：{string.Join(", ", frameworks)}；登记表必须声明 publishTargetFramework。"),
            _ when !frameworks.Contains(configured!, StringComparer.OrdinalIgnoreCase) => throw new InvalidOperationException(
                $"发布 TFM 不一致：项目声明={string.Join(", ", frameworks)}；登记声明={configured}；实际输出=未查询。"),
            _ => configured!,
        };

        var output = ReadProperties(projectRoot, project, ["-p:TargetFramework=" + selected]);
        var targetDir = output.GetValueOrDefault("TargetDir");
        if (string.IsNullOrWhiteSpace(targetDir))
            throw new InvalidOperationException(
                $"无法查询实际输出目录：项目声明={string.Join(", ", frameworks)}；登记声明={configured ?? "<自动>"}；实际输出=<空>。");

        log.WriteLine($"发布 TFM 预检通过：项目={string.Join(",", frameworks)}；选择={selected}；输出={targetDir}");
        return (selected, Path.GetFullPath(targetDir));
    }

    private static Dictionary<string, string> ReadProperties(
        string workingDirectory,
        string project,
        IReadOnlyList<string> extra)
    {
        var arguments = new List<string>
        {
            "msbuild", project, "-nologo", "-getProperty:TargetFramework", "-getProperty:TargetFrameworks",
            "-getProperty:TargetDir",
        };
        arguments.AddRange(extra);
        var output = ToolProcess.Capture("dotnet", arguments, workingDirectory);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;
            values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return values;
    }

    private static List<string> Split(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
}
