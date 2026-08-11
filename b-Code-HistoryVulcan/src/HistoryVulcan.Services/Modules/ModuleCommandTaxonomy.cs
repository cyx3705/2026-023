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
            ExecutionSite = source.ExecutionSite,
            AllowMcpExecution = source.AllowMcpExecution,
            AllowUnspecifiedParameters = source.AllowUnspecifiedParameters,
            Handler = source.Handler,
        };
}
