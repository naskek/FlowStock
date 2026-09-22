using System.Threading.Channels;

namespace FlowStock.Server.Telegram;

internal enum OrderRequestTelegramSignal
{
    NewOrderRequest
}

internal sealed class OrderRequestTelegramQueue
{
    internal const int Capacity = 100;

    private readonly ChannelWriter<OrderRequestTelegramSignal>? _writer;

    private OrderRequestTelegramQueue(bool enabled)
    {
        var channel = Channel.CreateBounded<OrderRequestTelegramSignal>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false
        });
        Reader = channel.Reader;
        _writer = enabled ? channel.Writer : null;
    }

    internal OrderRequestTelegramQueue(
        ChannelWriter<OrderRequestTelegramSignal> writer,
        ChannelReader<OrderRequestTelegramSignal> reader)
    {
        _writer = writer;
        Reader = reader;
    }

    internal ChannelReader<OrderRequestTelegramSignal> Reader { get; }

    internal static OrderRequestTelegramQueue CreateEnabled() => new(enabled: true);

    internal static OrderRequestTelegramQueue CreateDisabled() => new(enabled: false);

    internal void TryEnqueue()
    {
        try
        {
            _writer?.TryWrite(OrderRequestTelegramSignal.NewOrderRequest);
        }
        catch (Exception)
        {
            // Best effort: notification failures must never escape into the business endpoint.
        }
    }
}
