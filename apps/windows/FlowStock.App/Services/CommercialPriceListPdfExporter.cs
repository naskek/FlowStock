using System.Globalization;
using System.IO;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;

namespace FlowStock.App;

internal static class CommercialPriceListPdfExporter
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static byte[] Create(WpfCommercialPriceListReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var document = new Document();
        document.Info.Title = "FlowStock — прайс-лист";
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = 7;

        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.Orientation = MigraDoc.DocumentObjectModel.Orientation.Landscape;
        section.PageSetup.TopMargin = Unit.FromCentimeter(1);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(0.8);
        section.PageSetup.RightMargin = Unit.FromCentimeter(0.8);

        var title = section.AddParagraph(report.Mode switch
        {
            "partner" => "FlowStock — прайс клиента",
            "item" => "FlowStock — цены по товару",
            _ => "FlowStock — аудит коммерческих цен"
        });
        title.Format.Font.Bold = true;
        title.Format.Font.Size = 15;
        title.Format.SpaceAfter = Unit.FromCentimeter(0.3);

        var info = report.Items.FirstOrDefault();
        if (report.Mode == "partner" && info != null)
            section.AddParagraph($"Клиент: {info.PartnerName} ({info.PartnerCode})");
        if (report.Mode == "item" && info != null)
            section.AddParagraph($"Товар: {info.ItemName} / GTIN {info.Gtin}");
        section.AddParagraph($"Строк: {report.TotalCount}. Цены указаны с НДС.");
        section.AddParagraph($"Сформировано: {DateTime.Now.ToString("dd.MM.yyyy HH:mm", Ru)}");
        section.AddParagraph().Format.SpaceAfter = Unit.FromCentimeter(0.2);

        var table = section.AddTable();
        table.Borders.Visible = true;
        foreach (var cm in new[] { 1.65, 3.65, 1.8, 4.8, 2.75, 1.9, 1.9, 1.9, 1.9, 2.0, 1.2 })
            table.AddColumn(Unit.FromCentimeter(cm));

        var header = table.AddRow();
        header.HeadingFormat = true;
        string[] labels =
        [
            "Тип", "Клиент", "Код", "Товар", "GTIN",
            "Фасовка", "Базовая", "Клиента", "Итоговая", "Источник", "НДС %"
        ];
        for (var i = 0; i < labels.Length; i++)
        {
            var cell = header.Cells[i].AddParagraph(labels[i]);
            cell.Format.Font.Bold = true;
        }

        foreach (var item in report.Items)
        {
            var row = table.AddRow();
            string[] values =
            [
                item.RowKindDisplay, item.PartnerName ?? "", item.PartnerCode ?? "",
                item.ItemName, item.Gtin ?? "", item.Volume ?? "",
                Money(item.DefaultPriceGross), Money(item.PartnerPriceGross),
                Money(item.EffectivePriceGross), item.PriceSourceDisplay,
                Money(item.VatRate)
            ];
            for (var i = 0; i < values.Length; i++)
                row.Cells[i].AddParagraph(values[i]);
        }

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, false);
        return stream.ToArray();
    }

    private static string Money(decimal? value) =>
        value.HasValue ? value.Value.ToString("0.####", Ru) : "—";
}
