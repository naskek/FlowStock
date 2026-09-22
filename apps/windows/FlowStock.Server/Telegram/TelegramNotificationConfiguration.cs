using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FlowStock.Server.Telegram;

internal enum TelegramNotificationState
{
    Disabled,
    Misconfigured,
    Enabled
}

internal sealed class TelegramNotificationConfiguration
{
    private TelegramNotificationConfiguration(
        TelegramNotificationState state,
        string? token = null,
        string? chatId = null,
        Uri? proxyUri = null)
    {
        State = state;
        Token = token;
        ChatId = chatId;
        ProxyUri = proxyUri;
    }

    internal TelegramNotificationState State { get; }

    internal string? Token { get; }

    internal string? ChatId { get; }

    internal Uri? ProxyUri { get; }

    internal void LogStartupWarning(ILogger logger)
    {
        if (State == TelegramNotificationState.Misconfigured)
        {
            logger.LogWarning("Telegram notifications are disabled because configuration is incomplete.");
        }
    }

    internal static TelegramNotificationConfiguration Load(
        IConfiguration configuration,
        Func<string, string>? readAllText = null)
    {
        if (!string.Equals(
                configuration["FLOWSTOCK_TELEGRAM_ENABLED"],
                "1",
                StringComparison.Ordinal))
        {
            return new TelegramNotificationConfiguration(TelegramNotificationState.Disabled);
        }

        var tokenFile = configuration["FLOWSTOCK_TELEGRAM_BOT_TOKEN_FILE"]?.Trim();
        var chatId = configuration["FLOWSTOCK_TELEGRAM_CHAT_ID"]?.Trim();
        var proxyValue = configuration["FLOWSTOCK_TELEGRAM_PROXY_URL"]?.Trim();
        if (string.IsNullOrWhiteSpace(tokenFile)
            || string.IsNullOrWhiteSpace(chatId)
            || !Uri.TryCreate(proxyValue, UriKind.Absolute, out var proxyUri)
            || !string.Equals(proxyUri.Scheme, "socks5", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(proxyUri.Host)
            || proxyUri.Port <= 0)
        {
            return new TelegramNotificationConfiguration(TelegramNotificationState.Misconfigured);
        }

        string token;
        try
        {
            token = (readAllText ?? File.ReadAllText)(tokenFile).Trim();
        }
        catch (Exception)
        {
            return new TelegramNotificationConfiguration(TelegramNotificationState.Misconfigured);
        }

        return string.IsNullOrWhiteSpace(token)
            ? new TelegramNotificationConfiguration(TelegramNotificationState.Misconfigured)
            : new TelegramNotificationConfiguration(
                TelegramNotificationState.Enabled,
                token,
                chatId,
                proxyUri);
    }
}
