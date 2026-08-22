using System.Xml.Linq;
using HistoryVulcan.Core.Logging;

namespace HistoryVulcan.Services.Modules;

/// <summary>
/// 读取编译器生成的 XML 文档文件(模块 DLL 同目录同名 .xml),
/// 把方法的 &lt;summary&gt; / &lt;param&gt; 注释变成指令帮助文本(MD-03)。
/// </summary>
internal sealed class XmlDocs
{
    private readonly List<XElement> _members;

    private XmlDocs(List<XElement> members) => _members = members;

    public static XmlDocs? TryLoad(string dllPath, IShellLog log)
    {
        var xmlPath = Path.ChangeExtension(dllPath, ".xml");
        if (!File.Exists(xmlPath))
            return null;
        try
        {
            var doc = XDocument.Load(xmlPath);
            return new XmlDocs(doc.Descendants("member").ToList());
        }
        catch (Exception ex)
        {
            log.Warn("module", $"读取 XML 注释失败 ({Path.GetFileName(xmlPath)}): {ex.Message}");
            return null;
        }
    }

    public (string Summary, IReadOnlyDictionary<string, string> Params) ForMethod(string ns, string cls, string method)
    {
        // 成员名形如 M:Ns.Cls.Method 或 M:Ns.Cls.Method(System.Int32,...)
        var exact = $"M:{ns}.{cls}.{method}";
        var member = _members.FirstOrDefault(m =>
        {
            var n = m.Attribute("name")?.Value;
            return n == exact || (n != null && n.StartsWith(exact + "(", StringComparison.Ordinal));
        });
        if (member == null)
            return ("", new Dictionary<string, string>());

        var summary = Clean(member.Element("summary")?.Value);
        var prms = member.Elements("param")
            .Where(p => p.Attribute("name")?.Value is { Length: > 0 })
            .ToDictionary(p => p.Attribute("name")!.Value, p => Clean(p.Value));
        return (summary, prms);
    }

    private static string Clean(string? text) =>
        text == null ? "" : string.Join(" ",
            text.Split('\r', '\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())
                .Where(l => l.Length > 0));
}
