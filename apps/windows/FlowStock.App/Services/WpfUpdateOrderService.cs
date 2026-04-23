using System.Globalization;
using System.Net.Http;
using FlowStock.Core.Models;

namespace FlowStock.App;

public sealed class WpfUpdateOrderService
{
    private readonly SettingsService _settings;
    private readonly FileLogger _logger;
    private readonly UpdateOrderApiClient _apiClient;

    public WpfUpdateOrderService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
        _apiClient = new UpdateOrderApiClient();
    }

    public WpfServerUpdateOrderConfiguration GetEffectiveConfiguration()
    {
        return LoadConfiguration();
    }

    public async Task<WpfUpdateOrderResult> UpdateOrderAsync(
        WpfUpdateOrderContext context,
        CancellationToken cancellationToken = default)
    {
        var configuration = LoadConfiguration();
        var request = new UpdateOrderApiRequest
        {
            OrderRef = NormalizeValue(context.RequestedOrderRef),
            Type = OrderStatusMapper.TypeToString(context.OrderType),
            PartnerId = context.OrderType == OrderType.Customer ? context.PartnerId : null,
            DueDate = context.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Status = string.Empty,
            Comment = NormalizeValue(context.Comment),
            Lines = context.Lines
                .Select(line => new UpdateOrderApiLineRequest
                {
                    ItemId = line.ItemId,
                    QtyOrdered = line.QtyOrdered
                })
                .ToList()
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(configuration.RequestTimeoutSeconds));

        UpdateOrderApiCallResult apiCall;
        try
        {
            apiCall = await _apiClient.UpdateAsync(
                    new ServerCloseClientOptions
                    {
                        BaseUrl = configuration.BaseUrl,
                        AllowInvalidTls = configuration.AllowInvalidTls
                    },
                    context.OrderId,
                    request,
                    timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Warn($"WPF server update order timed out for order_id={context.OrderId}");
            return WpfUpdateOrderResult.Failure(
                WpfUpdateOrderResultKind.Timeout,
                "Сервер не ответил вовремя при обновлении заказа. Обновите заказ и проверьте фактическое состояние перед повтором.",
                ex);
        }
        catch (HttpRequestException ex) when (UpdateOrderApiClient.IsTlsFailure(ex))
        {
            _logger.Error($"TLS failure during WPF server update order for order_id={context.OrderId}", ex);
            return WpfUpdateOrderResult.Failure(
                WpfUpdateOrderResultKind.InvalidConfiguration,
                "Ошибка TLS при обращении к серверу. Проверьте сертификат или настройку Allow invalid TLS.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.Error($"WPF server update order request failed for order_id={context.OrderId}", ex);
            return WpfUpdateOrderResult.Failure(
                WpfUpdateOrderResultKind.ServerUnavailable,
                "Не удалось связаться с сервером обновления заказов. Проверьте, что FlowStock.Server запущен и доступен.",
                ex);
        }
        catch (Exception ex)
        {
            _logger.Error($"Unexpected WPF server update order failure for order_id={context.OrderId}", ex);
            return WpfUpdateOrderResult.Failure(
                WpfUpdateOrderResultKind.UnexpectedError,
                "Не удалось обновить заказ через сервер. Подробности записаны в лог.",
                ex);
        }

        if (apiCall.IsTransportFailure)
        {
            if (apiCall.TransportException != null)
            {
                _logger.Error($"Transport failure during WPF server update order for order_id={context.OrderId}", apiCall.TransportException);
            }
            else
            {
                _logger.Warn($"Transport failure during WPF server update order for order_id={context.OrderId}: {apiCall.TransportErrorMessage}");
            }

            var kind = apiCall.TransportFailureKind switch
            {
                UpdateOrderTransportFailureKind.InvalidConfiguration => WpfUpdateOrderResultKind.InvalidConfiguration,
                UpdateOrderTransportFailureKind.Timeout => WpfUpdateOrderResultKind.Timeout,
                UpdateOrderTransportFailureKind.Network => WpfUpdateOrderResultKind.ServerUnavailable,
                UpdateOrderTransportFailureKind.InvalidResponse => WpfUpdateOrderResultKind.InvalidResponse,
                _ => WpfUpdateOrderResultKind.UnexpectedError
            };

            return WpfUpdateOrderResult.Failure(
                kind,
                apiCall.TransportErrorMessage ?? "Не удалось выполнить server update order.",
                apiCall.TransportException);
        }

        if (apiCall.Response != null)
        {
            return MapSuccessResponse(context, apiCall.Response);
        }

        return MapHttpError(apiCall);
    }

    private static WpfUpdateOrderResult MapSuccessResponse(
        WpfUpdateOrderContext context,
        UpdateOrderApiResponse response)
    {
        if (!response.Ok)
        {
            return WpfUpdateOrderResult.Failure(
                WpfUpdateOrderResultKind.ValidationFailed,
                "Сервер отклонил обновление заказа.");
        }

        if (response.OrderId <= 0 || string.IsNullOrWhiteSpace(response.OrderRef) || string.IsNullOrWhiteSpace(response.Status))
        {
            return WpfUpdateOrderResult.Failure(
                WpfUpdateOrderResultKind.InvalidResponse,
                "Сервер вернул неполный ответ при обновлении заказа.");
        }

        var orderRefChanged = response.OrderRefChanged;
        if (!orderRefChanged && !string.IsNullOrWhiteSpace(context.RequestedOrderRef))
        {
            orderRefChanged = !string.Equals(
                NormalizeValue(context.RequestedOrderRef),
                NormalizeValue(response.OrderRef),
                StringComparison.OrdinalIgnoreCase);
        }

        var message = orderRefChanged
            ? $"Сервер заменил номер заказа: {response.OrderRef}"
            : string.Empty;

        return WpfUpdateOrderResult.Success(message, response);
    }

    private static WpfUpdateOrderResult MapHttpError(UpdateOrderApiCallResult apiCall)
    {
        var errorCode = apiCall.Error?.Error;
        var message = errorCode switch
        {
            "ORDER_NOT_FOUND" => "Сервер не нашел указанный заказ.",
            "ORDER_NOT_EDITABLE" => "Сервер не разрешает редактировать этот заказ.",
            "ORDER_TYPE_MISMATCH" => "Смена типа заказа разрешена только между клиентским и внутренним заказом.",
            "ORDER_TYPE_CHANGE_FORBIDDEN" => "Смена типа запрещена: по заказу уже есть проведенные документы текущего типа.",
            "INVALID_TYPE" => "Сервер отклонил тип заказа.",
            "INVALID_STATUS" => "Сервер отклонил статус заказа.",
            "SHIPPED_STATUS_FORBIDDEN" => "Статус \"Отгружен/Завершен\" нельзя задавать вручную.",
            "MISSING_PARTNER_ID" => "Для клиентского заказа нужно выбрать контрагента.",
            "PARTNER_NOT_FOUND" => "Сервер не нашел выбранного контрагента.",
            "PARTNER_IS_SUPPLIER" => "В заказе нельзя выбрать контрагента со статусом \"Поставщик\".",
            "INVALID_DUE_DATE" => "Сервер отклонил дату. Используйте корректную дату срока.",
            "MISSING_LINES" => "Добавьте хотя бы одну строку заказа.",
            "MISSING_ITEM_ID" => "Одна из строк заказа не содержит товара.",
            "INVALID_QTY_ORDERED" => "Количество в строках заказа должно быть больше нуля.",
            "ITEM_NOT_FOUND" => "Сервер не нашел один из товаров заказа.",
            "MISSING_ORDER_REF" => "Номер заказа обязателен.",
            _ => string.IsNullOrWhiteSpace(errorCode)
                ? $"Сервер вернул ошибку {(int?)apiCall.StatusCode ?? 0}."
                : $"Сервер вернул ошибку: {errorCode}"
        };

        var kind = errorCode switch
        {
            "ORDER_NOT_FOUND" => WpfUpdateOrderResultKind.NotFound,
            "ORDER_NOT_EDITABLE" => WpfUpdateOrderResultKind.ValidationFailed,
            "ORDER_TYPE_MISMATCH" => WpfUpdateOrderResultKind.ValidationFailed,
            "ORDER_TYPE_CHANGE_FORBIDDEN" => WpfUpdateOrderResultKind.ValidationFailed,
            "INVALID_TYPE" => WpfUpdateOrderResultKind.ValidationFailed,
            "INVALID_STATUS" => WpfUpdateOrderResultKind.ValidationFailed,
            "SHIPPED_STATUS_FORBIDDEN" => WpfUpdateOrderResultKind.ValidationFailed,
            "MISSING_PARTNER_ID" => WpfUpdateOrderResultKind.ValidationFailed,
            "PARTNER_NOT_FOUND" => WpfUpdateOrderResultKind.ValidationFailed,
            "PARTNER_IS_SUPPLIER" => WpfUpdateOrderResultKind.ValidationFailed,
            "INVALID_DUE_DATE" => WpfUpdateOrderResultKind.ValidationFailed,
            "MISSING_LINES" => WpfUpdateOrderResultKind.ValidationFailed,
            "MISSING_ITEM_ID" => WpfUpdateOrderResultKind.ValidationFailed,
            "INVALID_QTY_ORDERED" => WpfUpdateOrderResultKind.ValidationFailed,
            "ITEM_NOT_FOUND" => WpfUpdateOrderResultKind.ValidationFailed,
            "MISSING_ORDER_REF" => WpfUpdateOrderResultKind.ValidationFailed,
            _ => WpfUpdateOrderResultKind.ServerRejected
        };

        return WpfUpdateOrderResult.Failure(kind, message);
    }

    private WpfServerUpdateOrderConfiguration LoadConfiguration()
    {
        var settings = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = NormalizeBaseUrl(ReadEnvOrSettings("FLOWSTOCK_SERVER_BASE_URL", settings.BaseUrl) ?? WpfCloseDocumentService.DefaultServerBaseUrl);
        var timeoutSeconds = ReadEnvInt("FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS") ?? settings.CloseTimeoutSeconds;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = WpfCloseDocumentService.DefaultCloseTimeoutSeconds;
        }

        var allowInvalidTls = ReadEnvBool("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS") ?? settings.AllowInvalidTls;
        return new WpfServerUpdateOrderConfiguration(baseUrl, timeoutSeconds, allowInvalidTls);
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

    private static string? NormalizeValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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

public sealed record WpfUpdateOrderContext(
    long OrderId,
    string? RequestedOrderRef,
    OrderType OrderType,
    long? PartnerId,
    DateTime? DueDate,
    OrderStatus Status,
    string? Comment,
    IReadOnlyList<OrderLineView> Lines);

public sealed record WpfServerUpdateOrderConfiguration(
    string BaseUrl,
    int RequestTimeoutSeconds,
    bool AllowInvalidTls);

public sealed class WpfUpdateOrderResult
{
    public WpfUpdateOrderResultKind Kind { get; init; }
    public string Message { get; init; } = string.Empty;
    public UpdateOrderApiResponse? Response { get; init; }
    public Exception? Exception { get; init; }

    public bool IsSuccess => Kind == WpfUpdateOrderResultKind.Updated;

    public static WpfUpdateOrderResult Success(string message, UpdateOrderApiResponse response)
    {
        return new WpfUpdateOrderResult
        {
            Kind = WpfUpdateOrderResultKind.Updated,
            Message = message,
            Response = response
        };
    }

    public static WpfUpdateOrderResult Failure(
        WpfUpdateOrderResultKind kind,
        string message,
        Exception? exception = null)
    {
        return new WpfUpdateOrderResult
        {
            Kind = kind,
            Message = message,
            Exception = exception
        };
    }
}

public enum WpfUpdateOrderResultKind
{
    Updated,
    ValidationFailed,
    NotFound,
    ServerRejected,
    ServerUnavailable,
    Timeout,
    InvalidConfiguration,
    InvalidResponse,
    UnexpectedError
}
