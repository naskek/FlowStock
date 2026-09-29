using System.Globalization;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace FlowStock.App;

internal static class CommercialStatisticsExcelExporter
{
    public static byte[] Create(CommercialStatisticsExportReport report)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(
                   stream,
                   SpreadsheetDocumentType.Workbook,
                   true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var sheets = workbookPart.Workbook.AppendChild(new Sheets());

            AddSheet(
                workbookPart,
                sheets,
                "Сводка",
                1,
                BuildSummaryRows(report),
                autoFilterReference: null);
            AddSheet(
                workbookPart,
                sheets,
                "По месяцам",
                2,
                BuildMonthlyRows(report),
                autoFilterReference: report.Monthly.Count > 0
                    ? $"A1:I{report.Monthly.Count + 1}"
                    : null);
            AddSheet(
                workbookPart,
                sheets,
                "Детализация",
                3,
                BuildDetailRows(report),
                autoFilterReference: report.Groups.Count > 0
                    ? $"A1:I{report.Groups.Count + 1}"
                    : null);

            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    private static void AddSheet(
        WorkbookPart workbookPart,
        Sheets sheets,
        string name,
        uint sheetId,
        IEnumerable<Row> rows,
        string? autoFilterReference)
    {
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        var worksheet = new Worksheet(sheetData);
        worksheetPart.Worksheet = worksheet;

        foreach (var row in rows)
        {
            sheetData.Append(row);
        }

        if (!string.IsNullOrWhiteSpace(autoFilterReference))
        {
            worksheet.Append(new AutoFilter { Reference = autoFilterReference });
        }

        worksheet.Save();
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = sheetId,
            Name = name
        });
    }

    private static IEnumerable<Row> BuildSummaryRows(CommercialStatisticsExportReport report)
    {
        yield return TextRow("FlowStock — коммерческая статистика");
        yield return EmptyRow();

        foreach (var (name, value) in report.Selection.Criteria)
        {
            yield return RowOf(TextCell(name), TextCell(value));
        }

        yield return EmptyRow();
        yield return TextRow("Итоги");
        yield return RowOf(TextCell("Показатель"), TextCell("Значение"));
        foreach (var (label, value) in AmountRows(report.Summary))
        {
            yield return RowOf(TextCell(label), value);
        }

        yield return EmptyRow();
        yield return TextRow("Качество данных");
        yield return RowOf(
            TextCell("Финансовые snapshots полные"),
            TextCell(report.DataQuality.IsFinanciallyComplete ? "Да" : "Нет"));
        yield return RowOf(TextCell("Описание"), TextCell(report.DataQualityText));
        yield return RowOf(TextCell("Без цены — фактов"), NumberCell(report.DataQuality.MissingPriceFactCount));
        yield return RowOf(TextCell("Без цены — количество"), NumberCell(report.DataQuality.MissingPriceQuantity));
        yield return RowOf(TextCell("Без НДС — фактов"), NumberCell(report.DataQuality.MissingVatFactCount));
        yield return RowOf(TextCell("Без НДС — количество"), NumberCell(report.DataQuality.MissingVatQuantity));
        yield return RowOf(TextCell("Неполные — фактов"), NumberCell(report.DataQuality.FinanciallyIncompleteFactCount));
        yield return RowOf(TextCell("Неполные — количество"), NumberCell(report.DataQuality.FinanciallyIncompleteQuantity));
        yield return RowOf(TextCell("Непривязанные продажи — фактов"), NumberCell(report.DataQuality.UnlinkedSalesFactCount));
        yield return RowOf(TextCell("Непривязанные продажи — количество"), NumberCell(report.DataQuality.UnlinkedSalesQuantity));
        yield return RowOf(TextCell("Несовпадения товара — фактов"), NumberCell(report.DataQuality.ItemMismatchSalesFactCount));
        yield return RowOf(TextCell("Несовпадения товара — количество"), NumberCell(report.DataQuality.ItemMismatchSalesQuantity));
    }

    private static IEnumerable<Row> BuildMonthlyRows(CommercialStatisticsExportReport report)
    {
        yield return AmountHeaderRow("Месяц");
        foreach (var month in report.Monthly)
        {
            yield return AmountDataRow(month.Month, month.Amounts);
        }
    }

    private static IEnumerable<Row> BuildDetailRows(CommercialStatisticsExportReport report)
    {
        yield return AmountHeaderRow("Группа");
        foreach (var group in report.Groups)
        {
            yield return AmountDataRow(group.Label, group.Amounts);
        }
    }

    private static Row AmountHeaderRow(string firstColumn) =>
        RowOf(
            TextCell(firstColumn),
            TextCell("Заказы"),
            TextCell("Документы"),
            TextCell("Факты"),
            TextCell("Количество"),
            TextCell("Количество с фин. snapshots"),
            TextCell("С НДС"),
            TextCell("Без НДС"),
            TextCell("НДС"));

    private static Row AmountDataRow(string label, WpfCommercialStatisticsAmounts amounts) =>
        RowOf(
            TextCell(label),
            NumberCell(amounts.OrderCount),
            NumberCell(amounts.DocumentCount),
            NumberCell(amounts.FactCount),
            NumberCell(amounts.Quantity),
            NumberCell(amounts.KnownFinancialQuantity),
            NumberCell(amounts.Gross),
            NumberCell(amounts.Net),
            NumberCell(amounts.Vat));

    private static IEnumerable<(string Label, Cell Value)> AmountRows(
        WpfCommercialStatisticsAmounts amounts)
    {
        yield return ("Заказы", NumberCell(amounts.OrderCount));
        yield return ("Документы", NumberCell(amounts.DocumentCount));
        yield return ("Факты", NumberCell(amounts.FactCount));
        yield return ("Количество", NumberCell(amounts.Quantity));
        yield return ("Количество с фин. snapshots", NumberCell(amounts.KnownFinancialQuantity));
        yield return ("С НДС", NumberCell(amounts.Gross));
        yield return ("Без НДС", NumberCell(amounts.Net));
        yield return ("НДС", NumberCell(amounts.Vat));
    }

    private static Row EmptyRow() => new();

    private static Row TextRow(string value) => RowOf(TextCell(value));

    private static Row RowOf(params Cell[] cells)
    {
        var row = new Row();
        row.Append(cells);
        return row;
    }

    private static Cell TextCell(string? value) =>
        new()
        {
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(value ?? string.Empty))
        };

    private static Cell NumberCell(int value) => NumberCell((decimal)value);

    private static Cell NumberCell(decimal value) =>
        new()
        {
            DataType = CellValues.Number,
            CellValue = new CellValue(value.ToString(CultureInfo.InvariantCulture))
        };
}
