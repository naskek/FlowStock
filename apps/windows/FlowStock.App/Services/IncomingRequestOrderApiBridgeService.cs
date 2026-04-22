using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowStock.Core.Models;

namespace FlowStock.App;

public sealed class IncomingRequestOrderApiBridgeService
{
    private readonly SettingsService _settings;
    private readonly FileLogger _logger;
    private readonly WpfIncomingRequestsApiService _incomingRequestsApi;
    private readonly CreateOrderApiClient _createOrderApiClient = new();
    private readonly SetOrderStatusApiClient _setOrderStatusApiClient = new();

    public IncomingRequestOrderApiBridgeService(
        SettingsService settings,
        FileLogger logger,
        WpfIncomingRequestsApiService incomingRequestsApi)
    {
        _settings = settings;
        _logger = logger;
        _incomingRequestsApi = incomingRequestsApi;
    }

    public IncomingRequestOrderApiBridgeConfiguration GetEffectiveConfiguration()
    {
        return LoadConfiguration();
    }

    public bool CanHandle(string? requestType)
    {
        return string.Equals(requestType, OrderRequestType.CreateOrder, StringComparison.OrdinalIgnoreCase)
               || string.Equals(requestType, OrderRequestType.SetOrderStatus, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IncomingRequestOrderApprovalResult> ApproveAsync(
        OrderRequest request,
        string resolvedBy,
        CancellationToken cancellationToken = default)
    {
        var configuration = LoadConfiguration();
        if (string.Equals(request.RequestType, OrderRequestType.CreateOrder, StringComparison.OrdinalIgnoreCase))
        {
            return await ApproveCreateOrderAsync(request, resolvedBy, configuration, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(request.RequestType, OrderRequestType.SetOrderStatus, StringComparison.OrdinalIgnoreCase))
        {
            return await ApproveSetOrderStatusAsync(request, resolvedBy, configuration, cancellationToken).ConfigureAwait(false);
        }

        return IncomingRequestOrderApprovalResult.Failure(
            IncomingRequestOrderApprovalResultKind.UnsupportedRequest,
            $"Неподдерживаемый тип заявки: {request.RequestType}");
    }

    private async Task<IncomingRequestOrderApprovalResult> ApproveCreateOrderAsync(
        OrderRequest request,
        string resolvedBy,
        IncomingRequestOrderApiBridgeConfiguration configuration,
        CancellationToken cancellationToken)
    {
        CreateOrderPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<CreateOrderPayload>(request.PayloadJson, JsonOptions)
                      ?? throw new InvalidOperationException("Некорректный payload заявки CREATE_ORDER.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return IncomingRequestOrderApprovalResult.Failure(
                IncomingRequestOrderApprovalResultKind.ValidationFailed,
                "Некорректный payload заявки CREATE_ORDER.",
                ex);
        }

        var createRequest = new CreateOrderApiRequest
        {
            OrderRef = NormalizeValue(payload.OrderRef),
            Type = "CUSTOMER",
            PartnerId = payload.PartnerId > 0 ? payload.PartnerId : null,
            DueDate = NormalizeValue(payload.DueDate),
            Status = "ACCEPTED",
            Comment = NormalizeValue(payload.Comment),
            Lines = payload.Lines?
                .Select(line => new CreateOrderApiLineRequest
                {
                    ItemId = line.ItemId,
                    QtyOrdered = line.QtyOrdered
                })
                .ToList()
                ?? new List<CreateOrderApiLineRequest>()
        };

        CreateOrderApiCallResult apiCall;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(configuration.RequestTimeoutSeconds));

            apiCall = await _createOrderApiClient.CreateAsync(
                    new ServerCloseClientOptions
                    {
                        BaseUrl = configuration.BaseUrl,
                        AllowInvalidTls = configuration.AllowInvalidTls
                    },
                    createRequest,
                    timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Warn($"Incoming request CREATE_ORDER approval timed out for request_id={request.Id}");
            return IncomingRequestOrderApprovalResult.Failure(
                IncomingRequestOrderApprovalResultKind.Timeout,
                "Сервер не ответил вовремя при подтверждении заявки CREATE_ORDER.",
                ex);
        }
        catch (HttpRequestException ex) when (CreateOrderApiClient.IsTlsFailure(ex))
        {
            _logger.Error($"TLS failure during CREATE_ORDER approval for request_id={request.Id}", ex);
            return IncomingRequestOrderApprovalResult.Failure(
                IncomingRequestOrderApprovalResultKind.InvalidConfiguration,
                "Ошибка TLS при обращении к серверу. Проверьте сертификат или настройку Allow invalid TLS.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.Error($"Server CREATE_ORDER approval request failed for request_id={request.Id}", ex);
            return IncomingRequestOrderApprovalResult.Failure(
                IncomingRequestOrderApprovalResultKind.ServerUnavailable,
                "Не удалось связаться с сервером создания заказов.",
                ex);
        }
        catch (Exception ex)
        {
            _logger.Error($"Unexpected CREATE_ORDER approval failure for request_id={request.Id}", ex);
            return IncomingRequestOrderApprovalResult.Failure(
                IncomingRequestOrderApprovalResultKind.UnexpectedError,
                "Не удалось подтвердить заявку CREATE_ORDER через сервер.",
                ex);
        }

        if (apiCall.IsTransportFailure)
        {
            return IncomingRequestOrderApprovalResult.Failure(
                MapCreateTransportFailure(apiCall.TransportFailureKind),
                apiCall.TransportErrorMessage ?? "Не удалось выполнить подтверждение CREATE_ORDER.",
                apiCall.TransportException);
        }

        if (apiCall.Response == null)
        {
            return MapCreateHttpError(apiCall);
        }

        if (!apiCall.Response.Ok || apiCall.Response.OrderId <= 0)
        {
            return IncomingRequestOrderApprovalResult.Failure(
                IncomingRequestOrderApprovalResultKind.InvalidResponse,
                "Сервер вернул неполный ответ при подтверждении заявки CREATE_ORDER.");
        }

        var note = $"Создан заказ ID={apiCall.Response.OrderId}.";
        await MarkRequestApprovedAsync(request.Id, resolvedBy, note, apiCall.Response.OrderId, cancellationToken)
            .ConfigureAwait(false);

        return IncomingRequestOrderApprovalResult.Success(apiCall.Response.OrderId, note);
    }

    private async Task<IncomingRequestOrderApprovalResult> ApproveSetOrderStatusAsync(
        OrderRequest request,
        string resolvedBy,
        IncomingRequestOrderApiBridgeConfiguration configuration,
        CancellationToken cancellationToken)
    {
        SetOrderStatusPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<SetOrderStatusPayload>(request.PayloadJson, JsonOptions)
                      ?? throw new InvalidOperationException("Некорректный payload заявки SET_ORDER_STATUS.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return IncomingRequestOrderApprovalResult.Failure(
                IncomingRequestOrderApprovalResultKind.ValidationFailed,
                "Некорректный payload заявки SET_ORDER_STATUS.",
                ex);
        }

        SetOrderStatusApiCallResult apiCall;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(configuration.RequestTimeoutSeconds));

            apiCall = await _setOrderStatusApiClient.SetStatusAsync(
                    new ServerCloseClientOptions
                    {
                        BaseUrl = configuration.BaseUrl,
                        AllowInvalidTls = configuration.AllowInvalidTls
                    },
                    payload.OrderId,
                    new SetOrderStatusApiRequest
                    {
                        Status = NormalizeValue(payload.Status) ?? string.Empty
                    },
                    timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Warn($"Incoming request SET_ORDER_STATUS approval timed out for request_id={request.Id}");
            return IncomingRequestOrderApprovalResult.Failure(
                IncomingRequestOrderApprovalResultKind.Timeout,
                "Сервер не ответил вовремя при подтверждении заявки SET_ORDER_STATUS.",
                ex);
        }
        catch (HttpRequestException ex) when (SetOrderStatusApiClient.IsTlsFailure(ex))
        {
            _logger.Error($"TLS failure during SET_ORDER_STATUS approval for request_id={request.Id}", ex);
            return IncomingRequestOrderApprovalResult.Failure(
                IncomingRequestOrderApprovalResultKind.InvalidConfiguration,
                "Ошибка TLS при обращении к серверу. Проверьте сертификат или настройку Allow invalid TLS.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.Error($"Server SET_ORDER_STATUS approval request failed for request_id={request.Id}", ex);
            return IncomingRequestOrderApprovalResult.Failure(
                IncomingRequestOrderApprovalResultKind.ServerUnavailable,
                "Не удалось связаться с сервером смены статуса заказов.",
                ex);
        }
        catch (Exception ex)
        {
            _logger.Error($"Unexpected SET_ORDER_STATUS approval failure for request_id={request.Id}", ex);
            return IncomingRequestOrderApprovalResult.Failure(
                IncomingRequestOrderApprovalResultKind.UnexpectedError,
                "Не удалось подтвердить заявку SET_ORDER_STATUS через сервер.",
                ex);
        }

        if (apiCall.IsTransportFailure)
        {
            return IncomingRequestOrderApprovalResult.Failure(
                MapStatusTransportFailure(apiCall.TransportFailureKind),
                apiCall.TransportErrorMessage ?? "Не удалось выполнить подтверждение SET_ORDER_STATUS.",
                apiCall.TransportException);
        }

        if (apiCall.Response == null)
        {
            return MapStatusHttpError(apiCall);
        }

        if (!apiCall.Response.Ok || apiCall.Response.OrderId <= 0 || string.IsNullOrWhiteSpace(apiCall.Response.Status))
        {
            return IncomingRequestOrderApprovalResult.Failure(
                IncomingRequestOrderApprovalResultKind.InvalidResponse,
                "Сервер вернул неполный ответ при подтверждении заявки SET_ORDER_STATUS.");
        }

        var displayStatus = OrderStatusMapper.StatusFromString(apiCall.Response.Status) is { } parsed
            ? OrderStatusMapper.StatusToDisplayName(parsed)
            : apiCall.Response.Status;
        var note = $"Статус изменен на \"{displayStatus}\".";
        await MarkRequestApprovedAsync(request.Id, resolvedBy, note, payload.OrderId, cancellationToken)
            .ConfigureAwait(false);

        return IncomingRequestOrderApprovalResult.Success(payload.OrderId, note);
    }

    private async Task MarkRequestApprovedAsync(
        long requestId,
        string resolvedBy,
        string note,
        long appliedOrderId,
        CancellationToken cancellationToken)
    {
        if (await _incomingRequestsApi
                .TryResolveOrderRequestAsync(
                    requestId,
                    OrderRequestStatus.Approved,
                    resolvedBy,
                    note,
                    appliedOrderId,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }
        
        throw new InvalidOperationException("Не удалось подтвердить заявку на сервере: не выполнена фиксация статуса APPROVED.");
    }

    private static IncomingRequestOrderApprovalResult MapCreateHttpError(CreateOrderApiCallResult apiCall)
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
            "INVALID_DUE_DATE" => "Сервер отклонил дату срока.",
            "MISSING_LINES" => "В заявке нет строк заказа.",
            "MISSING_ITEM_ID" => "Одна из строк заказа не содержит товара.",
            "INVALID_QTY_ORDERED" => "Количество в строках заказа должно быть больше нуля.",
            "ITEM_NOT_FOUND" => "Сервер не нашел один из товаров заказа.",
            _ => string.IsNullOrWhiteSpace(errorCode)
                ? $"Сервер вернул ошибку {(int?)apiCall.StatusCode ?? 0}."
                : $"Сервер вернул ошибку: {errorCode}"
        };

        return IncomingRequestOrderApprovalResult.Failure(IncomingRequestOrderApprovalResultKind.ValidationFailed, message);
    }

