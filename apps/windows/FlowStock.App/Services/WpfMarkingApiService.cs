using System.Globalization;
using System.IO;
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

    public async Task<OrderMarkingImportPreviewApiResult> TryPreviewOrderImportAsync(
        long orderId,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var configuration = LoadConfiguration();
            if (!configuration.IsConfigured)
            {
                return OrderMarkingImportPreviewApiResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = _handlerFactory(configuration);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(configuration.BaseUrl!, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds)
            };
            using var content = BuildImportMultipart(filePaths);
            using var response = await client
                .PostAsync($"/api/orders/{orderId}/marking/import/preview", content, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return OrderMarkingImportPreviewApiResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            var payload = await response.Content
                .ReadFromJsonAsync<OrderMarkingImportPreviewResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return payload == null
                ? OrderMarkingImportPreviewApiResult.Failure("Пустой ответ сервера.")
                : new OrderMarkingImportPreviewApiResult(
                    true,
                    payload.Message ?? "Импорт готов к подтверждению.",
                    payload.SnapshotHash ?? string.Empty,
                    payload.RequiresRecoveryConfirmation,
                    payload.Warnings ?? Array.Empty<string>(),
                    payload.Files?.Select(file => new OrderMarkingImportFileApiResult(
                        file.Filename ?? string.Empty, file.ValidRows, file.InvalidRows, file.DuplicateRows)).ToArray()
                        ?? Array.Empty<OrderMarkingImportFileApiResult>(),
                    payload.Requests?.Select(request => new OrderMarkingImportRequestApiResult(
                        request.MarkingOrderId,
                        request.RequestNumber ?? string.Empty,
                        request.Gtin ?? string.Empty,
                        request.RequiredQuantity,
                        request.RequestedQuantity,
                        request.ImportedAfter,
                        request.CoverageWillActivate,
                        request.ReserveShort)).ToArray()
                        ?? Array.Empty<OrderMarkingImportRequestApiResult>());
        }
        catch (Exception ex)
        {
            _logger.Error("Order marking import preview failed", ex);
            return OrderMarkingImportPreviewApiResult.Failure("Не удалось проверить файлы КМ.");
        }
    }

    public async Task<OrderMarkingImportConfirmApiResult> TryConfirmOrderImportAsync(
        long orderId,
        IReadOnlyList<string> filePaths,
        OrderMarkingImportPreviewApiResult preview,
        bool confirmRecovery,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var configuration = LoadConfiguration();
            if (!configuration.IsConfigured)
            {
                return OrderMarkingImportConfirmApiResult.Failure("FlowStock Server API не настроен.");
            }

            var batchId = Guid.NewGuid();
            using var handler = _handlerFactory(configuration);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(configuration.BaseUrl!, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds)
            };
            using var content = BuildImportMultipart(filePaths);
            content.Add(new StringContent(batchId.ToString("D")), "batch_id");
            content.Add(new StringContent(preview.SnapshotHash), "snapshot_hash");
            content.Add(new StringContent($"wpf-{batchId:N}"), "idempotency_key");
            content.Add(new StringContent(confirmRecovery.ToString(CultureInfo.InvariantCulture)), "confirm_recovery");
            using var response = await client
                .PostAsync($"/api/orders/{orderId}/marking/import/confirm", content, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return OrderMarkingImportConfirmApiResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            var payload = await response.Content
                .ReadFromJsonAsync<OrderMarkingImportConfirmResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return payload == null
                ? OrderMarkingImportConfirmApiResult.Failure("Пустой ответ сервера.")
                : new OrderMarkingImportConfirmApiResult(
                    true,
                    $"Импортировано реальных КМ: {payload.PersistedCodeCount}. "
                    + $"Активировано requests: {payload.ActivatedMarkingOrderIds?.Length ?? 0}.",
                    payload.BatchId,
                    payload.PersistedCodeCount,
                    payload.ActivatedMarkingOrderIds?.Length ?? 0);
        }
        catch (Exception ex)
        {
            _logger.Error("Order marking import confirm failed", ex);
            return OrderMarkingImportConfirmApiResult.Failure("Не удалось подтвердить импорт КМ; проверьте состояние заказа на сервере.");
        }
    }

    private static MultipartFormDataContent BuildImportMultipart(IReadOnlyList<string> filePaths)
    {
        var content = new MultipartFormDataContent();
        foreach (var filePath in filePaths)
        {
            var bytes = File.ReadAllBytes(filePath);
            content.Add(new ByteArrayContent(bytes), "files", Path.GetFileName(filePath));
        }

        return content;
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
            OperatorStatusPresentation = MapOrderStatusPresentation(element),
            DueDate = ReadDateOnly(element, "due_date"),
            MarkingStatus = MarkingStatusMapper.FromString(ReadString(element, "marking_status")),
            MarkingLineCount = ReadInt32(element, "marking_line_count"),
            MarkingCodeCount = ReadDouble(element, "marking_code_count"),
            LastGeneratedAt = ReadDateTime(element, "last_generated_at")
        };
    }

    private static OrderOperatorStatusPresentation? MapOrderStatusPresentation(JsonElement element)
    {
        if (!element.TryGetProperty("order_status_presentation", out var presentation)
            || presentation.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var code = ReadString(presentation, "code")?.Trim();
        var label = ReadString(presentation, "label")?.Trim();
        return string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(label)
            ? null
            : new OrderOperatorStatusPresentation(code, label);
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

    private sealed class OrderMarkingImportPreviewResponse
    {
        [JsonPropertyName("message")] public string? Message { get; init; }
        [JsonPropertyName("snapshot_hash")] public string? SnapshotHash { get; init; }
        [JsonPropertyName("requires_recovery_confirmation")] public bool RequiresRecoveryConfirmation { get; init; }
        [JsonPropertyName("warnings")] public string[]? Warnings { get; init; }
        [JsonPropertyName("files")] public OrderMarkingImportFileResponse[]? Files { get; init; }
        [JsonPropertyName("requests")] public OrderMarkingImportRequestResponse[]? Requests { get; init; }
    }

    private sealed class OrderMarkingImportFileResponse
    {
        [JsonPropertyName("filename")] public string? Filename { get; init; }
        [JsonPropertyName("valid_rows")] public int ValidRows { get; init; }
        [JsonPropertyName("invalid_rows")] public int InvalidRows { get; init; }
        [JsonPropertyName("duplicate_rows")] public int DuplicateRows { get; init; }
    }

    private sealed class OrderMarkingImportRequestResponse
    {
        [JsonPropertyName("marking_order_id")] public Guid MarkingOrderId { get; init; }
        [JsonPropertyName("request_number")] public string? RequestNumber { get; init; }
        [JsonPropertyName("gtin")] public string? Gtin { get; init; }
        [JsonPropertyName("required_qty")] public int RequiredQuantity { get; init; }
        [JsonPropertyName("requested_qty")] public int RequestedQuantity { get; init; }
        [JsonPropertyName("imported_after")] public int ImportedAfter { get; init; }
        [JsonPropertyName("coverage_will_activate")] public bool CoverageWillActivate { get; init; }
        [JsonPropertyName("reserve_short")] public bool ReserveShort { get; init; }
    }

    private sealed class OrderMarkingImportConfirmResponse
    {
        [JsonPropertyName("batch_id")] public Guid BatchId { get; init; }
        [JsonPropertyName("persisted_code_count")] public int PersistedCodeCount { get; init; }
        [JsonPropertyName("activated_marking_order_ids")] public Guid[]? ActivatedMarkingOrderIds { get; init; }
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

public sealed record OrderMarkingImportPreviewApiResult(
    bool IsSuccess,
    string Message,
    string SnapshotHash,
    bool RequiresRecoveryConfirmation,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<OrderMarkingImportFileApiResult> Files,
    IReadOnlyList<OrderMarkingImportRequestApiResult> Requests)
{
    public static OrderMarkingImportPreviewApiResult Failure(string message) =>
        new(false, message, string.Empty, false, Array.Empty<string>(),
            Array.Empty<OrderMarkingImportFileApiResult>(), Array.Empty<OrderMarkingImportRequestApiResult>());
}

public sealed record OrderMarkingImportFileApiResult(
    string Filename,
    int ValidRows,
    int InvalidRows,
    int DuplicateRows);

public sealed record OrderMarkingImportRequestApiResult(
    Guid MarkingOrderId,
    string RequestNumber,
    string Gtin,
    int RequiredQuantity,
    int RequestedQuantity,
    int ImportedAfter,
    bool CoverageWillActivate,
    bool ReserveShort);

public sealed record OrderMarkingImportConfirmApiResult(
    bool IsSuccess,
    string Message,
    Guid BatchId,
    int PersistedCodeCount,
    int ActivatedRequestCount)
{
    public static OrderMarkingImportConfirmApiResult Failure(string message) =>
        new(false, message, Guid.Empty, 0, 0);
}
