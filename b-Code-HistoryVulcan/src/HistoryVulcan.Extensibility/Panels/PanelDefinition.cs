namespace HistoryVulcan.Extensibility.Panels;

/// <summary>
/// 控制面板声明(§4.5,P-01):JSON 配置文件与 C# 注册 API 共用的信息模型。
///
/// 住在演进层而不是 Core:控件类型与属性是一份注定要生长的数据 schema——每加一种
/// 控件或一个属性都是一次契约变更。它同时是宿主便利功能而非模块地基(零模块消费),
/// 因此不该占用 Core 的冻结额度。
/// 面板 = 一个可停靠工具窗口,内容为若干输入控件 + 指令触发按钮。
/// </summary>
public sealed class PanelDefinition
{
    /// <summary>面板 id,同时是窗口名(vulcan.ui.show name=&lt;id&gt;);小写、进程内唯一。</summary>
    public required string Id { get; set; }

    /// <summary>标题栏显示名。</summary>
    public required string Title { get; set; }

    /// <summary>默认是否可见。</summary>
    public bool Visible { get; set; } = true;

    /// <summary>默认停靠方位:left / right / top / bottom。</summary>
    public string Side { get; set; } = "right";

    /// <summary>默认占主窗体比例。</summary>
    public double Ratio { get; set; } = 0.22;

    /// <summary>控件清单,按声明顺序纵向排布(P-06 简单行列栅格)。</summary>
    public List<PanelControl> Controls { get; set; } = new();
}

/// <summary>
/// 面板内一个控件(P-02 八类):
/// button / text / number / combo / check / slider / file / dir / label。
/// </summary>
public sealed class PanelControl
{
    /// <summary>控件类型(见类注释)。</summary>
    public required string Type { get; set; }

    /// <summary>控件 id:指令模板以 {id} 引用其当前值;button/label 可省略。</summary>
    public string? Id { get; set; }

    /// <summary>左侧标签文本;button 的按钮文字。</summary>
    public string? Label { get; set; }

    /// <summary>button 专用:指令模板,{控件id} 占位引用同面板输入控件当前值(P-03)。</summary>
    public string? Command { get; set; }

    /// <summary>combo 专用:候选项。</summary>
    public List<string>? Items { get; set; }

    /// <summary>number / slider:范围与步进(P-02)。</summary>
    public double? Min { get; set; }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public double? Max { get; set; }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public double? Step { get; set; }

    /// <summary>默认值(文本表达)。</summary>
    public string? Default { get; set; }

    /// <summary>button 可选样式:"danger" 红色强调。</summary>
    public string? Style { get; set; }

    /// <summary>校验(P-04):必填。</summary>
    public bool Required { get; set; }
}
