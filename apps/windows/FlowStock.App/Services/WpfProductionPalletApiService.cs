using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowStock.App;

public sealed class WpfProductionPalletApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsService _settings;
    private readonly FileLogger _logger;

    public WpfProductionPalletApiService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<WpfProductionPalletPlanApiResult> TryPlanOrderAsync(
        long orderId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info("Production pallet API skipped for order plan: server base URL is not configured.");
                return WpfProductionPalletPlanApiResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            using var response = await client.PostAsJsonAsync($"/api/orders/{orderId}/production-pallets/plan", new { }, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WpfProductionPalletPlanApiResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            var payload = await response.Content.ReadFromJsonAsync<OrderPlanResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (payload == null)
            {
                return WpfProductionPalletPlanApiResult.Failure("Сервер вернул пустой ответ.");
            }

            return new WpfProductionPalletPlanApiResult(
                true,
                payload.WasExisting ? "План паллет уже сформирован" : "План паллет сформирован",
                payload.OrderId,
                payload.OrderRef ?? string.Empty,
                payload.PrdDocId,
                payload.PrdRef ?? payload.PrdDocRef ?? string.Empty,
                payload.WasExisting,
                payload.PlannedPalletCount,
                payload.PlannedQty,
                payload.FilledPalletCount,
                payload.FilledQty,
                payload.RemainingPalletCount,
                payload.RemainingQty);
        }
        catch (Exception ex)
        {
            _logger.Error("Production pallet plan failed", ex);
            return WpfProductionPalletPlanApiResult.Failure(ex.Message);
        }
    }

    public async Task<WpfProductionPalletPrintRowsApiResult> TryGetPrintRowsAsync(
        long orderId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info("Production pallet API skipped for print rows: server base URL is not configured.");
                return WpfProductionPalletPrintRowsApiResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            using var response = await client.GetAsync($"/api/orders/{orderId}/production-pallets/print-rows", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WpfProductionPalletPrintRowsApiResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            var payload = await response.Content.ReadFromJsonAsync<List<PrintRowResponse>>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            var rows = (payload ?? new List<PrintRowResponse>())
                .Select(MapPrintRow)
                .ToArray();
            return new WpfProductionPalletPrintRowsApiResult(true, string.Empty, rows);
        }
        catch (Exception ex)
        {
            _logger.Error("Production pallet print rows load failed", ex);
            return WpfProductionPalletPrintRowsApiResult.Failure(ex.Message);
        }
    }

    public async Task<WpfProductionPalletCancelPlanApiResult> TryCancelPlanAsync(
        long orderId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info("Production pallet API skipped for cancel plan: server base URL is not configured.");
                return WpfProductionPalletCancelPlanApiResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            using var response = await client.PostAsJsonAsync($"/api/orders/{orderId}/production-pallets/cancel-plan", new { }, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WpfProductionPalletCancelPlanApiResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            var payload = await response.Content.ReadFromJsonAsync<CancelPlanResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (payload == null)
            {
                return WpfProductionPalletCancelPlanApiResult.Failure("Сервер вернул пустой ответ.");
            }

            return new WpfProductionPalletCancelPlanApiResult(
                true,
                string.IsNullOrWhiteSpace(payload.Message) ? "План паллет удалён." : payload.Message!,
                payload.PrdDocId,
                payload.RemovedPalletCount,
                payload.RemovedLineCount);
        }
        catch (Exception ex)
        {
            _logger.Error("Production pallet cancel plan failed", ex);
            return WpfProductionPalletCancelPlanApiResult.Failure(ex.Message);
        }
    }

    public async Task<WpfProductionPalletAdoptPlanApiResult> TryAdoptPlanFromInternalAsync(
        long targetCustomerOrderId,
        long sourceInternalOrderId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info("Production pallet API skipped for adopt plan: server base URL is not configured.");
                return WpfProductionPalletAdoptPlanApiResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            using var response = await client.PostAsJsonAsync(
                    $"/api/orders/{targetCustomerOrderId}/production-pallets/adopt-from-internal/{sourceInternalOrderId}",
                    new { },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WpfProductionPalletAdoptPlanApiResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            var payload = await response.Content.ReadFromJsonAsync<AdoptPlanResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (payload == null)
            {
                return WpfProductionPalletAdoptPlanApiResult.Failure("Сервер вернул пустой ответ.");
            }

            return new WpfProductionPalletAdoptPlanApiResult(
                true,
                string.IsNullOrWhiteSpace(payload.Message) ? "План паллет перенесён." : payload.Message!,
                payload.SourceOrderId,
                payload.TargetOrderId,
                payload.SourcePrdDocId,
                payload.TargetPrdDocId,
                payload.TransferredPalletCount,
                payload.TransferredLineCount,
                payload.TransferredHuCodes ?? Array.Empty<string>());
        }
        catch (Exception ex)
        {
            _logger.Error("Production pallet adopt plan failed", ex);
            return WpfProductionPalletAdoptPlanApiResult.Failure(ex.Message);
        }
    }

    public async Task<(bool IsSuccess, string? Error)> TryMarkPrintedAsync(
        long orderId,
        CancellationToken cancellationToken = default)
    {
        return await TryMarkPrintedAsync(orderId, palletIds: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TryMarkPrintedAsync(
        long orderId,
        IReadOnlyList<long>? palletIds,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info("Production pallet API skipped for mark printed: server base URL is not configured.");
                return (false, "FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            object body = palletIds is { Count: > 0 }
                ? new { pallet_ids = palletIds }
                : new { };
            using var response = await client.PostAsJsonAsync(
                    $"/api/orders/{orderId}/production-pallets/mark-printed",
                    body,
                    cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? (true, null)
                : (false, await ReadApiErrorAsync(response).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger.Error("Production pallet mark printed failed", ex);
            return (false, ex.Message);
        }
    }

    public async Task<WpfProductionPalletFillApiResult> TryFillPalletAsync(
        long prdDocId,
        long? orderId,
        string huCode,
        string? deviceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info("Production pallet API skipped for manual fill: server base URL is not configured.");
                return WpfProductionPalletFillApiResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            using var response = await client.PostAsJsonAsync("/api/tsd/production/fill-pallet", new
                {
                    order_id = orderId,
                    prd_doc_id = prdDocId,
                    hu_code = huCode,
                    device_id = deviceId
                }, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WpfProductionPalletFillApiResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            var payload = await response.Content.ReadFromJsonAsync<FillResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (payload == null)
            {
                return WpfProductionPalletFillApiResult.Failure("Сервер вернул пустой ответ.");
            }

            return new WpfProductionPalletFillApiResult(
                true,
                string.Empty,
                payload.AlreadyFilled,
                payload.Pallet?.HuCode ?? huCode,
                payload.Pallet?.Status ?? string.Empty,
                payload.Document?.Summary?.PlannedPalletCount ?? 0,
                payload.Document?.Summary?.FilledPalletCount ?? 0,
                payload.Document?.Summary?.RemainingPalletCount ?? 0);
        }
        catch (Exception ex)
        {
            _logger.Error("Production pallet fill failed", ex);
            return WpfProductionPalletFillApiResult.Failure(ex.Message);
        }
    }

    private bool TryLoadConfiguration(out WpfProductionPalletApiConfiguration configuration)
    {
        var settings = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = ReadEnvOrSettings("FLOWSTOCK_SERVER_BASE_URL", settings.BaseUrl);
        var timeoutSeconds = ReadEnvInt("FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS") ?? settings.CloseTimeoutSeconds;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = WpfCloseDocumentService.DefaultCloseTimeoutSeconds;
        }

        configuration = new WpfProductionPalletApiConfiguration(
            NormalizeBaseUrl(baseUrl),
            timeoutSeconds,
            ReadEnvBool("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS") ?? settings.AllowInvalidTls);
        return !string.IsNullOrWhiteSpace(configuration.BaseUrl);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler, WpfProductionPalletApiConfiguration configuration)
    {
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(configuration.BaseUrl!, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds)
        };
    }

    private static HttpMessageHandler CreateHandler(WpfProductionPalletApiConfiguration configuration)
    {
        var handler = new HttpClientHandler();
        if (configuration.AllowInvalidTls)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
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

        return $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}.";
    }

    private static PalletLabelPrintRow MapPrintRow(PrintRowResponse row)
    {
        return new PalletLabelPrintRow
        {
            PalletId = row.PalletId,
            OrderId = row.OrderId,
            OrderRef = row.OrderRef ?? string.Empty,
            ClientName = row.ClientName ?? string.Empty,
            PrdRef = row.PrdRef ?? string.Empty,
            HuCode = row.HuCode ?? string.Empty,
            ItemName = row.ItemName ?? string.Empty,
            Brand = row.Brand ?? string.Empty,
            Qty = row.Qty,
            Uom = string.IsNullOrWhiteSpace(row.Uom) ? "шт" : row.Uom!,
            PalletNo = row.PalletNo,
            PalletCount = row.PalletCount,
            StoragePlace = row.StoragePlace ?? string.Empty,
            ProductionDate = row.ProductionDate,
            Comment = row.Comment ?? string.Empty,
            IsMixedPallet = row.IsMixedPallet,
            Composition = row.Composition ?? string.Empty,
            Line1ItemName = row.Line1ItemName ?? string.Empty,
            Line1Qty = row.Line1Qty,
            Line2ItemName = row.Line2ItemName ?? string.Empty,
            Line2Qty = row.Line2Qty,
            Line3ItemName = row.Line3ItemName ?? string.Empty,
            Line3Qty = row.Line3Qty,
            Status = row.Status ?? string.Empty,
            SourceType = row.SourceType ?? string.Empty
        };
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
        return int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private sealed record WpfProductionPalletApiConfiguration(string? BaseUrl, int TimeoutSeconds, bool AllowInvalidTls);

    private sealed class CancelPlanResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("prd_doc_id")]
        public long PrdDocId { get; init; }

        [JsonPropertyName("removed_pallet_count")]
        public int RemovedPalletCount { get; init; }

        [JsonPropertyName("removed_line_count")]
        public int RemovedLineCount { get; init; }
    }

    private sealed class AdoptPlanResponse
    {
        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("source_order_id")]
        public long SourceOrderId { get; init; }

        [JsonPropertyName("target_order_id")]
        public long TargetOrderId { get; init; }

        [JsonPropertyName("source_prd_doc_id")]
        public long SourcePrdDocId { get; init; }

        [JsonPropertyName("target_prd_doc_id")]
        public long TargetPrdDocId { get; init; }

        [JsonPropertyName("transferred_pallet_count")]
        public int TransferredPalletCount { get; init; }

        [JsonPropertyName("transferred_line_count")]
        public int TransferredLineCount { get; init; }

        [JsonPropertyName("transferred_hu_codes")]
        public IReadOnlyList<string>? TransferredHuCodes { get; init; }
    }

    private sealed class OrderPlanResponse
    {
        [JsonPropertyName("order_id")]
        public long OrderId { get; init; }

        [JsonPropertyName("order_ref")]
        public string? OrderRef { get; init; }

        [JsonPropertyName("prd_doc_id")]
        public long PrdDocId { get; init; }

        [JsonPropertyName("prd_ref")]
        public string? PrdRef { get; init; }

        [JsonPropertyName("prd_doc_ref")]
        public string? PrdDocRef { get; init; }

        [JsonPropertyName("was_existing")]
        public bool WasExisting { get; init; }

        [JsonPropertyName("planned_pallet_count")]
        public int PlannedPalletCount { get; init; }

        [JsonPropertyName("planned_qty")]
        public double PlannedQty { get; init; }

        [JsonPropertyName("filled_pallet_count")]
        public int FilledPalletCount { get; init; }

        [JsonPropertyName("filled_qty")]
        public double FilledQty { get; init; }

        [JsonPropertyName("remaining_pallet_count")]
        public int RemainingPalletCount { get; init; }

        [JsonPropertyName("remaining_qty")]
        public double RemainingQty { get; init; }
    }

    private sealed class PrintRowResponse
    {
        [JsonPropertyName("pallet_id")]
        public long PalletId { get; init; }

        [JsonPropertyName("order_id")]
        public long OrderId { get; init; }

        [JsonPropertyName("order_ref")]
        public string? OrderRef { get; init; }

        [JsonPropertyName("client_name")]
        public string? ClientName { get; init; }

        [JsonPropertyName("prd_ref")]
        public string? PrdRef { get; init; }

        [JsonPropertyName("hu_code")]
        public string? HuCode { get; init; }

        [JsonPropertyName("item_name")]
        public string? ItemName { get; init; }

        [JsonPropertyName("brand")]
        public string? Brand { get; init; }

        [JsonPropertyName("qty")]
        public double Qty { get; init; }

        [JsonPropertyName("uom")]
        public string? Uom { get; init; }

        [JsonPropertyName("pallet_no")]
        public int PalletNo { get; init; }

        [JsonPropertyName("pallet_count")]
        public int PalletCount { get; init; }

        [JsonPropertyName("storage_place")]
        public string? StoragePlace { get; init; }

        [JsonPropertyName("production_date")]
        public DateTime? ProductionDate { get; init; }

        [JsonPropertyName("comment")]
        public string? Comment { get; init; }

        [JsonPropertyName("is_mixed_pallet")]
        public bool IsMixedPallet { get; init; }

        [JsonPropertyName("composition")]
        public string? Composition { get; init; }

        [JsonPropertyName("line1_item_name")]
        public string? Line1ItemName { get; init; }

        [JsonPropertyName("line1_qty")]
        public double Line1Qty { get; init; }

        [JsonPropertyName("line2_item_name")]
        public string? Line2ItemName { get; init; }

        [JsonPropertyName("line2_qty")]
        public double Line2Qty { get; init; }

        [JsonPropertyName("line3_item_name")]
        public string? Line3ItemName { get; init; }

        [JsonPropertyName("line3_qty")]
        public double Line3Qty { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("source_type")]
        public string? SourceType { get; init; }
    }

    private sealed class FillResponse
    {
        [JsonPropertyName("already_filled")]
        public bool AlreadyFilled { get; init; }

        [JsonPropertyName("pallet")]
        public FillPalletResponse? Pallet { get; init; }

        [JsonPropertyName("document")]
        public FillDocumentResponse? Document { get; init; }
    }

    private sealed class FillPalletResponse
    {
        [JsonPropertyName("hu_code")]
        public string? HuCode { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }
    }

    private sealed class FillDocumentResponse
    {
        [JsonPropertyName("summary")]
        public FillSummaryResponse? Summary { get; init; }
    }

    private sealed class FillSummaryResponse
    {
        [JsonPropertyName("planned_pallet_count")]
        public int PlannedPalletCount { get; init; }

        [JsonPropertyName("filled_pallet_count")]
        public int FilledPalletCount { get; init; }

        [JsonPropertyName("remaining_pallet_count")]
        public int RemainingPalletCount { get; init; }
    }
}

