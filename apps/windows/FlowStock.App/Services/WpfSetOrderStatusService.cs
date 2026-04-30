using System.Globalization;
using System.Net.Http;
using FlowStock.Core.Models;

namespace FlowStock.App;

public sealed class WpfSetOrderStatusService
{
    private readonly SettingsService _settings;
    private readonly FileLogger _logger;
    private readonly SetOrderStatusApiClient _apiClient;

    public WpfSetOrderStatusService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
        _apiClient = new SetOrderStatusApiClient();
    }

    public WpfServerSetOrderStatusConfiguration GetEffectiveConfiguration()
    {
        return LoadConfiguration();
    }

    public async Task<WpfSetOrderStatusResult> SetStatusAsync(long orderId, OrderStatus status, CancellationToken cancellationToken = default)
    {
        var configuration = LoadConfiguration();
        var request = new SetOrderStatusApiRequest
        {
            Status = OrderStatusMapper.StatusToString(status)
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(configuration.RequestTimeoutSeconds));

        SetOrderStatusApiCallResult apiCall;
        try
        {
            apiCall = await _apiClient.SetStatusAsync(
                    new ServerCloseClientOptions
                    {
                        BaseUrl = configuration.BaseUrl,
                        AllowInvalidTls = configuration.AllowInvalidTls
                    },
                    orderId,
                    request,
                    timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Warn($"WPF server set-order-status timed out for order_id={orderId}");
            return WpfSetOrderStatusResult.Failure(
                WpfSetOrderStatusResultKind.Timeout,
                "Сервер не ответил вовремя при смене статуса заказа. Обновите заказ и проверьте фактический статус перед повтором.",
                ex);
        }
        catch (HttpRequestException ex) when (SetOrderStatusApiClient.IsTlsFailure(ex))
        {
            _logger.Error($"TLS failure during WPF server set-order-status for order_id={orderId}", ex);
            return WpfSetOrderStatusResult.Failure(
                WpfSetOrderStatusResultKind.InvalidConfiguration,
                "Ошибка TLS при обращении к серверу. Проверьте сертификат или настройку Allow invalid TLS.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.Error($"WPF server set-order-status request failed for order_id={orderId}", ex);
            return WpfSetOrderStatusResult.Failure(
                WpfSetOrderStatusResultKind.ServerUnavailable,
                "Не удалось связаться с сервером смены статуса заказов. Проверьте, что FlowStock.Server запущен и доступен.",
                ex);
        }
        catch (Exception ex)
        {
            _logger.Error($"Unexpected WPF server set-order-status failure for order_id={orderId}", ex);
            return WpfSetOrderStatusResult.Failure(
                WpfSetOrderStatusResultKind.UnexpectedError,
                "Не удалось изменить статус заказа через сервер. Подробности записаны в лог.",
                ex);
        }

        if (apiCall.IsTransportFailure)
        {
            var kind = apiCall.TransportFailureKind switch
            {
                SetOrderStatusTransportFailureKind.InvalidConfiguration => WpfSetOrderStatusResultKind.InvalidConfiguration,
                SetOrderStatusTransportFailureKind.Timeout => WpfSetOrderStatusResultKind.Timeout,
                SetOrderStatusTransportFailureKind.Network => WpfSetOrderStatusResultKind.ServerUnavailable,
                SetOrderStatusTransportFailureKind.InvalidResponse => WpfSetOrderStatusResultKind.InvalidResponse,
                _ => WpfSetOrderStatusResultKind.UnexpectedError
            };

            return WpfSetOrderStatusResult.Failure(
                kind,
                apiCall.TransportErrorMessage ?? "Не удалось выполнить server set-order-status.",
                apiCall.TransportException);
        }

        if (apiCall.Response != null)
        {
            if (!apiCall.Response.Ok)
            {
                return WpfSetOrderStatusResult.Failure(
                    WpfSetOrderStatusResultKind.ValidationFailed,
                    "Сервер отклонил смену статуса заказа.");
            }

            if (apiCall.Response.OrderId <= 0 || string.IsNullOrWhiteSpace(apiCall.Response.Status))
            {
                return WpfSetOrderStatusResult.Failure(
                    WpfSetOrderStatusResultKind.InvalidResponse,
                    "Сервер вернул неполный ответ при смене статуса заказа.");
            }

            return WpfSetOrderStatusResult.Success(apiCall.Response);
        }

        return MapHttpError(apiCall);
    }

    private static WpfSetOrderStatusResult MapHttpError(SetOrderStatusApiCallResult apiCall)
    {
        var errorCode = apiCall.Error?.Error;
        var message = errorCode switch
        {
            "ORDER_NOT_FOUND" => "Сервер не нашел указанный заказ.",
            "INVALID_STATUS" => "Сервер отклонил статус заказа.",
            "ORDER_CANCEL_FORBIDDEN" => "Выполненный заказ нельзя отменить.",
            "ORDER_STATUS_SHIPPED_FORBIDDEN" => "Статус \"Отгружен/Завершен\" ставится автоматически.",
            "ORDER_STATUS_INVALID_TARGET" => "Допустимы только статусы \"Принят\" и \"В процессе\".",
            "ORDER_STATUS_CHANGE_FORBIDDEN" => "Заказ в конечном статусе нельзя менять вручную.",
            "ORDER_STATUS_MANUAL_DISABLED" => "Ручная смена статуса отключена. Статус считается автоматически по выпуску и отгрузке.",
            _ => string.IsNullOrWhiteSpace(errorCode)
                ? $"Сервер вернул ошибку {(int?)apiCall.StatusCode ?? 0}."
                : $"Сервер вернул ошибку: {errorCode}"
        };

        var kind = errorCode switch
        {
            "ORDER_NOT_FOUND" => WpfSetOrderStatusResultKind.NotFound,
            "INVALID_STATUS" => WpfSetOrderStatusResultKind.ValidationFailed,
            "ORDER_CANCEL_FORBIDDEN" => WpfSetOrderStatusResultKind.ValidationFailed,
            "ORDER_STATUS_SHIPPED_FORBIDDEN" => WpfSetOrderStatusResultKind.ValidationFailed,
            "ORDER_STATUS_INVALID_TARGET" => WpfSetOrderStatusResultKind.ValidationFailed,
            "ORDER_STATUS_CHANGE_FORBIDDEN" => WpfSetOrderStatusResultKind.ValidationFailed,
            "ORDER_STATUS_MANUAL_DISABLED" => WpfSetOrderStatusResultKind.ValidationFailed,
            _ => WpfSetOrderStatusResultKind.ServerRejected
        };

        return WpfSetOrderStatusResult.Failure(kind, message);
    }

    private WpfServerSetOrderStatusConfiguration LoadConfiguration()
    {
        var settings = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = NormalizeBaseUrl(ReadEnvOrSettings("FLOWSTOCK_SERVER_BASE_URL", settings.BaseUrl) ?? WpfCloseDocumentService.DefaultServerBaseUrl);
        var timeoutSeconds = ReadEnvInt("FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS") ?? settings.CloseTimeoutSeconds;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = WpfCloseDocumentService.DefaultCloseTimeoutSeconds;
        }

        var allowInvalidTls = ReadEnvBool("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS") ?? settings.AllowInvalidTls;
        return new WpfServerSetOrderStatusConfiguration(baseUrl, timeoutSeconds, allowInvalidTls);
    }

    private static string NormalizeBaseUrl(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }

        return trimmed.TrimEnd('/');
    }

    private static string? ReadEnvOrSettings(string envKey, string? settingsValue)
    {
        var env = Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        return string.IsNullOrWhiteSpace(settingsValue) ? null : settingsValue.Trim();
    }

    private static bool? ReadEnvBool(string envKey)
    {
        var env = Environment.GetEnvironmentVariable(envKey);
        if (string.IsNullOrWhiteSpace(env))
        {
            return null;
        }

        return env.Trim().ToLowerInvariant() switch
        {
            "1" => true,
            "true" => true,
            "yes" => true,
            "on" => true,
            "0" => false,
            "false" => false,
            "no" => false,
            "off" => false,
            _ => null
        };
    }

    private static int? ReadEnvInt(string envKey)
    {
        var env = Environment.GetEnvironmentVariable(envKey);
        if (string.IsNullOrWhiteSpace(env))
        {
            return null;
        }

        return int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}

