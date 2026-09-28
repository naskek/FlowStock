using System.Net;
using FlowStock.Server.Telegram;
using Microsoft.Extensions.Logging;

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
        var logger = new RecordingLogger<TelegramNotificationWorker>();
        using var worker = new TelegramNotificationWorker(queue, client, logger);

        await worker.StartAsync(CancellationToken.None);
        queue.TryEnqueue();
        queue.TryEnqueue();
        await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, handler.Attempts);
        var warning = Assert.Single(logger.Messages);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("transport was unavailable", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("first attempt fails", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("test-token", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("test-chat", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpFailureIsLoggedWithoutRetryAndWorkerContinues()
    {
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new HttpErrorSequenceHandler(secondAttempt);
        using var client = new TelegramBotClient("test-token", "test-chat", handler);
        var queue = OrderRequestTelegramQueue.CreateEnabled();
        var logger = new RecordingLogger<TelegramNotificationWorker>();
        using var worker = new TelegramNotificationWorker(queue, client, logger);

        await worker.StartAsync(CancellationToken.None);
        queue.TryEnqueue();
        queue.TryEnqueue();
        await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, handler.Attempts);
        var warning = Assert.Single(logger.Messages);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("HTTP response was not successful", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("test-token", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("test-chat", warning.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        internal List<(LogLevel Level, string Message)> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add((logLevel, formatter(state, exception)));
        }
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

    private sealed class HttpErrorSequenceHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _secondAttempt;

        internal HttpErrorSequenceHandler(TaskCompletionSource secondAttempt)
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
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            }

            _secondAttempt.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
