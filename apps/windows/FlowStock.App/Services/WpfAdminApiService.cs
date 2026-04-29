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

    public async Task<WpfMaintenanceBackfillReportResult> RunReservationBackfillDryRunAsync(CancellationToken cancellationToken = default)
    {
        return await TryPostForReportAsync(
                "/api/admin/maintenance/backfill-reservations/dry-run",
                new { },
                "admin-backfill-reservations-dry-run",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<WpfMaintenanceBackfillReportResult> RunReservationBackfillApplyAsync(string? confirm, CancellationToken cancellationToken = default)
    {
        return await TryPostForReportAsync(
                "/api/admin/maintenance/backfill-reservations/apply",
                new { confirm },
                "admin-backfill-reservations-apply",
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

    private async Task<WpfMaintenanceBackfillReportResult> TryPostForReportAsync(
        string relativePath,
        object payload,
        string operationName,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info($"Admin API skipped for {operationName}: server base URL is not configured.");
                return WpfMaintenanceBackfillReportResult.Failure("Server API не настроен.");
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
                return WpfMaintenanceBackfillReportResult.Failure(await TryReadApiErrorAsync(response).ConfigureAwait(false));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return WpfMaintenanceBackfillReportResult.Success(MapBackfillReport(document.RootElement));
        }
        catch (Exception ex)
        {
            _logger.Error($"Admin API failed for {operationName}", ex);
            return WpfMaintenanceBackfillReportResult.Failure(ex.Message);
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
                "CONFIRM_REQUIRED" => "Для apply нужно ввести подтверждение APPLY.",
                "BACKFILL_ALREADY_RUNNING" => "Backfill резервов уже выполняется на сервере.",
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

    private static WpfMaintenanceBackfillReport MapBackfillReport(JsonElement element)
    {
        return new WpfMaintenanceBackfillReport
        {
            Mode = ReadString(element, "mode") ?? string.Empty,
            CustomerOrders = ReadInt32(element, "customer_orders"),
            ActiveCustomerOrders = ReadInt32(element, "active_customer_orders"),
            InactiveSkippedCustomerOrders = ReadInt32(element, "inactive_skipped_customer_orders"),
            PlanLinesBefore = ReadInt32(element, "plan_lines_before"),
            PlanLinesAfter = ReadInt32(element, "plan_lines_after"),
            QtyBefore = ReadDouble(element, "qty_before"),
            QtyAfter = ReadDouble(element, "qty_after"),
            OrdersWithChanges = ReadInt32(element, "orders_with_changes"),
            ConflictingHu = ReadInt32(element, "conflicting_hu"),
            LedgerRowsBefore = ReadInt64(element, "ledger_rows_before"),
            LedgerRowsAfter = ReadInt64(element, "ledger_rows_after"),
            Messages = ReadArray(element, "messages")
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.ToString())
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .ToList(),
            Conflicts = ReadArray(element, "conflicts").Select(MapBackfillConflict).ToList(),
            Details = ReadArray(element, "details").Select(MapBackfillDetail).ToList()
        };
    }

    private static WpfMaintenanceBackfillConflict MapBackfillConflict(JsonElement element)
    {
        return new WpfMaintenanceBackfillConflict
        {
            HuCode = ReadString(element, "hu_code") ?? string.Empty,
            ItemId = ReadInt64(element, "item_id"),
            Claims = ReadArray(element, "claims").Select(claim => new WpfMaintenanceBackfillConflictClaim
            {
                OrderId = ReadInt64(claim, "order_id"),
                OrderRef = ReadString(claim, "order_ref") ?? string.Empty,
                QtyPlanned = ReadDouble(claim, "qty_planned")
            }).ToList()
        };
    }

    private static WpfMaintenanceBackfillOrderDetail MapBackfillDetail(JsonElement element)
    {
        return new WpfMaintenanceBackfillOrderDetail
        {
            OrderId = ReadInt64(element, "order_id"),
            OrderRef = ReadString(element, "order_ref") ?? string.Empty,
            EffectiveStatus = ReadString(element, "effective_status") ?? string.Empty,
            Active = ReadBool(element, "active"),
            PlanLinesBefore = ReadInt32(element, "plan_lines_before"),
            PlanLinesAfter = ReadInt32(element, "plan_lines_after"),
            QtyBefore = ReadDouble(element, "qty_before"),
            QtyAfter = ReadDouble(element, "qty_after"),
            WillChange = ReadBool(element, "will_change"),
            SkipReason = ReadString(element, "skip_reason"),
            Lines = ReadArray(element, "lines").Select(line => new WpfMaintenanceBackfillLineDetail
            {
                OrderLineId = ReadInt64(line, "order_line_id"),
                ItemId = ReadInt64(line, "item_id"),
                RequestedQty = ReadDouble(line, "requested_qty"),
                PlannedQty = ReadDouble(line, "planned_qty"),
                SkipReason = ReadString(line, "skip_reason")
            }).ToList()
        };
    }

    private static IReadOnlyList<JsonElement> ReadArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<JsonElement>();
        }

        return value.EnumerateArray().ToList();
    }

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;
    }

    private static double ReadDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetDouble(out var parsed)
            ? parsed
            : 0d;
    }
}

