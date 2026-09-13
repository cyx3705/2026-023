namespace HistoryVulcan.Core.Commands;

/// <summary>一次请求持有解析、描述符和脱敏快照；处理器修改注册表不影响本次审计。</summary>
internal sealed class CommandRequest
{
    private const string Redacted = "[REDACTED]";
    private readonly HashSet<int> _sensitivePositions = [];
    private readonly string[] _secrets;
    private readonly bool _secretSetting;
    private readonly bool _sensitiveData;

    private CommandRequest(string text, ParsedCommand? parsed, string? syntaxError, string name, CommandRegistry registry)
    {
        Text = text;
        Parsed = parsed;
        SyntaxError = syntaxError;
        Name = name;
        (Descriptor, Domain, CommandClass) = registry.ResolveRequest(name);
        if (parsed == null)
        {
            _secrets = [];
            return;
        }

        var key = parsed.Named.GetValueOrDefault("key") ?? parsed.Positionals.FirstOrDefault();
        var sensitiveKey = key != null && SensitiveName.IsSensitive(key);
        _secretSetting = name.Equals("vulcan.app.set", StringComparison.OrdinalIgnoreCase) && sensitiveKey;
        if (_secretSetting)
            _sensitivePositions.Add(1);
        if (SensitiveName.IsSensitive(name))
            _sensitivePositions.Add(0);
        if (Descriptor != null)
        {
            // 与绑定共用排序；Position 可以不连续，不能用声明的数值直接当输入下标。
            var positions = CommandArguments.Positions(Descriptor);
            for (var i = 0; i < positions.Count; i++)
                if (SensitiveName.IsSensitive(positions[i].Name))
                    _sensitivePositions.Add(i);
        }

        _secrets = parsed.Named.Where(pair => SensitiveParameter(pair.Key)).Select(pair => pair.Value)
            .Concat(parsed.Positionals.Where((_, index) => _sensitivePositions.Contains(index)))
            .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length).ToArray();
        _sensitiveData = _secrets.Length > 0
            || (name.Equals("vulcan.app.get", StringComparison.OrdinalIgnoreCase) && sensitiveKey);
    }

    internal string Text { get; }
    internal ParsedCommand? Parsed { get; }
    internal string? SyntaxError { get; }
    internal string Name { get; }
    internal CommandDescriptor? Descriptor { get; }
    internal string Domain { get; }
    internal string CommandClass { get; }

    internal static CommandRequest Create(string text, CommandRegistry registry)
    {
        var trimmed = text.Trim();
        try
        {
            var parsed = CommandParser.Parse(trimmed);
            return new CommandRequest(trimmed, parsed, null, parsed.Name, registry);
        }
        catch (CommandSyntaxException ex)
        {
            var separator = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
            return new CommandRequest(trimmed, null, $"语法错误: {ex.Message}",
                separator >= 0 ? trimmed[..separator] : trimmed, registry);
        }
    }

    internal string DisplayText()
    {
        if (Parsed is not { } parsed)
            return Text.Length == Name.Length ? Text : Name + " " + Redacted;
        var parts = new List<string> { Name };
        parts.AddRange(parsed.Positionals.Select((value, index) =>
            CommandParser.QuoteArg(_sensitivePositions.Contains(index) ? Redacted : value)));
        parts.AddRange(parsed.Named.Select(pair =>
            $"{pair.Key}={CommandParser.QuoteArg(SensitiveParameter(pair.Key) ? Redacted : pair.Value)}"));
        return string.Join(' ', parts);
    }

    internal string Mask(string text)
    {
        foreach (var secret in _secrets)
            text = text.Replace(secret, Redacted, StringComparison.Ordinal);
        return text;
    }

    internal CommandResult RedactResult(CommandResult result)
    {
        if (Parsed == null)
            return result;
        var message = Mask(result.Message);
        var data = result.Data is string text ? Mask(text) : _sensitiveData ? null : result.Data;
        return message.Equals(result.Message, StringComparison.Ordinal) && ReferenceEquals(data, result.Data)
            ? result
            : new CommandResult { Success = result.Success, Message = message, Data = data };
    }

    private bool SensitiveParameter(string name)
        => SensitiveName.IsSensitive(name)
           || (_secretSetting && name.Equals("value", StringComparison.OrdinalIgnoreCase));
}
