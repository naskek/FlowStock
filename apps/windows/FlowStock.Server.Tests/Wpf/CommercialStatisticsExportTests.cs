using FlowStock.App;
using System.IO.Compression;
using System.Text;

namespace FlowStock.Server.Tests.Wpf;

public sealed class CommercialStatisticsExportTests
{
    [Fact]
    public async Task Loader_RequestsAllGroupsInServerSizedPages()
    {
        var requests = new List<WpfCommercialStatisticsRequest>();
        var selection = CreateSelection();
        var groups = Enumerable.Range(1, 1_101)
            .Select(index => CreateGroup($"Группа {index}"))
            .ToArray();

        async Task<WpfCommercialStatisticsResult> Fetch(
            WpfCommercialStatisticsRequest request,
            CancellationToken cancellationToken)
        {
            requests.Add(request);
            await Task.Yield();
            return CreateResult(
                groups.Skip(request.Offset).Take(request.Limit).ToArray(),
                groups.Length,
                request.Offset,
                request.Limit);
        }

        var report = await CommercialStatisticsExportLoader.LoadAsync(selection, Fetch);

        Assert.Equal(1_101, report.Groups.Count);
        Assert.Equal(new[] { 0, 500, 1000 }, requests.Select(request => request.Offset).ToArray());
        Assert.All(requests, request => Assert.Equal(500, request.Limit));
        Assert.All(requests, request => Assert.Equal(selection.Request.DetailMonth, request.DetailMonth));
        Assert.All(requests, request => Assert.Equal(selection.Request.PartnerId, request.PartnerId));
    }

    [Fact]
    public async Task Loader_FailsIfPagedSnapshotChangesDuringExport()
    {
        var selection = CreateSelection(totalCount: 0);
        var call = 0;

        async Task<WpfCommercialStatisticsResult> Fetch(
            WpfCommercialStatisticsRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            call++;
            return CreateResult(
                [CreateGroup($"Страница {call}")],
                call == 1 ? 2 : 3,
                request.Offset,
                request.Limit);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CommercialStatisticsExportLoader.LoadAsync(selection, Fetch));

        Assert.Contains("изменилась", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExcelExporter_CreatesSummaryMonthlyAndDetailSheetsWithNativeNumbers()
    {
        var report = CreateReport();

        var bytes = CommercialStatisticsExcelExporter.Create(report);

        Assert.True(bytes.Length > 500);
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var workbook = ReadEntry(archive, "xl/workbook.xml");
        Assert.Contains("Сводка", workbook, StringComparison.Ordinal);
        Assert.Contains("По месяцам", workbook, StringComparison.Ordinal);
        Assert.Contains("Детализация", workbook, StringComparison.Ordinal);

        var detail = ReadEntry(archive, "xl/worksheets/sheet3.xml");
        Assert.Contains("Товар А", detail, StringComparison.Ordinal);
        Assert.Contains(">123.45<", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfExporter_CreatesPdfWithoutViewerDependency()
    {
        var report = CreateReport();

        var bytes = CommercialStatisticsPdfExporter.Create(report);

        Assert.True(bytes.Length > 1_000);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5), StringComparison.Ordinal);
    }

    [Fact]
    public void Report_PreservesDataQualityWarningFromStatisticsContract()
    {
        var report = CreateReport();

        Assert.False(report.DataQuality.IsFinanciallyComplete);
        Assert.Contains("Без цены", report.DataQualityText, StringComparison.Ordinal);
        Assert.Contains("непривязанные продажи", report.DataQualityText, StringComparison.Ordinal);
    }

    private static CommercialStatisticsExportSelection CreateSelection() =>
        new(
            new WpfCommercialStatisticsRequest(
                "orders",
                "item",
                new DateTime(2026, 1, 1),
                new DateTime(2026, 9, 29),
                DetailMonth: "2026-09",
                PartnerId: 42,
                ItemId: null,
                Gtin: null,
                Brand: "Русские закуски",
                Volume: "190 мл",
                Statuses: "ACCEPTED,IN_PROGRESS",
                Limit: 100,
                Offset: 100,
                Sort: "gross_desc"),
            "Заказы",
            "Товар",
            "ООО Тест",
            "Все товары",
            "Все GTIN",
            "Русские закуски",
            "190 мл",
            "Готов, В работе");

    private static CommercialStatisticsExportReport CreateReport()
    {
        var result = CreateResult(
            [CreateGroup("Товар А")],
            totalCount: 1,
            offset: 0,
            limit: 500);
        result.Monthly =
        [
            new WpfCommercialStatisticsMonth
            {
                Month = "2026-09",
                Amounts = CreateAmounts()
            }
        ];
        result.DataQuality = new WpfCommercialStatisticsDataQuality
        {
            MissingPriceFactCount = 1,
            MissingPriceQuantity = 2,
            MissingVatFactCount = 0,
            MissingVatQuantity = 0,
            FinanciallyIncompleteFactCount = 1,
            FinanciallyIncompleteQuantity = 2,
            UnlinkedSalesFactCount = 1,
            UnlinkedSalesQuantity = 3,
            ItemMismatchSalesFactCount = 0,
            ItemMismatchSalesQuantity = 0,
            IsFinanciallyComplete = false
        };

        return new CommercialStatisticsExportReport(
            CreateSelection(),
            result.Summary,
            result.Monthly,
            result.Groups.Items,
            result.DataQuality);
    }

    private static WpfCommercialStatisticsResult CreateResult(
        IReadOnlyList<WpfCommercialStatisticsGroup> groups,
        int totalCount,
        int offset,
        int limit) =>
        new()
        {
            Mode = "orders",
            GroupBy = "item",
            Summary = CreateAmounts(),
            Groups = new WpfCommercialStatisticsGroups
            {
                Items = groups.ToList(),
                TotalCount = totalCount,
                Offset = offset,
                Limit = limit
            },
            DataQuality = new WpfCommercialStatisticsDataQuality
            {
                IsFinanciallyComplete = true
            }
        };

    private static WpfCommercialStatisticsGroup CreateGroup(string label) =>
        new()
        {
            Key = label,
            Label = label,
            Amounts = CreateAmounts()
        };

    private static WpfCommercialStatisticsAmounts CreateAmounts() =>
        new()
        {
            OrderCount = 2,
            DocumentCount = 3,
            FactCount = 4,
            Quantity = 12.5m,
            KnownFinancialQuantity = 10.5m,
            Gross = 123.45m,
            Net = 102.02m,
            Vat = 21.43m
        };

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name)
            ?? throw new InvalidOperationException($"ZIP entry not found: {name}");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
