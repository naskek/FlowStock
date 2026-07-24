using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowStock.Core.Models;

namespace FlowStock.App;

public sealed class WpfCommercialCatalogApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsService _settings;
    private readonly FileLogger _logger;

    public WpfCommercialCatalogApiService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<PartnerItemSalePricePage> GetPricesAsync(
        long? partnerId = null,
        long? itemId = null,
        bool? isActive = null,
        string? search = null,
        int limit = 100,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>
        {
            $"limit={Math.Clamp(limit, 1, 500).ToString(CultureInfo.InvariantCulture)}",
            $"offset={Math.Max(0, offset).ToString(CultureInfo.InvariantCulture)}"
        };
        if (partnerId.HasValue)
        {
            query.Add($"partner_id={partnerId.Value.ToString(CultureInfo.InvariantCulture)}");
        }
        if (itemId.HasValue)
        {
            query.Add($"item_id={itemId.Value.ToString(CultureInfo.InvariantCulture)}");
        }
        if (isActive.HasValue)
        {
            query.Add($"is_active={isActive.Value.ToString().ToLowerInvariant()}");
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            query.Add($"q={Uri.EscapeDataString(search.Trim())}");
        }

        using var client = CreateClient();
        using var response = await client.GetAsync(
            $"/api/partner-item-sale-prices?{string.Join("&", query)}",
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<PartnerPricePageDto>(
            JsonOptions,
            cancellationToken).ConfigureAwait(false)
            ?? new PartnerPricePageDto();
        return new PartnerItemSalePricePage(
            payload.Items.Select(row => new PartnerItemSalePrice
            {
                Id = row.Id,
                PartnerId = row.PartnerId,
                PartnerName = row.PartnerName ?? string.Empty,
                PartnerCode = row.PartnerCode,
                ItemId = row.ItemId,
                ItemName = row.ItemName ?? string.Empty,
                UnitPriceGross = row.UnitPriceGross,
                IsActive = row.IsActive
            }).ToArray(),
            payload.TotalCount,
            payload.Limit,
            payload.Offset);
    }

    public async Task SavePriceAsync(
        long? id,
        long partnerId,
        long itemId,
        decimal unitPriceGross,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        using var response = await client.PostAsJsonAsync(
            id.HasValue
                ? $"/api/partner-item-sale-prices/{id.Value.ToString(CultureInfo.InvariantCulture)}"
                : "/api/partner-item-sale-prices",
            new
            {
                partner_id = partnerId,
                item_id = itemId,
                unit_price_gross = unitPriceGross,
                is_active = isActive
            },
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task DeletePriceAsync(long id, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        using var response = await client.DeleteAsync(
            $"/api/partner-item-sale-prices/{id.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task<CommercialTermsPreview> GetPreviewAsync(
        long partnerId,
        long itemId,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(
            $"/api/commercial-terms/preview?partner_id={partnerId.ToString(CultureInfo.InvariantCulture)}&item_id={itemId.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<CommercialPreviewDto>(
            JsonOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Сервер вернул пустой preview коммерческих условий.");
        return new CommercialTermsPreview(
            payload.PartnerId,
            payload.ItemId,
            payload.AutomaticUnitPriceGross,
            ParsePriceSource(payload.PriceSource),
            payload.VatRate,
            payload.IssueCode,
            payload.IssueMessage);
    }

    private HttpClient CreateClient()
    {
        var server = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = Environment.GetEnvironmentVariable("FLOWSTOCK_SERVER_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = server.BaseUrl;
        }
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Не настроен адрес сервера FlowStock.");
        }
        baseUrl = baseUrl.Trim();
        if (!baseUrl.Contains("://", StringComparison.Ordinal))
        {
            baseUrl = "https://" + baseUrl;
        }

        var handler = new HttpClientHandler();
        var allowInvalidTls = ReadEnvBool("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS") ?? server.AllowInvalidTls;
        if (allowInvalidTls)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(Math.Max(1, server.CloseTimeoutSeconds))
        };
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        try
        {
            var error = JsonSerializer.Deserialize<ApiErrorDto>(raw, JsonOptions);
            if (!string.IsNullOrWhiteSpace(error?.Message))
            {
                throw new InvalidOperationException(error.Message);
            }
            if (!string.IsNullOrWhiteSpace(error?.Error))
            {
                throw new InvalidOperationException(error.Error);
            }
        }
        catch (JsonException ex)
        {
            _logger.Error("Failed to parse commercial catalog API error", ex);
        }
        throw new InvalidOperationException($"Сервер вернул ошибку {(int)response.StatusCode}.");
    }

    private static SalePriceSource ParsePriceSource(string? value) =>
        value?.Trim().ToUpperInvariant() switch
        {
            "PARTNERITEM" or "PARTNER_ITEM" => SalePriceSource.PartnerItem,
            "ITEMDEFAULT" or "ITEM_DEFAULT" => SalePriceSource.ItemDefault,
            "MANUAL" => SalePriceSource.Manual,
            _ => SalePriceSource.None
        };

    private static bool? ReadEnvBool(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return value?.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => null
        };
    }

    private sealed class PartnerPricePageDto
    {
        [JsonPropertyName("items")]
        public List<PartnerPriceDto> Items { get; set; } = [];
        [JsonPropertyName("total_count")]
        public int TotalCount { get; set; }
        [JsonPropertyName("limit")]
        public int Limit { get; set; }
        [JsonPropertyName("offset")]
        public int Offset { get; set; }
    }

    private sealed class PartnerPriceDto
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }
        [JsonPropertyName("partner_id")]
        public long PartnerId { get; set; }
        [JsonPropertyName("partner_name")]
        public string? PartnerName { get; set; }
        [JsonPropertyName("partner_code")]
        public string? PartnerCode { get; set; }
        [JsonPropertyName("item_id")]
        public long ItemId { get; set; }
        [JsonPropertyName("item_name")]
        public string? ItemName { get; set; }
        [JsonPropertyName("unit_price_gross")]
        public decimal UnitPriceGross { get; set; }
        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; }
    }

    private sealed class CommercialPreviewDto
    {
        [JsonPropertyName("partner_id")]
        public long PartnerId { get; set; }
        [JsonPropertyName("item_id")]
        public long ItemId { get; set; }
        [JsonPropertyName("automatic_unit_price_gross")]
        public decimal? AutomaticUnitPriceGross { get; set; }
        [JsonPropertyName("price_source")]
        public string? PriceSource { get; set; }
        [JsonPropertyName("vat_rate")]
        public decimal? VatRate { get; set; }
        [JsonPropertyName("issue_code")]
        public string? IssueCode { get; set; }
        [JsonPropertyName("issue_message")]
        public string? IssueMessage { get; set; }
    }

    private sealed class ApiErrorDto
    {
        [JsonPropertyName("error")]
        public string? Error { get; set; }
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}
