namespace HistoryVulcan.Services;

/// <summary>
/// 宿主数据目录：%AppData%/&lt;应用名&gt;/。宿主自己只用日志与设置，外加运行区 Modules/；
/// 布局、面板等界面数据归前端模块，模块数据在宿主给的 ModuleData/&lt;模块名&gt;/（6.0.0 起不再预建这些目录）。
/// </summary>
internal sealed class AppPaths
{
    public AppPaths(string appName, bool createModulesDirectory = true)
        : this(
            appName,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                appName),
            createModulesDirectory)
    {
    }

    /// <summary>以指定目录为根（正式服务的设置与日志在 <c>service/</c> 子根下）。</summary>
    public AppPaths(string appName, string rootDirectory, bool createModulesDirectory = true)
    {
        if (string.IsNullOrWhiteSpace(appName))
            throw new ArgumentException("应用名不能为空", nameof(appName));
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("数据根目录不能为空", nameof(rootDirectory));

        Root = Path.GetFullPath(rootDirectory);
        LogsDir = Path.Combine(Root, "logs");
        ModulesDir = Path.Combine(Root, "Modules");

        if (createModulesDirectory)
            Directory.CreateDirectory(ModulesDir);
    }

    /// <summary>数据根。</summary>
    public string Root { get; }

    /// <summary>滚动日志目录（由日志自己创建）。</summary>
    public string LogsDir { get; }

    /// <summary>运行区：模块完整包的槽位目录。</summary>
    public string ModulesDir { get; }
}
