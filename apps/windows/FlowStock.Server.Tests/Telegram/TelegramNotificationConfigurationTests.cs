using FlowStock.Server.Telegram;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FlowStock.Server.Tests.Telegram;

public sealed class TelegramNotificationConfigurationTests
{
    [Fact]
    public void EnabledConfigurationReadsAndTrimsConfiguredValues()
    {
        var configuration = CreateConfiguration(
            ("FLOWSTOCK_TELEGRAM_ENABLED", "1"),
            ("FLOWSTOCK_TELEGRAM_BOT_TOKEN_FILE", " /run/secrets/token "),
            ("FLOWSTOCK_TELEGRAM_CHAT_ID", " chat "),
            ("FLOWSTOCK_TELEGRAM_PROXY_URL", " socks5://tailscale-egress:1055 "));

        var result = TelegramNotificationConfiguration.Load(
            configuration,
            path =>
            {
                Assert.Equal("/run/secrets/token", path);
                return " token\n";
            });

        Assert.Equal(TelegramNotificationState.Enabled, result.State);
        Assert.Equal("token", result.Token);
        Assert.Equal("chat", result.ChatId);
        Assert.Equal(new Uri("socks5://tailscale-egress:1055"), result.ProxyUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("01")]
    public void DisabledValuesDoNotReadTokenFile(string? enabled)
    {
        var reads = 0;
        var configuration = CreateConfiguration(
            ("FLOWSTOCK_TELEGRAM_ENABLED", enabled),
            ("FLOWSTOCK_TELEGRAM_BOT_TOKEN_FILE", "/run/secrets/token"),
            ("FLOWSTOCK_TELEGRAM_CHAT_ID", "chat"),
            ("FLOWSTOCK_TELEGRAM_PROXY_URL", "socks5://tailscale-egress:1055"));

        var result = TelegramNotificationConfiguration.Load(
            configuration,
            _ =>
            {
                reads++;
                return "token";
            });

        Assert.Equal(TelegramNotificationState.Disabled, result.State);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void MisconfiguredStartupWarningIsSingleAndDoesNotContainConfigurationOrExceptionDetails()
    {
        const string tokenPath = "/secret/path/token";
        const string chatId = "private-chat";
        const string proxy = "socks5://private-proxy:1055";
        const string exceptionMessage = "private-file-error";
        var configuration = CreateConfiguration(
            ("FLOWSTOCK_TELEGRAM_ENABLED", "1"),
            ("FLOWSTOCK_TELEGRAM_BOT_TOKEN_FILE", tokenPath),
            ("FLOWSTOCK_TELEGRAM_CHAT_ID", chatId),
            ("FLOWSTOCK_TELEGRAM_PROXY_URL", proxy));
        var logger = new RecordingLogger();

        var result = TelegramNotificationConfiguration.Load(
            configuration,
            _ => throw new IOException(exceptionMessage));
        result.LogStartupWarning(logger);

        var warning = Assert.Single(logger.Messages);
        Assert.DoesNotContain(tokenPath, warning, StringComparison.Ordinal);
        Assert.DoesNotContain(chatId, warning, StringComparison.Ordinal);
        Assert.DoesNotContain(proxy, warning, StringComparison.Ordinal);
        Assert.DoesNotContain(exceptionMessage, warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "token", "chat", "socks5://tailscale-egress:1055")]
    [InlineData("/run/secrets/token", " ", "chat", "socks5://tailscale-egress:1055")]
    [InlineData("/run/secrets/token", "token", " ", "socks5://tailscale-egress:1055")]
    [InlineData("/run/secrets/token", "token", "chat", "http://tailscale-egress:1055")]
    [InlineData("/run/secrets/token", "token", "chat", "socks5://tailscale-egress")]
    public void IncompleteEnabledConfigurationIsMisconfigured(
        string? tokenFile,
        string token,
        string chatId,
        string proxy)
    {
        var configuration = CreateConfiguration(
            ("FLOWSTOCK_TELEGRAM_ENABLED", "1"),
            ("FLOWSTOCK_TELEGRAM_BOT_TOKEN_FILE", tokenFile),
            ("FLOWSTOCK_TELEGRAM_CHAT_ID", chatId),
            ("FLOWSTOCK_TELEGRAM_PROXY_URL", proxy));

        var result = TelegramNotificationConfiguration.Load(configuration, _ => token);

        Assert.Equal(TelegramNotificationState.Misconfigured, result.State);
    }

    [Fact]
    public void LegacyTokenEnvironmentValueIsNotAFileFallback()
    {
        var reads = 0;
        var configuration = CreateConfiguration(
            ("FLOWSTOCK_TELEGRAM_ENABLED", "1"),
            ("FLOWSTOCK_TELEGRAM_BOT_TOKEN", "legacy-secret-value"),
            ("FLOWSTOCK_TELEGRAM_CHAT_ID", "chat"),
            ("FLOWSTOCK_TELEGRAM_PROXY_URL", "socks5://tailscale-egress:1055"));

        var result = TelegramNotificationConfiguration.Load(
            configuration,
            _ =>
            {
                reads++;
                return "token";
            });

        Assert.Equal(TelegramNotificationState.Misconfigured, result.State);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void EnabledConfigurationDoesNotLogSecretValues()
    {
        const string token = "private-token-value";
        var configuration = CreateConfiguration(
            ("FLOWSTOCK_TELEGRAM_ENABLED", "1"),
            ("FLOWSTOCK_TELEGRAM_BOT_TOKEN_FILE", "/run/secrets/token"),
            ("FLOWSTOCK_TELEGRAM_CHAT_ID", "private-chat"),
            ("FLOWSTOCK_TELEGRAM_PROXY_URL", "socks5://private-proxy:1055"));
        var logger = new RecordingLogger();

        var result = TelegramNotificationConfiguration.Load(configuration, _ => token);
        result.LogStartupWarning(logger);

        Assert.Equal(TelegramNotificationState.Enabled, result.State);
        Assert.Empty(logger.Messages);
    }

    private static IConfiguration CreateConfiguration(params (string Key, string? Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(x => new KeyValuePair<string, string?>(x.Key, x.Value)))
            .Build();
    }

    private sealed class RecordingLogger : ILogger
    {
        internal List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }
}
