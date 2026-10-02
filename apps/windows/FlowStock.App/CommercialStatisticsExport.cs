using System.Globalization;
using System.IO;

namespace FlowStock.App;

internal sealed record CommercialStatisticsExportSelection(
    WpfCommercialStatisticsRequest Request,
    string ModeLabel,
    string GroupByLabel,
    string PartnerLabel,
    string ItemLabel,
    string GtinLabel,
    string BrandLabel,
    string VolumeLabel,
    string StatusesLabel,
    string? GtinsLabel = null,
    string? ItemNameContainsLabel = null)
{
    private static readonly CultureInfo RussianCulture = CultureInfo.GetCultureInfo("ru-RU");

    public string PeriodLabel => $"{Request.From:dd.MM.yyyy} — {Request.To:dd.MM.yyyy}";

    public string DetailLabel
    {
        get
        {
            if (!DateTime.TryParseExact(
                    Request.DetailMonth,
                    "yyyy-MM",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var month))
            {
                return "Весь выбранный период";
            }

            return $"{month.ToString("MMMM", RussianCulture)} {month.Year}";
        }
    }

    public IReadOnlyList<(string Name, string Value)> Criteria =>
    [
        ("Период", PeriodLabel),
        ("Режим", ModeLabel),
        ("Группировка", GroupByLabel),
        ("Детализация", DetailLabel),
        ("Контрагент", PartnerLabel),
        ("Товар", ItemLabel),
        ("Название содержит", string.IsNullOrWhiteSpace(ItemNameContainsLabel) ? "Не задано" : ItemNameContainsLabel),
        ("GTIN", GtinLabel),
        ("GTIN (набор)", string.IsNullOrWhiteSpace(GtinsLabel) ? "Не задан" : GtinsLabel),
        ("Бренд", BrandLabel),
        ("Фасовка", VolumeLabel),
        ("Статусы", StatusesLabel)
    ];
}

internal sealed record CommercialStatisticsExportReport(
    CommercialStatisticsExportSelection Selection,
    WpfCommercialStatisticsAmounts Summary,
    IReadOnlyList<WpfCommercialStatisticsMonth> Monthly,
    IReadOnlyList<WpfCommercialStatisticsGroup> Groups,
    WpfCommercialStatisticsDataQuality DataQuality)
{
    public string DataQualityText =>
        CommercialStatisticsDataQualityPresentation.Format(DataQuality);
}

internal static class CommercialStatisticsExportLoader
{
    internal const int PageSize = 500;

    public static async Task<CommercialStatisticsExportReport> LoadAsync(
        CommercialStatisticsExportSelection selection,
        Func<WpfCommercialStatisticsRequest, CancellationToken, Task<WpfCommercialStatisticsResult>> fetchPage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fetchPage);

        WpfCommercialStatisticsResult? firstPage = null;
        var groups = new List<WpfCommercialStatisticsGroup>();
        var offset = 0;
        var expectedTotal = -1;

        while (expectedTotal < 0 || offset < expectedTotal)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = selection.Request with
            {
                Limit = PageSize,
                Offset = offset
            };
            var page = await fetchPage(request, cancellationToken).ConfigureAwait(false);

            if (page.Groups.Offset != offset)
            {
                throw new InvalidOperationException(
                    $"Сервер статистики вернул неожиданный offset {page.Groups.Offset} вместо {offset}.");
            }

            if (firstPage is null)
            {
                firstPage = page;
                expectedTotal = Math.Max(0, page.Groups.TotalCount);
            }
            else
            {
                if (page.Groups.TotalCount != expectedTotal
                    || !SameSnapshot(firstPage, page))
                {
                    throw new InvalidOperationException(
                        "Статистика изменилась во время формирования отчёта. Повторите экспорт.");
                }
            }

            if (page.Groups.Items.Count == 0)
            {
                if (offset < expectedTotal)
                {
                    throw new InvalidOperationException(
                        "Сервер статистики вернул пустую страницу до конца выборки.");
                }
                break;
            }

            groups.AddRange(page.Groups.Items);
            offset += page.Groups.Items.Count;
        }

        firstPage ??= new WpfCommercialStatisticsResult();
        if (groups.Count != Math.Max(0, expectedTotal))
        {
            throw new InvalidOperationException(
                $"Получено {groups.Count} групп статистики вместо ожидаемых {Math.Max(0, expectedTotal)}.");
        }

        return new CommercialStatisticsExportReport(
            selection,
            firstPage.Summary,
            firstPage.Monthly,
            groups,
            firstPage.DataQuality);
    }

    private static bool SameSnapshot(
        WpfCommercialStatisticsResult expected,
        WpfCommercialStatisticsResult actual) =>
        string.Equals(expected.Mode, actual.Mode, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.GroupBy, actual.GroupBy, StringComparison.OrdinalIgnoreCase)
        && SameAmounts(expected.Summary, actual.Summary)
        && SameDataQuality(expected.DataQuality, actual.DataQuality)
        && SameMonthly(expected.Monthly, actual.Monthly);

    private static bool SameMonthly(
        IReadOnlyList<WpfCommercialStatisticsMonth> expected,
        IReadOnlyList<WpfCommercialStatisticsMonth> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        for (var index = 0; index < expected.Count; index++)
        {
            if (!string.Equals(
                    expected[index].Month,
                    actual[index].Month,
                    StringComparison.Ordinal)
                || !SameAmounts(expected[index].Amounts, actual[index].Amounts))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameAmounts(
        WpfCommercialStatisticsAmounts expected,
        WpfCommercialStatisticsAmounts actual) =>
        expected.OrderCount == actual.OrderCount
        && expected.DocumentCount == actual.DocumentCount
        && expected.FactCount == actual.FactCount
        && expected.Quantity == actual.Quantity
        && expected.KnownFinancialQuantity == actual.KnownFinancialQuantity
        && expected.Gross == actual.Gross
        && expected.Net == actual.Net
        && expected.Vat == actual.Vat;

    private static bool SameDataQuality(
        WpfCommercialStatisticsDataQuality expected,
        WpfCommercialStatisticsDataQuality actual) =>
        expected.MissingPriceFactCount == actual.MissingPriceFactCount
        && expected.MissingPriceQuantity == actual.MissingPriceQuantity
        && expected.MissingVatFactCount == actual.MissingVatFactCount
        && expected.MissingVatQuantity == actual.MissingVatQuantity
        && expected.FinanciallyIncompleteFactCount == actual.FinanciallyIncompleteFactCount
        && expected.FinanciallyIncompleteQuantity == actual.FinanciallyIncompleteQuantity
        && expected.UnlinkedSalesFactCount == actual.UnlinkedSalesFactCount
        && expected.UnlinkedSalesQuantity == actual.UnlinkedSalesQuantity
        && expected.ItemMismatchSalesFactCount == actual.ItemMismatchSalesFactCount
        && expected.ItemMismatchSalesQuantity == actual.ItemMismatchSalesQuantity
        && expected.IsFinanciallyComplete == actual.IsFinanciallyComplete;
}

internal static class CommercialStatisticsExportFileWriter
{
    public static void WriteAtomically(string path, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Не удалось определить папку для отчёта.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, content);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
