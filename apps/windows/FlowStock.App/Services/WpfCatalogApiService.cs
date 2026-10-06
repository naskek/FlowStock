using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlowStock.Core.Models;

namespace FlowStock.App;

public sealed class WpfCatalogApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsService _settings;
    private readonly FileLogger _logger;
    private readonly Func<HttpMessageHandler>? _readHandlerFactory;

    public WpfCatalogApiService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    internal WpfCatalogApiService(SettingsService settings, FileLogger logger, Func<HttpMessageHandler> readHandlerFactory)
        : this(settings, logger)
    {
        _readHandlerFactory = readHandlerFactory;
    }

    public bool TryGetUoms(out IReadOnlyList<Uom> uoms)
    {
        var result = TryGetUomsAsync().GetAwaiter().GetResult();
        uoms = result.Value;
        return result.IsSuccess;
    }

    public Task<(bool IsSuccess, IReadOnlyList<Uom> Value)> TryGetUomsAsync(CancellationToken cancellationToken = default)
    {
        return TryReadAsync<IReadOnlyList<Uom>>(
            "/api/uoms",
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                    .Select(element => new Uom
                    {
                        Id = ReadInt64(element, "id"),
                        Name = ReadString(element, "name") ?? string.Empty
                    })
                    .ToList()
                : new List<Uom>(),
            "catalog-uoms",
            Array.Empty<Uom>(), cancellationToken);
    }

    public bool TryGetWriteOffReasons(out IReadOnlyList<WriteOffReason> reasons)
    {
        var result = TryGetWriteOffReasonsAsync().GetAwaiter().GetResult();
        reasons = result.Value;
        return result.IsSuccess;
    }

    public Task<(bool IsSuccess, IReadOnlyList<WriteOffReason> Value)> TryGetWriteOffReasonsAsync(CancellationToken cancellationToken = default)
    {
        return TryReadAsync<IReadOnlyList<WriteOffReason>>(
            "/api/write-off-reasons",
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                    .Select(element => new WriteOffReason
                    {
                        Id = ReadInt64(element, "id"),
                        Code = ReadString(element, "code") ?? string.Empty,
                        Name = ReadString(element, "name") ?? string.Empty
                    })
                    .ToList()
                : new List<WriteOffReason>(),
            "catalog-write-off-reasons",
            Array.Empty<WriteOffReason>(), cancellationToken);
    }

    public bool TryGetTaras(out IReadOnlyList<Tara> taras)
    {
        var result = TryGetTarasAsync().GetAwaiter().GetResult();
        taras = result.Value;
        return result.IsSuccess;
    }

    public Task<(bool IsSuccess, IReadOnlyList<Tara> Value)> TryGetTarasAsync(CancellationToken cancellationToken = default)
    {
        return TryReadAsync<IReadOnlyList<Tara>>(
            "/api/taras",
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                    .Select(element => new Tara
                    {
                        Id = ReadInt64(element, "id"),
                        Name = ReadString(element, "name") ?? string.Empty
                    })
                    .ToList()
                : new List<Tara>(),
            "catalog-taras",
            Array.Empty<Tara>(), cancellationToken);
    }

    public bool TryGetItemTypes(bool includeInactive, out IReadOnlyList<ItemType> itemTypes)
    {
        var result = TryGetItemTypesAsync(includeInactive).GetAwaiter().GetResult();
        itemTypes = result.Value;
        return result.IsSuccess;
    }

    public Task<(bool IsSuccess, IReadOnlyList<ItemType> Value)> TryGetItemTypesAsync(bool includeInactive, CancellationToken cancellationToken = default)
    {
        var path = includeInactive ? "/api/item-types?include_inactive=1" : "/api/item-types";
        return TryReadAsync<IReadOnlyList<ItemType>>(
            path,
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                    .Select(element => new ItemType
                    {
                        Id = ReadInt64(element, "id"),
                        Name = ReadString(element, "name") ?? string.Empty,
                        Code = ReadString(element, "code"),
                        SortOrder = ReadInt32(element, "sort_order"),
                        IsActive = ReadBool(element, "is_active"),
                        IsVisibleInProductCatalog = ReadBool(element, "is_visible_in_product_catalog"),
                        EnableMinStockControl = ReadBool(element, "enable_min_stock_control"),
                        MinStockUsesOrderBinding = ReadBool(element, "min_stock_uses_order_binding"),
                        EnableOrderReservation = ReadBool(element, "enable_order_reservation"),
                        EnableHuDistribution = ReadBool(element, "enable_hu_distribution"),
                        EnableMarking = ReadBool(element, "enable_marking")
                    })
                    .ToList()
                : new List<ItemType>(),
            "catalog-item-types",
            Array.Empty<ItemType>(), cancellationToken);
    }

    public bool TryGetVatRates(bool includeInactive, out IReadOnlyList<VatRate> vatRates)
    {
        var result = TryGetVatRatesAsync(includeInactive).GetAwaiter().GetResult();
        vatRates = result.Value;
        return result.IsSuccess;
    }

    public Task<(bool IsSuccess, IReadOnlyList<VatRate> Value)> TryGetVatRatesAsync(bool includeInactive, CancellationToken cancellationToken = default)
    {
        var path = includeInactive ? "/api/vat-rates?include_inactive=true" : "/api/vat-rates";
        return TryReadAsync<IReadOnlyList<VatRate>>(
            path,
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                    .Select(element => new VatRate
                    {
                        Id = ReadInt64(element, "id"),
                        Name = ReadString(element, "name") ?? string.Empty,
                        Rate = ReadDecimal(element, "rate"),
                        IsActive = ReadBool(element, "is_active"),
                        SortOrder = ReadInt32(element, "sort_order")
                    })
                    .ToList()
                : new List<VatRate>(),
            "catalog-vat-rates",
            Array.Empty<VatRate>(), cancellationToken);
    }

    public async Task<(bool IsSuccess, long? CreatedId, string? Error)> TryCreateItemAsync(Item item, CancellationToken cancellationToken = default)
    {
        return await TryPostForIdAsync(
                "/api/items",
                new
                {
                    name = item.Name,
                    is_active = item.IsActive,
                    barcode = item.Barcode,
                    gtin = item.Gtin,
                    base_uom = item.BaseUom,
                    brand = item.Brand,
                    volume = item.Volume,
                    shelf_life_months = item.ShelfLifeMonths,
                    storage_conditions = item.StorageConditions,
                    tara_id = item.TaraId,
                    max_qty_per_hu = item.MaxQtyPerHu,
                    item_type_id = item.ItemTypeId,
                    min_stock_qty = item.MinStockQty,
                    default_sale_price_gross = item.DefaultSalePriceGross,
                    default_sale_vat_rate_id = item.DefaultSaleVatRateId
                },
                "item_id",
                "catalog-create-item",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TryUpdateItemAsync(Item item, CancellationToken cancellationToken = default)
    {
        return await TryPostAsync(
                $"/api/items/{item.Id}",
                new
                {
                    name = item.Name,
                    is_active = item.IsActive,
                    barcode = item.Barcode,
                    gtin = item.Gtin,
                    base_uom = item.BaseUom,
                    brand = item.Brand,
                    volume = item.Volume,
                    shelf_life_months = item.ShelfLifeMonths,
                    storage_conditions = item.StorageConditions,
                    tara_id = item.TaraId,
                    max_qty_per_hu = item.MaxQtyPerHu,
                    item_type_id = item.ItemTypeId,
                    min_stock_qty = item.MinStockQty,
                    default_sale_price_gross = item.DefaultSalePriceGross,
                    default_sale_vat_rate_id = item.DefaultSaleVatRateId
                },
                "catalog-update-item",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TryDeleteItemAsync(long itemId, CancellationToken cancellationToken = default)
    {
        return await TryDeleteAsync($"/api/items/{itemId}", "catalog-delete-item", cancellationToken).ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, long? CreatedId, string? Error)> TryCreateLocationAsync(
        string code,
        string name,
        int? maxHuSlots,
        bool autoHuDistributionEnabled,
        CancellationToken cancellationToken = default)
    {
        return await TryPostForIdAsync(
                "/api/locations",
                new
                {
                    code,
                    name,
                    max_hu_slots = maxHuSlots,
                    auto_hu_distribution_enabled = autoHuDistributionEnabled
                },
                "location_id",
                "catalog-create-location",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TryUpdateLocationAsync(
        long id,
        string code,
        string name,
        int? maxHuSlots,
        bool autoHuDistributionEnabled,
        CancellationToken cancellationToken = default)
    {
        return await TryPostAsync(
                $"/api/locations/{id}",
                new
                {
                    code,
                    name,
                    max_hu_slots = maxHuSlots,
                    auto_hu_distribution_enabled = autoHuDistributionEnabled
                },
                "catalog-update-location",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TryDeleteLocationAsync(long id, CancellationToken cancellationToken = default)
    {
        return await TryDeleteAsync($"/api/locations/{id}", "catalog-delete-location", cancellationToken).ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, long? CreatedId, string? Error)> TryCreateUomAsync(string name, CancellationToken cancellationToken = default)
    {
        return await TryPostForIdAsync(
                "/api/uoms",
                new { name },
                "uom_id",
                "catalog-create-uom",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TryDeleteUomAsync(long id, CancellationToken cancellationToken = default)
    {
        return await TryDeleteAsync($"/api/uoms/{id}", "catalog-delete-uom", cancellationToken).ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TryUpdateUomAsync(
        long id,
        string name,
        CancellationToken cancellationToken = default)
    {
        return await TryPostAsync(
                $"/api/uoms/{id}",
                new { name },
                "catalog-update-uom",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, long? CreatedId, string? Error)> TryCreateWriteOffReasonAsync(
        string code,
        string name,
        CancellationToken cancellationToken = default)
    {
        return await TryPostForIdAsync(
                "/api/write-off-reasons",
                new { code, name },
                "reason_id",
                "catalog-create-write-off-reason",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TryDeleteWriteOffReasonAsync(long id, CancellationToken cancellationToken = default)
    {
        return await TryDeleteAsync($"/api/write-off-reasons/{id}", "catalog-delete-write-off-reason", cancellationToken).ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, long? CreatedId, string? Error)> TryCreateTaraAsync(string name, CancellationToken cancellationToken = default)
    {
        return await TryPostForIdAsync(
                "/api/taras",
                new { name },
                "tara_id",
                "catalog-create-tara",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TryDeleteTaraAsync(long id, CancellationToken cancellationToken = default)
    {
        return await TryDeleteAsync($"/api/taras/{id}", "catalog-delete-tara", cancellationToken).ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, long? CreatedId, string? Error)> TryCreateItemTypeAsync(ItemType itemType, CancellationToken cancellationToken = default)
    {
        return await TryPostForIdAsync(
                "/api/item-types",
                new
                {
                    name = itemType.Name,
                    code = itemType.Code,
                    sort_order = itemType.SortOrder,
                    is_active = itemType.IsActive,
                    is_visible_in_product_catalog = itemType.IsVisibleInProductCatalog,
                    enable_min_stock_control = itemType.EnableMinStockControl,
                    min_stock_uses_order_binding = itemType.MinStockUsesOrderBinding,
                    enable_order_reservation = itemType.EnableOrderReservation,
                    enable_hu_distribution = itemType.EnableHuDistribution,
                    enable_marking = itemType.EnableMarking
                },
                "item_type_id",
                "catalog-create-item-type",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TryUpdateItemTypeAsync(ItemType itemType, CancellationToken cancellationToken = default)
    {
        return await TryPostAsync(
                $"/api/item-types/{itemType.Id}",
                new
                {
                    name = itemType.Name,
                    code = itemType.Code,
                    sort_order = itemType.SortOrder,
                    is_active = itemType.IsActive,
                    is_visible_in_product_catalog = itemType.IsVisibleInProductCatalog,
                    enable_min_stock_control = itemType.EnableMinStockControl,
                    min_stock_uses_order_binding = itemType.MinStockUsesOrderBinding,
                    enable_order_reservation = itemType.EnableOrderReservation,
                    enable_hu_distribution = itemType.EnableHuDistribution,
                    enable_marking = itemType.EnableMarking
                },
                "catalog-update-item-type",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TryDeleteItemTypeAsync(long id, CancellationToken cancellationToken = default)
    {
        return await TryDeleteAsync($"/api/item-types/{id}", "catalog-delete-item-type", cancellationToken).ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, long? CreatedId, string? Error)> TryCreateVatRateAsync(
        VatRate vatRate,
        CancellationToken cancellationToken = default)
    {
        return await TryPostForIdAsync(
                "/api/vat-rates",
                new
                {
                    name = vatRate.Name,
                    rate = vatRate.Rate,
                    is_active = vatRate.IsActive,
                    sort_order = vatRate.SortOrder
                },
                "vat_rate_id",
                "catalog-create-vat-rate",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TryUpdateVatRateAsync(
        VatRate vatRate,
        CancellationToken cancellationToken = default)
    {
        return await TryPostAsync(
                $"/api/vat-rates/{vatRate.Id}",
                new
                {
                    name = vatRate.Name,
                    rate = vatRate.Rate,
                    is_active = vatRate.IsActive,
                    sort_order = vatRate.SortOrder
                },
                "catalog-update-vat-rate",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TryDeleteVatRateAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        return await TryDeleteAsync($"/api/vat-rates/{id}", "catalog-delete-vat-rate", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(bool IsSuccess, T Value)> TryReadAsync<T>(
        string relativePath, Func<JsonElement, T> map, string operationName, T emptyValue, CancellationToken cancellationToken)
    {
        try
        {
            var configuration = await Task.Run(() =>
                TryLoadConfiguration(out var loaded) ? loaded : null, cancellationToken).ConfigureAwait(false);
            if (configuration is null)
            {
                _logger.Info($"Catalog API skipped for {operationName}: server base URL is not configured.");
                return (false, emptyValue);
            }

            using var handler = _readHandlerFactory?.Invoke() ?? CreateHandler(configuration);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(configuration.BaseUrl!, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds)
            };
            WpfTrustedRequestHeaders.Add(client, configuration.WpfAdminApiKey);
            using var response = await client.GetAsync(relativePath, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn($"Catalog API request failed: {relativePath} -> {(int)response.StatusCode} {response.ReasonPhrase}");
                return (false, emptyValue);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            return (true, map(document.RootElement));
        }
        catch (Exception ex)
        {
            _logger.Error($"Catalog API failed for {operationName}", ex);
            return (false, emptyValue);
        }
    }

    private async Task<(bool IsSuccess, string? Error)> TryPostAsync(string relativePath, object payload, string operationName, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info($"Catalog API skipped for {operationName}: server base URL is not configured.");
                return (false, null);
            }

            using var handler = CreateHandler(configuration);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(configuration.BaseUrl!, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds)
            };
            WpfTrustedRequestHeaders.Add(client, configuration.WpfAdminApiKey);
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
            _logger.Error($"Catalog API failed for {operationName}", ex);
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
                _logger.Info($"Catalog API skipped for {operationName}: server base URL is not configured.");
                return (false, null, null);
            }

            using var handler = CreateHandler(configuration);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(configuration.BaseUrl!, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds)
            };
            WpfTrustedRequestHeaders.Add(client, configuration.WpfAdminApiKey);
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
            var createdId = document != null && document.RootElement.TryGetProperty(idField, out var idElement) && idElement.TryGetInt64(out var id)
                ? id
                : (long?)null;
            return (true, createdId, null);
        }
        catch (Exception ex)
        {
            _logger.Error($"Catalog API failed for {operationName}", ex);
            return (false, null, null);
        }
    }

    private async Task<(bool IsSuccess, string? Error)> TryDeleteAsync(string relativePath, string operationName, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info($"Catalog API skipped for {operationName}: server base URL is not configured.");
                return (false, null);
            }

            using var handler = CreateHandler(configuration);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(configuration.BaseUrl!, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds)
            };
            WpfTrustedRequestHeaders.Add(client, configuration.WpfAdminApiKey);
            using var response = await client.DeleteAsync(relativePath, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return (true, null);
            }

            return (false, await ReadApiErrorAsync(response).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger.Error($"Catalog API failed for {operationName}", ex);
            return (false, null);
        }
    }

    private bool TryLoadConfiguration(out WpfCatalogApiConfiguration configuration)
    {
        var settings = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = ReadEnvOrSettings("FLOWSTOCK_SERVER_BASE_URL", settings.BaseUrl);
        var timeoutSeconds = ReadEnvInt("FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS") ?? settings.CloseTimeoutSeconds;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = WpfCloseDocumentService.DefaultCloseTimeoutSeconds;
        }

        configuration = new WpfCatalogApiConfiguration(
            NormalizeBaseUrl(baseUrl),
            timeoutSeconds,
            ReadEnvBool("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS") ?? settings.AllowInvalidTls,
            WpfTrustedRequestHeaders.ReadAdminApiKey(settings));
        return !string.IsNullOrWhiteSpace(configuration.BaseUrl);
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

    private static HttpMessageHandler CreateHandler(WpfCatalogApiConfiguration configuration)
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

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;
    }

    private static decimal ReadDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0m;
        }

        if (value.TryGetDecimal(out var parsed))
        {
            return parsed;
        }

        return decimal.TryParse(value.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out parsed)
            ? parsed
            : 0m;
    }

    private static bool ReadBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
        {
            return property.GetBoolean();
        }

        return bool.TryParse(property.ToString(), out var parsed) && parsed;
    }
}

internal sealed record WpfCatalogApiConfiguration(
    string? BaseUrl,
    int TimeoutSeconds,
    bool AllowInvalidTls,
    string? WpfAdminApiKey);
