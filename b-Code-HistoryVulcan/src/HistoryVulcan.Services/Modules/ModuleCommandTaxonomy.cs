using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.Services.Modules;

internal static class ModuleCommandTaxonomy
{
    public static CommandDescriptor Apply(CommandDescriptor source, string moduleName)
        => new()
        {
            Name = source.Name,
            Domain = moduleName,
            // 空类原样保留：两段名是该域的无类直接方法（DEC-025），
            // 这里若替换成 core，无类指令会在命令集里被误报成 core 类。
            CommandClass = source.CommandClass,
            Summary = source.Summary,
            Example = source.Example,
            Parameters = source.Parameters,
            ConfirmPrompt = source.ConfirmPrompt,
            Dangerous = source.Dangerous,
            Readonly = source.Readonly,
            RequiresUiThread = source.RequiresUiThread,
            AllowMcpExecution = source.AllowMcpExecution,
            AllowCliExecution = source.AllowCliExecution,
            AllowUnspecifiedParameters = source.AllowUnspecifiedParameters,
            // 注解必须一并带过来：模块经它声明消费方能力，漏掉这一行等于
            // 模块的注解在进入宿主注册表的那一刻被静默清空。
            Annotations = source.Annotations,
            Handler = source.Handler,
        };
}
