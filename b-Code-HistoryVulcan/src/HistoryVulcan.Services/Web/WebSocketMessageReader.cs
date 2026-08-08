using System.Net.WebSockets;

namespace HistoryVulcan.Services.Web;

internal static class WebSocketMessageReader
{
    internal const int MaximumMessageBytes = 1_048_576;

    internal static async Task<byte[]?> ReceiveTextAsync(
        WebSocket socket,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var payload = new MemoryStream();
            WebSocketMessageType? messageType = null;
            while (true)
            {
                var received = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                    return null;
                messageType ??= received.MessageType;
                if (messageType != received.MessageType)
                    throw new InvalidDataException("WebSocket 分片消息类型不一致");

                payload.Write(buffer, 0, received.Count);
                if (payload.Length > MaximumMessageBytes)
                    throw new InvalidDataException("WebSocket 消息超过 1 MiB 上限");
                if (received.EndOfMessage)
                    break;
            }

            if (messageType == WebSocketMessageType.Text)
                return payload.ToArray();
        }
    }
}
