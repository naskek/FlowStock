using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowStock.App;

public sealed class WpfOrderPartialOutboundPermissionService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly SettingsService _settings;
    private readonly FileLogger _logger;

    public WpfOrderPartialOutboundPermissionService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<WpfOrderPartialOutboundPermissionResult> SetAsync(
        long orderId,
        bool requestedValue,
        CancellationToken cancellationToken = default)
    {
        var settings = _settings.Load();
        var server = settings.Server ?? new ServerSettings();
        var baseUrl = Environment.GetEnvironmentVariable("FLOWSTOCK_SERVER_BASE_URL") ?? server.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = WpfCloseDocumentService.DefaultServerBaseUrl;
        }
        if (!baseUrl.Contains("://", StringComparison.Ordinal))
        {
            baseUrl = "https://" + baseUrl;
        }

        var handler = new HttpClientHandler();
        if ((ReadEnvBool("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS") ?? server.AllowInvalidTls))
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(Math.Max(1, server.CloseTimeoutSeconds))
        };

        try
        {
            using var response = await client.PutAsJsonAsync(
                $"api/orders/{orderId}/partial-outbound-permission",
                new PermissionRequest
                {
                    AllowPartialOutbound = requestedValue,
                    DeviceId = server.DeviceId
                },
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var payload = string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<PermissionResponse>(json, JsonOptions);
            if (!response.IsSuccessStatusCode || payload?.Ok != true)
            {
                return WpfOrderPartialOutboundPermissionResult.Failure(
                    payload?.Message ?? "Сервер отклонил изменение разрешения частичной отгрузки.",
                    payload?.Error,
                    response: payload);
            }

            return WpfOrderPartialOutboundPermissionResult.Success(payload!);
        }
        catch (TaskCanceledException ex)
        {
            _logger.Warn($"WPF partial outbound permission timed out for order_id={orderId}");
            return WpfOrderPartialOutboundPermissionResult.Failure(
                "Сервер не ответил. Фактическое значение разрешения не определено; повторите после обновления состояния.",
                "TIMEOUT",
                ex);
        }
        catch (Exception ex)
        {
            _logger.Error($"WPF partial outbound permission failed for order_id={orderId}", ex);
            return WpfOrderPartialOutboundPermissionResult.Failure(
                "Не удалось изменить разрешение частичной отгрузки.",
                "SERVER_UNAVAILABLE",
                ex);
        }
    }

    private static bool? ReadEnvBool(string key) => Environment.GetEnvironmentVariable(key)?.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "yes" or "on" => true,
        "0" or "false" or "no" or "off" => false,
        _ => null
    };

    private sealed class PermissionRequest
    {
        [JsonPropertyName("allow_partial_outbound")]
        public bool AllowPartialOutbound { get; init; }

        [JsonPropertyName("device_id")]
        public string? DeviceId { get; init; }
    }
}

public sealed class PermissionResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("order_id")]
    public long? OrderId { get; init; }

    [JsonPropertyName("order_ref")]
    public string? OrderRef { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("allow_partial_outbound")]
    public bool? AllowPartialOutbound { get; init; }

    [JsonPropertyName("changed")]
    public bool Changed { get; init; }

    public bool TryGetCanonicalState(out FlowStock.Core.Models.OrderStatus status, out bool allowPartialOutbound)
    {
        status = FlowStock.Core.Models.OrderStatus.Draft;
        allowPartialOutbound = false;
        if (!OrderId.HasValue
            || OrderId.Value <= 0
            || string.IsNullOrWhiteSpace(Status)
            || !AllowPartialOutbound.HasValue)
        {
            return false;
        }

        var parsedStatus = FlowStock.Core.Models.OrderStatusMapper.StatusFromString(Status);
        if (!parsedStatus.HasValue)
        {
            return false;
        }

        status = parsedStatus.Value;
        allowPartialOutbound = AllowPartialOutbound.Value;
        return true;
    }
}

public sealed class WpfOrderPartialOutboundPermissionResult
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? ErrorCode { get; init; }
    public PermissionResponse? Response { get; init; }
    public Exception? Exception { get; init; }

    public static WpfOrderPartialOutboundPermissionResult Success(PermissionResponse response) => new()
    {
        IsSuccess = true,
        Response = response
    };

    public static WpfOrderPartialOutboundPermissionResult Failure(
        string message,
        string? errorCode,
        Exception? exception = null,
        PermissionResponse? response = null) => new()
    {
        Message = message,
        ErrorCode = errorCode,
        Exception = exception,
        Response = response
    };
}
