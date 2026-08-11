using System.IO;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Extensibility.Panels;

namespace HistoryVulcan.Shell.Panels;

/// <summary>
/// 控制窗口群管理器(§4.5):
/// 从 &lt;数据目录&gt;/panels/*.json 加载面板声明(P-01,JSON 为主),
/// 与 C# 注册通道(ShellConfig.Panels)合并;每个面板注册为一个独立
/// 可停靠工具窗口(P-05,窗口名 = 面板 id),随布局一起持久化。
/// vulcan.ui.panelreload 重读 JSON 并原地重建既有面板内容(P-08;新增面板需重启)。
/// </summary>
public sealed class PanelManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _panelsDir;
    private readonly CommandBus _bus;
    private readonly IShellLog _log;
    private readonly Dictionary<string, PanelView> _views = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PanelDefinition> _definitions = new();

    /// <summary>仅供框架命令目录生成；不读取文件，也不得执行面板处理器。</summary>
    internal PanelManager()
    {
        _panelsDir = "";
        _bus = null!;
        _log = null!;
    }

    public PanelManager(string panelsDir, IEnumerable<PanelDefinition>? configured, CommandBus bus, IShellLog log)
    {
        _panelsDir = panelsDir;
        _bus = bus;
        _log = log;
        Directory.CreateDirectory(panelsDir);

        if (configured != null)
            _definitions.AddRange(configured);
        LoadJsonFiles();
    }

    public IReadOnlyList<PanelDefinition> Definitions => _definitions;

    /// <summary>把每个面板注册为工具窗口描述符(启动时调用,先于停靠系统初始化)。</summary>
    public void RegisterWindows(List<ToolWindowDescriptor> windows)
    {
        foreach (var def in _definitions)
        {
            if (windows.Any(w => w.Id.Equals(def.Id, StringComparison.OrdinalIgnoreCase)))
            {
                _log.Error("panel", $"面板 id 与已注册窗口冲突,已跳过: {def.Id}");
                continue;
            }

            var captured = def;
            windows.Add(new ToolWindowDescriptor
            {
                Id = def.Id,
                Title = def.Title,
                DefaultSide = ParseSide(def.Side),
                DefaultRatio = def.Ratio,
                DefaultVisible = def.Visible,
                ContentFactory = () => GetView(captured),
            });
        }
    }

    /// <summary>vulcan.ui.panelset 落点(P-07)。</summary>
    public bool TrySetValue(string panelId, string controlId, string value, out string error)
    {
        error = "";
        if (!_views.TryGetValue(panelId, out var view))
        {
            var known = string.Join(" / ", _definitions.Select(d => d.Id));
            error = $"没有名为 {panelId} 的面板。已定义: {(known.Length > 0 ? known : "(无)")}";
            return false;
        }

        if (!view.TrySetValue(controlId, value))
        {
            error = $"面板 {panelId} 没有可写控件 {controlId}。可用: {string.Join(" / ", view.ControlIds)}";
            return false;
        }

        return true;
    }

    /// <summary>vulcan.ui.panelreload(P-08):重读 JSON,重建既有面板内容;新增面板提示重启。</summary>
    public string Reload()
    {
        var before = _definitions.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _definitions.Clear();
        LoadJsonFiles();

        var rebuilt = 0;
        var pendingRestart = new List<string>();
        foreach (var def in _definitions)
        {
            if (_views.TryGetValue(def.Id, out var view))
            {
                view.Rebuild(def);
                rebuilt++;
            }
            else if (!before.Contains(def.Id))
            {
                pendingRestart.Add(def.Id);
            }
        }

        var message = $"已重载 {rebuilt} 个面板";
        if (pendingRestart.Count > 0)
            message += $";新增面板需重启后生效: {string.Join(" / ", pendingRestart)}";
        return message;
    }

    private PanelView GetView(PanelDefinition def)
    {
        if (!_views.TryGetValue(def.Id, out var view))
        {
            view = new PanelView(def, _bus, _log);
            _views[def.Id] = view;
        }

        return view;
    }

    private void LoadJsonFiles()
    {
        foreach (var file in Directory.EnumerateFiles(_panelsDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var def = JsonSerializer.Deserialize<PanelDefinition>(File.ReadAllText(file), JsonOptions);
                if (def == null || string.IsNullOrWhiteSpace(def.Id))
                {
                    _log.Error("panel", $"面板配置无效(缺 id),已跳过: {Path.GetFileName(file)}");
                    continue;
                }

                if (_definitions.Any(d => d.Id.Equals(def.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    _log.Error("panel", $"面板 id 重复,已跳过: {def.Id}({Path.GetFileName(file)})");
                    continue;
                }

                _definitions.Add(def);
            }
            catch (Exception ex)
            {
                // 单个配置损坏不阻断其余面板(N-05 思想)
                _log.Error("panel", $"面板配置解析失败,已跳过 {Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    private static DockSide ParseSide(string side) => side.ToLowerInvariant() switch
    {
        "left" => DockSide.Left,
        "top" => DockSide.Top,
        "bottom" => DockSide.Bottom,
        "center" => DockSide.Center,
        _ => DockSide.Right,
    };
}
