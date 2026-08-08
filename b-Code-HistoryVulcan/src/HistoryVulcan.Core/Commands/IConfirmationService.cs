namespace HistoryVulcan.Core.Commands;

/// <summary>
/// 二次确认通道(§5.2 拦截器链的首个内置拦截器;T-08 / R-06 等危险操作依赖)。
/// Shell 层以模态对话框实现;无 UI 场景(脚本/测试)可注入自动拒绝或自动通过的实现。
/// </summary>
public interface IConfirmationService
{
    /// <summary>返回 true 表示用户确认继续。</summary>
    bool Confirm(string prompt);
}
