using FlowStock.App;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class PalletLabelPrintRowFactoryTests
{
    private static PalletLabelPrintRow BuildFullRow(
        string sourceType = "production_pallet",
        long palletId = 101,
        DateTime? productionDate = null,
        string batchNumber = "")
    {
        return new PalletLabelPrintRow
        {
            PalletId = palletId,
            OrderId = 55,
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
            ProductionDate = productionDate ?? new DateTime(2026, 1, 1),
            Comment = "комментарий",
            BatchNumber = batchNumber,
            IsMixedPallet = true,
            Composition = "состав",
            Line1ItemName = "Строка 1",
            Line1Qty = 10,
            Line2ItemName = "Строка 2",
            Line2Qty = 20,
            Line3ItemName = "Строка 3",
            Line3Qty = 30,
            Status = "PLANNED",
            SourceType = sourceType
        };
    }

    [Fact]
    public void ApplyPrintParameters_ReturnsNewInstances()
    {
        var rows = new[] { BuildFullRow() };

        var result = PalletLabelPrintRowFactory.ApplyPrintParameters(
            rows, new DateTime(2026, 6, 29), "ПАРТИЯ-2026-15");

        Assert.Single(result);
        Assert.NotSame(rows[0], result[0]);
    }

    [Fact]
    public void ApplyPrintParameters_DoesNotMutateSourceRows()
    {
        var source = BuildFullRow(productionDate: new DateTime(2026, 1, 1), batchNumber: "OLD");
        var rows = new[] { source };

        PalletLabelPrintRowFactory.ApplyPrintParameters(rows, new DateTime(2026, 6, 29), "NEW");

        Assert.Equal(new DateTime(2026, 1, 1), source.ProductionDate);
        Assert.Equal("OLD", source.BatchNumber);
    }

    [Fact]
    public void ApplyPrintParameters_PreservesAllOtherProperties()
    {
        var source = BuildFullRow();

        var copy = PalletLabelPrintRowFactory.ApplyPrintParameters(
            new[] { source }, new DateTime(2026, 6, 29), "ПАРТИЯ")[0];

        Assert.Equal(source.PalletId, copy.PalletId);
        Assert.Equal(source.OrderId, copy.OrderId);
        Assert.Equal(source.OrderRef, copy.OrderRef);
        Assert.Equal(source.ClientName, copy.ClientName);
        Assert.Equal(source.PrdRef, copy.PrdRef);
        Assert.Equal(source.HuCode, copy.HuCode);
        Assert.Equal(source.ItemName, copy.ItemName);
        Assert.Equal(source.Brand, copy.Brand);
        Assert.Equal(source.StorageConditions, copy.StorageConditions);
        Assert.Equal(source.Qty, copy.Qty);
        Assert.Equal(source.Uom, copy.Uom);
        Assert.Equal(source.PalletNo, copy.PalletNo);
        Assert.Equal(source.PalletCount, copy.PalletCount);
        Assert.Equal(source.StoragePlace, copy.StoragePlace);
        Assert.Equal(source.Comment, copy.Comment);
        Assert.Equal(source.IsMixedPallet, copy.IsMixedPallet);
        Assert.Equal(source.Composition, copy.Composition);
        Assert.Equal(source.Line1ItemName, copy.Line1ItemName);
        Assert.Equal(source.Line1Qty, copy.Line1Qty);
        Assert.Equal(source.Line2ItemName, copy.Line2ItemName);
        Assert.Equal(source.Line2Qty, copy.Line2Qty);
        Assert.Equal(source.Line3ItemName, copy.Line3ItemName);
        Assert.Equal(source.Line3Qty, copy.Line3Qty);
        Assert.Equal(source.Status, copy.Status);
        Assert.Equal(source.SourceType, copy.SourceType);
    }

    [Fact]
    public void ApplyPrintParameters_FilledValues_AppliedToAllRows()
    {
        var rows = new[]
        {
            BuildFullRow(sourceType: "production_pallet", palletId: 1),
            BuildFullRow(sourceType: "production_pallet", palletId: 2),
            BuildFullRow(sourceType: "reserved_hu", palletId: 3)
        };
        var date = new DateTime(2026, 6, 29);

        var result = PalletLabelPrintRowFactory.ApplyPrintParameters(rows, date, "ПАРТИЯ-2026-15");

        Assert.All(result, row =>
        {
            Assert.Equal(date, row.ProductionDate);
            Assert.Equal("ПАРТИЯ-2026-15", row.BatchNumber);
        });
    }

    [Fact]
    public void ApplyPrintParameters_EmptyValues_OverrideApiDate()
    {
        var rows = new[] { BuildFullRow(productionDate: new DateTime(2026, 1, 1), batchNumber: "OLD") };

        var result = PalletLabelPrintRowFactory.ApplyPrintParameters(rows, productionDate: null, batchNumber: null);

        Assert.Null(result[0].ProductionDate);
        Assert.Equal(string.Empty, result[0].BatchNumber);
        Assert.Equal(string.Empty, result[0].ToNamedSubStrings()["ProductionDate"]);
        Assert.Equal(string.Empty, result[0].ToNamedSubStrings()["BatchNumber"]);
    }

    [Fact]
    public void ApplyPrintParameters_BatchNumber_IsTrimmed()
    {
        var rows = new[] { BuildFullRow() };

        var result = PalletLabelPrintRowFactory.ApplyPrintParameters(rows, null, "  ПАРТИЯ  ");

        Assert.Equal("ПАРТИЯ", result[0].BatchNumber);
    }

    [Fact]
    public void ApplyPrintParameters_WhitespaceBatchNumber_BecomesEmpty()
    {
        var rows = new[] { BuildFullRow() };

        var result = PalletLabelPrintRowFactory.ApplyPrintParameters(rows, null, "   ");

        Assert.Equal(string.Empty, result[0].BatchNumber);
    }

    [Fact]
    public void ApplyPrintParameters_ProductionAndWarehouse_GetSameValues()
    {
        var rows = new[]
        {
            BuildFullRow(sourceType: "production_pallet", palletId: 10),
            BuildFullRow(sourceType: "reserved_hu", palletId: 20)
        };
        var date = new DateTime(2026, 6, 29);

        var result = PalletLabelPrintRowFactory.ApplyPrintParameters(rows, date, "ПАРТИЯ");

        Assert.Equal(date, result[0].ProductionDate);
        Assert.Equal(date, result[1].ProductionDate);
        Assert.Equal("ПАРТИЯ", result[0].BatchNumber);
        Assert.Equal("ПАРТИЯ", result[1].BatchNumber);

        // PalletId/SourceType сохранены — логика выбора и mark-printed не затронуты.
        Assert.Equal("production_pallet", result[0].SourceType);
        Assert.Equal(10, result[0].PalletId);
        Assert.Equal("reserved_hu", result[1].SourceType);
        Assert.Equal(20, result[1].PalletId);
    }
}