internal sealed record WpfAdminApiConfiguration(string? BaseUrl, int TimeoutSeconds, bool AllowInvalidTls);

public sealed class WpfMaintenanceBackfillReportResult
{
    public bool IsSuccess { get; init; }
    public WpfMaintenanceBackfillReport? Report { get; init; }
    public string? Error { get; init; }

    public static WpfMaintenanceBackfillReportResult Success(WpfMaintenanceBackfillReport report)
    {
        return new WpfMaintenanceBackfillReportResult { IsSuccess = true, Report = report };
    }

    public static WpfMaintenanceBackfillReportResult Failure(string? error)
    {
        return new WpfMaintenanceBackfillReportResult { IsSuccess = false, Error = error };
    }
}

public sealed class WpfMaintenanceBackfillReport
{
    public string Mode { get; init; } = string.Empty;
    public int CustomerOrders { get; init; }
    public int ActiveCustomerOrders { get; init; }
    public int InactiveSkippedCustomerOrders { get; init; }
    public int PlanLinesBefore { get; init; }
    public int PlanLinesAfter { get; init; }
    public double QtyBefore { get; init; }
    public double QtyAfter { get; init; }
    public int OrdersWithChanges { get; init; }
    public int ConflictingHu { get; init; }
    public long LedgerRowsBefore { get; init; }
    public long LedgerRowsAfter { get; init; }
    public IReadOnlyList<string> Messages { get; init; } = Array.Empty<string>();
    public IReadOnlyList<WpfMaintenanceBackfillConflict> Conflicts { get; init; } = Array.Empty<WpfMaintenanceBackfillConflict>();
    public IReadOnlyList<WpfMaintenanceBackfillOrderDetail> Details { get; init; } = Array.Empty<WpfMaintenanceBackfillOrderDetail>();
}

public sealed class WpfMaintenanceBackfillConflict
{
    public string HuCode { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public IReadOnlyList<WpfMaintenanceBackfillConflictClaim> Claims { get; init; } = Array.Empty<WpfMaintenanceBackfillConflictClaim>();
}

public sealed class WpfMaintenanceBackfillConflictClaim
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public double QtyPlanned { get; init; }
}

public sealed class WpfMaintenanceBackfillOrderDetail
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string EffectiveStatus { get; init; } = string.Empty;
    public bool Active { get; init; }
    public int PlanLinesBefore { get; init; }
    public int PlanLinesAfter { get; init; }
    public double QtyBefore { get; init; }
    public double QtyAfter { get; init; }
    public bool WillChange { get; init; }
    public string? SkipReason { get; init; }
    public IReadOnlyList<WpfMaintenanceBackfillLineDetail> Lines { get; init; } = Array.Empty<WpfMaintenanceBackfillLineDetail>();
}

public sealed class WpfMaintenanceBackfillLineDetail
{
    public long OrderLineId { get; init; }
    public long ItemId { get; init; }
    public double RequestedQty { get; init; }
    public double PlannedQty { get; init; }
    public string? SkipReason { get; init; }
}
