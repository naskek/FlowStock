using System.Globalization;
using System.Net.Http;
using FlowStock.Core.Models;

namespace FlowStock.App;

public sealed class WpfCreateOrderService
{
    private readonly SettingsService _settings;
    private readonly FileLogger _logger;
    private readonly CreateOrderApiClient _apiClient;

    public WpfCreateOrderService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
        _apiClient = new CreateOrderApiClient();
    }

    public WpfServerCreateOrderConfiguration GetEffectiveConfiguration()
    {
        return LoadConfiguration();
    }

    public async Task<WpfCreateOrderResult> CreateOrderAsync(
        WpfCreateOrderContext context,
        CancellationToken cancellationToken = default)
    {
        var configuration = LoadConfiguration();
        var request = new CreateOrderApiRequest
        {
            OrderRef = NormalizeValue(context.RequestedOrderRef),
            Type = OrderStatusMapper.TypeToString(context.OrderType),
            PartnerId = context.OrderType == OrderType.Customer ? context.PartnerId : null,
            DueDate = context.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Status = OrderStatusMapper.StatusToString(context.Status),
            Comment = NormalizeValue(context.Comment),
            Lines = context.Lines
                .Select(line => new CreateOrderApiLineRequest
                {
                    ItemId = line.ItemId,
                    QtyOrdered = line.QtyOrdered
                })
                .ToList()
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(configuration.RequestTimeoutSeconds));

        CreateOrderApiCallResult apiCall;
        try
        {
            apiCall = await _apiClient.CreateAsync(
                    new ServerCloseClientOptions
                    {
                        BaseUrl = configuration.BaseUrl,
                        AllowInvalidTls = configuration.AllowInvalidTls
                    },
                    request,
                    timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Warn("WPF server create order timed out");
            return WpfCreateOrderResult.Failure(
                WpfCreateOrderResultKind.Timeout,
                "Сервер не ответил вовремя при создании заказа. Проверьте, не был ли заказ уже создан, затем повторите.",
                ex);
        }
        catch (HttpRequestException ex) when (CreateOrderApiClient.IsTlsFailure(ex))
        {
            _logger.Error("TLS failure during WPF server create order", ex);
            return WpfCreateOrderResult.Failure(
                WpfCreateOrderResultKind.InvalidConfiguration,
                "Ошибка TLS при обращении к серверу. Проверьте сертификат или настройку Allow invalid TLS.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.Error("WPF server create order request failed", ex);
            return WpfCreateOrderResult.Failure(
                WpfCreateOrderResultKind.ServerUnavailable,
                "Не удалось связаться с сервером создания заказов. Проверьте, что FlowStock.Server запущен и доступен.",
                ex);
        }
        catch (Exception ex)
        {
            _logger.Error("Unexpected WPF server create order failure", ex);
            return WpfCreateOrderResult.Failure(
                WpfCreateOrderResultKind.UnexpectedError,
                "Не удалось создать заказ через сервер. Подробности записаны в лог.",
                ex);
        }

        if (apiCall.IsTransportFailure)
        {
            if (apiCall.TransportException != null)
            {
                _logger.Error("Transport failure during WPF server create order", apiCall.TransportException);
            }
            else
            {
                _logger.Warn($"Transport failure during WPF server create order: {apiCall.TransportErrorMessage}");
            }

            var kind = apiCall.TransportFailureKind switch
            {
                CreateOrderTransportFailureKind.InvalidConfiguration => WpfCreateOrderResultKind.InvalidConfiguration,
                CreateOrderTransportFailureKind.Timeout => WpfCreateOrderResultKind.Timeout,
                CreateOrderTransportFailureKind.Network => WpfCreateOrderResultKind.ServerUnavailable,
                CreateOrderTransportFailureKind.InvalidResponse => WpfCreateOrderResultKind.InvalidResponse,
                _ => WpfCreateOrderResultKind.UnexpectedError
            };

            return WpfCreateOrderResult.Failure(
                kind,
                apiCall.TransportErrorMessage ?? "Не удалось выполнить server create order.",
                apiCall.TransportException);
        }

        if (apiCall.Response != null)
        {
            return MapSuccessResponse(context, apiCall.Response);
        }

        return MapHttpError(apiCall);
    }

    private static WpfCreateOrderResult MapSuccessResponse(
        WpfCreateOrderContext context,
        CreateOrderApiResponse response)
    {
        if (!response.Ok)
        {
            return WpfCreateOrderResult.Failure(
                WpfCreateOrderResultKind.ValidationFailed,
                "Сервер отклонил создание заказа.");
        }

        if (response.OrderId <= 0 || string.IsNullOrWhiteSpace(response.OrderRef) || string.IsNullOrWhiteSpace(response.Status))
        {
            return WpfCreateOrderResult.Failure(
                WpfCreateOrderResultKind.InvalidResponse,
                "Сервер вернул неполный ответ при создании заказа.");
        }

        var orderRefChanged = response.OrderRefChanged;
        if (!orderRefChanged && !string.IsNullOrWhiteSpace(context.RequestedOrderRef))
        {
            orderRefChanged = !string.Equals(
                NormalizeValue(context.RequestedOrderRef),
                NormalizeValue(response.OrderRef),
                StringComparison.OrdinalIgnoreCase);
        }

        var message = orderRefChanged || string.IsNullOrWhiteSpace(context.RequestedOrderRef)
            ? $"Сервер назначил номер заказа: {response.OrderRef}"
            : string.Empty;

        return WpfCreateOrderResult.Success(message, response);
    }

    private static WpfCreateOrderResult MapHttpError(CreateOrderApiCallResult apiCall)
    {
        var errorCode = apiCall.Error?.Error;
        var message = errorCode switch
        {
            "INVALID_TYPE" => "Сервер отклонил тип заказа.",
            "INVALID_STATUS" => "Сервер отклонил статус заказа.",
            "SHIPPED_STATUS_FORBIDDEN" => "Статус \"Отгружен/Завершен\" нельзя задавать при создании заказа.",
            "MISSING_PARTNER_ID" => "Для клиентского заказа нужно выбрать контрагента.",
            "PARTNER_NOT_FOUND" => "Сервер не нашел выбранного контрагента.",
            "PARTNER_IS_SUPPLIER" => "В заказе нельзя выбрать контрагента со статусом \"Поставщик\".",
            "INVALID_DUE_DATE" => "Сервер отклонил дату. Используйте корректную дату срока.",
            "MISSING_LINES" => "Добавьте хотя бы одну строку заказа.",
            "MISSING_ITEM_ID" => "Одна из строк заказа не содержит товара.",
            "INVALID_QTY_ORDERED" => "Количество в строках заказа должно быть больше нуля.",
            "ITEM_NOT_FOUND" => "Сервер не нашел один из товаров заказа.",
            _ => string.IsNullOrWhiteSpace(errorCode)
                ? $"Сервер вернул ошибку {(int?)apiCall.StatusCode ?? 0}."
                : $"Сервер вернул ошибку: {errorCode}"
        };

        var kind = errorCode switch
        {
            "INVALID_TYPE" => WpfCreateOrderResultKind.ValidationFailed,
            "INVALID_STATUS" => WpfCreateOrderResultKind.ValidationFailed,
            "SHIPPED_STATUS_FORBIDDEN" => WpfCreateOrderResultKind.ValidationFailed,
            "MISSING_PARTNER_ID" => WpfCreateOrderResultKind.ValidationFailed,
            "PARTNER_NOT_FOUND" => WpfCreateOrderResultKind.ValidationFailed,
            "PARTNER_IS_SUPPLIER" => WpfCreateOrderResultKind.ValidationFailed,
            "INVALID_DUE_DATE" => WpfCreateOrderResultKind.ValidationFailed,
            "MISSING_LINES" => WpfCreateOrderResultKind.ValidationFailed,
            "MISSING_ITEM_ID" => WpfCreateOrderResultKind.ValidationFailed,
            "INVALID_QTY_ORDERED" => WpfCreateOrderResultKind.ValidationFailed,
            "ITEM_NOT_FOUND" => WpfCreateOrderResultKind.ValidationFailed,
            _ => WpfCreateOrderResultKind.ServerRejected
        };

        return WpfCreateOrderResult.Failure(kind, message);
    }

    private WpfServerCreateOrderConfiguration LoadConfiguration()
    {
        var settings = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = NormalizeBaseUrl(ReadEnvOrSettings("FLOWSTOCK_SERVER_BASE_URL", settings.BaseUrl) ?? WpfCloseDocumentService.DefaultServerBaseUrl);
        var timeoutSeconds = ReadEnvInt("FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS") ?? settings.CloseTimeoutSeconds;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = WpfCloseDocumentService.DefaultCloseTimeoutSeconds;
        }

        var allowInvalidTls = ReadEnvBool("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS") ?? settings.AllowInvalidTls;
        return new WpfServerCreateOrderConfiguration(baseUrl, timeoutSeconds, allowInvalidTls);
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

public sealed record WpfCreateOrderContext(
    string? RequestedOrderRef,
    OrderType OrderType,
    long? PartnerId,
    DateTime? DueDate,
    OrderStatus Status,
    string? Comment,
    IReadOnlyList<OrderLineView> Lines);

public sealed record WpfServerCreateOrderConfiguration(
    string BaseUrl,
    int RequestTimeoutSeconds,
    bool AllowInvalidTls);

public sealed class WpfCreateOrderResult
{
    public WpfCreateOrderResultKind Kind { get; init; }
    public string Message { get; init; } = string.Empty;
    public CreateOrderApiResponse? Response { get; init; }
    public Exception? Exception { get; init; }

    public bool IsSuccess => Kind == WpfCreateOrderResultKind.Created;

    public static WpfCreateOrderResult Success(string message, CreateOrderApiResponse response)
    {
        return new WpfCreateOrderResult
        {
            Kind = WpfCreateOrderResultKind.Created,
            Message = message,
            Response = response
        };
    }

    public static WpfCreateOrderResult Failure(
        WpfCreateOrderResultKind kind,
        string message,
        Exception? exception = null)
    {
        return new WpfCreateOrderResult
        {
            Kind = kind,
            Message = message,
            Exception = exception
        };
    }
}

public enum WpfCreateOrderResultKind
{
    Created,
    ValidationFailed,
    ServerRejected,
    ServerUnavailable,
    Timeout,
    InvalidConfiguration,
    InvalidResponse,
    UnexpectedError
}
