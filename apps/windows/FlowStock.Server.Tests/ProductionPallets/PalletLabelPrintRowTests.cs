using FlowStock.App;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class PalletLabelPrintRowTests
{
    [Fact]
    public void ToNamedSubStrings_MapsBarTenderFields()
    {
        var row = new PalletLabelPrintRow
        {
            OrderRef = "056",
            PrdRef = "PRD-2026-000142",
            HuCode = "HU-0000001",
            ItemName = "Горчица Русская 1 кг",
            Brand = "Печагин",
            Qty = 600,
            Uom = "шт",
            PalletNo = 2,
            PalletCount = 6,
            StoragePlace = "Производство",
            ProductionDate = new DateTime(2026, 5, 13),
            Comment = string.Empty
        };

        var fields = row.ToNamedSubStrings();

        Assert.Equal("056", fields["OrderRef"]);
        Assert.Equal("PRD-2026-000142", fields["PrdRef"]);
        Assert.Equal("HU-0000001", fields["HuCode"]);
        Assert.Equal("Горчица Русская 1 кг", fields["ItemName"]);
        Assert.Equal("Печагин", fields["Brand"]);
        Assert.Equal("600", fields["Qty"]);
        Assert.Equal("шт", fields["Uom"]);
        Assert.Equal("2", fields["PalletNo"]);
        Assert.Equal("6", fields["PalletCount"]);
        Assert.Equal("Производство", fields["StoragePlace"]);
        Assert.Equal("2026-05-13", fields["ProductionDate"]);
        Assert.Equal(string.Empty, fields["Comment"]);
    }

    [Fact]
    public void ToNamedSubStrings_DoesNotGenerateHu()
    {
        var row = new PalletLabelPrintRow
        {
            HuCode = "HU-0000042",
            ItemName = "Товар",
            Qty = 1
        };

        var fields = row.ToNamedSubStrings();

        Assert.Equal("HU-0000042", fields["HuCode"]);
    }
}