    private static IncomingRequestOrderApprovalResult MapStatusHttpError(SetOrderStatusApiCallResult apiCall)
    {
        var errorCode = apiCall.Error?.Error;
        var message = errorCode switch
        {
            "ORDER_NOT_FOUND" => "Сервер не нашел указанный заказ.",
            "INVALID_STATUS" => "Сервер отклонил статус заказа.",
            "ORDER_STATUS_SHIPPED_FORBIDDEN" => "Статус \"Отгружен/Завершен\" ставится автоматически.",
            "ORDER_STATUS_INVALID_TARGET" => "Допустимы только статусы \"Принят\" и \"В процессе\".",
            "ORDER_STATUS_CHANGE_FORBIDDEN" => "Заказ в конечном статусе нельзя менять вручную.",
            _ => string.IsNullOrWhiteSpace(errorCode)
                ? $"Сервер вернул ошибку {(int?)apiCall.StatusCode ?? 0}."
                : $"Сервер вернул ошибку: {errorCode}"
        };

        var kind = errorCode == "ORDER_NOT_FOUND"
            ? IncomingRequestOrderApprovalResultKind.NotFound
            : IncomingRequestOrderApprovalResultKind.ValidationFailed;
        return IncomingRequestOrderApprovalResult.Failure(kind, message);
    }

