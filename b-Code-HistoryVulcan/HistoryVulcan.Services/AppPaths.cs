namespace HistoryVulcan.Services;

/// <summary>
/// 应用数据目录约定(F-02,Q9 已定):%AppData%/&lt;应用名&gt;/,
/// 布局 / 设置 / 历史 / data / modules / panels / logs 均置于其下。
/// </summary>
public sealed class AppPaths
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public AppPaths(string appName, bool createBusinessDirectories = true)
        : this(
            appName,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                appName),
            createBusinessDirectories)
    {
    }

    /// <summary>Creates an isolated runtime root for a cooperating host process.</summary>
    public AppPaths(string appName, string rootDirectory, bool createBusinessDirectories = true)
    {
        if (string.IsNullOrWhiteSpace(appName))
            throw new ArgumentException("应用名不能为空", nameof(appName));
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("数据根目录不能为空", nameof(rootDirectory));

        Root = Path.GetFullPath(rootDirectory);

        LayoutDir = Path.Combine(Root, "layout");
        DataDir = GetDataDir(Root);
        LogsDir = Path.Combine(Root, "logs");
        ModulesDir = GetModulesDir(Root);
        PanelsDir = GetPanelsDir(Root);

        Directory.CreateDirectory(LayoutDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(PanelsDir);
        if (createBusinessDirectories)
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(ModulesDir);
        }
    }

    /// <summary>%AppData%/&lt;应用名&gt;/</summary>
    public string Root { get; }

    /// <summary>布局文件目录(W-07 / W-08)。</summary>
    public string LayoutDir { get; }

    /// <summary>应用自有业务数据目录。</summary>
    public string DataDir { get; }

    /// <summary>滚动日志目录(L-02,M2 正式接管)。</summary>
    public string LogsDir { get; }

    /// <summary>模块 DLL 与模块槽目录。</summary>
    public string ModulesDir { get; }

    /// <summary>JSON 控制面板目录。</summary>
    public string PanelsDir { get; }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static string GetDataDir(string root) => Path.Combine(root, "data");

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static string GetModulesDir(string root) => Path.Combine(root, "Modules");

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static string GetPanelsDir(string root) => Path.Combine(root, "panels");
}
