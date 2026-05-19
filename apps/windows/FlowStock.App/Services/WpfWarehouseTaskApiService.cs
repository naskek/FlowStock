using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowStock.App.Services;

public sealed class WpfWarehouseTaskApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsService _settings;
    private readonly FileLogger _logger;

    public WpfWarehouseTaskApiService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<WpfWarehouseBundleListResult> TryListBundlesAsync(
        string? statusFilter,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                return WpfWarehouseBundleListResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            var url = string.IsNullOrWhiteSpace(statusFilter)
                ? "/api/planner/bundles"
                : $"/api/planner/bundles?status={Uri.EscapeDataString(statusFilter)}";
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WpfWarehouseBundleListResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            var payload = await response.Content.ReadFromJsonAsync<BundleListResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            var rows = (payload?.Bundles ?? Array.Empty<BundleSummaryResponse>())
                .Select(MapSummary)
                .ToArray();
            return new WpfWarehouseBundleListResult(true, null, rows);
        }
        catch (Exception ex)
        {
            _logger.Error("Warehouse bundle list failed", ex);
            return WpfWarehouseBundleListResult.Failure(ex.Message);
        }
    }

    public async Task<WpfWarehouseBundleDetailResult> TryGetBundleAsync(
        long bundleId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                return WpfWarehouseBundleDetailResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            using var response = await client.GetAsync($"/api/planner/bundles/{bundleId}", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WpfWarehouseBundleDetailResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            var payload = await response.Content.ReadFromJsonAsync<BundleDetailResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (payload?.Bundle == null)
            {
                return WpfWarehouseBundleDetailResult.Failure("Сервер вернул пустой ответ.");
            }

            return new WpfWarehouseBundleDetailResult(
                true,
                null,
                MapSummary(payload.Bundle),
                (payload.Lines ?? Array.Empty<BundleLineResponse>()).Select(MapLine).ToArray(),
                (payload.Tasks ?? Array.Empty<BundleTaskDetailResponse>()).Select(MapTaskDetail).ToArray());
        }
        catch (Exception ex)
        {
            _logger.Error("Warehouse bundle detail failed", ex);
            return WpfWarehouseBundleDetailResult.Failure(ex.Message);
        }
    }

    public async Task<WpfWarehouseBundleOperationApiResult> TryCreateBundleAsync(
        string? comment = null,
        CancellationToken cancellationToken = default)
    {
        return await PostOperationAsync(
            "/api/planner/bundles",
            new { source = "WPF", created_by = Environment.UserName, comment },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<WpfWarehouseBundleOperationApiResult> TryAddLineAsync(
        long bundleId,
        WarehouseBundleLineRequest line,
        CancellationToken cancellationToken = default)
    {
        return await PostOperationAsync(
            $"/api/planner/bundles/{bundleId}/lines",
            line,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<WpfWarehouseBundleOperationApiResult> TrySubmitBundleAsync(
        long bundleId,
        CancellationToken cancellationToken = default)
    {
        return await PostOperationAsync(
            $"/api/planner/bundles/{bundleId}/submit",
            new { actor = Environment.UserName },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<WpfWarehouseBundleOperationApiResult> TryApproveBundleAsync(
        long bundleId,
        CancellationToken cancellationToken = default)
    {
        return await PostOperationAsync(
            $"/api/planner/bundles/{bundleId}/approve",
            new { actor = Environment.UserName },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<WpfWarehouseBundleOperationApiResult> TryRejectBundleAsync(
        long bundleId,
        string? comment,
        CancellationToken cancellationToken = default)
    {
        return await PostOperationAsync(
            $"/api/planner/bundles/{bundleId}/reject",
            new { actor = Environment.UserName, comment },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<WpfWarehouseBundleOperationApiResult> TryConfirmExecutionAsync(
        long bundleId,
        CancellationToken cancellationToken = default)
    {
        return await PostOperationAsync(
            $"/api/planner/bundles/{bundleId}/confirm-execution",
            new { actor = Environment.UserName },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<WpfWarehouseBundleOperationApiResult> PostOperationAsync(
        string path,
        object body,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                return WpfWarehouseBundleOperationApiResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            using var response = await client.PostAsJsonAsync(path, body, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WpfWarehouseBundleOperationApiResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            var payload = await response.Content.ReadFromJsonAsync<OperationResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return new WpfWarehouseBundleOperationApiResult(
                true,
                null,
                payload?.BundleId,
                payload?.BundleRef,
                payload?.Status,
                payload?.Message);
        }
        catch (Exception ex)
        {
            _logger.Error($"Warehouse API failed: {path}", ex);
            return WpfWarehouseBundleOperationApiResult.Failure(ex.Message);
        }
    }

    private bool TryLoadConfiguration(out WpfApiConfiguration configuration)
    {
        var server = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = NormalizeBaseUrl(ReadEnvOrSettings("FLOWSTOCK_SERVER_BASE_URL", server.BaseUrl));
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            configuration = default!;
            return false;
        }

        var timeoutSeconds = ReadEnvInt("FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS") ?? server.CloseTimeoutSeconds;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = 120;
        }

        var allowInvalidTls = ReadEnvBool("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS") ?? server.AllowInvalidTls;
        configuration = new WpfApiConfiguration(baseUrl, timeoutSeconds, allowInvalidTls);
        return true;
    }

    private static HttpClient CreateClient(HttpMessageHandler handler, WpfApiConfiguration configuration) =>
        new(handler)
        {
            BaseAddress = new Uri(configuration.BaseUrl!, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds)
        };

    private static HttpMessageHandler CreateHandler(WpfApiConfiguration configuration)
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
            if (!string.IsNullOrWhiteSpace(error?.Message))
            {
                return error.Message;
            }

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

    private static WarehouseBundleListRow MapSummary(BundleSummaryResponse bundle) =>
        new()
        {
            Id = bundle.Id,
            BundleRef = bundle.BundleRef ?? string.Empty,
            Source = bundle.Source ?? string.Empty,
            Status = bundle.Status ?? string.Empty,
            StatusDisplay = MapStatusDisplay(bundle.Status),
            CreatedAt = bundle.CreatedAt ?? DateTime.MinValue,
            CreatedBy = bundle.CreatedBy,
            CompletedAt = bundle.CompletedAt
        };

    private static WarehouseBundleLineRow MapLine(BundleLineResponse line) =>
        new()
        {
            LineNo = line.LineNo,
            ActionType = line.ActionType ?? string.Empty,
            Status = line.Status ?? string.Empty,
            HuCode = line.HuCode,
            Summary = BuildLineSummary(line)
        };

    private static WarehouseBundleTaskDetailRow MapTaskDetail(BundleTaskDetailResponse detail) =>
        new()
        {
            TaskRef = detail.Task?.TaskRef ?? string.Empty,
            Status = detail.Task?.Status ?? string.Empty,
            ExpectedHuCode = detail.Lines?.FirstOrDefault()?.ExpectedHuCode,
            Events = (detail.Events ?? Array.Empty<TaskEventResponse>())
                .Select(e => $"{e.EventAt:HH:mm} {e.EventType} {e.HuCode} {e.Message}".Trim())
                .ToArray()
        };

    private static string MapStatusDisplay(string? status) => status?.ToUpperInvariant() switch
    {
        "DRAFT" => "Черновик",
        "SUBMITTED" => "На подтверждении",
        "APPROVED" => "Подтверждён",
        "IN_EXECUTION" => "В работе",
        "EXECUTED" => "Исполнено ТСД",
        "COMPLETED" => "Проведено",
        "REJECTED" => "Отклонено",
        "CANCELLED" => "Отменён",
        "FAILED" => "Ошибка",
        _ => status ?? string.Empty
    };

    private static string BuildLineSummary(BundleLineResponse line)
    {
        if (string.Equals(line.ActionType, "MOVE_HU", StringComparison.OrdinalIgnoreCase))
        {
            return $"MOVE {line.HuCode} → loc {line.ToLocationId}";
        }

        if (string.Equals(line.ActionType, "ADOPT_PALLET_PLAN", StringComparison.OrdinalIgnoreCase))
        {
            return $"ADOPT {line.SourceOrderId} → {line.TargetOrderId}";
        }

        return line.ActionType ?? string.Empty;
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
        return !string.IsNullOrWhiteSpace(env) ? env.Trim() : string.IsNullOrWhiteSpace(settingsValue) ? null : settingsValue.Trim();
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
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => null
        };
    }

    private static int? ReadEnvInt(string envKey) =>
        int.TryParse(Environment.GetEnvironmentVariable(envKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private sealed record WpfApiConfiguration(string? BaseUrl, int TimeoutSeconds, bool AllowInvalidTls);

    private sealed class ApiErrorResponse
    {
        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }
    }

    private sealed class BundleListResponse
    {
        [JsonPropertyName("bundles")]
        public IReadOnlyList<BundleSummaryResponse>? Bundles { get; init; }
    }

    private sealed class BundleDetailResponse
    {
        [JsonPropertyName("bundle")]
        public BundleSummaryResponse? Bundle { get; init; }

        [JsonPropertyName("lines")]
        public IReadOnlyList<BundleLineResponse>? Lines { get; init; }

        [JsonPropertyName("tasks")]
        public IReadOnlyList<BundleTaskDetailResponse>? Tasks { get; init; }
    }

    private sealed class BundleSummaryResponse
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("bundle_ref")]
        public string? BundleRef { get; init; }

        [JsonPropertyName("source")]
        public string? Source { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("created_at")]
        public DateTime? CreatedAt { get; init; }

        [JsonPropertyName("created_by")]
        public string? CreatedBy { get; init; }

        [JsonPropertyName("completed_at")]
        public DateTime? CompletedAt { get; init; }
    }

    private sealed class BundleLineResponse
    {
        [JsonPropertyName("line_no")]
        public int LineNo { get; init; }

        [JsonPropertyName("action_type")]
        public string? ActionType { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("hu_code")]
        public string? HuCode { get; init; }

        [JsonPropertyName("source_order_id")]
        public long? SourceOrderId { get; init; }

        [JsonPropertyName("target_order_id")]
        public long? TargetOrderId { get; init; }

        [JsonPropertyName("to_location_id")]
        public long? ToLocationId { get; init; }
    }

    private sealed class BundleTaskDetailResponse
    {
        [JsonPropertyName("task")]
        public TaskResponse? Task { get; init; }

        [JsonPropertyName("lines")]
        public IReadOnlyList<TaskLineResponse>? Lines { get; init; }

        [JsonPropertyName("events")]
        public IReadOnlyList<TaskEventResponse>? Events { get; init; }
    }

    private sealed class TaskResponse
    {
        [JsonPropertyName("task_ref")]
        public string? TaskRef { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }
    }

    private sealed class TaskLineResponse
    {
        [JsonPropertyName("expected_hu_code")]
        public string? ExpectedHuCode { get; init; }
    }

    private sealed class TaskEventResponse
    {
        [JsonPropertyName("event_type")]
        public string? EventType { get; init; }

        [JsonPropertyName("event_at")]
        public DateTime EventAt { get; init; }

        [JsonPropertyName("hu_code")]
        public string? HuCode { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }
    }

    private sealed class OperationResponse
    {
        [JsonPropertyName("bundle_id")]
        public long? BundleId { get; init; }

        [JsonPropertyName("bundle_ref")]
        public string? BundleRef { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }
    }
}

public sealed class WarehouseBundleListRow
{
    public long Id { get; init; }
    public string BundleRef { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string StatusDisplay { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? CompletedAt { get; init; }
}

public sealed class WarehouseBundleLineRow
{
    public int LineNo { get; init; }
    public string ActionType { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? HuCode { get; init; }
    public string Summary { get; init; } = string.Empty;
}

public sealed class WarehouseBundleTaskDetailRow
{
    public string TaskRef { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? ExpectedHuCode { get; init; }
    public IReadOnlyList<string> Events { get; init; } = Array.Empty<string>();
}

public sealed record WarehouseBundleLineRequest
{
    [JsonPropertyName("action_type")]
    public string ActionType { get; init; } = string.Empty;

    [JsonPropertyName("payload_json")]
    public string PayloadJson { get; init; } = "{}";

    [JsonPropertyName("source_order_id")]
    public long? SourceOrderId { get; init; }

    [JsonPropertyName("target_order_id")]
    public long? TargetOrderId { get; init; }

    [JsonPropertyName("item_id")]
    public long? ItemId { get; init; }

    [JsonPropertyName("hu_code")]
    public string? HuCode { get; init; }

    [JsonPropertyName("from_location_id")]
    public long? FromLocationId { get; init; }

    [JsonPropertyName("to_location_id")]
    public long? ToLocationId { get; init; }

    [JsonPropertyName("qty")]
    public double? Qty { get; init; }
}

public sealed record WpfWarehouseBundleListResult(
    bool IsSuccess,
    string? ErrorMessage,
    IReadOnlyList<WarehouseBundleListRow> Bundles)
{
    public static WpfWarehouseBundleListResult Failure(string message) =>
        new(false, message, Array.Empty<WarehouseBundleListRow>());
}

public sealed record WpfWarehouseBundleDetailResult(
    bool IsSuccess,
    string? ErrorMessage,
    WarehouseBundleListRow? Bundle,
    IReadOnlyList<WarehouseBundleLineRow> Lines,
    IReadOnlyList<WarehouseBundleTaskDetailRow> Tasks)
{
    public static WpfWarehouseBundleDetailResult Failure(string message) =>
        new(false, message, null, Array.Empty<WarehouseBundleLineRow>(), Array.Empty<WarehouseBundleTaskDetailRow>());
}

public sealed record WpfWarehouseBundleOperationApiResult(
    bool IsSuccess,
    string? ErrorMessage,
    long? BundleId,
    string? BundleRef,
    string? Status,
    string? Message)
{
    public static WpfWarehouseBundleOperationApiResult Failure(string message) =>
        new(false, message, null, null, null, null);
}
