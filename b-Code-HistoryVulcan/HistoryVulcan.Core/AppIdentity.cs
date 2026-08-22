using System.Reflection;

namespace HistoryVulcan.Core;

/// <summary>应用名称与版本的运行时单一真值，由入口程序集的元数据生成。</summary>
public sealed record ApplicationIdentity(
    string Name,
    string Version,
    string InformationalVersion,
    string FileVersion);

/// <summary>
/// 从程序集元数据读取应用身份（0.4.4 由 HistoryJanus 反哺）。
/// 派生应用无需自建身份类：Shell 装配时默认取入口程序集，
/// 也可用 <see cref="From"/> 指定程序集。
/// </summary>
public static class AppIdentity
{
    private static ApplicationIdentity? _current;

    /// <summary>默认取入口程序集；首次访问后缓存。</summary>
    public static ApplicationIdentity Current =>
        _current ??= From(Assembly.GetEntryAssembly()
                          ?? throw new InvalidOperationException("无法确定入口程序集，请显式调用 AppIdentity.Use"));

    /// <summary>显式指定身份来源程序集（测试宿主或多程序集场景）。</summary>
    public static void Use(Assembly assembly) => _current = From(assembly);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static ApplicationIdentity From(Assembly assembly)
    {
        var product = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product?.Trim();
        var title = assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title?.Trim();
        if (string.IsNullOrWhiteSpace(product) || !string.Equals(product, title, StringComparison.Ordinal))
            throw new InvalidOperationException("程序集 Product 与 Title 必须是同一个非空应用名");

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Trim();
        if (string.IsNullOrWhiteSpace(informational))
            throw new InvalidOperationException("程序集 InformationalVersion 不能为空");

        var version = informational.Split('+', 2)[0];
        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version?.Trim()
                          ?? assembly.GetName().Version?.ToString()
                          ?? version;

        return new ApplicationIdentity(product, version, informational, fileVersion);
    }
}
