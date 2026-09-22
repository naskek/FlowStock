using FlowStock.Server.Telegram;
using System.Threading.Channels;

namespace FlowStock.Server.Tests.Telegram;

public sealed class OrderRequestTelegramQueueTests
{
    [Fact]
    public void FullQueueDropsAdditionalNotificationWithoutThrowing()
    {
        var queue = OrderRequestTelegramQueue.CreateEnabled();

        for (var index = 0; index < 101; index++)
        {
            queue.TryEnqueue();
        }

        var count = 0;
        while (queue.Reader.TryRead(out var signal))
        {
            Assert.Equal(OrderRequestTelegramSignal.NewOrderRequest, signal);
            count++;
        }

        Assert.Equal(100, count);
    }

    [Fact]
    public void WriterExceptionCannotEscapeTryEnqueue()
    {
        var reader = Channel.CreateUnbounded<OrderRequestTelegramSignal>().Reader;
        var queue = new OrderRequestTelegramQueue(new ThrowingWriter(), reader);

        var exception = Record.Exception(queue.TryEnqueue);

        Assert.Null(exception);
    }

    [Fact]
    public void DisabledQueueIsNoOp()
    {
        var queue = OrderRequestTelegramQueue.CreateDisabled();

        queue.TryEnqueue();

        Assert.False(queue.Reader.TryRead(out _));
    }

    private sealed class ThrowingWriter : ChannelWriter<OrderRequestTelegramSignal>
    {
        public override bool TryComplete(Exception? error = null) => true;

        public override bool TryWrite(OrderRequestTelegramSignal item) =>
            throw new InvalidOperationException("queue failure");

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
    }
}
