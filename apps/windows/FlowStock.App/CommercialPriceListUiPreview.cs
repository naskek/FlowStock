using FlowStock.Core.Models;

namespace FlowStock.App;

// Only presentation data: this partial window never creates AppServices or opens API/DB.
public partial class CommercialPriceListWindow
{
    private static readonly Partner[] PreviewPartners =
    [
        new() { Id = 101, Name = "ООО «Торговый дом Север»", Code = "7701234567", PartnerRole = "CLIENT" },
        new() { Id = 102, Name = "ООО «Гастрономия и традиции»", Code = "7801234567", PartnerRole = "CLIENT" },
        new() { Id = 103, Name = "ООО «Ресторанный поставщик»", Code = "5001234567", PartnerRole = "BOTH" }
    ];

    private static readonly Item[] PreviewItems =
    [
        new() { Id = 11, Name = "Хрен столовый классический 200 г", Gtin = "04600000000001", Brand = "Русские закуски", Volume = "200 г", DefaultSalePriceGross = 98m },
        new() { Id = 12, Name = "Горчица русская острая 200 г", Gtin = "04600000000002", Brand = "Русские закуски", Volume = "200 г", DefaultSalePriceGross = 83.50m },
        new() { Id = 13, Name = "Аджика домашняя 200 г", Gtin = "04600000000003", Brand = "Русские закуски", Volume = "200 г", DefaultSalePriceGross = 125m },
        new() { Id = 14, Name = "Паста чесночная 200 г", Gtin = "04600000000004", Brand = "СТМ", Volume = "200 г", DefaultSalePriceGross = 105.90m },
        new() { Id = 15, Name = "Хрен со свёклой 1 кг — длинное наименование для проверки колонок", Gtin = "04600000000005", Brand = "Русские закуски", Volume = "1 кг", DefaultSalePriceGross = 219m },
        new() { Id = 16, Name = "Товар без коммерческой цены", Gtin = "04600000000006", Brand = "СТМ", Volume = "500 г", IsActive = false }
    ];

    private static readonly (long PartnerId, long ItemId, decimal Price, bool Active)[] PreviewOverrides =
    [
        (101, 11, 0m, true),
        (102, 11, 91.50m, true),
        (103, 12, 78m, false),
        (101, 13, 125m, true),
        (102, 14, 89.99m, true),
        (103, 15, 199m, true)
    ];

    public CommercialPriceListWindow(UiPreviewContext preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        InitializeComponent();
        UiPreviewContext.Prepare(this);
        PartnerCombo.ItemsSource = PreviewPartners;
        ItemCombo.ItemsSource = PreviewItems;
        // Only this read-only demo action is enabled. File exports stay disabled.
        RefreshButton.IsEnabled = true;
        var demo = BuildPreviewReport(new WpfCommercialPriceListQuery(
            "all", null, null, null, null, null, null, true, false, false));
        ReportGrid.ItemsSource = demo.Items;
        StatusText.Text = $"UI Preview / DEV — {demo.TotalCount} демонстрационных строк. Экспорт отключён.";
    }

    internal static WpfCommercialPriceListReport BuildPreviewReport(WpfCommercialPriceListQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        // Always use synthetic records, even when a local PostgreSQL container is running.
        var rows = new List<WpfCommercialPriceListRow>();
        foreach (var item in PreviewItems)
        {
            if (query.ActiveItemsOnly && !item.IsActive)
                continue;
            if (query.ItemId.HasValue && item.Id != query.ItemId.Value)
                continue;
            if (!string.IsNullOrWhiteSpace(query.Gtin)
                && !string.Equals(item.Gtin, query.Gtin.Trim(), StringComparison.Ordinal))
                continue;
            if (!string.IsNullOrWhiteSpace(query.Search)
                && !item.Name.Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase)
                && !(item.Gtin ?? "").Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(query.Brand)
                && !string.Equals(item.Brand, query.Brand.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(query.Volume)
                && !string.Equals(item.Volume, query.Volume.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;

            if (query.Mode != "partner")
            {
                rows.Add(CreateRow(item, null, null));
                foreach (var price in PreviewOverrides.Where(p => p.ItemId == item.Id
                    && (p.Active || query.IncludeInactiveOverrides)
                    && (!query.PartnerId.HasValue || p.PartnerId == query.PartnerId.Value)))
                {
                    var partner = PreviewPartners.Single(p => p.Id == price.PartnerId);
                    rows.Add(CreateRow(item, partner, price));
                }
            }
            else
            {
                var partner = PreviewPartners.SingleOrDefault(p => p.Id == query.PartnerId)
                    ?? throw new InvalidOperationException("Выберите демонстрационного клиента.");
                var found = PreviewOverrides.Where(p => p.PartnerId == partner.Id && p.ItemId == item.Id)
                    .Select(p => ((long PartnerId, long ItemId, decimal Price, bool Active)?)p)
                    .FirstOrDefault();
                if (found.HasValue && !found.Value.Active && !query.IncludeInactiveOverrides)
                    found = null;
                var row = CreateRow(item, partner, found);
                if (query.IncludeUnpriced || row.EffectivePriceGross.HasValue)
                    rows.Add(row);
            }
        }

        rows = rows.OrderBy(r => r.ItemName, StringComparer.CurrentCulture)
            .ThenBy(r => r.ItemId)
            .ThenBy(r => r.RowKind == "BASE" ? 0 : 1)
            .ThenBy(r => r.PartnerName, StringComparer.CurrentCulture)
            .ToList();
        return new WpfCommercialPriceListReport
        {
            Mode = query.Mode,
            TotalCount = rows.Count,
            Items = rows
        };
    }

    private static WpfCommercialPriceListRow CreateRow(
        Item item, Partner? partner,
        (long PartnerId, long ItemId, decimal Price, bool Active)? overridePrice)
    {
        var activeOverride = overridePrice.HasValue && overridePrice.Value.Active;
        return new WpfCommercialPriceListRow
        {
            RowKind = partner is null ? "BASE" : "PARTNER",
            PartnerId = partner?.Id,
            PartnerName = partner?.Name,
            PartnerCode = partner?.Code,
            ItemId = item.Id,
            ItemName = item.Name,
            Gtin = item.Gtin,
            Brand = item.Brand,
            Volume = item.Volume,
            DefaultPriceGross = item.DefaultSalePriceGross,
            PartnerPriceGross = overridePrice?.Price,
            EffectivePriceGross = activeOverride ? overridePrice?.Price : item.DefaultSalePriceGross,
            PriceSource = activeOverride ? "PARTNER_ITEM" :
                item.DefaultSalePriceGross.HasValue ? "ITEM_DEFAULT" : "NONE",
            VatRate = 10m,
            VatRateName = "НДС (демо)",
            ItemIsActive = item.IsActive,
            PartnerPriceIsActive = overridePrice?.Active
        };
    }
}
