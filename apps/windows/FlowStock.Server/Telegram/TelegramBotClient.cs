using System.Net;
using System.Net.Http.Json;

namespace FlowStock.Server.Telegram;

internal sealed class TelegramBotClient : IDisposable
{
    internal const string MessageText = "Новый заказ. Требуется подтверждение в FlowStock.";
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly string _token;
    private readonly string _chatId;
    private readonly HttpClient _httpClient;

    internal TelegramBotClient(string token, string chatId, HttpMessageHandler handler)
    {
        _token = token;
        _chatId = chatId;
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = RequestTimeout
        };
    }

    internal static TelegramBotClient Create(TelegramNotificationConfiguration configuration)
    {
        if (configuration.State != TelegramNotificationState.Enabled
            || configuration.Token == null
            || configuration.ChatId == null
            || configuration.ProxyUri == null)
        {
            throw new InvalidOperationException("Telegram client requires enabled configuration.");
        }

        return new TelegramBotClient(
            configuration.Token,
            configuration.ChatId,
            CreateProxyHandler(configuration.ProxyUri));
    }

    internal static SocketsHttpHandler CreateProxyHandler(Uri proxyUri)
    {
        return new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new WebProxy(proxyUri)
            {
                BypassProxyOnLocal = false
            }
        };
    }

    internal async Task SendNewOrderRequestAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://api.telegram.org/bot{_token}/sendMessage")
            {
                Content = JsonContent.Create(new
                {
                    chat_id = _chatId,
                    text = MessageText
                })
            };
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Best effort: one failed transport attempt is the end of this notification.
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
