using System.Text.Json;
using System.Threading.Channels;
using HistoryVulcan.Core.Modules;

namespace HistoryVulcan.Services.Nodes;

internal sealed record NodeEvent(long Sequence, string Topic, string Module, JsonElement Payload, long Session);

internal sealed partial class NodeRuntime
{
    private readonly Channel<NodeEvent> _events = Channel.CreateBounded<NodeEvent>(128);
    private readonly CancellationTokenSource _eventStop = new();
    private readonly object _eventGate = new();
    private Task? _eventPump;
    private long _eventSequence;
    private long _receivedSequence;
    internal long EventGaps { get; private set; }

    private void Published(BusEvent value)
    {
        if (!Connected || !value.Source.StartsWith("module:", StringComparison.Ordinal)) return;
        var module = value.Source[7..];
        var state = _ownership.State(module);
        if (_bus.FrontendOwner != module && (state.Conflict || state.OwnerNodeId != _identity.NodeId)) return;
        lock (_eventGate)
            if (!_events.Writer.TryWrite(new NodeEvent(++_eventSequence, value.Topic, module, value.Payload, _session)))
                EventGaps++;
    }

    private async Task SendEventsAsync()
    {
        try
        {
            await foreach (var value in _events.Reader.ReadAllAsync(_eventStop.Token).ConfigureAwait(false))
            {
                try
                {
                    if (Connected && value.Session == _session) await RequestAsync("event", value, _eventStop.Token).ConfigureAwait(false);
                    else EventGaps++;
                }
                catch (Exception) { if (_eventStop.IsCancellationRequested) break; EventGaps++; }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void ReceiveEvent(NodeEvent value)
    {
        var state = _ownership.State(value.Module);
        if (!_frontends.Any(item => item.Module == value.Module) && (state.Conflict || state.OwnerNodeId != _peerId))
            throw new InvalidDataException("事件发布节点不拥有该模块。");
        lock (_eventGate)
        {
            if (value.Sequence <= _receivedSequence) throw new InvalidDataException("事件序号倒退。");
            if (value.Sequence != _receivedSequence + 1) EventGaps++;
            _receivedSequence = value.Sequence;
            using var scope = new HistoryVulcan.Core.Commands.NodeExecutionScope(_ => false);
            _bus.Events.Publish(value.Topic, "node:" + _peerId + ":module:" + value.Module, value.Payload);
        }
    }
}
