using FlowStock.App;

namespace FlowStock.Server.Tests.Wpf;

public sealed class CommercialStatisticsAdvancedExportTests
{
    [Fact]
    public async Task Full_export_preserves_advanced_filters_on_every_page()
    {
        var selection = new CommercialStatisticsExportSelection(
            new WpfCommercialStatisticsRequest(
                Mode: "sales",
                GroupBy: "item",
                From: new DateTime(2026, 9, 1),
                To: new DateTime(2026, 9, 30),
                Gtins: "0460111,0460222",
                ItemNameContains: "аджика",
                Volumes: "190 мл,500 мл"),
            ModeLabel: "Продажи",
            GroupByLabel: "Товар",
            PartnerLabel: "Все контрагенты",
            ItemLabel: "Все товары",
            GtinLabel: "Все GTIN",
            BrandLabel: "Все бренды",
            VolumeLabel: "190 мл, 500 мл",
            StatusesLabel: "Не применяется",
            GtinsLabel: "0460111,0460222",
            ItemNameContainsLabel: "аджика");
        var requests = new List<WpfCommercialStatisticsRequest>();

        var report = await CommercialStatisticsExportLoader.LoadAsync(
            selection,
            (request, _) =>
            {
                requests.Add(request);
                var items = request.Offset == 0
                    ? Enumerable.Range(0, CommercialStatisticsExportLoader.PageSize)
                        .Select(index => Group($"first-{index}"))
                        .ToList()
                    : new List<WpfCommercialStatisticsGroup> { Group("last") };
                return Task.FromResult(new WpfCommercialStatisticsResult
                {
                    Mode = "sales",
                    GroupBy = "item",
                    Summary = Amounts(501m),
                    Monthly =
                    [
                        new WpfCommercialStatisticsMonth
                        {
                            Month = "2026-09",
                            Amounts = Amounts(501m)
                        }
                    ],
                    Groups = new WpfCommercialStatisticsGroups
                    {
                        Offset = request.Offset,
                        Limit = request.Limit,
                        TotalCount = 501,
                        Items = items
                    },
                    DataQuality = new WpfCommercialStatisticsDataQuality
                    {
                        IsFinanciallyComplete = true
                    }
                });
            });

        Assert.Equal(501, report.Groups.Count);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request =>
        {
            Assert.Equal("0460111,0460222", request.Gtins);
            Assert.Equal("аджика", request.ItemNameContains);
            Assert.Equal("190 мл,500 мл", request.Volumes);
            Assert.Null(request.Volume);
        });
        Assert.Contains(
            report.Selection.Criteria,
            criterion => criterion.Name == "Название содержит" && criterion.Value == "аджика");
        Assert.Contains(
            report.Selection.Criteria,
            criterion => criterion.Name == "Фасовка" && criterion.Value == "190 мл, 500 мл");
    }

    private static WpfCommercialStatisticsGroup Group(string key) => new()
    {
        Key = key,
        Label = key,
        Amounts = Amounts(1m)
    };

    private static WpfCommercialStatisticsAmounts Amounts(decimal quantity) => new()
    {
        FactCount = (int)quantity,
        Quantity = quantity,
        KnownFinancialQuantity = quantity,
        Gross = quantity,
        Net = quantity,
        Vat = 0m
    };
}
