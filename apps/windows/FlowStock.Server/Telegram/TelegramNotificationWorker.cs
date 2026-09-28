using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowStock.Server.Telegram;

internal sealed class TelegramNotificationWorker : BackgroundService
{
    private readonly OrderRequestTelegramQueue _queue;
    private readonly TelegramBotClient _client;
    private readonly ILogger<TelegramNotificationWorker> _logger;

    internal TelegramNotificationWorker(
        OrderRequestTelegramQueue queue,
        TelegramBotClient client,
        ILogger<TelegramNotificationWorker> logger)
    {
        _queue = queue;
        _client = client;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var _ in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    var result = await _client.SendNewOrderRequestAsync(stoppingToken);
                    if (result == TelegramDeliveryResult.HttpError)
                    {
                        _logger.LogWarning(
                            "Telegram order notification delivery failed because the HTTP response was not successful.");
                    }
                    else if (result == TelegramDeliveryResult.TransportError)
                    {
                        _logger.LogWarning(
                            "Telegram order notification delivery failed because the transport was unavailable.");
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    _logger.LogWarning(
                        "Telegram order notification worker failed unexpectedly; the notification was dropped.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal hosted-service shutdown.
        }
    }
}