public sealed record WpfProductionPalletCancelPlanApiResult(
    bool IsSuccess,
    string Message,
    long PrdDocId,
    int RemovedPalletCount,
    int RemovedLineCount)
{
    public static WpfProductionPalletCancelPlanApiResult Failure(string message)
    {
        return new WpfProductionPalletCancelPlanApiResult(false, message, 0, 0, 0);
    }
}

public sealed record WpfProductionPalletAdoptPlanApiResult(
    bool IsSuccess,
    string Message,
    long SourceOrderId,
    long TargetOrderId,
    long SourcePrdDocId,
    long TargetPrdDocId,
    int TransferredPalletCount,
    int TransferredLineCount,
    IReadOnlyList<string> TransferredHuCodes)
{
    public static WpfProductionPalletAdoptPlanApiResult Failure(string message)
    {
        return new WpfProductionPalletAdoptPlanApiResult(false, message, 0, 0, 0, 0, 0, 0, Array.Empty<string>());
    }
}

public sealed record WpfProductionPalletPlanApiResult(
    bool IsSuccess,
    string Message,
    long OrderId,
    string OrderRef,
    long PrdDocId,
    string PrdRef,
    bool WasExisting,
    int PlannedPalletCount,
    double PlannedQty,
    int FilledPalletCount,
    double FilledQty,
    int RemainingPalletCount,
    double RemainingQty)
{
    public static WpfProductionPalletPlanApiResult Failure(string message)
    {
        return new WpfProductionPalletPlanApiResult(false, message, 0, string.Empty, 0, string.Empty, false, 0, 0, 0, 0, 0, 0);
    }
}

public sealed record WpfProductionPalletPrintRowsApiResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<PalletLabelPrintRow> Rows)
{
    public static WpfProductionPalletPrintRowsApiResult Failure(string message)
    {
        return new WpfProductionPalletPrintRowsApiResult(false, message, Array.Empty<PalletLabelPrintRow>());
    }
}

public sealed record WpfProductionPalletFillApiResult(
    bool IsSuccess,
    string Message,
    bool AlreadyFilled,
    string HuCode,
    string PalletStatus,
    int PlannedPalletCount,
    int FilledPalletCount,
    int RemainingPalletCount)
{
    public static WpfProductionPalletFillApiResult Failure(string message)
    {
        return new WpfProductionPalletFillApiResult(false, message, false, string.Empty, string.Empty, 0, 0, 0);
    }
}
