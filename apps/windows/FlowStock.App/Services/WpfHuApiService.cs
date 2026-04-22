using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlowStock.Core.Models;

namespace FlowStock.App;

public sealed class WpfHuApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsService _settings;
    private readonly FileLogger _logger;

    public WpfHuApiService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool TryGetHus(string? search, int take, out IReadOnlyList<HuRecord> hus)
    {
        hus = Array.Empty<HuRecord>();
        var path = $"/api/hus?take={Math.Clamp(take, 1, 1000)}";
        if (!string.IsNullOrWhiteSpace(search))
        {
            path += "&q=" + Uri.EscapeDataString(search.Trim());
        }

        return TryRead(
            path,
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().Select(MapHuRecord).ToList()
                : new List<HuRecord>(),
            "hu-list",
            out hus);
    }

    public bool TryGetHuByCode(string code, out HuRecord? record)
    {
        record = null;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        return TryRead(
            $"/api/hus/{Uri.EscapeDataString(code.Trim())}",
            root =>
            {
                if (root.TryGetProperty("ok", out var okElement)
                    && okElement.ValueKind == JsonValueKind.False)
                {
                    return null;
                }

                return root.TryGetProperty("hu", out var huElement) && huElement.ValueKind == JsonValueKind.Object
                    ? MapHuRecord(huElement)
                    : null;
            },
            "hu-detail",
            out record);
    }

    public bool TryGetHuLedgerRows(string code, out IReadOnlyList<HuLedgerRow> rows)
    {
        rows = Array.Empty<HuLedgerRow>();
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        return TryRead(
            $"/api/hus/{Uri.EscapeDataString(code.Trim())}/ledger",
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().Select(MapHuLedgerRow).ToList()
                : new List<HuLedgerRow>(),
            "hu-ledger",
            out rows);
    }

    public async Task<(bool IsSuccess, IReadOnlyList<string> Codes, string? Error)> TryGenerateAsync(
        int count,
        string? createdBy,
        CancellationToken cancellationToken = default)
    {
        return await TryPostAsync(
                "/api/hus/generate",
                new
                {
                    count,
                    created_by = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim()
                },
                root => root.TryGetProperty("hus", out var codesElement) && codesElement.ValueKind == JsonValueKind.Array
                    ? codesElement.EnumerateArray()
                        .Select(element => element.GetString())
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Select(code => code!)
                        .ToList()
                    : new List<string>(),
                "hu-generate",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, HuRecord? Record, string? Error)> TryCreateHuAsync(
        string code,
        string? createdBy,
        CancellationToken cancellationToken = default)
    {
        return await TryPostAsync(
                "/api/hus",
                new
                {
                    hu_code = code,
                    created_by = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim()
                },
                root => root.TryGetProperty("hu", out var huElement) && huElement.ValueKind == JsonValueKind.Object
                    ? MapHuRecord(huElement)
                    : null,
                "hu-create",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, bool Value, string? Error)> TryCloseHuAsync(
        string code,
        string? note,
        string? closedBy,
        CancellationToken cancellationToken = default)
    {
        return await TryPostAsync(
                $"/api/hus/{Uri.EscapeDataString(code.Trim())}/close",
                new
                {
                    note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                    closed_by = string.IsNullOrWhiteSpace(closedBy) ? null : closedBy.Trim()
                },
                _ => true,
                "hu-close",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private bool TryRead<T>(string relativePath, Func<JsonElement, T> map, string operationName, out T value)
    {
        value = default!;

        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info($"HU API skipped for {operationName}: server base URL is not configured.");
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
                _logger.Warn($"HU API request failed: {relativePath} -> {(int)response.StatusCode} {response.ReasonPhrase}");
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
            _logger.Error($"HU API failed for {operationName}", ex);
            return false;
        }
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
                _logger.Info($"HU API skipped for {operationName}: server base URL is not configured.");
                return (false, default!, null);
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
            using var document = JsonDocument.Parse(json);
            return (true, map(document.RootElement), null);
        }
        catch (Exception ex)
        {
            _logger.Error($"HU API failed for {operationName}", ex);
            return (false, default!, null);
        }
    }

    private bool TryLoadConfiguration(out WpfHuApiConfiguration configuration)
    {
        var settings = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = ReadEnvOrSettings("FLOWSTOCK_SERVER_BASE_URL", settings.BaseUrl);
        var timeoutSeconds = ReadEnvInt("FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS") ?? settings.CloseTimeoutSeconds;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = WpfCloseDocumentService.DefaultCloseTimeoutSeconds;
        }

        configuration = new WpfHuApiConfiguration(
            NormalizeBaseUrl(baseUrl),
            timeoutSeconds,
            ReadEnvBool("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS") ?? settings.AllowInvalidTls);

        return !string.IsNullOrWhiteSpace(configuration.BaseUrl);
    }

    private static HttpMessageHandler CreateHandler(WpfHuApiConfiguration configuration)
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
            if (string.IsNullOrWhiteSpace(error?.Error))
            {
                return $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}.";
            }

            return error.Error switch
            {
                "MISSING_HU" => "HU не задан.",
                "INVALID_HU" => "Некорректный формат HU.",
                "INVALID_COUNT" => "Некорректное количество HU.",
                "UNKNOWN_HU" => "HU не найден.",
                _ => $"Server returned error: {error.Error}"
            };
        }
        catch
        {
            return $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}.";
        }
    }

    private static HuRecord MapHuRecord(JsonElement element)
    {
        return new HuRecord
        {
            Id = ReadInt64(element, "id"),
            Code = ReadString(element, "hu_code") ?? ReadString(element, "code") ?? string.Empty,
            Status = ReadString(element, "status") ?? "ACTIVE",
            CreatedAt = ReadDateTime(element, "created_at"),
            CreatedBy = ReadString(element, "created_by"),
            ClosedAt = ReadNullableDateTime(element, "closed_at"),
            Note = ReadString(element, "note")
        };
    }

    private static HuLedgerRow MapHuLedgerRow(JsonElement element)
    {
        return new HuLedgerRow
        {
            HuCode = ReadString(element, "hu_code") ?? string.Empty,
            ItemId = ReadInt64(element, "item_id"),
            ItemName = ReadString(element, "item_name") ?? string.Empty,
            LocationId = ReadInt64(element, "location_id"),
            LocationCode = ReadString(element, "location_code") ?? string.Empty,
            Qty = ReadDouble(element, "qty"),
            BaseUom = ReadString(element, "base_uom") ?? "шт"
        };
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
    }

    private static long ReadInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => 0
        };
    }

    private static double ReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var value) => value,
            JsonValueKind.String when double.TryParse(property.GetString(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value) => value,
            _ => 0
        };
    }

    private static DateTime ReadDateTime(JsonElement element, string propertyName)
    {
        return ReadNullableDateTime(element, propertyName) ?? DateTime.MinValue;
    }

    private static DateTime? ReadNullableDateTime(JsonElement element, string propertyName)
    {
        var raw = ReadString(element, propertyName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var roundtrip))
        {
            return roundtrip;
        }

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        return null;
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

    private sealed record ApiErrorResponse(string? Error);
    private sealed record WpfHuApiConfiguration(string? BaseUrl, int TimeoutSeconds, bool AllowInvalidTls);
}
