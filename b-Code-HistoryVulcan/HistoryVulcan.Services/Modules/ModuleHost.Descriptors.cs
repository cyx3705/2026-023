using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;

namespace HistoryVulcan.Services.Modules;

public sealed partial class ModuleHost
{

    private static CommandDescriptor BuildDescriptor(
        Snapshot snap, string commandName, string moduleName, Type type, MethodInfo method,
        string summary, IReadOnlyDictionary<string, string> paramDocs)
    {
        var parameters = new List<ParameterSpec>();
        var example = commandName;
        var ps = method.GetParameters();
        for (var i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            var paramType = MapType(p.ParameterType);
            parameters.Add(new ParameterSpec
            {
                Name = p.Name ?? $"arg{i}",
                Description = paramDocs.GetValueOrDefault(p.Name ?? "", ""),
                Type = paramType,
                Required = !p.HasDefaultValue,
                Default = p.HasDefaultValue ? DefaultText(p.DefaultValue) : null,
                Position = i,
            });
            example += $" {p.Name}={SampleValue(paramType)}";
        }

        return new CommandDescriptor
        {
            Name = commandName,
            Domain = moduleName,
            // 未声明类时留空，交给注册表按名称结构推导（三段取第二段、两段判为无类）。
            // 固定回退 core 会给两段式直接方法凭空安上一个 core 类。
            CommandClass = method.GetCustomAttribute<ModuleCommandAttribute>()?.CommandClass ?? string.Empty,
            Summary = summary.Length > 0 ? summary : $"{moduleName} 模块 {type.Name}.{method.Name} 方法",
            Example = example,
            Parameters = parameters,
            Readonly = method.GetCustomAttribute<ModuleCommandAttribute>()?.Readonly == true,
            Handler = async ctx =>
            {
                var args = BindArgs(method, ctx);
                var target = method.IsStatic ? null : snap.GetInstance(type);
                object? result;
                try
                {
                    result = method.Invoke(target, args);
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    // MD-06:剥掉反射包装,把模块自身异常清晰上抛(总线红字兜底)
                    throw new InvalidOperationException($"模块方法异常: {tie.InnerException.Message}");
                }

                if (result is Task task)
                {
                    await task.ConfigureAwait(false);
                    result = task.GetType().GetProperty("Result")?.GetValue(task);
                    if (result?.GetType().Name == "VoidTaskResult")
                        result = null;
                }

                return CommandResult.Ok(Render(result), result);
            },
        };
    }

    // ---------------------------------------------------------------- 参数绑定与结果渲染(MD-04)

    private static object?[] BindArgs(MethodInfo method, CommandContext ctx)
    {
        var ps = method.GetParameters();
        var args = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            var raw = p.Name != null ? ctx.GetString(p.Name) : null;
            if (raw == null)
            {
                args[i] = p.HasDefaultValue ? p.DefaultValue
                    : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType)
                    : null;
                continue;
            }

            var t = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
            try
            {
                args[i] = t == typeof(string) ? raw
                    : t.IsEnum ? Enum.Parse(t, raw, ignoreCase: true)
                    : t == typeof(bool) ? ctx.GetBool(p.Name!)
                    : Convert.ChangeType(raw, t, CultureInfo.InvariantCulture);
            }
            catch
            {
                throw new ArgumentException($"参数 {p.Name} 类型转换失败,期望 {t.Name},实际 \"{raw}\"");
            }
        }

        return args;
    }

    private static readonly JsonSerializerOptions RenderOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Render(object? result) => result switch
    {
        null => "(无返回值)",
        string s => s,
        _ when result.GetType().IsPrimitive || result is decimal || result is DateTime
            => Convert.ToString(result, CultureInfo.InvariantCulture) ?? "",
        _ => JsonSerializer.Serialize(result, RenderOpts),
    };

    private static ParamType MapType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte))
            return ParamType.Int;
        if (t == typeof(double) || t == typeof(float) || t == typeof(decimal))
            return ParamType.Double;
        if (t == typeof(bool))
            return ParamType.Bool;
        return ParamType.String;
    }

    private static string SampleValue(ParamType t) => t switch
    {
        ParamType.Int => "1",
        ParamType.Double => "1.5",
        ParamType.Bool => "true",
        _ => "文本",
    };

    private static string? DefaultText(object? value) => value switch
    {
        null => null,
        bool b => b ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    // ---------------------------------------------------------------- 鸭子类型契约(MD-02)

    /// <summary>按基类全名判断,模块编译时引用哪个版本的 BaseVariable.dll 都能识别。</summary>
    private static bool IsModuleInfo(Type t)
    {
        for (var b = t.BaseType; b != null; b = b.BaseType)
        {
            if (b.FullName == "BaseVariable.ModuleInfoBase")
                return true;
        }

        return false;
    }

    private static object? GetProp(object o, string name)
    {
        try
        {
            return o.GetType().GetProperty(name)?.GetValue(o);
        }
        catch
        {
            return null;
        }
    }

}
