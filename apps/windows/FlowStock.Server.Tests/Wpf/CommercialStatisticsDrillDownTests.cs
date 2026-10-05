using FlowStock.App;

namespace FlowStock.Server.Tests.Wpf;

public sealed class CommercialStatisticsDrillDownTests
{
    [Fact]
    public void Partner_items_request_preserves_authoritative_scope()
    {
        var state = new CommercialStatisticsViewState(pageSize: 100);
        state.SetAdvancedFilters(
            gtins: "0460001,0460002",
            itemNameContains: "чесночная паста",
            volumes: "1 кг,200 гр");
        state.SelectDetailMonth(
            "2026-09",
            new DateTime(2026, 1, 1),
            new DateTime(2026, 12, 31));

        var filters = new WpfCommercialStatisticsFilters(
            Mode: "orders",
            GroupBy: "partner",
            From: new DateTime(2026, 9, 1),
            To: new DateTime(2026, 9, 30),
            PartnerId: null,
            ItemId: 43,
            Gtin: null,
            Brand: "Печагин",
            Volume: null,
            Statuses: "SHIPPED",
            Sort: "gross_desc");

        var request = CommercialStatisticsDrillDown.BuildPartnerItemsRequest(
            filters,
            state,
            partnerId: 8,
            offset: 500);

        Assert.Equal("orders", request.Mode);
        Assert.Equal("item", request.GroupBy);
        Assert.Equal(new DateTime(2026, 1, 1), request.From);
        Assert.Equal(new DateTime(2026, 12, 31), request.To);
        Assert.Equal("2026-09", request.DetailMonth);
        Assert.Equal(8, request.PartnerId);
        Assert.Equal(43, request.ItemId);
        Assert.Equal("Печагин", request.Brand);
        Assert.Equal("SHIPPED", request.Statuses);
        Assert.Equal("0460001,0460002", request.Gtins);
        Assert.Equal("чесночная паста", request.ItemNameContains);
        Assert.Equal("1 кг,200 гр", request.Volumes);
        Assert.Equal(CommercialStatisticsDrillDown.PageSize, request.Limit);
        Assert.Equal(500, request.Offset);
        Assert.Equal("name_asc", request.Sort);
    }

    [Fact]
    public void Group_by_volume_formats_gtin_and_name_and_keeps_amounts()
    {
        var firstAmounts = Amounts(quantity: 10m, gross: 100m, net: 80m, vat: 20m);
        var secondAmounts = Amounts(quantity: 5m, gross: 60m, net: 50m, vat: 10m);
        var groups = new[]
        {
            Group("11", "Legacy A", firstAmounts),
            Group("12", "Legacy B", secondAmounts)
        };
        var catalog = new Dictionary<long, CommercialStatisticsDrillDownItemCatalogEntry>
        {
            [11] = new(11, "Аджика", "04600011", "1 кг"),
            [12] = new(12, "Хрен", "04600012", "200 гр")
        };

        var sections = CommercialStatisticsDrillDown.GroupByVolume(groups, catalog);

        Assert.Equal(2, sections.Count);
        Assert.Equal("1 кг", sections[0].Volume);
        Assert.Equal("04600011 — Аджика", Assert.Single(sections[0].Items).Label);
        Assert.Equal(firstAmounts, sections[0].Items[0].Amounts);
        Assert.Equal("200 гр", sections[1].Volume);
        Assert.Equal("04600012 — Хрен", Assert.Single(sections[1].Items).Label);
    }

    [Fact]
    public void Amounts_match_checks_quantity_and_financial_totals()
    {
        var children = new[]
        {
            Group("1", "A", Amounts(2m, 20m, 16m, 4m)),
            Group("2", "B", Amounts(3m, 30m, 24m, 6m))
        };

        Assert.True(CommercialStatisticsDrillDown.AmountsMatch(
            Amounts(5m, 50m, 40m, 10m),
            children));
        Assert.False(CommercialStatisticsDrillDown.AmountsMatch(
            Amounts(6m, 50m, 40m, 10m),
            children));
    }

    [Theory]
    [InlineData("0460", "Аджика", "0460 — Аджика")]
    [InlineData(null, "Аджика", "Аджика")]
    [InlineData("", "", "Запасное имя")]
    public void Item_label_prefers_gtin_and_catalog_name(
        string? gtin,
        string? name,
        string expected)
    {
        Assert.Equal(
            expected,
            CommercialStatisticsDrillDown.FormatItemLabel(gtin, name, "Запасное имя"));
    }

    private static WpfCommercialStatisticsGroup Group(
        string key,
        string label,
        WpfCommercialStatisticsAmounts amounts) =>
        new()
        {
            Key = key,
            Label = label,
            Amounts = amounts
        };

    private static WpfCommercialStatisticsAmounts Amounts(
        decimal quantity,
        decimal gross,
        decimal net,
        decimal vat) =>
        new()
        {
            Quantity = quantity,
            KnownFinancialQuantity = quantity,
            Gross = gross,
            Net = net,
            Vat = vat
        };
}
