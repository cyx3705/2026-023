namespace HistoryVulcan.Core.Commands;

/// <summary>命令参数、命令名与配置键共用的敏感名称规则。</summary>
internal static class SensitiveName
{
    internal static bool IsSensitive(string name)
    {
        var normalized = name.Replace(".", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal);
        return normalized.Equals("code", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("token", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("password", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("passwd", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("secret", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("privatekey", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("connectionstring", StringComparison.OrdinalIgnoreCase);
    }
}
