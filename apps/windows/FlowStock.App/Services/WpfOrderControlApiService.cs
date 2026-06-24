using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowStock.App.Services;

public sealed class WpfOrderControlApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly SettingsService _settings;
    private readonly FileLogger _logger;

    public WpfOrderControlApiService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<WpfOrderControlPreviewResult> PreviewAsync(IReadOnlyList<long> orderIds, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                return WpfOrderControlPreviewResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            using var response = await client.PostAsJsonAsync("/api/order-control/preview", new { order_ids = orderIds }, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WpfOrderControlPreviewResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapPreview(doc.RootElement);
        }
        catch (Exception ex)
        {
            _logger.Error("Order control preview failed", ex);
            return WpfOrderControlPreviewResult.Failure(ex.Message);
        }
    }

    public async Task<WpfOrderControlOperationResult> CreateAsync(IReadOnlyList<long> orderIds, CancellationToken cancellationToken = default)
    {
        return await PostOperationAsync(
            "/api/order-control/tasks",
            new { order_ids = orderIds, created_by = Environment.UserName },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<WpfOrderControlListResult> ListAsync(bool activeOnly = false, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                return WpfOrderControlListResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            var url = $"/api/order-control/tasks?activeOnly={activeOnly.ToString().ToLowerInvariant()}";
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WpfOrderControlListResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var rows = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().Select(MapTaskSummary).ToArray()
                : Array.Empty<WpfOrderControlTaskRow>();
            return new WpfOrderControlListResult(true, null, rows);
        }
        catch (Exception ex)
        {
            _logger.Error("Order control list failed", ex);
            return WpfOrderControlListResult.Failure(ex.Message);
        }
    }

    public async Task<WpfOrderControlDetailsResult> GetAsync(long taskId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                return WpfOrderControlDetailsResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            using var response = await client.GetAsync($"/api/order-control/tasks/{taskId}", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WpfOrderControlDetailsResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapDetails(doc.RootElement);
        }
        catch (Exception ex)
        {
            _logger.Error("Order control details failed", ex);
            return WpfOrderControlDetailsResult.Failure(ex.Message);
        }
    }

    public Task<WpfOrderControlOperationResult> CancelAsync(long taskId, CancellationToken cancellationToken = default)
    {
        return PostOperationAsync(
            $"/api/order-control/tasks/{taskId}/cancel",
            new { cancelled_by = Environment.UserName },
            cancellationToken);
    }

    private async Task<WpfOrderControlOperationResult> PostOperationAsync(string path, object body, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                return WpfOrderControlOperationResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            using var response = await client.PostAsJsonAsync(path, body, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WpfOrderControlOperationResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var task = doc.RootElement.TryGetProperty("task", out var taskElement)
                ? MapTaskSummary(taskElement.TryGetProperty("task", out var nested) ? nested : taskElement)
                : null;
            return new WpfOrderControlOperationResult(true, null, ReadString(doc.RootElement, "message"), task);
        }
        catch (Exception ex)
        {
            _logger.Error($"Order control API failed: {path}", ex);
            return WpfOrderControlOperationResult.Failure(ex.Message);
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

    private static WpfOrderControlPreviewResult MapPreview(JsonElement element)
    {
        return new WpfOrderControlPreviewResult(
            true,
            null,
            ReadBool(element, "can_create"),
            ReadString(element, "message"),
            element.TryGetProperty("orders", out var orders) && orders.ValueKind == JsonValueKind.Array
                ? orders.EnumerateArray().Select(order => new WpfOrderControlPreviewOrder(
                    ReadInt64(order, "order_id"),
                    ReadString(order, "order_ref") ?? string.Empty,
                    ReadString(order, "partner_name"),
                    ReadBool(order, "is_eligible"),
                    ReadString(order, "error"),
                    ReadString(order, "message"))).ToArray()
                : Array.Empty<WpfOrderControlPreviewOrder>(),
            element.TryGetProperty("hus", out var hus) && hus.ValueKind == JsonValueKind.Array
                ? hus.EnumerateArray().Select(hu => new WpfOrderControlPreviewHu(
                    ReadString(hu, "hu_code") ?? string.Empty,
                    ReadString(hu, "order_refs") ?? string.Empty,
                    ReadString(hu, "item_summary") ?? string.Empty,
                    ReadString(hu, "location_code") ?? string.Empty,
                    ReadString(hu, "source_type") ?? string.Empty,
                    ReadDouble(hu, "qty"))).ToArray()
                : Array.Empty<WpfOrderControlPreviewHu>());
    }

    private static WpfOrderControlDetailsResult MapDetails(JsonElement element)
    {
        var task = element.TryGetProperty("task", out var taskElement) ? MapTaskSummary(taskElement) : null;
        var hus = element.TryGetProperty("hus", out var husElement) && husElement.ValueKind == JsonValueKind.Array
            ? husElement.EnumerateArray().Select(hu => new WpfOrderControlHuRow(
                ReadString(hu, "hu_code") ?? string.Empty,
                ReadString(hu, "status") ?? string.Empty,
                ReadString(hu, "item_summary") ?? string.Empty,
                ReadDouble(hu, "qty"),
                ReadString(hu, "checked_by_device_id"),
                ReadDateTime(hu, "checked_at"),
                ReadString(hu, "error"),
                ReadString(hu, "message"))).ToArray()
            : Array.Empty<WpfOrderControlHuRow>();
        var events = element.TryGetProperty("events", out var eventsElement) && eventsElement.ValueKind == JsonValueKind.Array
            ? eventsElement.EnumerateArray().Select(e => new WpfOrderControlEventRow(
                ReadDateTime(e, "event_at") ?? DateTime.MinValue,
                ReadString(e, "event_type") ?? string.Empty,
                ReadString(e, "hu_code"),
                ReadString(e, "device_id"),
                ReadString(e, "error"),
                ReadString(e, "message"))).ToArray()
            : Array.Empty<WpfOrderControlEventRow>();
        return new WpfOrderControlDetailsResult(true, null, task, hus, events);
    }

    private static WpfOrderControlTaskRow MapTaskSummary(JsonElement element)
    {
        var orderRefs = element.TryGetProperty("orders", out var orders) && orders.ValueKind == JsonValueKind.Array
            ? string.Join(", ", orders.EnumerateArray().Select(order => ReadString(order, "order_ref")).Where(value => !string.IsNullOrWhiteSpace(value)))
            : string.Empty;
        return new WpfOrderControlTaskRow(
            ReadInt64(element, "id"),
            ReadString(element, "task_ref") ?? string.Empty,
            ReadString(element, "status_display") ?? ReadString(element, "status") ?? string.Empty,
            orderRefs,
            ReadInt32(element, "checked_hu_count"),
            ReadInt32(element, "expected_hu_count"),
            ReadInt32(element, "discrepancy_hu_count"),
            ReadDateTime(element, "created_at") ?? DateTime.MinValue);
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

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null ? property.GetString() : null;

    private static bool ReadBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;

    private static long ReadInt64(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.TryGetInt64(out var value) ? value : 0;

    private static int ReadInt32(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.TryGetInt32(out var value) ? value : 0;

    private static double ReadDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.TryGetDouble(out var value) ? value : 0d;

    private static DateTime? ReadDateTime(JsonElement element, string name)
        => DateTime.TryParse(ReadString(element, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value : null;

    private sealed record WpfApiConfiguration(string? BaseUrl, int TimeoutSeconds, bool AllowInvalidTls);

    private sealed class ApiErrorResponse
    {
        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }
    }
}

public sealed record WpfOrderControlPreviewResult(
    bool IsSuccess,
    string? ErrorMessage,
    bool CanCreate,
    string? Message,
    IReadOnlyList<WpfOrderControlPreviewOrder> Orders,
    IReadOnlyList<WpfOrderControlPreviewHu> Hus)
{
    public static WpfOrderControlPreviewResult Failure(string message) =>
        new(false, message, false, message, Array.Empty<WpfOrderControlPreviewOrder>(), Array.Empty<WpfOrderControlPreviewHu>());
}

public sealed record WpfOrderControlPreviewOrder(
    long OrderId,
    string OrderRef,
    string? PartnerName,
    bool IsEligible,
    string? Error,
    string? Message);

public sealed record WpfOrderControlPreviewHu(
    string HuCode,
    string OrderRefs,
    string ItemSummary,
    string LocationCode,
    string SourceType,
    double Qty);

public sealed record WpfOrderControlTaskRow(
    long Id,
    string TaskRef,
    string Status,
    string OrderRefs,
    int CheckedHuCount,
    int ExpectedHuCount,
    int DiscrepancyHuCount,
    DateTime CreatedAt)
{
    public string ProgressDisplay => $"{CheckedHuCount}/{ExpectedHuCount}";
}

public sealed record WpfOrderControlHuRow(
    string HuCode,
    string Status,
    string ItemSummary,
    double Qty,
    string? DeviceId,
    DateTime? CheckedAt,
    string? Error,
    string? Message);

public sealed record WpfOrderControlEventRow(
    DateTime EventAt,
    string EventType,
    string? HuCode,
    string? DeviceId,
    string? Error,
    string? Message);

public sealed record WpfOrderControlListResult(
    bool IsSuccess,
    string? ErrorMessage,
    IReadOnlyList<WpfOrderControlTaskRow> Tasks)
{
    public static WpfOrderControlListResult Failure(string message) =>
        new(false, message, Array.Empty<WpfOrderControlTaskRow>());
}

public sealed record WpfOrderControlDetailsResult(
    bool IsSuccess,
    string? ErrorMessage,
    WpfOrderControlTaskRow? Task,
    IReadOnlyList<WpfOrderControlHuRow> Hus,
    IReadOnlyList<WpfOrderControlEventRow> Events)
{
    public static WpfOrderControlDetailsResult Failure(string message) =>
        new(false, message, null, Array.Empty<WpfOrderControlHuRow>(), Array.Empty<WpfOrderControlEventRow>());
}

public sealed record WpfOrderControlOperationResult(
    bool IsSuccess,
    string? ErrorMessage,
    string? Message,
    WpfOrderControlTaskRow? Task)
{
    public static WpfOrderControlOperationResult Failure(string message) =>
        new(false, message, null, null);
}
