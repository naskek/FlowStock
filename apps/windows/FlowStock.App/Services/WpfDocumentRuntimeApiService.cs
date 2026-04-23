using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace FlowStock.App;

public sealed class WpfDocumentRuntimeApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsService _settings;
    private readonly FileLogger _logger;

    public WpfDocumentRuntimeApiService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<(bool IsSuccess, string? Error)> TryMarkDocForRecountAsync(long docId, CancellationToken cancellationToken = default)
    {
        var result = await TryPostAsync(
            $"/api/docs/{docId.ToString(CultureInfo.InvariantCulture)}/recount",
            new { },
            _ => true,
            "doc-recount",
            cancellationToken).ConfigureAwait(false);
        return (result.IsSuccess, result.Error);
    }

    public async Task<(bool IsSuccess, string? Error)> TrySaveHeaderAsync(
        long docId,
        long? partnerId,
        long? orderId,
        string? shippingRef,
        string? reasonCode,
        string? productionBatchNo,
        string? comment,
        CancellationToken cancellationToken = default)
    {
        var result = await TryPostAsync(
            $"/api/docs/{docId.ToString(CultureInfo.InvariantCulture)}/header",
            new
            {
                partner_id = partnerId,
                order_id = orderId,
                shipping_ref = Normalize(shippingRef),
                reason_code = Normalize(reasonCode),
                production_batch_no = Normalize(productionBatchNo),
                comment = Normalize(comment)
            },
            _ => true,
            "doc-save-header",
            cancellationToken).ConfigureAwait(false);
        return (result.IsSuccess, result.Error);
    }

    public async Task<(bool IsSuccess, string? Error)> TryAssignDocLineHuAsync(
        long docId,
        long lineId,
        double qty,
        string? fromHu,
        string? toHu,
        CancellationToken cancellationToken = default)
    {
        var result = await TryPostAsync(
            $"/api/docs/{docId.ToString(CultureInfo.InvariantCulture)}/lines/{lineId.ToString(CultureInfo.InvariantCulture)}/assign-hu",
            new
            {
                qty,
                from_hu = Normalize(fromHu),
                to_hu = Normalize(toHu)
            },
            _ => true,
            "doc-assign-line-hu",
            cancellationToken).ConfigureAwait(false);
        return (result.IsSuccess, result.Error);
    }

    public Task<(bool IsSuccess, int UsedHuCount, string? Error)> TryAutoDistributeProductionReceiptHusAsync(
        long docId,
        IReadOnlyCollection<long>? lineIds,
        CancellationToken cancellationToken = default)
    {
        return TryPostAsync(
            $"/api/docs/{docId.ToString(CultureInfo.InvariantCulture)}/production-receipt/auto-distribute-hus",
            new
            {
                line_ids = (lineIds ?? Array.Empty<long>()).Where(id => id > 0).Distinct().ToList()
            },
            root => root.TryGetProperty("used_hu_count", out var value) && value.TryGetInt32(out var parsed) ? parsed : 0,
            "doc-auto-distribute-hus",
            cancellationToken);
    }

    public async Task<(bool IsSuccess, string? Error)> TryDistributeProductionLineByHuCapacityAsync(
        long docId,
        long lineId,
        double maxQtyPerHu,
        IReadOnlyCollection<string> huCodes,
        CancellationToken cancellationToken = default)
    {
        var result = await TryPostAsync(
            $"/api/docs/{docId.ToString(CultureInfo.InvariantCulture)}/lines/{lineId.ToString(CultureInfo.InvariantCulture)}/distribute-hu-capacity",
            new
            {
                max_qty_per_hu = maxQtyPerHu,
                hu_codes = (huCodes ?? Array.Empty<string>())
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code.Trim())
                    .ToList()
            },
            _ => true,
            "doc-distribute-line-hu-capacity",
            cancellationToken).ConfigureAwait(false);
        return (result.IsSuccess, result.Error);
    }

    public async Task<(bool IsSuccess, string? Error)> TryDiscardDraftByDocUidAsync(
        string docUid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(docUid))
        {
            return (false, "DocUid is required.");
        }

        return await TryDeleteAsync(
            $"/api/docs/{Uri.EscapeDataString(docUid.Trim())}/draft",
            "doc-discard-draft",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TrySetProductionLinePackSingleHuAsync(
        long docId,
        long lineId,
        bool packSingleHu,
        CancellationToken cancellationToken = default)
    {
        var result = await TryPostAsync(
            $"/api/docs/{docId.ToString(CultureInfo.InvariantCulture)}/lines/{lineId.ToString(CultureInfo.InvariantCulture)}/pack-single-hu",
            new
            {
                pack_single_hu = packSingleHu
            },
            _ => true,
            "doc-pack-single-hu",
            cancellationToken).ConfigureAwait(false);
        return (result.IsSuccess, result.Error);
    }

    private async Task<(bool IsSuccess, T Value, string? Error)> TryPostAsync<T>(
        string relativePath,
        object payload,
        Func<JsonElement, T> map,
        string operationName,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info($"Document runtime API skipped for {operationName}: server base URL is not configured.");
                return (false, default!, "Server base URL is not configured.");
            }

            using var handler = CreateHandler(configuration);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(configuration.BaseUrl!, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds)
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, relativePath)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, default!, await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return (true, map(document.RootElement), null);
        }
        catch (Exception ex)
        {
            _logger.Error($"Document runtime API failed for {operationName}", ex);
            return (false, default!, null);
        }
    }

    private async Task<(bool IsSuccess, string? Error)> TryDeleteAsync(
        string relativePath,
        string operationName,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info($"Document runtime API skipped for {operationName}: server base URL is not configured.");
                return (false, "Server base URL is not configured.");
            }

            using var handler = CreateHandler(configuration);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(configuration.BaseUrl!, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds)
            };
            using var request = new HttpRequestMessage(HttpMethod.Delete, relativePath);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.Error($"Document runtime API failed for {operationName}", ex);
            return (false, null);
        }
    }

    private bool TryLoadConfiguration(out WpfDocumentRuntimeApiConfiguration configuration)
    {
        var settings = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = ReadEnvOrSettings("FLOWSTOCK_SERVER_BASE_URL", settings.BaseUrl);
        var timeoutSeconds = ReadEnvInt("FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS") ?? settings.CloseTimeoutSeconds;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = WpfCloseDocumentService.DefaultCloseTimeoutSeconds;
        }

        configuration = new WpfDocumentRuntimeApiConfiguration(
            NormalizeBaseUrl(baseUrl),
            timeoutSeconds,
            ReadEnvBool("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS") ?? settings.AllowInvalidTls);
        return !string.IsNullOrWhiteSpace(configuration.BaseUrl);
    }

    private static async Task<string> ReadApiErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(error?.Error))
            {
                return $"Server returned error: {error.Error}";
            }
        }
        catch
        {
        }

        return $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}.";
    }

    private static HttpMessageHandler CreateHandler(WpfDocumentRuntimeApiConfiguration configuration)
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

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed record WpfDocumentRuntimeApiConfiguration(string? BaseUrl, int TimeoutSeconds, bool AllowInvalidTls);
}
