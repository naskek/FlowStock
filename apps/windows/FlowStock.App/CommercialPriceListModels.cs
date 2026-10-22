using System.Text.Json.Serialization;

namespace FlowStock.App;

// A single server-owned snapshot is used for both the grid and both export formats.
public sealed record WpfCommercialPriceListQuery(
    string Mode,
    long? PartnerId,
    long? ItemId,
    string? Gtin,
    string? Search,
    string? Brand,
    string? Volume,
    bool ActiveItemsOnly,
    bool IncludeInactiveOverrides,
    bool IncludeUnpriced);

public sealed class WpfCommercialPriceListReport
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "all";
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }
    [JsonPropertyName("items")]
    public List<WpfCommercialPriceListRow> Items { get; set; } = [];
    [JsonIgnore]
    public string SelectionDescription { get; set; } = string.Empty;
}

public sealed class WpfCommercialPriceListRow
{
    [JsonPropertyName("row_kind")]
    public string RowKind { get; set; } = "";
    [JsonPropertyName("partner_id")]
    public long? PartnerId { get; set; }
    [JsonPropertyName("partner_name")]
    public string? PartnerName { get; set; }
    [JsonPropertyName("partner_code")]
    public string? PartnerCode { get; set; }
    [JsonPropertyName("item_id")]
    public long ItemId { get; set; }
    [JsonPropertyName("item_name")]
    public string ItemName { get; set; } = "";
    [JsonPropertyName("gtin")]
    public string? Gtin { get; set; }
    [JsonPropertyName("brand")]
    public string? Brand { get; set; }
    [JsonPropertyName("volume")]
    public string? Volume { get; set; }
    [JsonPropertyName("default_price_gross")]
    public decimal? DefaultPriceGross { get; set; }
    [JsonPropertyName("partner_price_gross")]
    public decimal? PartnerPriceGross { get; set; }
    [JsonPropertyName("effective_price_gross")]
    public decimal? EffectivePriceGross { get; set; }
    [JsonPropertyName("price_source")]
    public string PriceSource { get; set; } = "NONE";
    [JsonPropertyName("vat_rate")]
    public decimal? VatRate { get; set; }
    [JsonPropertyName("vat_rate_name")]
    public string? VatRateName { get; set; }
    [JsonPropertyName("item_is_active")]
    public bool ItemIsActive { get; set; }
    [JsonPropertyName("partner_price_is_active")]
    public bool? PartnerPriceIsActive { get; set; }

    public string PriceSourceDisplay => PriceSource switch
    {
        "PARTNER_ITEM" => "Цена клиента",
        "ITEM_DEFAULT" => "Базовая",
        _ => "Нет цены"
    };

    public string RowKindDisplay => RowKind == "BASE" ? "Каталог" : "Клиент";
}

internal static class CommercialPriceListSelection
{
    // Normalize the mode-specific filter, even if UI state is stale.
    public static (long? PartnerId, long? ItemId) ForMode(
        string mode, long? partnerId, long? itemId) => mode switch
    {
        "item" => (null, itemId),
        "partner" => (partnerId, itemId),
        "all" => (partnerId, itemId),
        _ => throw new ArgumentException("Неизвестный режим прайс-листа.", nameof(mode))
    };

    public static string Describe(
        WpfCommercialPriceListQuery query,
        string? partnerName,
        string? partnerCode,
        string? itemName,
        string? itemGtin)
    {
        var filters = new List<string>();
        if (query.PartnerId.HasValue)
            filters.Add($"Клиент: {partnerName ?? "(не указан)"} ({partnerCode ?? "без ИНН"}; ID {query.PartnerId})");
        if (query.ItemId.HasValue)
            filters.Add($"Товар: {itemName ?? "(не указан)"}; GTIN {itemGtin ?? "—"}; ID {query.ItemId}");
        if (!string.IsNullOrWhiteSpace(query.Search))
            filters.Add($"Поиск: {query.Search.Trim()}");
        if (!string.IsNullOrWhiteSpace(query.Gtin))
            filters.Add($"GTIN: {query.Gtin.Trim()}");
        if (!string.IsNullOrWhiteSpace(query.Brand))
            filters.Add($"Бренд: {query.Brand.Trim()}");
        if (!string.IsNullOrWhiteSpace(query.Volume))
            filters.Add($"Фасовка: {query.Volume.Trim()}");
        filters.Add(query.ActiveItemsOnly ? "Только активные товары" : "Все товары");
        if (query.IncludeInactiveOverrides)
            filters.Add("Включены неактивные цены");
        if (query.IncludeUnpriced)
            filters.Add("Включены товары без цены");
        return string.Join(" | ", filters);
    }
}
