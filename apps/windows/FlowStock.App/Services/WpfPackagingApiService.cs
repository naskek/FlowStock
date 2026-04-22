using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlowStock.Core.Models;

namespace FlowStock.App;

public sealed class WpfPackagingApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsService _settings;
    private readonly FileLogger _logger;

    public WpfPackagingApiService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool TryGetPackagings(long? itemId, bool includeInactive, out IReadOnlyList<ItemPackaging> packagings)
    {
        packagings = Array.Empty<ItemPackaging>();
        var query = new List<string> { $"include_inactive={(includeInactive ? "1" : "0")}" };
        if (itemId.HasValue && itemId.Value > 0)
        {
            query.Add($"item_id={itemId.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        return TryRead(
            "/api/packagings?" + string.Join("&", query),
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().Select(MapPackaging).ToList()
                : new List<ItemPackaging>(),
            "packagings",
            out packagings);
    }

    public async Task<(bool IsSuccess, long? CreatedId, string? Error)> TryCreatePackagingAsync(
        long itemId,
        string code,
        string name,
        double factorToBase,
        int sortOrder,
        CancellationToken cancellationToken = default)
    {
        return await TryPostForIdAsync(
                "/api/packagings",
                new
                {
                    item_id = itemId,
                    code,
                    name,
                    factor_to_base = factorToBase,
                    sort_order = sortOrder
                },
                "packaging_id",
                "packaging-create",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TryUpdatePackagingAsync(
        long packagingId,
        long itemId,
        string code,
        string name,
        double factorToBase,
        int sortOrder,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        return await TryPostAsync(
                $"/api/packagings/{packagingId}",
                new
                {
                    item_id = itemId,
                    code,
                    name,
                    factor_to_base = factorToBase,
                    sort_order = sortOrder,
                    is_active = isActive
                },
                "packaging-update",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TryDeletePackagingAsync(long packagingId, CancellationToken cancellationToken = default)
    {
        return await TryDeleteAsync($"/api/packagings/{packagingId}", "packaging-delete", cancellationToken).ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TrySetDefaultPackagingAsync(long itemId, long? packagingId, CancellationToken cancellationToken = default)
    {
        return await TryPostAsync(
                $"/api/items/{itemId}/default-packaging",
                new { packaging_id = packagingId },
                "packaging-set-default",
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
                _logger.Info($"Packaging API skipped for {operationName}: server base URL is not configured.");
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
                _logger.Warn($"Packaging API request failed: {relativePath} -> {(int)response.StatusCode} {response.ReasonPhrase}");
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
            _logger.Error($"Packaging API failed for {operationName}", ex);
            return false;
        }
    }

    private async Task<(bool IsSuccess, string? Error)> TryPostAsync(string relativePath, object payload, string operationName, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info($"Packaging API skipped for {operationName}: server base URL is not configured.");
                return (false, null);
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
            if (response.IsSuccessStatusCode)
            {
                return (true, null);
            }

            return (false, await ReadApiErrorAsync(response).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger.Error($"Packaging API failed for {operationName}", ex);
            return (false, null);
        }
    }

    private async Task<(bool IsSuccess, long? CreatedId, string? Error)> TryPostForIdAsync(
        string relativePath,
        object payload,
        string idField,
        string operationName,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info($"Packaging API skipped for {operationName}: server base URL is not configured.");
                return (false, null, null);
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
                return (false, null, await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return document.RootElement.TryGetProperty(idField, out var idElement) && idElement.TryGetInt64(out var idValue)
                ? (true, idValue, null)
                : (true, null, null);
        }
        catch (Exception ex)
        {
            _logger.Error($"Packaging API failed for {operationName}", ex);
            return (false, null, null);
        }
    }

    private async Task<(bool IsSuccess, string? Error)> TryDeleteAsync(string relativePath, string operationName, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info($"Packaging API skipped for {operationName}: server base URL is not configured.");
                return (false, null);
            }

            using var handler = CreateHandler(configuration);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(configuration.BaseUrl!, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds)
            };
            using var response = await client.DeleteAsync(relativePath, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? (true, null)
                : (false, await ReadApiErrorAsync(response).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger.Error($"Packaging API failed for {operationName}", ex);
            return (false, null);
        }
    }

    private bool TryLoadConfiguration(out WpfPackagingApiConfiguration configuration)
    {
        var settings = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = ReadEnvOrSettings("FLOWSTOCK_SERVER_BASE_URL", settings.BaseUrl);
        var timeoutSeconds = ReadEnvInt("FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS") ?? settings.CloseTimeoutSeconds;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = WpfCloseDocumentService.DefaultCloseTimeoutSeconds;
        }

        configuration = new WpfPackagingApiConfiguration(
            NormalizeBaseUrl(baseUrl),
            timeoutSeconds,
            ReadEnvBool("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS") ?? settings.AllowInvalidTls);
        return !string.IsNullOrWhiteSpace(configuration.BaseUrl);
    }

    private static HttpMessageHandler CreateHandler(WpfPackagingApiConfiguration configuration)
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

            return $"Server returned error: {error.Error}";
        }
        catch
        {
            return $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}.";
        }
    }

    private static ItemPackaging MapPackaging(JsonElement element)
    {
        return new ItemPackaging
        {
            Id = ReadInt64(element, "id"),
            ItemId = ReadInt64(element, "item_id"),
            Code = ReadString(element, "code") ?? string.Empty,
            Name = ReadString(element, "name") ?? string.Empty,
            FactorToBase = ReadDouble(element, "factor_to_base"),
            IsActive = ReadBool(element, "is_active"),
            SortOrder = ReadInt32(element, "sort_order")
        };
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
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

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
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

    private static bool ReadBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var value) => value,
            JsonValueKind.Number when property.TryGetInt32(out var numeric) => numeric != 0,
            _ => false
        };
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

        return int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private sealed record ApiErrorResponse(string? Error);
    private sealed record WpfPackagingApiConfiguration(string? BaseUrl, int TimeoutSeconds, bool AllowInvalidTls);
}
