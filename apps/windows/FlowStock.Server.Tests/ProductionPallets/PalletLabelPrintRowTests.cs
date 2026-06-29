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
            ClientName = "ПЕЧАГИН ПРОДУКТ",
            PrdRef = "PRD-2026-000142",
            HuCode = "HU-0000001",
            ItemName = "Горчица Русская 1 кг",
            Brand = "Печагин",
            StorageConditions = "от 0С до +10С",
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
        Assert.Equal("ПЕЧАГИН ПРОДУКТ", fields["ClientName"]);
        Assert.Equal("PRD-2026-000142", fields["PrdRef"]);
        Assert.Equal("HU-0000001", fields["HuCode"]);
        Assert.Equal("Горчица Русская 1 кг", fields["ItemName"]);
        Assert.Equal("Печагин", fields["Brand"]);
        Assert.Equal("от 0С до +10С", fields["StorageConditions"]);
        Assert.Equal("600", fields["Qty"]);
        Assert.Equal("шт", fields["Uom"]);
        Assert.Equal("2", fields["PalletNo"]);
        Assert.Equal("6", fields["PalletCount"]);
        Assert.Equal("Производство", fields["StoragePlace"]);
        Assert.Equal("13.05.2026", fields["ProductionDate"]);
        Assert.Equal(string.Empty, fields["Comment"]);
    }

    [Fact]
    public void ToNamedSubStrings_ContainsBatchNumber()
    {
        var row = new PalletLabelPrintRow
        {
            HuCode = "HU-0000001",
            ItemName = "Товар",
            Qty = 1,
            BatchNumber = "ПАРТИЯ-2026-15"
        };

        var fields = row.ToNamedSubStrings();

        Assert.True(fields.ContainsKey("BatchNumber"));
        Assert.Equal("ПАРТИЯ-2026-15", fields["BatchNumber"]);
    }

    [Fact]
    public void ToNamedSubStrings_ContainsStorageConditionsAndPreservesText()
    {
        var storageConditions = "Хранить при температуре от 0С до +10С\r\nи относительной влажности не более 75%";
        var row = new PalletLabelPrintRow
        {
            HuCode = "HU-0000001",
            ItemName = "Товар",
            Qty = 1,
            StorageConditions = storageConditions
        };

        var fields = row.ToNamedSubStrings();

        Assert.True(fields.ContainsKey("StorageConditions"));
        Assert.Equal(storageConditions, fields["StorageConditions"]);
    }

    [Fact]
    public void ToNamedSubStrings_EmptyBatchNumber_IsEmptyString()
    {
        var row = new PalletLabelPrintRow
        {
            HuCode = "HU-0000001",
            ItemName = "Товар",
            Qty = 1
        };

        var fields = row.ToNamedSubStrings();

        Assert.True(fields.ContainsKey("BatchNumber"));
        Assert.Equal(string.Empty, fields["BatchNumber"]);
    }

    [Fact]
    public void ToNamedSubStrings_FormatsProductionDateAsDayMonthYear()
    {
        var row = new PalletLabelPrintRow
        {
            HuCode = "HU-0000001",
            ItemName = "Товар",
            Qty = 1,
            ProductionDate = new DateTime(2026, 6, 29)
        };

        var fields = row.ToNamedSubStrings();

        Assert.Equal("29.06.2026", fields["ProductionDate"]);
    }

    [Fact]
    public void ToNamedSubStrings_NullProductionDate_IsEmptyString()
    {
        var row = new PalletLabelPrintRow
        {
            HuCode = "HU-0000001",
            ItemName = "Товар",
            Qty = 1,
            ProductionDate = null
        };

        var fields = row.ToNamedSubStrings();

        Assert.Equal(string.Empty, fields["ProductionDate"]);
    }

    [Fact]
    public void ToNamedSubStrings_ContainsClientName()
    {
        var row = new PalletLabelPrintRow
        {
            ClientName = "ПЕЧАГИН ПРОДУКТ"
        };

        var fields = row.ToNamedSubStrings();

        Assert.True(fields.ContainsKey("ClientName"));
        Assert.Equal("ПЕЧАГИН ПРОДУКТ", fields["ClientName"]);
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
