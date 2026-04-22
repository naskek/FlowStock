using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FlowStock.Core.Models;

namespace FlowStock.App;

public sealed class WpfIncomingRequestsApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsService _settings;
    private readonly FileLogger _logger;

    public WpfIncomingRequestsApiService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool TryGetItemRequests(bool includeResolved, out IReadOnlyList<ItemRequest> requests)
    {
        requests = Array.Empty<ItemRequest>();
        var path = includeResolved ? "/api/item-requests?include_resolved=1" : "/api/item-requests";
        return TryRead(
            path,
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().Select(MapItemRequest).ToList()
                : new List<ItemRequest>(),
            "incoming-item-requests",
            out requests);
    }

    public bool TryGetOrderRequests(bool includeResolved, out IReadOnlyList<OrderRequest> requests)
    {
        requests = Array.Empty<OrderRequest>();
        var path = includeResolved ? "/api/orders/requests?include_resolved=1" : "/api/orders/requests";
        return TryRead(
            path,
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().Select(MapOrderRequest).ToList()
                : new List<OrderRequest>(),
            "incoming-order-requests",
            out requests);
    }

    public bool TryGetSummary(out IncomingRequestsSummary summary)
    {
        summary = new IncomingRequestsSummary(0, 0);
        return TryRead(
            "/api/requests/summary",
            root => new IncomingRequestsSummary(
                ReadInt32(root, "item_requests_pending"),
                ReadInt32(root, "order_requests_pending")),
            "incoming-requests-summary",
            out summary);
    }

    public async Task<bool> TryResolveItemRequestAsync(long requestId, CancellationToken cancellationToken = default)
    {
        return await TryPostAsync(
                $"/api/item-requests/{requestId}/resolve",
                null,
                "incoming-item-request-resolve",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> TryResolveOrderRequestAsync(
        long requestId,
        string status,
        string resolvedBy,
        string? note,
        long? appliedOrderId,
        CancellationToken cancellationToken = default)
    {
        return await TryPostAsync(
                $"/api/orders/requests/{requestId}/resolve",
                new
                {
                    status,
                    resolved_by = resolvedBy,
                    note,
                    applied_order_id = appliedOrderId
                },
                "incoming-order-request-resolve",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private bool TryRead<T>(
        string relativePath,
        Func<JsonElement, T> map,
        string operationName,
        out T value)
    {
        value = default!;

        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info($"Incoming requests API skipped for {operationName}: server base URL is not configured.");
                return false;
            }

            var payload = SendRequest(relativePath, configuration);
            if (payload == null)
            {
                return false;
            }

            value = map(payload.RootElement);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Incoming requests API failed for {operationName}", ex);
            return false;
        }
    }

    private async Task<bool> TryPostAsync(
        string relativePath,
        object? payload,
        string operationName,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info($"Incoming requests API skipped for {operationName}: server base URL is not configured.");
                return false;
            }

            using var handler = CreateHandler(configuration);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(configuration.BaseUrl!, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds)
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, relativePath);
            if (payload != null)
            {
                request.Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json");
            }

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            _logger.Warn($"Incoming requests API request failed: {relativePath} -> {(int)response.StatusCode} {response.ReasonPhrase}");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error($"Incoming requests API failed for {operationName}", ex);
            return false;
        }
    }

    private JsonDocument? SendRequest(string relativePath, WpfIncomingRequestsApiConfiguration configuration)
    {
        using var handler = CreateHandler(configuration);
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(configuration.BaseUrl!, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds)
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        using var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

        if (!response.IsSuccessStatusCode)
        {
            _logger.Warn($"Incoming requests API request failed: {relativePath} -> {(int)response.StatusCode} {response.ReasonPhrase}");
            return null;
        }

        var json = response.Content.ReadAsStringAsync()
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

        return JsonDocument.Parse(json);
    }

    private bool TryLoadConfiguration(out WpfIncomingRequestsApiConfiguration configuration)
    {
        var settings = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = ReadEnvOrSettings("FLOWSTOCK_SERVER_BASE_URL", settings.BaseUrl);
        var timeoutSeconds = ReadEnvInt("FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS") ?? settings.CloseTimeoutSeconds;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = WpfCloseDocumentService.DefaultCloseTimeoutSeconds;
        }

        configuration = new WpfIncomingRequestsApiConfiguration(
            NormalizeBaseUrl(baseUrl),
            timeoutSeconds,
            ReadEnvBool("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS") ?? settings.AllowInvalidTls);

        return !string.IsNullOrWhiteSpace(configuration.BaseUrl);
    }

    private static HttpMessageHandler CreateHandler(WpfIncomingRequestsApiConfiguration configuration)
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

    private static ItemRequest MapItemRequest(JsonElement element)
    {
        return new ItemRequest
        {
            Id = ReadInt64(element, "id"),
            Barcode = ReadString(element, "barcode") ?? string.Empty,
            Comment = ReadString(element, "comment") ?? string.Empty,
            DeviceId = ReadString(element, "device_id"),
            Login = ReadString(element, "login"),
            CreatedAt = ReadDateTime(element, "created_at"),
            Status = ReadString(element, "status") ?? "NEW",
            ResolvedAt = ReadNullableDateTime(element, "resolved_at")
        };
    }

    private static OrderRequest MapOrderRequest(JsonElement element)
    {
        return new OrderRequest
        {
            Id = ReadInt64(element, "id"),
            RequestType = ReadString(element, "request_type") ?? OrderRequestType.CreateOrder,
            PayloadJson = ReadPayloadJson(element, "payload_json"),
            Status = ReadString(element, "status") ?? OrderRequestStatus.Pending,
            CreatedAt = ReadDateTime(element, "created_at"),
            CreatedByLogin = ReadString(element, "created_by_login"),
            CreatedByDeviceId = ReadString(element, "created_by_device_id"),
            ResolvedAt = ReadNullableDateTime(element, "resolved_at"),
            ResolvedBy = ReadString(element, "resolved_by"),
            ResolutionNote = ReadString(element, "resolution_note"),
            AppliedOrderId = ReadNullableInt64(element, "applied_order_id")
        };
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;
    }

    private static string ReadPayloadJson(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return "{}";
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "{}"
            : value.GetRawText();
    }

    private static long ReadInt64(JsonElement element, string propertyName)
    {
        return TryReadInt64(element, propertyName, out var parsed)
            ? parsed
            : 0L;
    }

    private static long? ReadNullableInt64(JsonElement element, string propertyName)
    {
        return TryReadInt64(element, propertyName, out var parsed)
            ? parsed
            : null;
    }

    private static bool TryReadInt64(JsonElement element, string propertyName, out long value)
    {
        value = 0L;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value))
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.String
               && long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;
    }

    private static DateTime ReadDateTime(JsonElement element, string propertyName)
    {
        return ReadNullableDateTime(element, propertyName) ?? DateTime.MinValue;
    }

    private static DateTime? ReadNullableDateTime(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String
            && DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}

public sealed record IncomingRequestsSummary(int ItemRequestsPending, int OrderRequestsPending)
{
    public int TotalPending => ItemRequestsPending + OrderRequestsPending;
}

internal sealed record WpfIncomingRequestsApiConfiguration(string? BaseUrl, int TimeoutSeconds, bool AllowInvalidTls);