public sealed record WpfServerSetOrderStatusConfiguration(
    string BaseUrl,
    int RequestTimeoutSeconds,
    bool AllowInvalidTls);

public sealed class WpfSetOrderStatusResult
{
    public WpfSetOrderStatusResultKind Kind { get; init; }
    public string Message { get; init; } = string.Empty;
    public SetOrderStatusApiResponse? Response { get; init; }
    public Exception? Exception { get; init; }

    public bool IsSuccess => Kind == WpfSetOrderStatusResultKind.StatusChanged;

    public static WpfSetOrderStatusResult Success(SetOrderStatusApiResponse response)
    {
        return new WpfSetOrderStatusResult
        {
            Kind = WpfSetOrderStatusResultKind.StatusChanged,
            Response = response
        };
    }

    public static WpfSetOrderStatusResult Failure(
        WpfSetOrderStatusResultKind kind,
        string message,
        Exception? exception = null)
    {
        return new WpfSetOrderStatusResult
        {
            Kind = kind,
            Message = message,
            Exception = exception
        };
    }
}

public enum WpfSetOrderStatusResultKind
{
    StatusChanged,
    ValidationFailed,
    NotFound,
    ServerRejected,
    ServerUnavailable,
    Timeout,
    InvalidConfiguration,
    InvalidResponse,
    UnexpectedError
}