    private static IncomingRequestOrderApprovalResultKind MapCreateTransportFailure(CreateOrderTransportFailureKind kind)
    {
        return kind switch
        {
            CreateOrderTransportFailureKind.InvalidConfiguration => IncomingRequestOrderApprovalResultKind.InvalidConfiguration,
            CreateOrderTransportFailureKind.Timeout => IncomingRequestOrderApprovalResultKind.Timeout,
            CreateOrderTransportFailureKind.Network => IncomingRequestOrderApprovalResultKind.ServerUnavailable,
            CreateOrderTransportFailureKind.InvalidResponse => IncomingRequestOrderApprovalResultKind.InvalidResponse,
            _ => IncomingRequestOrderApprovalResultKind.UnexpectedError
        };
    }

    private static IncomingRequestOrderApprovalResultKind MapStatusTransportFailure(SetOrderStatusTransportFailureKind kind)
    {
        return kind switch
        {
            SetOrderStatusTransportFailureKind.InvalidConfiguration => IncomingRequestOrderApprovalResultKind.InvalidConfiguration,
            SetOrderStatusTransportFailureKind.Timeout => IncomingRequestOrderApprovalResultKind.Timeout,
            SetOrderStatusTransportFailureKind.Network => IncomingRequestOrderApprovalResultKind.ServerUnavailable,
            SetOrderStatusTransportFailureKind.InvalidResponse => IncomingRequestOrderApprovalResultKind.InvalidResponse,
            _ => IncomingRequestOrderApprovalResultKind.UnexpectedError
        };
    }

