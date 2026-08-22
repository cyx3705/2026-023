using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Storage;

namespace HistoryVulcan.Services.Development;

/// <summary>
/// 开发路线指令所需的宿主服务。
/// </summary>
/// <remarks>
/// 这条路线迁入宿主前住在 HistoryDiana 里，参数类型是 <c>IModuleContext</c>——
/// 那是**宿主提供给模块**的契约。留着它，等于让宿主自己去实现一份「模块视角」
/// 才能调用自己的代码，方向是反的。
///
/// 所以换成这个只含实际用到的三样的类型：指令总线（装包要经它走）、
/// 设置（项目库根与工作区根可配）、数据根（发布日志落盘）。
/// 少一样都跑不了，多一样就是给这条路线开了它不需要的口子。
/// </remarks>
/// <param name="Bus">权威指令总线。</param>
/// <param name="Settings">宿主设置存储。</param>
/// <param name="DataDirectory">应用数据根，发布日志以它为基准。</param>
internal sealed record DevelopmentContext(
    CommandBus Bus,
    ISettingsService Settings,
    string DataDirectory);
