using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowStock.Core.Models;

namespace FlowStock.App;

public sealed class WpfMarkingApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsService _settings;
    private readonly FileLogger _logger;
    private readonly Func<WpfMarkingApiConfiguration, HttpMessageHandler> _handlerFactory;

    public WpfMarkingApiService(
        SettingsService settings,
        FileLogger logger,
        Func<WpfMarkingApiConfiguration, HttpMessageHandler>? handlerFactory = null)
    {
        _settings = settings;
        _logger = logger;
        _handlerFactory = handlerFactory ?? CreateHandler;
    }

    public bool TryGetOrders(bool includeCompleted, out IReadOnlyList<MarkingOrderQueueRow> orders)
    {
        orders = Array.Empty<MarkingOrderQueueRow>();
        var path = includeCompleted ? "/api/marking/orders?include_completed=1" : "/api/marking/orders";
        return TryRead(
            path,
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().Select(MapOrder).ToList()
                : new List<MarkingOrderQueueRow>(),
            "marking-orders",
            out orders);
    }

    public async Task<(bool IsSuccess, byte[]? FileBytes, string? FileName, string? Error)> TryExportAsync(
        IReadOnlyCollection<Guid> markingOrderIds,
        IReadOnlyCollection<long> orderIds,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var configuration = LoadConfiguration();
            if (!configuration.IsConfigured)
            {
                _logger.Info("Marking export skipped: server base URL is not configured.");
                return (false, null, null, "FlowStock Server API не настроен.");
            }

            using var handler = _handlerFactory(configuration);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(configuration.BaseUrl!, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds)
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/marking/export")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { marking_order_ids = markingOrderIds, order_ids = orderIds }),
                    Encoding.UTF8,
                    "application/json")
            };
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, null, null, await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                           ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                           ?? $"chestny_znak_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
            return (true, bytes, fileName, null);
        }
        catch (Exception ex)
        {
            _logger.Error("Marking export failed", ex);
            return (false, null, null, "Не удалось сформировать Excel ЧЗ.");
        }
    }

    public async Task<OrderMarkingExportPreviewApiResult> TryPreviewOrderAsync(
        long orderId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var configuration = LoadConfiguration();
            if (!configuration.IsConfigured)
            {
                _logger.Info("Order marking preview skipped: server base URL is not configured.");
                return OrderMarkingExportPreviewApiResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = _handlerFactory(configuration);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(configuration.BaseUrl!, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds)
            };
            using var response = await client
                .GetAsync($"/api/orders/{orderId}/marking/preview", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return OrderMarkingExportPreviewApiResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            var payload = await response.Content
                .ReadFromJsonAsync<OrderMarkingExportPreviewResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (payload == null)
            {
                return OrderMarkingExportPreviewApiResult.Failure("Пустой ответ сервера.");
            }

            return new OrderMarkingExportPreviewApiResult(
                true,
                payload.Message ?? "Предпросмотр Excel ЧЗ.",
                payload.OrderId,
                payload.OrderRef ?? string.Empty,
                payload.LineCount,
                payload.TotalQty,
                payload.Lines?.Select(MapPreviewLine).ToArray() ?? Array.Empty<OrderMarkingExportPreviewLineApiResult>());
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            _logger.Error("Order marking preview cancelled", ex);
            return OrderMarkingExportPreviewApiResult.Failure("Предпросмотр Excel ЧЗ отменён.");
        }
        catch (OperationCanceledException ex)
        {
            _logger.Error("Order marking preview timed out", ex);
            return OrderMarkingExportPreviewApiResult.Failure(
                "Предпросмотр Excel ЧЗ не выполнен из-за превышения времени ожидания. Это операция только для чтения — её можно безопасно повторить.");
        }
        catch (TimeoutException ex)
        {
            _logger.Error("Order marking preview timed out", ex);
            return OrderMarkingExportPreviewApiResult.Failure(
                "Предпросмотр Excel ЧЗ не выполнен из-за превышения времени ожидания. Это операция только для чтения — её можно безопасно повторить.");
        }
        catch (HttpRequestException ex)
        {
            _logger.Error("Order marking preview network request failed", ex);
            return OrderMarkingExportPreviewApiResult.Failure(
                "Не удалось связаться с сервером для предпросмотра Excel ЧЗ. Предпросмотр не изменяет данные, поэтому запрос можно повторить.");
        }
        catch (Exception ex)
        {
            _logger.Error("Order marking preview failed", ex);
            return OrderMarkingExportPreviewApiResult.Failure("Не удалось выполнить предпросмотр Excel ЧЗ.");
        }
    }

    public async Task<OrderMarkingExportApiResult> TryExportOrderAsync(
        long orderId,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return OrderMarkingExportApiResult.Cancelled("Формирование Excel ЧЗ отменено до отправки запроса.");
        }

        var postStarted = false;
        try
        {
            var configuration = LoadConfiguration();
            if (!configuration.IsConfigured)
            {
                _logger.Info("Order marking export skipped: server base URL is not configured.");
                return OrderMarkingExportApiResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = new RequestTrackingHandler(
                _handlerFactory(configuration),
                () => postStarted = true);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(configuration.BaseUrl!, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds)
            };
            using var response = await client.PostAsJsonAsync($"/api/orders/{orderId}/marking/export", new { }, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return OrderMarkingExportApiResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (string.Equals(contentType, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                               ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                               ?? $"chestny_znak_order_{orderId}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
                return new OrderMarkingExportApiResult(
                    true,
                    "Excel ЧЗ сформирован из заказа.",
                    bytes,
                    fileName,
                    ReadIntHeader(response, "X-FlowStock-Marking-Line-Count"),
                    ReadIntHeader(response, "X-FlowStock-Marking-Export-Line-Count"),
                    ReadDoubleHeader(response, "X-FlowStock-Marking-Created-Qty"),
                    ReadDoubleHeader(response, "X-FlowStock-Marking-Reused-Qty"));
            }

            var payload = await response.Content.ReadFromJsonAsync<OrderMarkingExportResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return new OrderMarkingExportApiResult(
                true,
                payload?.Message ?? "Маркировка по заказу уже проведена.",
                null,
                null,
                payload?.LineCount ?? 0,
                payload?.ExportLineCount ?? 0,
                payload?.CreatedCodeQty ?? 0,
                payload?.ReusedCodeQty ?? 0);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            _logger.Error("Order marking export cancelled", ex);
            return postStarted
                ? OrderMarkingExportApiResult.CancelledOutcomeUnknown(BuildCancelledOutcomeUnknownMessage())
                : OrderMarkingExportApiResult.Cancelled("Формирование Excel ЧЗ отменено до отправки запроса.");
        }
        catch (OperationCanceledException ex)
        {
            _logger.Error("Order marking export timed out", ex);
            return OrderMarkingExportApiResult.OutcomeUnknown(BuildOutcomeUnknownMessage());
        }
        catch (TimeoutException ex)
        {
            _logger.Error("Order marking export timed out", ex);
            return OrderMarkingExportApiResult.OutcomeUnknown(BuildOutcomeUnknownMessage());
        }
        catch (HttpRequestException ex)
        {
            _logger.Error("Order marking export network request failed", ex);
            return OrderMarkingExportApiResult.OutcomeUnknown(BuildOutcomeUnknownMessage());
        }
        catch (Exception ex)
        {
            _logger.Error("Order marking export failed", ex);
            return OrderMarkingExportApiResult.Failure("Не удалось сформировать Excel ЧЗ.");
        }
    }

    public async Task<(bool IsSuccess, string Message, int CreatedTaskCount, double CreatedQty)> TryCreateFromProductionNeedsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var configuration = LoadConfiguration();
            if (!configuration.IsConfigured)
            {
                _logger.Info("Marking creation skipped: server base URL is not configured.");
                return (false, "FlowStock Server API не настроен.", 0, 0);
            }

            using var handler = _handlerFactory(configuration);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(configuration.BaseUrl!, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds)
            };
            using var response = await client.PostAsJsonAsync("/api/marking/create-from-production-needs", new { }, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, await ReadApiErrorAsync(response).ConfigureAwait(false), 0, 0);
            }

            var payload = await response.Content.ReadFromJsonAsync<CreateMarkingResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return (
                true,
                payload?.Message ?? "Маркировка создана.",
                payload?.CreatedTaskCount ?? 0,
                payload?.CreatedQty ?? 0);
        }
        catch (Exception ex)
        {
            _logger.Error("Marking creation failed", ex);
            return (false, "Не удалось создать задачи маркировки.", 0, 0);
        }
    }

    private bool TryRead<T>(string relativePath, Func<JsonElement, T> map, string operationName, out T value)
    {
        value = default!;
        try
        {
            var configuration = LoadConfiguration();
            if (!configuration.IsConfigured)
            {
                _logger.Info($"Marking API skipped for {operationName}: server base URL is not configured.");
                return false;
            }

            using var handler = _handlerFactory(configuration);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(configuration.BaseUrl!, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds)
            };
            using var response = client.GetAsync(relativePath, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn($"Marking API request failed: {relativePath} -> {(int)response.StatusCode} {response.ReasonPhrase}");
                return false;
            }

            var json = response.Content.ReadAsStringAsync()
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            using var document = JsonDocument.Parse(json);
            value = map(document.RootElement);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Marking API failed for {operationName}", ex);
            return false;
        }
    }

    public WpfMarkingApiConfiguration GetEffectiveConfiguration()
    {
        return LoadConfiguration();
    }

    private WpfMarkingApiConfiguration LoadConfiguration()
    {
        var settings = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = ReadEnvOrSettings("FLOWSTOCK_SERVER_BASE_URL", settings.BaseUrl);
        var timeoutSeconds = Math.Clamp(
            ReadEnvInt("FLOWSTOCK_SERVER_MARKING_TIMEOUT_SECONDS") ?? settings.MarkingTimeoutSeconds,
            1,
            600);

        return new WpfMarkingApiConfiguration(
            NormalizeBaseUrl(baseUrl),
            timeoutSeconds,
            ReadEnvBool("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS") ?? settings.AllowInvalidTls);
    }

    private static MarkingOrderQueueRow MapOrder(JsonElement element)
    {
        return new MarkingOrderQueueRow
        {
            MarkingOrderId = ReadGuid(element, "marking_order_id"),
            OrderId = ReadNullableInt64(element, "order_id"),
            OrderRef = ReadString(element, "order_ref") ?? string.Empty,
            PartnerName = ReadString(element, "partner_name"),
            PartnerCode = ReadString(element, "partner_code"),
            SourceType = ReadString(element, "source_type"),
            SourceOrderId = ReadNullableInt64(element, "source_order_id"),
            ItemId = ReadNullableInt64(element, "item_id"),
            ItemName = ReadString(element, "item_name"),
            Gtin = ReadString(element, "gtin"),
            RequestedQuantity = ReadInt32(element, "requested_quantity"),
            TaskStatus = ReadString(element, "status"),
            CodesTotal = ReadInt32(element, "codes_total"),
            CodesFree = ReadInt32(element, "codes_free"),
            CodesBound = ReadInt32(element, "codes_bound"),
            DisplaySource = ReadString(element, "display_source"),
            EffectiveStatus = ReadString(element, "effective_status"),
            DisplayStatus = ReadString(element, "display_status"),
            OrderStatus = OrderStatusMapper.StatusFromString(ReadString(element, "order_status")) ?? OrderStatus.InProgress,
            DueDate = ReadDateOnly(element, "due_date"),
            MarkingStatus = MarkingStatusMapper.FromString(ReadString(element, "marking_status")),
            MarkingLineCount = ReadInt32(element, "marking_line_count"),
            MarkingCodeCount = ReadDouble(element, "marking_code_count"),
            LastGeneratedAt = ReadDateTime(element, "last_generated_at")
        };
    }

    private static async Task<string> ReadApiErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(error?.Error))
            {
                return error.Error;
            }
        }
        catch
        {
        }

        return $"Сервер вернул ошибку HTTP {(int)response.StatusCode}.";
    }

    private static string BuildOutcomeUnknownMessage()
    {
        return "Ответ сервера не получен. Сервер мог уже завершить или всё ещё выполнять формирование Excel ЧЗ. "
               + "Автоматический повтор не выполнен. Подождите и обновите либо переоткройте заказ. "
               + "После завершения операции ручной повтор безопасен благодаря серверной идемпотентности.";
    }

    private static string BuildCancelledOutcomeUnknownMessage()
    {
        return "Запрос формирования Excel ЧЗ был отменён после отправки, поэтому результат неизвестен. "
               + "Сервер мог уже завершить или всё ещё выполнять формирование. Автоматический повтор не выполнен. "
               + "Подождите и обновите либо переоткройте заказ. После завершения операции ручной повтор безопасен "
               + "благодаря серверной идемпотентности.";
    }

    private static HttpMessageHandler CreateHandler(WpfMarkingApiConfiguration configuration)
    {
        var handler = new HttpClientHandler();
        if (configuration.AllowInvalidTls)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
    }

    private sealed class RequestTrackingHandler : DelegatingHandler
    {
        private readonly Action _onRequestStarted;

        public RequestTrackingHandler(HttpMessageHandler innerHandler, Action onRequestStarted)
            : base(innerHandler)
        {
            _onRequestStarted = onRequestStarted;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _onRequestStarted();
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static string? NormalizeBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

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

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString()
            : null;
    }

    private static long ReadInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value) ? value : 0L;
    }

    private static long? ReadNullableInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.TryGetInt64(out var value) ? value : null;
    }

    private static Guid? ReadGuid(JsonElement element, string propertyName)
    {
        var raw = ReadString(element, propertyName);
        return Guid.TryParse(raw, out var value) ? value : null;
    }

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value) ? value : 0;
    }

    private static double ReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0d;
        }

        if (property.TryGetDouble(out var value))
        {
            return value;
        }

        return double.TryParse(property.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0d;
    }

    private static DateTime? ReadDateTime(JsonElement element, string propertyName)
    {
        var raw = ReadString(element, propertyName);
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;
    }

    private static DateTime? ReadDateOnly(JsonElement element, string propertyName)
    {
        var raw = ReadString(element, propertyName);
        return DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : null;
    }

    private static string? ReadHeader(HttpResponseMessage response, string name)
    {
        return response.Headers.TryGetValues(name, out var values)
            ? values.FirstOrDefault()
            : null;
    }

    private static int ReadIntHeader(HttpResponseMessage response, string name)
    {
        var raw = ReadHeader(response, name);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static OrderMarkingExportPreviewLineApiResult MapPreviewLine(OrderMarkingExportPreviewLineResponse line)
    {
        return new OrderMarkingExportPreviewLineApiResult(
            line.OrderLineId,
            line.ItemId,
            line.ItemName ?? string.Empty,
            line.Gtin ?? string.Empty,
            line.Qty,
            line.HuCount,
            line.HuCodes ?? Array.Empty<string>());
    }

    private static double ReadDoubleHeader(HttpResponseMessage response, string name)
    {
        var raw = ReadHeader(response, name);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private sealed class CreateMarkingResponse
    {
        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;

        [JsonPropertyName("created_task_count")]
        public int CreatedTaskCount { get; init; }

        [JsonPropertyName("created_qty")]
        public double CreatedQty { get; init; }
    }

    private sealed class OrderMarkingExportPreviewResponse
    {
        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("order_id")]
        public long OrderId { get; init; }

        [JsonPropertyName("order_ref")]
        public string? OrderRef { get; init; }

        [JsonPropertyName("line_count")]
        public int LineCount { get; init; }

        [JsonPropertyName("total_qty")]
        public double TotalQty { get; init; }

        [JsonPropertyName("lines")]
        public OrderMarkingExportPreviewLineResponse[]? Lines { get; init; }
    }

    private sealed class OrderMarkingExportPreviewLineResponse
    {
        [JsonPropertyName("order_line_id")]
        public long OrderLineId { get; init; }

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("item_name")]
        public string? ItemName { get; init; }

        [JsonPropertyName("gtin")]
        public string? Gtin { get; init; }

        [JsonPropertyName("qty")]
        public double Qty { get; init; }

        [JsonPropertyName("hu_count")]
        public int HuCount { get; init; }

        [JsonPropertyName("hu_codes")]
        public string[]? HuCodes { get; init; }
    }

    private sealed class OrderMarkingExportResponse
    {
        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;

        [JsonPropertyName("line_count")]
        public int LineCount { get; init; }

        [JsonPropertyName("export_line_count")]
        public int ExportLineCount { get; init; }

        [JsonPropertyName("created_code_qty")]
        public double CreatedCodeQty { get; init; }

        [JsonPropertyName("reused_code_qty")]
        public double ReusedCodeQty { get; init; }
    }
}

