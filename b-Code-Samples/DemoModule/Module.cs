using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Modules;

namespace DemoModule;

/// <summary>现行模块接入面：Attach 时只通过总线登记命令。</summary>
public sealed class Module : IModuleContextAware
{
    /// <inheritdoc />
    public void Attach(IModuleContext context)
    {
        context.RegisterCommands(registry =>
        {
            registry.Register(new CommandDescriptor
            {
                Name = "demomodule.calc.add",
                CommandClass = "calc",
                Summary = "两个整数相加。",
                Example = "demomodule.calc.add a=1 b=2",
                Readonly = true,
                Parameters =
                [
                    new ParameterSpec
                    {
                        Name = "a",
                        Description = "第一个加数",
                        Type = ParamType.Int,
                        Required = true,
                    },
                    new ParameterSpec
                    {
                        Name = "b",
                        Description = "第二个加数",
                        Type = ParamType.Int,
                        Required = true,
                    },
                ],
                Annotations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ui.button"] = "true",
                    ["ui.button.label"] = "相加",
                },
                Handler = CommandDescriptor.Sync(ctx =>
                    CommandResult.Ok(Calculator.Add(ctx.GetInt("a"), ctx.GetInt("b")).ToString())),
            });

            registry.Register(new CommandDescriptor
            {
                Name = "demomodule.calc.reverse",
                CommandClass = "calc",
                Summary = "反转字符串。",
                Example = "demomodule.calc.reverse text=OneHistory",
                Readonly = true,
                Parameters =
                [
                    new ParameterSpec
                    {
                        Name = "text",
                        Description = "要反转的文本",
                        Type = ParamType.String,
                        Required = true,
                    },
                ],
                Handler = CommandDescriptor.Sync(ctx =>
                    CommandResult.Ok(Calculator.Reverse(ctx.RequireString("text")))),
            });

            registry.Register(new CommandDescriptor
            {
                Name = "demomodule.host.ready",
                CommandClass = "host",
                Summary = "整轮模块装载完成后由宿主调用一次。",
                Readonly = true,
                HiddenReason = "宿主装载完成通知，不进通用目录",
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ready")),
            });
        });
    }
}
