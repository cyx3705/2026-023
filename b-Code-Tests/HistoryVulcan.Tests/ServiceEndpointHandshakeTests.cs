using Xunit;
using VulcanApp = HistoryVulcan.App.App;

namespace HistoryVulcan.Tests;

/// <summary>
/// 前端与后台的 endpoint.json 握手（3.13.0，DEC-046）。
///
/// 这条回归来自一次真机启动：强杀后台会留下 endpoint.json，前端派生新服务后的等待循环
/// 只判 <c>null</c>，于是第一轮就读到残留记录（端口有效、accessToken 为空）并立即退出等待，
/// 拿着空凭据连新服务，稳定 401 后转本地模式（/api/health 显示 shells=0）。
/// 3.13.0 之前这个缺陷是潜伏的：端口恰好相同且后台不校验令牌，连上了就看不出来。
/// </summary>
public sealed class ServiceEndpointHandshakeTests
{
    [Fact]
    public void StaleEndpointWithoutTokenIsNotUsable()
    {
        var stale = new VulcanApp.ServiceEndpoint(8963, "HistoryVulcan.service", ProcessId: 2700);

        Assert.False(VulcanApp.IsUsableEndpoint(stale));
    }

    [Fact]
    public void EndpointWithEmptyTokenIsNotUsable()
    {
        var empty = new VulcanApp.ServiceEndpoint(8963, "HistoryVulcan.service", 2700, AccessToken: "");

        Assert.False(VulcanApp.IsUsableEndpoint(empty));
    }

    [Fact]
    public void EndpointWithoutPortIsNotUsable()
    {
        var portless = new VulcanApp.ServiceEndpoint(0, "HistoryVulcan.service", 2700, AccessToken: "token");

        Assert.False(VulcanApp.IsUsableEndpoint(portless));
    }

    [Fact]
    public void MissingEndpointIsNotUsable()
        => Assert.False(VulcanApp.IsUsableEndpoint(null));

    [Fact]
    public void FreshEndpointWithPortAndTokenIsUsable()
    {
        var fresh = new VulcanApp.ServiceEndpoint(8963, "HistoryVulcan.service", 26972, AccessToken: "m_lWS_vr2hT8");

        Assert.True(VulcanApp.IsUsableEndpoint(fresh));
    }
}
