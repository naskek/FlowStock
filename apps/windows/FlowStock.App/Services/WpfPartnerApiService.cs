using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlowStock.Core.Models;

namespace FlowStock.App;

public sealed class WpfPartnerApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsService _settings;
    private readonly FileLogger _logger;

    public WpfPartnerApiService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool TryGetPartners(out IReadOnlyList<PartnerWithStatus> partners)
    {
        partners = Array.Empty<PartnerWithStatus>();
        return TryRead(
            "/api/partners",
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().Select(MapPartner).ToList()
                : new List<PartnerWithStatus>(),
            "partners-read",
            out partners);
    }

    public async Task<(bool IsSuccess, long? PartnerId, string? Error)> TryCreatePartnerAsync(
        string name,
        string? code,
        PartnerStatus status,
        CancellationToken cancellationToken = default)
    {
        return await TryPostForIdAsync(
                "/api/partners",
                new
                {
                    name,
                    code,
                    status = MapStatus(status)
                },
                "partner_id",
                "partners-create",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TryUpdatePartnerAsync(
        long partnerId,
        string name,
        string? code,
        PartnerStatus status,
        CancellationToken cancellationToken = default)
    {
        return await TryPostAsync(
                $"/api/partners/{partnerId}",
                new
                {
                    name,
                    code,
                    status = MapStatus(status)
                },
                "partners-update",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TryDeletePartnerAsync(long partnerId, CancellationToken cancellationToken = default)
    {
        return await TryDeleteAsync($"/api/partners/{partnerId}", "partners-delete", cancellationToken).ConfigureAwait(false);
    }

    private bool TryRead<T>(string relativePath, Func<JsonElement, T> map, string operationName, out T value)
    {
        value = default!;

        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info($"Partner API skipped for {operationName}: server base URL is not configured.");
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
                _logger.Warn($"Partner API request failed: {relativePath} -> {(int)response.StatusCode} {response.ReasonPhrase}");
                return false;
            }

            var json = response.Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(json);
            value = map(document.RootElement);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Partner API failed for {operationName}", ex);
            return false;
        }
    }

    private async Task<(bool IsSuccess, string? Error)> TryPostAsync(string relativePath, object payload, string operationName, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info($"Partner API skipped for {operationName}: server base URL is not configured.");
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
            _logger.Error($"Partner API failed for {operationName}", ex);
            return (false, null);
        }
    }

    private async Task<(bool IsSuccess, long? PartnerId, string? Error)> TryPostForIdAsync(
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
                _logger.Info($"Partner API skipped for {operationName}: server base URL is not configured.");
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

            var document = await response.Content.ReadFromJsonAsync<JsonDocument>(JsonOptions, cancellationToken).ConfigureAwait(false);
            var partnerId = document != null && document.RootElement.TryGetProperty(idField, out var idElement) && idElement.TryGetInt64(out var id)
                ? id
                : (long?)null;
            return (true, partnerId, null);
        }
        catch (Exception ex)
        {
            _logger.Error($"Partner API failed for {operationName}", ex);
            return (false, null, null);
        }
    }

    private async Task<(bool IsSuccess, string? Error)> TryDeleteAsync(string relativePath, string operationName, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info($"Partner API skipped for {operationName}: server base URL is not configured.");
                return (false, null);
            }

            using var handler = CreateHandler(configuration);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(configuration.BaseUrl!, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds)
            };
            using var response = await client.DeleteAsync(relativePath, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return (true, null);
            }

            return (false, await ReadApiErrorAsync(response).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger.Error($"Partner API failed for {operationName}", ex);
            return (false, null);
        }
    }

    private bool TryLoadConfiguration(out WpfPartnerApiConfiguration configuration)
    {
        var settings = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = ReadEnvOrSettings("FLOWSTOCK_SERVER_BASE_URL", settings.BaseUrl);
        var timeoutSeconds = ReadEnvInt("FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS") ?? settings.CloseTimeoutSeconds;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = WpfCloseDocumentService.DefaultCloseTimeoutSeconds;
        }

        configuration = new WpfPartnerApiConfiguration(
            NormalizeBaseUrl(baseUrl),
            timeoutSeconds,
            ReadEnvBool("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS") ?? settings.AllowInvalidTls);
        return !string.IsNullOrWhiteSpace(configuration.BaseUrl);
    }

    private static HttpMessageHandler CreateHandler(WpfPartnerApiConfiguration configuration)
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

    private static PartnerWithStatus MapPartner(JsonElement element)
    {
        return new PartnerWithStatus(
            new Partner
            {
                Id = ReadInt64(element, "id"),
                Name = ReadString(element, "name") ?? string.Empty,
                Code = ReadString(element, "code"),
                CreatedAt = DateTime.MinValue
            },
            ParseStatus(ReadString(element, "status")));
    }

    private static PartnerStatus ParseStatus(string? status)
    {
        return status?.Trim().ToUpperInvariant() switch
        {
            "SUPPLIER" => PartnerStatus.Supplier,
            "CLIENT" => PartnerStatus.Client,
            _ => PartnerStatus.Both
        };
    }

    private static string MapStatus(PartnerStatus status)
    {
        return status switch
        {
            PartnerStatus.Supplier => "SUPPLIER",
            PartnerStatus.Client => "CLIENT",
            _ => "BOTH"
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
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;
    }

    private static long ReadInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var parsed)
            ? parsed
            : 0L;
    }
}

public sealed record PartnerWithStatus(Partner Partner, PartnerStatus Status);

internal sealed record WpfPartnerApiConfiguration(string? BaseUrl, int TimeoutSeconds, bool AllowInvalidTls);
