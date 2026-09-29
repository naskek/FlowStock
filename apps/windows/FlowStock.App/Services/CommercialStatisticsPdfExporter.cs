using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

namespace FlowStock.App;

internal static class CommercialStatisticsPdfExporter
{
    private static readonly CultureInfo RussianCulture = CultureInfo.GetCultureInfo("ru-RU");

    public static byte[] Create(CommercialStatisticsExportReport report)
    {
        var document = BuildDocument(report);
        var renderer = new PdfDocumentRenderer
        {
            Document = document
        };
        renderer.RenderDocument();

        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    private static Document BuildDocument(CommercialStatisticsExportReport report)
    {
        var document = new Document();
        document.Info.Title = "FlowStock — коммерческая статистика";
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = 8;

        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.Orientation = MigraDoc.DocumentObjectModel.Orientation.Landscape;
        section.PageSetup.TopMargin = Unit.FromCentimeter(1);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(1);
        section.PageSetup.RightMargin = Unit.FromCentimeter(1);

        var title = section.AddParagraph("FlowStock — коммерческая статистика");
        title.Format.Font.Size = 16;
        title.Format.Font.Bold = true;
        title.Format.SpaceAfter = Unit.FromCentimeter(0.4);

        AddCriteria(section, report.Selection);
        AddHeading(section, "Итоги");
        AddSummaryTable(section, report.Summary);

        AddHeading(section, "Качество данных");
        var quality = section.AddParagraph(report.DataQualityText);
        quality.Format.SpaceAfter = Unit.FromCentimeter(0.25);

        AddHeading(section, "Помесячная сводка всего периода");
        AddAmountsTable(
            section,
            "Месяц",
            report.Monthly.Select(row => (row.Month, row.Amounts)));

        AddHeading(section, $"Детализация — {report.Selection.DetailLabel}");
        AddAmountsTable(
            section,
            "Группа",
            report.Groups.Select(row => (row.Label, row.Amounts)));

        return document;
    }

    private static void AddCriteria(Section section, CommercialStatisticsExportSelection selection)
    {
        var table = section.AddTable();
        table.Borders.Visible = true;
        table.AddColumn(Unit.FromCentimeter(4));
        table.AddColumn(Unit.FromCentimeter(22));

        foreach (var (name, value) in selection.Criteria)
        {
            var row = table.AddRow();
            row.Cells[0].AddParagraph(name);
            row.Cells[0].Format.Font.Bold = true;
            row.Cells[1].AddParagraph(value);
        }

        table.Format.SpaceAfter = Unit.FromCentimeter(0.35);
    }

    private static void AddSummaryTable(Section section, WpfCommercialStatisticsAmounts amounts)
    {
        var table = section.AddTable();
        table.Borders.Visible = true;
        table.AddColumn(Unit.FromCentimeter(8));
        table.AddColumn(Unit.FromCentimeter(4));

        foreach (var (label, value) in AmountRows(amounts))
        {
            var row = table.AddRow();
            row.Cells[0].AddParagraph(label);
            row.Cells[1].AddParagraph(value);
        }

        table.Format.SpaceAfter = Unit.FromCentimeter(0.35);
    }

    private static void AddAmountsTable(
        Section section,
        string firstHeader,
        IEnumerable<(string Label, WpfCommercialStatisticsAmounts Amounts)> rows)
    {
        var table = section.AddTable();
        table.Borders.Visible = true;
        table.AddColumn(Unit.FromCentimeter(5.4));
        table.AddColumn(Unit.FromCentimeter(1.4));
        table.AddColumn(Unit.FromCentimeter(1.6));
        table.AddColumn(Unit.FromCentimeter(1.4));
        table.AddColumn(Unit.FromCentimeter(2.2));
        table.AddColumn(Unit.FromCentimeter(2.5));
        table.AddColumn(Unit.FromCentimeter(2.2));
        table.AddColumn(Unit.FromCentimeter(2.2));
        table.AddColumn(Unit.FromCentimeter(2.2));

        var header = table.AddRow();
        header.HeadingFormat = true;
        header.Format.Font.Bold = true;
        var headers = new[]
        {
            firstHeader,
            "Заказы",
            "Документы",
            "Факты",
            "Количество",
            "С фин. snapshots",
            "С НДС",
            "Без НДС",
            "НДС"
        };
        for (var index = 0; index < headers.Length; index++)
        {
            header.Cells[index].AddParagraph(headers[index]);
        }

        foreach (var (label, amounts) in rows)
        {
            var row = table.AddRow();
            var values = new[]
            {
                label,
                amounts.OrderCount.ToString(RussianCulture),
                amounts.DocumentCount.ToString(RussianCulture),
                amounts.FactCount.ToString(RussianCulture),
                FormatQuantity(amounts.Quantity),
                FormatQuantity(amounts.KnownFinancialQuantity),
                FormatMoney(amounts.Gross),
                FormatMoney(amounts.Net),
                FormatMoney(amounts.Vat)
            };
            for (var index = 0; index < values.Length; index++)
            {
                row.Cells[index].AddParagraph(values[index]);
            }
        }

        table.Format.SpaceAfter = Unit.FromCentimeter(0.35);
    }

    private static IEnumerable<(string Label, string Value)> AmountRows(
        WpfCommercialStatisticsAmounts amounts)
    {
        yield return ("Заказы", amounts.OrderCount.ToString(RussianCulture));
        yield return ("Документы", amounts.DocumentCount.ToString(RussianCulture));
        yield return ("Факты", amounts.FactCount.ToString(RussianCulture));
        yield return ("Количество", FormatQuantity(amounts.Quantity));
        yield return ("Количество с фин. snapshots", FormatQuantity(amounts.KnownFinancialQuantity));
        yield return ("С НДС", FormatMoney(amounts.Gross));
        yield return ("Без НДС", FormatMoney(amounts.Net));
        yield return ("НДС", FormatMoney(amounts.Vat));
    }

    private static void AddHeading(Section section, string text)
    {
        var paragraph = section.AddParagraph(text);
        paragraph.Format.Font.Size = 11;
        paragraph.Format.Font.Bold = true;
        paragraph.Format.SpaceBefore = Unit.FromCentimeter(0.2);
        paragraph.Format.SpaceAfter = Unit.FromCentimeter(0.15);
        paragraph.Format.KeepWithNext = true;
    }

    private static string FormatQuantity(decimal value) =>
        value.ToString("0.######", RussianCulture);

    private static string FormatMoney(decimal value) =>
        value.ToString("N2", RussianCulture);
}
