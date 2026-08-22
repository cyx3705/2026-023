using System.Reflection;
using System.Text.Json;
using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.ServiceHost;

/// <summary>
/// 命令行入口：在本进程内执行一条已声明暴露的指令，打印结果后退出。
/// </summary>
/// <remarks>
/// **它不连接任何正在运行的宿主，也不开任何监听。** 它自己装配一份组合根、装载模块、
/// 执行一条指令、退出。这不是省事，是这条路存在的理由：
///
/// 走 MCP 要 agent 的会话活着，走 Web 要 HistoryPortunus 装载成功——而需要用命令行的时刻，
/// 恰恰是「某个模块坏了」或者「宿主起不来」。让恢复通道依赖被恢复的东西，等于没有通道。
/// 本进程内执行不需要端口、不需要令牌、不需要 endpoint.json，也不需要另一个进程活着。
///
/// 代价是**它操作的是磁盘状态，不是活着的宿主**：装包、查指令面都成立，
/// 而「让正在跑的那个宿主重载」不成立——那要走 <c>vulcan.module.install</c>。
/// 两者的分工写在 <see cref="OfflineModuleInstall"/> 的注释里。
///
/// 暴露面见 <see cref="CliExposurePolicy"/>：一份 16 条的名单，恰好是开发管线与模块恢复。
/// 它是三个消费面里唯一不由描述符声明的——理由与那份名单的代价都写在该类注释里。
/// </remarks>
public static class CommandLineRunner
{
    /// <summary>
    /// 执行一条指令。
    /// </summary>
    /// <returns>0 成功；1 指令执行失败；2 参数错误或未声明暴露。</returns>
    public static int Run(string commandText, Assembly identityAssembly)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            Console.Error.WriteLine("需要一条指令文本。");
            return 2;
        }

        ServiceComposition? composition = null;
        try
        {
            var executable = Environment.ProcessPath ?? identityAssembly.Location;
            composition = ServiceComposer.Build(executable, identityAssembly);

            // 先解析、先查声明，**再装载模块**：未声明的指令不该换来一次完整装载的副作用。
            var parsed = CommandParser.Parse(commandText);
            if (string.IsNullOrWhiteSpace(parsed.Name))
            {
                Console.Error.WriteLine("无法从输入中解析出指令名。");
                return 2;
            }

            // 模块指令也可以声明暴露，因此必须先装载才能查到它们。
            // 框架指令在 Build 时就已注册，装载失败不影响它们——
            // 这正是「模块全坏了还能用命令行修」的前提，所以此处只告警不中断。
            try
            {
                composition.Modules?.Start();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"模块装载失败，仅框架指令可用: {ex.Message}");
            }

            // 名单与注册表对账。名单按名字写，指令改名时它会**静默失配**——
            // 而失配的方向最坏：等到某天模块坏掉、需要 --cli 救火时，才发现那条恢复
            // 指令已经不在面上。本体系因为按名字写的规则栽过四次，这里不再赌第五次。
            //
            // 报错而不是跳过：名单缺条目意味着名单本身过期了，此刻执行任何一条都
            // 建立在一份已知不准的判据上。
            var missing = CliExposurePolicy.MissingCommands(composition.Registry);
            if (missing.Count > 0)
            {
                Console.Error.WriteLine(
                    "命令行名单与注册表对不上，以下指令已不存在: " + string.Join("、", missing));
                Console.Error.WriteLine("请更新 CliExposurePolicy.ExposedCommands 后重试。");
                return 2;
            }

            if (!composition.Registry.TryGet(parsed.Name, out _))
            {
                Console.Error.WriteLine($"未知指令: {parsed.Name}");
                return 2;
            }

            if (!CliExposurePolicy.IsExposed(parsed.Name))
            {
                Console.Error.WriteLine(CliExposurePolicy.RefusalReason(parsed.Name));
                return 2;
            }

            var result = composition.Bus
                .ExecuteAsync(commandText, Source)
                .GetAwaiter()
                .GetResult();

            Print(result);
            return result.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"命令行执行失败: {ex.Message}");
            return 1;
        }
        finally
        {
            composition?.Dispose();
        }
    }

    /// <summary>
    /// 来源标签。与 Web 的 <c>Shell:…</c> 明确区分：留痕里要看得出这条是谁下的。
    /// </summary>
    private const string Source = "cli:local";

    private static void Print(CommandResult result)
    {
        if (!string.IsNullOrEmpty(result.Message))
            Console.WriteLine(result.Message);

        // 结构化数据按 JSON 打到标准输出，供脚本消费；没有数据时不打空对象。
        if (result.Data == null)
            return;

        Console.WriteLine(JsonSerializer.Serialize(result.Data, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
    };
}