    private IncomingRequestOrderApiBridgeConfiguration LoadConfiguration()
    {
        var settings = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = NormalizeBaseUrl(ReadEnvOrSettings("FLOWSTOCK_SERVER_BASE_URL", settings.BaseUrl) ?? WpfCloseDocumentService.DefaultServerBaseUrl);
        var timeoutSeconds = ReadEnvInt("FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS") ?? settings.CloseTimeoutSeconds;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = WpfCloseDocumentService.DefaultCloseTimeoutSeconds;
        }

        var allowInvalidTls = ReadEnvBool("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS") ?? settings.AllowInvalidTls;
        return new IncomingRequestOrderApiBridgeConfiguration(baseUrl, timeoutSeconds, allowInvalidTls);
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record CreateOrderPayload
    {
        [JsonPropertyName("order_ref")]
        public string? OrderRef { get; init; }

        [JsonPropertyName("partner_id")]
        public long PartnerId { get; init; }

        [JsonPropertyName("due_date")]
        public string? DueDate { get; init; }

        [JsonPropertyName("comment")]
        public string? Comment { get; init; }

        [JsonPropertyName("lines")]
        public List<CreateOrderLinePayload>? Lines { get; init; }
    }

    private sealed record CreateOrderLinePayload
    {
        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("qty_ordered")]
        public double QtyOrdered { get; init; }
    }

    private sealed record SetOrderStatusPayload
    {
        [JsonPropertyName("order_id")]
        public long OrderId { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }
    }
}

public sealed record IncomingRequestOrderApiBridgeConfiguration(
    string BaseUrl,
    int RequestTimeoutSeconds,
    bool AllowInvalidTls);

public sealed class IncomingRequestOrderApprovalResult
{
    public IncomingRequestOrderApprovalResultKind Kind { get; init; }
    public string Message { get; init; } = string.Empty;
    public long? AppliedOrderId { get; init; }
    public Exception? Exception { get; init; }

    public bool IsSuccess => Kind == IncomingRequestOrderApprovalResultKind.Approved;

    public static IncomingRequestOrderApprovalResult Success(long? appliedOrderId, string message)
    {
        return new IncomingRequestOrderApprovalResult
        {
            Kind = IncomingRequestOrderApprovalResultKind.Approved,
            AppliedOrderId = appliedOrderId,
            Message = message
        };
    }

    public static IncomingRequestOrderApprovalResult Failure(
        IncomingRequestOrderApprovalResultKind kind,
        string message,
        Exception? exception = null)
    {
        return new IncomingRequestOrderApprovalResult
        {
            Kind = kind,
            Message = message,
            Exception = exception
        };
    }
}

public enum IncomingRequestOrderApprovalResultKind
{
    Approved,
    ValidationFailed,
    NotFound,
    UnsupportedRequest,
    ServerRejected,
    ServerUnavailable,
    Timeout,
    InvalidConfiguration,
    InvalidResponse,
    UnexpectedError
}
