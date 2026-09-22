using Microsoft.Extensions.Hosting;

namespace FlowStock.Server.Telegram;

internal sealed class TelegramNotificationWorker : BackgroundService
{
    private readonly OrderRequestTelegramQueue _queue;
    private readonly TelegramBotClient _client;

    internal TelegramNotificationWorker(
        OrderRequestTelegramQueue queue,
        TelegramBotClient client)
    {
        _queue = queue;
        _client = client;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var _ in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await _client.SendNewOrderRequestAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    // Keep the worker alive even if the notification subsystem fails unexpectedly.
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal hosted-service shutdown.
        }
    }
}
