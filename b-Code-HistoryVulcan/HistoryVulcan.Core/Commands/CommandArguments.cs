using System.Globalization;

namespace HistoryVulcan.Core.Commands;

internal static class CommandArguments
{
    internal static IReadOnlyList<ParameterSpec> Positions(CommandDescriptor descriptor)
        => descriptor.Parameters.Where(p => p.Position.HasValue).OrderBy(p => p.Position!.Value).ToArray();

    internal static string? Bind(
        CommandDescriptor descriptor,
        ParsedCommand parsed,
        out IReadOnlyDictionary<string, string> values)
    {
        if (descriptor.AllowUnspecifiedParameters)
        {
            values = parsed.Named.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
            return null;
        }

        var bound = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        values = bound;

        // 位置参数 → 声明了 Position 的参数(按序)
        var positionalSpecs = Positions(descriptor);
        if (parsed.Positionals.Count > positionalSpecs.Count)
            return $"多余的位置参数: {string.Join(" ", parsed.Positionals.Skip(positionalSpecs.Count))}";
        for (var i = 0; i < parsed.Positionals.Count; i++)
            bound[positionalSpecs[i].Name] = parsed.Positionals[i];

        // 键=值 参数
        foreach (var (key, value) in parsed.Named)
        {
            var spec = descriptor.Parameters.FirstOrDefault(
                p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (spec == null)
            {
                var known = string.Join(" ", descriptor.Parameters.Select(p => p.Name + "="));
                return $"未知参数: {key}=" + (known.Length > 0 ? $"(可用: {known})" : "(该指令不接受参数)");
            }

            bound[spec.Name] = value;
        }

        // 必填与类型
        foreach (var spec in descriptor.Parameters)
        {
            if (!bound.TryGetValue(spec.Name, out var value))
            {
                if (spec.Required)
                    return $"缺少必填参数: {spec.Name}=";
                continue;
            }

            var typeError = spec.Type switch
            {
                ParamType.Int when !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                    => $"参数 {spec.Name} 应为整数,实际: {value}",
                ParamType.Double when !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                    => $"参数 {spec.Name} 应为数值,实际: {value}",
                ParamType.Bool when !IsBoolText(value)
                    => $"参数 {spec.Name} 应为 true/false,实际: {value}",
                _ => null,
            };
            if (typeError != null)
                return typeError;

            if (spec.AllowedValues is { Length: > 0 }
                && !spec.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                return $"参数 {spec.Name} 取值应为 {string.Join("/", spec.AllowedValues)},实际: {value}";
            }
        }

        return null;
    }

    private static bool IsBoolText(string value)
        => value.Equals("true", StringComparison.OrdinalIgnoreCase)
           || value.Equals("false", StringComparison.OrdinalIgnoreCase)
           || value is "1" or "0"
           || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
           || value.Equals("no", StringComparison.OrdinalIgnoreCase)
           || value.Equals("on", StringComparison.OrdinalIgnoreCase)
           || value.Equals("off", StringComparison.OrdinalIgnoreCase);

}
