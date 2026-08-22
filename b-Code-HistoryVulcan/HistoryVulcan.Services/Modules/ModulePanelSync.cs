using System.IO;
using HistoryVulcan.Core.Logging;

namespace HistoryVulcan.Services.Modules;

/// <summary>
/// MD-08 模块旁面板自动发现(MP-01/02):
/// Modules 目录中的 `&lt;模块名&gt;.panel.json` 自动并入 panels 目录
/// (副本名 module-&lt;模块名&gt;.json,前缀便于识别与回收),模块下线时副本移除。
/// 语义遵循框架 P-08 约定:已有面板 vulcan.ui.panelreload 原地刷新即时生效;
/// **全新面板窗口重启后出现**(窗口注册先于停靠系统初始化,框架文档明示)。
/// 面板 id 冲突由 PanelManager 既有校验拒绝并告警(MP-01 后半)。
/// </summary>
public static class ModulePanelSync
{
    private const string CopyPrefix = "module-";

    /// <summary>
    /// 把模块旁 *.panel.json 同步到 panels 目录;返回是否有变化(调用方据此触发 vulcan.ui.panelreload)。
    /// 纯文件级操作,不加载任何 DLL——启动期(窗口创建前)与热重载后均可调用。
    /// </summary>
    public static bool SyncFiles(string modulesDir, string panelsDir, IShellLog log)
    {
        var changed = false;
        try
        {
            Directory.CreateDirectory(panelsDir);

            // 1. 现存模块旁面板 → 复制/更新副本(根目录 + 一层模块槽,V2.2 MH-04)
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(modulesDir))
            {
                var sources = Directory.EnumerateFiles(modulesDir, "*.panel.json")
                    .Concat(Directory.EnumerateDirectories(modulesDir)
                        .SelectMany(d => Directory.EnumerateFiles(d, "*.panel.json")));
                foreach (var source in sources)
                {
                    var moduleName = Path.GetFileName(source)[..^".panel.json".Length];
                    if (moduleName.Length == 0)
                        continue;

                    var copyName = $"{CopyPrefix}{moduleName}.json";
                    wanted.Add(copyName);
                    var target = Path.Combine(panelsDir, copyName);

                    if (!File.Exists(target)
                        || File.GetLastWriteTimeUtc(target) != File.GetLastWriteTimeUtc(source))
                    {
                        File.Copy(source, target, overwrite: true);
                        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
                        log.Info("module", $"模块面板已并入: panels/{copyName} ← {Path.GetFileName(source)}");
                        changed = true;
                    }
                }
            }

            // 2. 源已消失的副本 → 回收(仅动 module- 前缀,不碰用户手写面板)
            foreach (var copy in Directory.EnumerateFiles(panelsDir, $"{CopyPrefix}*.json"))
            {
                if (!wanted.Contains(Path.GetFileName(copy)))
                {
                    File.Delete(copy);
                    log.Info("module", $"模块面板已移除: panels/{Path.GetFileName(copy)}(源模块下线;窗口重启后消失)");
                    changed = true;
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn("module", $"模块面板同步失败(不影响模块本身): {ex.Message}");
        }

        return changed;
    }
}
