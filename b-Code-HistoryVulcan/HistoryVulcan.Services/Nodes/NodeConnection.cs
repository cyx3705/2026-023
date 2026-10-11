using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;

namespace HistoryVulcan.Services.Nodes;

internal sealed record NodeFrame(string Kind, string Id, string Operation, JsonElement Payload, string? Error = null);

/// <summary>A symmetric request channel over an authenticated stream. Requests are never replayed.</summary>
internal sealed class NodeConnection : IAsyncDisposable
{
    private const int MaximumFrameBytes = 4 * 1024 * 1024;
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<string, JsonElement, CancellationToken, Task<JsonElement>> _handler;
    private readonly Task _reader;
    private int _disposed;
    private int _activeRequests;

    internal NodeConnection(Stream stream, Func<string, JsonElement, CancellationToken, Task<JsonElement>> handler)
    {
        _stream = stream;
        _handler = handler;
        _reader = ReadAsync();
    }

    internal bool Connected => !_stop.IsCancellationRequested;
    internal Task Completion => _reader;

    internal async Task<JsonElement> RequestAsync(
        string operation, object payload, TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (!Connected || _pending.Count >= 128)
            throw new IOException("节点离线或请求队列已满，未发送。");
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
            throw new InvalidOperationException("请求身份重复。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stop.Token);
        deadline.CancelAfter(timeout);
        try
        {
            await SendAsync(new NodeFrame("request", id, operation,
                JsonSerializer.SerializeToElement(payload, NodeStateFile.Json)), deadline.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new IOException("远端请求结果未知：连接断开、超时或取消；未自动重放。");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendAsync(NodeFrame frame, CancellationToken cancellation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, NodeStateFile.Json);
        if (bytes.Length > MaximumFrameBytes)
            throw new InvalidDataException("节点消息超过 4 MiB 上限。");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await _writer.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(header, cancellation).ConfigureAwait(false);
            await _stream.WriteAsync(bytes, cancellation).ConfigureAwait(false);
            await _stream.FlushAsync(cancellation).ConfigureAwait(false);
        }
        catch
        {
            _stop.Cancel();
            throw;
        }
        finally
        {
            _writer.Release();
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            var header = new byte[4];
            while (!_stop.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(header, _stop.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length is <= 0 or > MaximumFrameBytes)
                    throw new InvalidDataException("节点消息长度无效。");
                var bytes = new byte[length];
                await _stream.ReadExactlyAsync(bytes, _stop.Token).ConfigureAwait(false);
                var frame = JsonSerializer.Deserialize<NodeFrame>(bytes, NodeStateFile.Json)
                    ?? throw new InvalidDataException("节点消息为空。");
                if (frame.Kind == "response")
                {
                    if (_pending.TryGetValue(frame.Id, out var completion))
                    {
                        if (frame.Error != null)
                            completion.TrySetException(new InvalidOperationException(frame.Error));
                        else
                            completion.TrySetResult(frame.Payload);
                    }
                }
                else if (frame.Kind == "request")
                {
                    // The reader must remain available for callbacks (confirmation and nested commands).
                    if (Interlocked.Increment(ref _activeRequests) > 128)
                        throw new InvalidDataException("节点并发请求过多。");
                    _ = Task.Run(async () =>
                    {
                        try { await RespondAsync(frame).ConfigureAwait(false); }
                        finally { Interlocked.Decrement(ref _activeRequests); }
                    });
                }
                else
                {
                    throw new InvalidDataException("未知节点消息类型。");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or ObjectDisposedException or ArgumentException)
        {
            // Pending callers receive an explicit unknown outcome below.
        }
        finally
        {
            _stop.Cancel();
            foreach (var pending in _pending.Values)
                pending.TrySetException(new IOException("节点连接已断开，执行结果未知；未自动重放。"));
        }
    }

    private async Task RespondAsync(NodeFrame request)
    {
        try
        {
            JsonElement result;
            string? error = null;
            try
            {
                result = await _handler(request.Operation, request.Payload, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = JsonSerializer.SerializeToElement(new { });
                error = "节点操作失败：" + ex.GetType().Name;
            }
            await SendAsync(new NodeFrame("response", request.Id, request.Operation, result, error), _stop.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException or NotSupportedException)
        {
            _stop.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        await _stream.DisposeAsync().ConfigureAwait(false);
        await _reader.ConfigureAwait(false);
    }
}