public sealed record WpfMarkingApiConfiguration(string? BaseUrl, int TimeoutSeconds, bool AllowInvalidTls)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
}

public sealed record OrderMarkingExportPreviewApiResult(
    bool IsSuccess,
    string Message,
    long OrderId,
    string OrderRef,
    int LineCount,
    double TotalQty,
    IReadOnlyList<OrderMarkingExportPreviewLineApiResult> Lines)
{
    public static OrderMarkingExportPreviewApiResult Failure(string message)
    {
        return new OrderMarkingExportPreviewApiResult(
            false,
            message,
            0,
            string.Empty,
            0,
            0,
            Array.Empty<OrderMarkingExportPreviewLineApiResult>());
    }
}

public sealed record OrderMarkingExportPreviewLineApiResult(
    long OrderLineId,
    long ItemId,
    string ItemName,
    string Gtin,
    double Qty,
    int HuCount,
    IReadOnlyList<string> HuCodes);

public sealed record OrderMarkingExportApiResult(
    bool IsSuccess,
    string Message,
    byte[]? FileBytes,
    string? FileName,
    int LineCount,
    int ExportLineCount,
    double CreatedCodeQty,
    double ReusedCodeQty,
    OrderMarkingExportOutcome Outcome = OrderMarkingExportOutcome.Success)
{
    public static OrderMarkingExportApiResult Failure(string message)
    {
        return new OrderMarkingExportApiResult(
            false, message, null, null, 0, 0, 0, 0, OrderMarkingExportOutcome.Failure);
    }

    public static OrderMarkingExportApiResult Cancelled(string message)
    {
        return new OrderMarkingExportApiResult(
            false, message, null, null, 0, 0, 0, 0, OrderMarkingExportOutcome.Cancelled);
    }

    public static OrderMarkingExportApiResult OutcomeUnknown(string message)
    {
        return new OrderMarkingExportApiResult(
            false, message, null, null, 0, 0, 0, 0, OrderMarkingExportOutcome.OutcomeUnknown);
    }

    public static OrderMarkingExportApiResult CancelledOutcomeUnknown(string message)
    {
        return new OrderMarkingExportApiResult(
            false, message, null, null, 0, 0, 0, 0, OrderMarkingExportOutcome.CancelledOutcomeUnknown);
    }
}

public enum OrderMarkingExportOutcome
{
    Success,
    Failure,
    Cancelled,
    OutcomeUnknown,
    CancelledOutcomeUnknown
}
