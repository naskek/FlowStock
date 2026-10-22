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
