using HistoryVulcan.Core;
using HistoryVulcan.Services.Modules;
using System.Windows.Controls;
using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.Shell.Views;

/// <summary>
/// 模块管理页(V2.1.1):已装载模块清单。
/// 数据经 vulcan.module.list 消费(Data = ModuleMeta 列表);动作按钮全部经总线
/// (vulcan.module.reload / vulcan.module.open)。命令详情统一由命令集页面提供。
/// </summary>
public partial class ModulesView : UserControl
{
    private readonly Func<CommandBus?> _busAccessor;
    private bool _initialLoadDone;

    public ModulesView(Func<CommandBus?> busAccessor)
    {
        InitializeComponent();
        _busAccessor = busAccessor;
        // 0.4.4 上抛框架:内联首次加载守卫,不再依赖 App 层 ViewKit(停靠重排会反复触发 Loaded)
        Loaded += async (_, _) =>
        {
            if (_initialLoadDone)
                return;
            _initialLoadDone = true;
            await RefreshAsync();
        };
    }

    public sealed record ModuleRow(
        string ModuleName, string Version, string Mode, int CommandCount,
        string AssemblyFile, string Description);

    public sealed record CommandRow(string Name, string Summary, string Example);

    private async void OnReloadClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var bus = _busAccessor();
        if (bus == null)
            return;
        RefreshButton.IsEnabled = false;
        try
        {
            var result = await bus.ExecuteAsync("vulcan.module.reload", "UI");
            if (!result.Success)
            {
                ClearModules("模块重载失败: " + FirstLine(result.Message));
                return;
            }
            await RefreshAsync();
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async void OnOpenDirClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var bus = _busAccessor();
        if (bus == null)
        {
            StatusText.Text = "命令总线尚未就绪";
            return;
        }

        OpenDirButton.IsEnabled = false;
        try
        {
            var result = await bus.ExecuteAsync("vulcan.module.open", "UI");
            if (!result.Success)
                StatusText.Text = FirstLine(result.Message);
        }
        finally
        {
            OpenDirButton.IsEnabled = true;
        }
    }

    private async Task RefreshAsync()
    {
        var bus = _busAccessor();
        if (bus == null)
        {
            ClearModules("命令总线尚未就绪");
            return;
        }

        RefreshButton.IsEnabled = false;
        ClearModules("正在扫描 Z 模块...");
        try
        {
            var result = await ModuleCatalogReader.LoadModulesAsync(bus);
            if (result.Success && result.Snapshot is { } snapshot)
            {
                var rows = snapshot.Modules.Select(m => new ModuleRow(
                        m.ModuleName, m.Version, m.Open ? "全暴露" : "精准暴露",
                        m.CommandCount, m.AssemblyFile, m.Description))
                    .ToList();
                ModuleList.ItemsSource = rows;
                StatusText.Text = rows.Count == 0
                    ? "当前无已装载模块；请检查 vulcan.module.roots 与 Z manifest 诊断"
                    : $"已装载 {rows.Count} 个模块,共 {snapshot.Modules.Sum(module => module.CommandCount)} 条模块指令";
            }
            else
            {
                ClearModules(result.Message);
            }
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void ClearModules(string status)
    {
        ModuleList.ItemsSource = null;
        StatusText.Text = status;
    }

    private static string FirstLine(string value)
        => value.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? value;
}
