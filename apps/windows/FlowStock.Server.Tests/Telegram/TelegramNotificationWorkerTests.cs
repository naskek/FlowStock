using System.Net;
using FlowStock.Server.Telegram;

namespace FlowStock.Server.Tests.Telegram;

public sealed class TelegramNotificationWorkerTests
{
    [Fact]
    public async Task TransportFailureDoesNotStopWorkerOrCauseRetry()
    {
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new SequenceHandler(secondAttempt);
        using var client = new TelegramBotClient("test-token", "test-chat", handler);
        var queue = OrderRequestTelegramQueue.CreateEnabled();
        using var worker = new TelegramNotificationWorker(queue, client);

        await worker.StartAsync(CancellationToken.None);
        queue.TryEnqueue();
        queue.TryEnqueue();
        await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, handler.Attempts);
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _secondAttempt;

        internal SequenceHandler(TaskCompletionSource secondAttempt)
        {
            _secondAttempt = secondAttempt;
        }

        internal int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts == 1)
            {
                throw new HttpRequestException("first attempt fails");
            }

            _secondAttempt.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
