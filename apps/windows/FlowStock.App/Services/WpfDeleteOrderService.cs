using System.Globalization;
using System.Net.Http;

namespace FlowStock.App;

public sealed class WpfDeleteOrderService
{
    private readonly SettingsService _settings;
    private readonly FileLogger _logger;
    private readonly DeleteOrderApiClient _apiClient;

    public WpfDeleteOrderService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
        _apiClient = new DeleteOrderApiClient();
    }

    public WpfServerDeleteOrderConfiguration GetEffectiveConfiguration()
    {
        return LoadConfiguration();
    }

    public async Task<WpfDeleteOrderResult> DeleteOrderAsync(long orderId, CancellationToken cancellationToken = default)
    {
        var configuration = LoadConfiguration();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(configuration.RequestTimeoutSeconds));

        DeleteOrderApiCallResult apiCall;
        try
        {
            apiCall = await _apiClient.DeleteAsync(
                    new ServerCloseClientOptions
                    {
                        BaseUrl = configuration.BaseUrl,
                        AllowInvalidTls = configuration.AllowInvalidTls
                    },
                    orderId,
                    timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Warn($"WPF server delete order timed out for order_id={orderId}");
            return WpfDeleteOrderResult.Failure(
                WpfDeleteOrderResultKind.Timeout,
                "Сервер не ответил вовремя при удалении заказа. Обновите список заказов и проверьте фактическое состояние перед повтором.",
                ex);
        }
        catch (HttpRequestException ex) when (DeleteOrderApiClient.IsTlsFailure(ex))
        {
            _logger.Error($"TLS failure during WPF server delete order for order_id={orderId}", ex);
            return WpfDeleteOrderResult.Failure(
                WpfDeleteOrderResultKind.InvalidConfiguration,
                "Ошибка TLS при обращении к серверу. Проверьте сертификат или настройку Allow invalid TLS.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.Error($"WPF server delete order request failed for order_id={orderId}", ex);
            return WpfDeleteOrderResult.Failure(
                WpfDeleteOrderResultKind.ServerUnavailable,
                "Не удалось связаться с сервером удаления заказов. Проверьте, что FlowStock.Server запущен и доступен.",
                ex);
        }
        catch (Exception ex)
        {
            _logger.Error($"Unexpected WPF server delete order failure for order_id={orderId}", ex);
            return WpfDeleteOrderResult.Failure(
                WpfDeleteOrderResultKind.UnexpectedError,
                "Не удалось удалить заказ через сервер. Подробности записаны в лог.",
                ex);
        }

        if (apiCall.IsTransportFailure)
        {
            var kind = apiCall.TransportFailureKind switch
            {
                DeleteOrderTransportFailureKind.InvalidConfiguration => WpfDeleteOrderResultKind.InvalidConfiguration,
                DeleteOrderTransportFailureKind.Timeout => WpfDeleteOrderResultKind.Timeout,
                DeleteOrderTransportFailureKind.Network => WpfDeleteOrderResultKind.ServerUnavailable,
                DeleteOrderTransportFailureKind.InvalidResponse => WpfDeleteOrderResultKind.InvalidResponse,
                _ => WpfDeleteOrderResultKind.UnexpectedError
            };

            return WpfDeleteOrderResult.Failure(
                kind,
                apiCall.TransportErrorMessage ?? "Не удалось выполнить server delete order.",
                apiCall.TransportException);
        }

        if (apiCall.Response != null)
        {
            if (!apiCall.Response.Ok)
            {
                return WpfDeleteOrderResult.Failure(
                    WpfDeleteOrderResultKind.ValidationFailed,
                    "Сервер отклонил удаление заказа.");
            }

            if (apiCall.Response.OrderId <= 0)
            {
                return WpfDeleteOrderResult.Failure(
                    WpfDeleteOrderResultKind.InvalidResponse,
                    "Сервер вернул неполный ответ при удалении заказа.");
            }

            return WpfDeleteOrderResult.Success(string.Empty, apiCall.Response);
        }

        return MapHttpError(apiCall);
    }

    private static WpfDeleteOrderResult MapHttpError(DeleteOrderApiCallResult apiCall)
    {
        var errorCode = apiCall.Error?.Error;
        var message = errorCode switch
        {
            "ORDER_NOT_FOUND" => "Сервер не нашел указанный заказ.",
            "ORDER_DELETE_FORBIDDEN_STATUS" => "Удалять можно только заказ в статусе \"Черновик\".",
            "ORDER_HAS_OUTBOUND_DOCS" => "Нельзя удалить заказ: есть отгрузки или связанные документы.",
            "ORDER_HAS_SHIPMENTS" => "Нельзя удалить заказ: по нему уже есть отгрузки.",
            "ORDER_HAS_PRODUCTION_DOCS" => "Нельзя удалить внутренний заказ: есть выпуски продукции или связанные документы.",
            "ORDER_HAS_PRODUCTION_RECEIPTS" => "Нельзя удалить внутренний заказ: по нему уже был выпуск продукции.",
            _ => string.IsNullOrWhiteSpace(errorCode)
                ? $"Сервер вернул ошибку {(int?)apiCall.StatusCode ?? 0}."
                : $"Сервер вернул ошибку: {errorCode}"
        };

        var kind = errorCode switch
        {
            "ORDER_NOT_FOUND" => WpfDeleteOrderResultKind.NotFound,
            "ORDER_DELETE_FORBIDDEN_STATUS" => WpfDeleteOrderResultKind.ValidationFailed,
            "ORDER_HAS_OUTBOUND_DOCS" => WpfDeleteOrderResultKind.ValidationFailed,
            "ORDER_HAS_SHIPMENTS" => WpfDeleteOrderResultKind.ValidationFailed,
            "ORDER_HAS_PRODUCTION_DOCS" => WpfDeleteOrderResultKind.ValidationFailed,
            "ORDER_HAS_PRODUCTION_RECEIPTS" => WpfDeleteOrderResultKind.ValidationFailed,
            _ => WpfDeleteOrderResultKind.ServerRejected
        };

        return WpfDeleteOrderResult.Failure(kind, message);
    }

    private WpfServerDeleteOrderConfiguration LoadConfiguration()
    {
        var settings = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = NormalizeBaseUrl(ReadEnvOrSettings("FLOWSTOCK_SERVER_BASE_URL", settings.BaseUrl) ?? WpfCloseDocumentService.DefaultServerBaseUrl);
        var timeoutSeconds = ReadEnvInt("FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS") ?? settings.CloseTimeoutSeconds;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = WpfCloseDocumentService.DefaultCloseTimeoutSeconds;
        }

        var allowInvalidTls = ReadEnvBool("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS") ?? settings.AllowInvalidTls;
        return new WpfServerDeleteOrderConfiguration(baseUrl, timeoutSeconds, allowInvalidTls);
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

public sealed record WpfServerDeleteOrderConfiguration(
    string BaseUrl,
    int RequestTimeoutSeconds,
    bool AllowInvalidTls);

public sealed class WpfDeleteOrderResult
{
    public WpfDeleteOrderResultKind Kind { get; init; }
    public string Message { get; init; } = string.Empty;
    public DeleteOrderApiResponse? Response { get; init; }
    public Exception? Exception { get; init; }

    public bool IsSuccess => Kind == WpfDeleteOrderResultKind.Deleted;

    public static WpfDeleteOrderResult Success(string message, DeleteOrderApiResponse response)
    {
        return new WpfDeleteOrderResult
        {
            Kind = WpfDeleteOrderResultKind.Deleted,
            Message = message,
            Response = response
        };
    }

    public static WpfDeleteOrderResult Failure(
        WpfDeleteOrderResultKind kind,
        string message,
        Exception? exception = null)
    {
        return new WpfDeleteOrderResult
        {
            Kind = kind,
            Message = message,
            Exception = exception
        };
    }
}

public enum WpfDeleteOrderResultKind
{
    Deleted,
    ValidationFailed,
    NotFound,
    ServerRejected,
    ServerUnavailable,
    Timeout,
    InvalidConfiguration,
    InvalidResponse,
    UnexpectedError
}
