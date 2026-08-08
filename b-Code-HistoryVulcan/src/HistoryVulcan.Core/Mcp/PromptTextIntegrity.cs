namespace HistoryVulcan.Core.Mcp;

/// <summary>拒绝已在上游丢失、无法由 UTF-8 解码恢复的提示词文本。</summary>
public static class PromptTextIntegrity
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static string ValidateDescription(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            throw new InvalidOperationException("描述不能为空");
        if (text.Length > 2000)
            throw new InvalidOperationException($"描述过长({text.Length} 字符，上限 2000)");
        if (LooksCorrupted(text))
            throw new InvalidOperationException(
                "描述疑似发生编码损坏（包含替换字符或大量连续问号），已拒绝保存；" +
                "请使用 UTF-8 JSON 请求体重新提交");
        return text;
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static bool LooksCorrupted(string text)
    {
        if (text.Contains('\uFFFD'))
            return true;

        var questionMarks = 0;
        var consecutive = 0;
        foreach (var character in text)
        {
            if (character == '?')
            {
                questionMarks++;
                consecutive++;
                if (consecutive >= 3)
                    return true;
            }
            else
            {
                consecutive = 0;
            }
        }

        return questionMarks >= 4 && questionMarks * 4 >= text.Length;
    }
}
