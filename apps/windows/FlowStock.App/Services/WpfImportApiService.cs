using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlowStock.Core.Models;

namespace FlowStock.App;

public sealed class WpfImportApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsService _settings;
    private readonly FileLogger _logger;

    public WpfImportApiService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool TryGetImportErrors(string? reason, out IReadOnlyList<ImportErrorView> errors)
    {
        errors = Array.Empty<ImportErrorView>();
        var path = "/api/import-errors";
        if (!string.IsNullOrWhiteSpace(reason))
        {
            path += "?reason=" + Uri.EscapeDataString(reason.Trim());
        }

        return TryRead(
            path,
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().Select(MapImportErrorView).ToList()
                : new List<ImportErrorView>(),
            "import-errors",
            out errors);
    }

    public async Task<(bool IsSuccess, string? Error)> TryReapplyErrorAsync(long errorId, CancellationToken cancellationToken = default)
    {
        return await TryPostAsync(
                $"/api/import-errors/{errorId}/reapply",
                new { },
                "import-reapply",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, ImportResult? Result, string? Error)> TryImportJsonlAsync(string content, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info("Import API skipped for import-jsonl: server base URL is not configured.");
                return (false, null, null);
            }

            using var handler = CreateHandler(configuration);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(configuration.BaseUrl!, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds)
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/imports/jsonl")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { content }),
                    Encoding.UTF8,
                    "application/json")
            };
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadFromJsonAsync<ApiResultEnvelope>(JsonOptions, cancellationToken).ConfigureAwait(false);
                return (false, null, TranslateApiError(error?.Error) ?? $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var document = await response.Content.ReadFromJsonAsync<JsonDocument>(JsonOptions, cancellationToken).ConfigureAwait(false);
            if (document == null)
            {
                return (false, null, "Server returned an empty import response.");
            }

            return (true, MapImportResult(document.RootElement), null);
        }
        catch (Exception ex)
        {
            _logger.Error("Import API failed for import-jsonl", ex);
            return (false, null, null);
        }
    }

    private bool TryRead<T>(string relativePath, Func<JsonElement, T> map, string operationName, out T value)
    {
        value = default!;

        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info($"Import API skipped for {operationName}: server base URL is not configured.");
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
                _logger.Warn($"Import API request failed: {relativePath} -> {(int)response.StatusCode} {response.ReasonPhrase}");
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
            _logger.Error($"Import API failed for {operationName}", ex);
            return false;
        }
    }

    private async Task<(bool IsSuccess, string? Error)> TryPostAsync(
        string relativePath,
        object payload,
        string operationName,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info($"Import API skipped for {operationName}: server base URL is not configured.");
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
                var apiResult = await response.Content.ReadFromJsonAsync<ApiResultEnvelope>(JsonOptions, cancellationToken).ConfigureAwait(false);
                if (apiResult?.Ok == false)
                {
                    return (false, TranslateApiError(apiResult.Error));
                }

                return (true, null);
            }

            var error = await response.Content.ReadFromJsonAsync<ApiResultEnvelope>(JsonOptions, cancellationToken).ConfigureAwait(false);
            return (false, TranslateApiError(error?.Error) ?? $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (Exception ex)
        {
            _logger.Error($"Import API failed for {operationName}", ex);
            return (false, null);
        }
    }

    private bool TryLoadConfiguration(out WpfImportApiConfiguration configuration)
    {
        var settings = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = ReadEnvOrSettings("FLOWSTOCK_SERVER_BASE_URL", settings.BaseUrl);
        var timeoutSeconds = ReadEnvInt("FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS") ?? settings.CloseTimeoutSeconds;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = WpfCloseDocumentService.DefaultCloseTimeoutSeconds;
        }

        configuration = new WpfImportApiConfiguration(
            NormalizeBaseUrl(baseUrl),
            timeoutSeconds,
            ReadEnvBool("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS") ?? settings.AllowInvalidTls);

        return !string.IsNullOrWhiteSpace(configuration.BaseUrl);
    }

    private static HttpMessageHandler CreateHandler(WpfImportApiConfiguration configuration)
    {
        var handler = new HttpClientHandler();
        if (configuration.AllowInvalidTls)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
    }

    private static ImportErrorView MapImportErrorView(JsonElement element)
    {
        return new ImportErrorView
        {
            Id = ReadInt64(element, "id"),
            EventId = ReadString(element, "event_id"),
            Reason = ReadString(element, "reason") ?? string.Empty,
            RawJson = ReadString(element, "raw_json") ?? string.Empty,
            CreatedAt = ReadDateTime(element, "created_at"),
            Barcode = ReadString(element, "barcode")
        };
    }

    private static ImportResult MapImportResult(JsonElement element)
    {
        var result = new ImportResult
        {
            Imported = ReadInt32(element, "imported"),
            Duplicates = ReadInt32(element, "duplicates"),
            Errors = ReadInt32(element, "errors"),
            DocumentsCreated = ReadInt32(element, "documents_created"),
            OperationsImported = ReadInt32(element, "operations_imported"),
            LinesImported = ReadInt32(element, "lines_imported"),
            ItemsUpserted = ReadInt32(element, "items_upserted")
        };

        if (element.TryGetProperty("device_ids", out var deviceIdsElement)
            && deviceIdsElement.ValueKind == JsonValueKind.Array)
        {
            result.DeviceIds = deviceIdsElement.EnumerateArray()
                .Select(entry => entry.ValueKind == JsonValueKind.String ? entry.GetString() : entry.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .ToList();
        }

        return result;
    }

    private static string? TranslateApiError(string? error)
    {
        return error switch
        {
            "REAPPLY_FAILED" => "Не удалось переприменить. Проверьте, что штрихкод привязан к товару.",
            "INVALID_IMPORT_ERROR_ID" => "Некорректный идентификатор ошибки импорта.",
            "EMPTY_CONTENT" => "Файл импорта пустой.",
            null or "" => null,
            _ => $"Server returned error: {error}"
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

    private static DateTime ReadDateTime(JsonElement element, string propertyName)
    {
        var raw = ReadString(element, propertyName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DateTime.MinValue;
        }

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var roundtrip))
        {
            return roundtrip;
        }

        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : DateTime.MinValue;
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

    private sealed record ApiResultEnvelope(bool Ok, string? Error);
    private sealed record WpfImportApiConfiguration(string? BaseUrl, int TimeoutSeconds, bool AllowInvalidTls);
}
