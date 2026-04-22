using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlowStock.Core.Models;

namespace FlowStock.App;

public sealed class WpfAdminApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsService _settings;
    private readonly FileLogger _logger;

    public WpfAdminApiService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool TryGetClientBlocks(out IReadOnlyList<ClientBlockSetting> settings)
    {
        settings = Array.Empty<ClientBlockSetting>();
        return TryRead(
            "/api/client-blocks",
            root =>
            {
                if (!root.TryGetProperty("blocks", out var blocksElement)
                    || blocksElement.ValueKind != JsonValueKind.Object)
                {
                    return Array.Empty<ClientBlockSetting>();
                }

                return blocksElement.EnumerateObject()
                    .Where(entry => ClientBlockCatalog.IsKnownKey(entry.Name))
                    .Select(entry => new ClientBlockSetting(entry.Name, entry.Value.ValueKind == JsonValueKind.True))
                    .ToList();
            },
            "admin-client-blocks",
            out settings);
    }

    public async Task<bool> TrySaveClientBlocksAsync(IReadOnlyList<ClientBlockSetting> settings, CancellationToken cancellationToken = default)
    {
        return await TryPostAsync(
                "/api/client-blocks",
                new
                {
                    blocks = settings.Select(setting => new
                    {
                        key = setting.Key,
                        is_enabled = setting.IsEnabled
                    }).ToList()
                },
                "admin-save-client-blocks",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public bool TryGetTsdDevices(out IReadOnlyList<TsdDeviceInfo> devices)
    {
        devices = Array.Empty<TsdDeviceInfo>();
        return TryRead(
            "/api/admin/tsd-devices",
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().Select(MapTsdDevice).ToList()
                : new List<TsdDeviceInfo>(),
            "admin-tsd-devices",
            out devices);
    }

    public async Task<bool> TryAddTsdDeviceAsync(string login, string password, bool isActive, string platform, CancellationToken cancellationToken = default)
    {
        return await TryPostAsync(
                "/api/admin/tsd-devices",
                new
                {
                    login,
                    password,
                    is_active = isActive,
                    platform
                },
                "admin-add-tsd-device",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> TryUpdateTsdDeviceAsync(long id, string login, string? password, bool isActive, string platform, CancellationToken cancellationToken = default)
    {
        return await TryPostAsync(
                $"/api/admin/tsd-devices/{id}",
                new
                {
                    login,
                    password,
                    is_active = isActive,
                    platform
                },
                "admin-update-tsd-device",
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
                _logger.Info($"Admin API skipped for {operationName}: server base URL is not configured.");
                return false;
            }

            var payload = SendGet(relativePath, configuration);
            if (payload == null)
            {
                return false;
            }

            value = map(payload.RootElement);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Admin API failed for {operationName}", ex);
            return false;
        }
    }

    private async Task<bool> TryPostAsync(string relativePath, object payload, string operationName, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info($"Admin API skipped for {operationName}: server base URL is not configured.");
                return false;
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
                return true;
            }

            var errorMessage = await TryReadApiErrorAsync(response).ConfigureAwait(false);
            throw new InvalidOperationException(errorMessage);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Admin API failed for {operationName}", ex);
            return false;
        }
    }

    private JsonDocument? SendGet(string relativePath, WpfAdminApiConfiguration configuration)
    {
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
            _logger.Warn($"Admin API request failed: {relativePath} -> {(int)response.StatusCode} {response.ReasonPhrase}");
            return null;
        }

        var json = response.Content.ReadAsStringAsync()
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
        return JsonDocument.Parse(json);
    }

    private bool TryLoadConfiguration(out WpfAdminApiConfiguration configuration)
    {
        var settings = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = ReadEnvOrSettings("FLOWSTOCK_SERVER_BASE_URL", settings.BaseUrl);
        var timeoutSeconds = ReadEnvInt("FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS") ?? settings.CloseTimeoutSeconds;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = WpfCloseDocumentService.DefaultCloseTimeoutSeconds;
        }

        configuration = new WpfAdminApiConfiguration(
            NormalizeBaseUrl(baseUrl),
            timeoutSeconds,
            ReadEnvBool("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS") ?? settings.AllowInvalidTls);

        return !string.IsNullOrWhiteSpace(configuration.BaseUrl);
    }

    private static HttpMessageHandler CreateHandler(WpfAdminApiConfiguration configuration)
    {
        var handler = new HttpClientHandler();
        if (configuration.AllowInvalidTls)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
    }

    private static async Task<string> TryReadApiErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions).ConfigureAwait(false);
            var errorCode = error?.Error;
            if (string.IsNullOrWhiteSpace(errorCode))
            {
                return $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}.";
            }

            return errorCode switch
            {
                "INVALID_BLOCK_KEY" => "Обнаружен неизвестный ключ веб-блока.",
                "MISSING_LOGIN" => "Логин не задан.",
                "MISSING_PASSWORD" => "Пароль не задан.",
                "DEVICE_NOT_FOUND" => "Аккаунт не найден на сервере.",
                "LOGIN_ALREADY_EXISTS" => "Логин уже используется другим аккаунтом ПК/ТСД.",
                _ => $"Server returned error: {errorCode}"
            };
        }
        catch
        {
            return $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}.";
        }
    }

    private static TsdDeviceInfo MapTsdDevice(JsonElement element)
    {
        return new TsdDeviceInfo
        {
            Id = ReadInt64(element, "id"),
            DeviceId = ReadString(element, "device_id") ?? string.Empty,
            Login = ReadString(element, "login") ?? string.Empty,
            Platform = NormalizePlatform(ReadString(element, "platform")),
            IsActive = ReadBool(element, "is_active"),
            CreatedAt = ReadString(element, "created_at"),
            LastSeen = ReadString(element, "last_seen")
        };
    }

    private static string NormalizePlatform(string? platform)
    {
        var normalized = string.IsNullOrWhiteSpace(platform) ? string.Empty : platform.Trim().ToUpperInvariant();
        return normalized switch
        {
            "PC" => "PC",
            "BOTH" => "BOTH",
            _ => "TSD"
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

    private static bool ReadBool(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;
    }
}

internal sealed record WpfAdminApiConfiguration(string? BaseUrl, int TimeoutSeconds, bool AllowInvalidTls);
