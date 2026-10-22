using FlowStock.App;
using System.IO.Compression;
using System.Text;

namespace FlowStock.Server.Tests.Wpf;

public sealed class CommercialPriceListExportTests
{
    [Fact]
    public void Excel_report_has_numeric_prices_and_gtin_preserves_leading_zero()
    {
        var report = ExampleReport();
        var bytes = CommercialPriceListExcelExporter.Create(report);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var xml = Read(zip, "xl/worksheets/sheet1.xml");

        Assert.Contains("Базовая цена с НДС", xml, StringComparison.Ordinal);
        Assert.Contains("Итоговая цена с НДС", xml, StringComparison.Ordinal);
        Assert.Contains("04620001500840", xml, StringComparison.Ordinal);
        Assert.Contains("PARTNER_ITEM", xml, StringComparison.Ordinal);
        Assert.Contains(">123.45<", xml, StringComparison.Ordinal);
        Assert.Contains(">0<", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", xml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("partner")]
    [InlineData("item")]
    public void Pdf_is_available_in_every_view(string mode)
    {
        var report = ExampleReport();
        report.Mode = mode;

        var bytes = CommercialPriceListPdfExporter.Create(report);

        Assert.True(bytes.Length > 1_000);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
    }

    [Fact]
    public void Pricing_ui_uses_server_report_and_same_loaded_snapshot_for_export()
    {
        var root = LocateRepositoryRoot();
        var priceWindow = File.ReadAllText(Path.Combine(root, "apps", "windows",
            "FlowStock.App", "CommercialPriceListWindow.xaml.cs"));
        var server = File.ReadAllText(Path.Combine(root, "apps", "windows",
            "FlowStock.Server", "CommercialPriceListReader.cs"));
        var endpoint = File.ReadAllText(Path.Combine(root, "apps", "windows",
            "FlowStock.Server", "CommercialPriceListEndpoint.cs"));
        var entry = File.ReadAllText(Path.Combine(root, "apps", "windows",
            "FlowStock.App", "PartnerItemSalePriceWindow.xaml"));
        Assert.Contains("GetPriceListAsync(query)", priceWindow, StringComparison.Ordinal);
        Assert.Contains("_snapshot = result", priceWindow, StringComparison.Ordinal);
        Assert.Contains("CommercialPriceListExcelExporter.Create(report)", priceWindow, StringComparison.Ordinal);
        Assert.Contains("CommercialPriceListPdfExporter.Create(report)", priceWindow, StringComparison.Ordinal);
        Assert.Contains("PartnerItemSalePriceWindow", entry.Replace("Прайс-листы…", "PartnerItemSalePriceWindow"),
            StringComparison.Ordinal); // Window remains the original entry surface.
        Assert.Contains("Прайс-листы…", entry, StringComparison.Ordinal);
        Assert.Contains("RequireManageCatalog(request)", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("order_lines", server, StringComparison.OrdinalIgnoreCase);
    }

    private static WpfCommercialPriceListReport ExampleReport() => new()
    {
        Mode = "partner",
        TotalCount = 1,
        Items =
        [
            new WpfCommercialPriceListRow
            {
                RowKind = "PARTNER",
                PartnerId = 5,
                PartnerName = "Клиент А",
                PartnerCode = "123456",
                ItemId = 17,
                ItemName = "Хрен столовый",
                Gtin = "04620001500840",
                Brand = "Русские закуски",
                Volume = "200 г",
                DefaultPriceGross = 123.45m,
                PartnerPriceGross = 0m,
                EffectivePriceGross = 0m,
                PriceSource = "PARTNER_ITEM",
                VatRate = 22m,
                VatRateName = "НДС",
                ItemIsActive = true,
                PartnerPriceIsActive = true
            }
        ]
    };

    private static string Read(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException(name);
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string LocateRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(current.FullName, "apps", "windows")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root missing.");
    }
}
