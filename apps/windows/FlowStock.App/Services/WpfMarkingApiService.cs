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

    public WpfMarkingApiService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
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

            using var handler = CreateHandler(configuration);
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
            return (false, null, null, ex.Message);
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

            using var handler = CreateHandler(configuration);
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
            return (false, ex.Message, 0, 0);
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

            using var handler = CreateHandler(configuration);
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

    private WpfMarkingApiConfiguration LoadConfiguration()
    {
        var settings = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = ReadEnvOrSettings("FLOWSTOCK_SERVER_BASE_URL", settings.BaseUrl);
        var timeoutSeconds = ReadEnvInt("FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS") ?? settings.CloseTimeoutSeconds;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = WpfCloseDocumentService.DefaultCloseTimeoutSeconds;
        }

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
            OrderId = ReadInt64(element, "order_id"),
            OrderRef = ReadString(element, "order_ref") ?? string.Empty,
            PartnerName = ReadString(element, "partner_name"),
            PartnerCode = ReadString(element, "partner_code"),
            SourceType = ReadString(element, "source_type"),
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

        return $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}.";
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

    private sealed class CreateMarkingResponse
    {
        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;

        [JsonPropertyName("created_task_count")]
        public int CreatedTaskCount { get; init; }

        [JsonPropertyName("created_qty")]
        public double CreatedQty { get; init; }
    }
}

public sealed record WpfMarkingApiConfiguration(string? BaseUrl, int TimeoutSeconds, bool AllowInvalidTls)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
}
