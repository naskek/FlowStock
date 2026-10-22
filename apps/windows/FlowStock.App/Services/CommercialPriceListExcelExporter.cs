using System.Globalization;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace FlowStock.App;

internal static class CommercialPriceListExcelExporter
{
    public static byte[] Create(WpfCommercialPriceListReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var stream = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            var wb = doc.AddWorkbookPart();
            wb.Workbook = new Workbook();
            var sheet = wb.AddNewPart<WorksheetPart>();
            var data = new SheetData();
            var worksheet = new Worksheet(data);
            sheet.Worksheet = worksheet;
            data.Append(MakeRow(Columns.Select(Text).ToArray()));

            foreach (var item in report.Items)
            {
                data.Append(MakeRow(
                    Text(item.RowKindDisplay),
                    Number(item.PartnerId),
                    Text(item.PartnerName),
                    Text(item.PartnerCode),
                    Number(item.ItemId),
                    Text(item.ItemName),
                    Text(item.Gtin), // Must stay text (GTIN leading zeros).
                    Text(item.Brand),
                    Text(item.Volume),
                    Number(item.DefaultPriceGross),
                    Number(item.PartnerPriceGross),
                    Number(item.EffectivePriceGross),
                    Text(item.PriceSource),
                    Number(item.VatRate),
                    Text(item.VatRateName),
                    Text(item.ItemIsActive ? "Да" : "Нет"),
                    Text(item.PartnerPriceIsActive switch
                    {
                        true => "Да", false => "Нет", null => ""
                    })));
            }

            worksheet.Append(new AutoFilter
            {
                Reference = $"A1:Q{Math.Max(1, report.Items.Count + 1)}"
            });
            worksheet.Save();
            wb.Workbook.Append(new Sheets(new Sheet
            {
                Id = wb.GetIdOfPart(sheet),
                SheetId = 1,
                Name = "Прайс-лист"
            }));
            wb.Workbook.Save();
        }
        return stream.ToArray();
    }

    private static readonly string[] Columns =
    [
        "Тип строки", "ID клиента", "Клиент", "Код клиента", "ID товара",
        "Товар", "GTIN", "Бренд", "Фасовка",
        "Базовая цена с НДС", "Цена клиента с НДС", "Итоговая цена с НДС",
        "Источник цены", "НДС, %", "Ставка НДС",
        "Товар активен", "Цена клиента активна"
    ];

    private static Row MakeRow(params Cell[] cells)
    {
        var row = new Row();
        row.Append(cells);
        return row;
    }

    private static Cell Text(string? value) => new()
    {
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(value ?? string.Empty))
    };

    private static Cell Number(long? value) =>
        value.HasValue ? Number((decimal)value.Value) : new Cell();

    private static Cell Number(decimal? value) =>
        value.HasValue ? Number(value.Value) : new Cell();

    private static Cell Number(decimal value) => new()
    {
        DataType = CellValues.Number,
        CellValue = new CellValue(value.ToString(CultureInfo.InvariantCulture))
    };
}
