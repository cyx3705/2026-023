using System.Text;
using System.Text.RegularExpressions;

namespace HistoryVulcan.Core.Commands;

/// <summary>
/// 指令语法解析(§5.1,Q12 已定稿):
/// <code>域.动作[.子动作 ...] [位置参数 ...] [键=值 ...]</code>
/// - 指令名大小写不敏感;参数值保留原始大小写
/// - 含空格的值用双引号包裹,内部引号以 \" 转义(\\ 表示反斜杠本身)
/// - # 开头整行为注释;空行忽略
/// </summary>
public static partial class CommandParser
{
    [GeneratedRegex(@"^[A-Za-z_][\w-]*(\.[A-Za-z_][\w-]*)*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z_][\w-]*$")]
    private static partial Regex KeyPattern();

    /// <summary>该行是否为注释/空行(脚本场景直接跳过)。</summary>
    public static bool IsBlankOrComment(string line)
    {
        var t = line.TrimStart();
        return t.Length == 0 || t.StartsWith('#');
    }

    /// <summary>
    /// 把参数值编码为可回读的指令文本片段(UI 生成等价指令用):
    /// 含空白/引号/等号时用双引号包裹并转义,否则原样返回。
    /// </summary>
    public static string QuoteArg(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c is '"' or '='))
            return value;
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>解析一行指令;语法错误时抛 <see cref="CommandSyntaxException"/>。</summary>
    public static ParsedCommand Parse(string line)
    {
        if (IsBlankOrComment(line))
            throw new CommandSyntaxException("空行或注释不是指令");

        var tokens = Tokenize(line);
        if (tokens.Count == 0)
            throw new CommandSyntaxException("空指令");

        var name = tokens[0].Text;
        if (tokens[0].WasQuoted || !NamePattern().IsMatch(name))
            throw new CommandSyntaxException($"无效的指令名: {name}");

        var positionals = new List<string>();
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in tokens.Skip(1))
        {
            // 仅当 = 前缀是合法标识符且未被引号包裹时视为 键=值
            var eq = token.WasKeyQuoted ? -1 : token.Text.IndexOf('=');
            if (eq > 0 && KeyPattern().IsMatch(token.Text[..eq]))
            {
                var key = token.Text[..eq];
                var value = token.Text[(eq + 1)..];
                if (named.ContainsKey(key))
                    throw new CommandSyntaxException($"参数 {key} 重复");
                named[key] = value;
            }
            else
            {
                positionals.Add(token.Text);
            }
        }

        return new ParsedCommand
        {
            Name = name.ToLowerInvariant(),
            Positionals = positionals,
            Named = named,
            RawText = line.Trim(),
        };
    }

    private readonly record struct Token(string Text, bool WasQuoted, bool WasKeyQuoted);

    /// <summary>
    /// 按空白切词;双引号内的空白不切分,引号在结果中剥除。
    /// WasKeyQuoted 标记“=”之前部分是否曾被引号包裹(避免 "a=b" 被当作键值)。
    /// </summary>
    private static List<Token> Tokenize(string line)
    {
        var tokens = new List<Token>();
        var sb = new StringBuilder();
        var inQuote = false;
        var wasQuoted = false;    // 本 token 是否出现过引号
        var quoteBeforeEq = false; // 首个 = 之前是否出现过引号
        var seenEq = false;

        void Flush()
        {
            if (sb.Length == 0 && !wasQuoted)
                return;
            tokens.Add(new Token(sb.ToString(), wasQuoted, quoteBeforeEq));
            sb.Clear();
            wasQuoted = false;
            quoteBeforeEq = false;
            seenEq = false;
        }

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && inQuote && i + 1 < line.Length && (line[i + 1] == '"' || line[i + 1] == '\\'))
            {
                sb.Append(line[i + 1]);
                i++;
            }
            else if (c == '"')
            {
                inQuote = !inQuote;
                wasQuoted = true;
                if (!seenEq)
                    quoteBeforeEq = true;
            }
            else if (!inQuote && char.IsWhiteSpace(c))
            {
                Flush();
            }
            else
            {
                if (c == '=' && !inQuote)
                    seenEq = true;
                sb.Append(c);
            }
        }

        if (inQuote)
            throw new CommandSyntaxException("引号未闭合");
        Flush();
        return tokens;
    }
}

/// <summary>指令文本语法错误。</summary>
public sealed class CommandSyntaxException(string message) : Exception(message);
