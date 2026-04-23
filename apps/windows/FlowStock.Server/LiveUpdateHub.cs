using System.Collections.Concurrent;
using System.Threading.Channels;

namespace FlowStock.Server;

public sealed class LiveUpdateHub
{
    private readonly ConcurrentDictionary<Guid, Channel<LiveUpdateEvent>> _subscribers = new();

    public (Guid SubscriberId, ChannelReader<LiveUpdateEvent> Reader) Subscribe()
    {
        var subscriberId = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<LiveUpdateEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _subscribers[subscriberId] = channel;
        return (subscriberId, channel.Reader);
    }

    public void Unsubscribe(Guid subscriberId)
    {
        if (_subscribers.TryRemove(subscriberId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public void Publish(string reason, string path)
    {
        var evt = new LiveUpdateEvent(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow.ToString("O"),
            string.IsNullOrWhiteSpace(reason) ? "api_write" : reason.Trim(),
            string.IsNullOrWhiteSpace(path) ? "/api" : path.Trim());

        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(evt);
        }
    }
}

public sealed record LiveUpdateEvent(
    string EventId,
    string TsUtc,
    string Reason,
    string Path);
