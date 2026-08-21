using System.Net;
using System.Text;

namespace HistoryVulcan.Services;

/// <summary>
/// 读请求体并按严格 UTF-8 解码，带 1 MiB 上限。
/// </summary>
/// <remarks>
/// 与 <see cref="LoopbackHttpTransport"/> 同属传输层零件，随 MCP 网关的迁出一并作废：
/// Web 网关搬到 HistoryPortunus 时它已在那边有一份同源副本，这里保留是因为
/// <c>McpGateway</c> 还没搬，而模块够不着宿主的 <c>internal</c>。
/// **第 2 轮搬完 MCP 后连同 <c>LoopbackHttpTransport</c> 一起删除。**
/// </remarks>
internal static class HttpRequestBodyReader
{
    internal const int MaximumBodyBytes = 1_048_576;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async Task<string> ReadUtf8Async(
        HttpListenerRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ContentLength64 > MaximumBodyBytes)
            throw new InvalidDataException("请求体超过 1 MiB 上限");

        using var payload = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await request.InputStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            payload.Write(buffer, 0, read);
            if (payload.Length > MaximumBodyBytes)
                throw new InvalidDataException("请求体超过 1 MiB 上限");
        }

        var bytes = payload.ToArray();
        var offset = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        return StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
    }
}
