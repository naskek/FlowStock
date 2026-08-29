using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class ProductionPalletLabelFingerprintTests
{
    [Fact]
    public void Compute_ChangesForEveryServerOwnedBarTenderPayloadField()
    {
        var source = new LabelSpec();
        var baseline = ProductionPalletLabelFingerprint.Compute(BuildRow(source));
        var variants = new[]
        {
            source with { HuCode = "HU-2" },
            source with { ItemName = "Товар 2" },
            source with { Qty = 350 },
            source with { OrderRef = "002" },
            source with { ClientName = "Другой клиент" },
            source with { PrdRef = "PRD-2" },
            source with { Brand = "Другой бренд" },
            source with { StorageConditions = "+2..+6" },
            source with { Uom = "кг" },
            source with { PalletNo = 2 },
            source with { PalletCount = 2 },
            source with { StoragePlace = "DOCK" },
            source with { Comment = "Другой комментарий" },
            source with { IsMixedPallet = true },
            source with { Composition = "Другой состав" },
            source with { Line1ItemName = "Компонент 1 изменён" },
            source with { Line1Qty = 11 },
            source with { Line2ItemName = "Компонент 2 изменён" },
            source with { Line2Qty = 21 },
            source with { Line3ItemName = "Компонент 3 изменён" },
            source with { Line3Qty = 31 }
        };

        Assert.All(variants, variant =>
            Assert.NotEqual(baseline, ProductionPalletLabelFingerprint.Compute(BuildRow(variant))));
    }

    [Fact]
    public void Compute_DoesNotIncludePrintSessionProductionDate()
    {
        var source = new LabelSpec();
        var first = BuildRow(source with { ProductionDate = new DateTime(2026, 8, 1) });
        var second = BuildRow(source with { ProductionDate = new DateTime(2026, 8, 28) });

        Assert.Equal(
            ProductionPalletLabelFingerprint.Compute(first),
            ProductionPalletLabelFingerprint.Compute(second));
    }

    private static ProductionPalletPrintRow BuildRow(LabelSpec spec) => new()
    {
        HuCode = spec.HuCode,
        ItemName = spec.ItemName,
        Qty = spec.Qty,
        OrderRef = spec.OrderRef,
        ClientName = spec.ClientName,
        PrdRef = spec.PrdRef,
        Brand = spec.Brand,
        StorageConditions = spec.StorageConditions,
        Uom = spec.Uom,
        PalletNo = spec.PalletNo,
        PalletCount = spec.PalletCount,
        StoragePlace = spec.StoragePlace,
        ProductionDate = spec.ProductionDate,
        Comment = spec.Comment,
        IsMixedPallet = spec.IsMixedPallet,
        Composition = spec.Composition,
        Lines =
        [
            new ProductionPalletPrintLine { ItemName = spec.Line1ItemName, Qty = spec.Line1Qty },
            new ProductionPalletPrintLine { ItemName = spec.Line2ItemName, Qty = spec.Line2Qty },
            new ProductionPalletPrintLine { ItemName = spec.Line3ItemName, Qty = spec.Line3Qty }
        ]
    };

    private sealed record LabelSpec
    {
        public string HuCode { get; init; } = "HU-1";
        public string ItemName { get; init; } = "Товар";
        public double Qty { get; init; } = 378;
        public string OrderRef { get; init; } = "001";
        public string ClientName { get; init; } = "Клиент";
        public string PrdRef { get; init; } = "PRD-1";
        public string Brand { get; init; } = "Бренд";
        public string StorageConditions { get; init; } = "0..+10";
        public string Uom { get; init; } = "шт";
        public int PalletNo { get; init; } = 1;
        public int PalletCount { get; init; } = 1;
        public string StoragePlace { get; init; } = "MAIN";
        public DateTime? ProductionDate { get; init; } = new DateTime(2026, 8, 1);
        public string Comment { get; init; } = "Комментарий";
        public bool IsMixedPallet { get; init; }
        public string Composition { get; init; } = "Состав";
        public string Line1ItemName { get; init; } = "Компонент 1";
        public double Line1Qty { get; init; } = 10;
        public string Line2ItemName { get; init; } = "Компонент 2";
        public double Line2Qty { get; init; } = 20;
        public string Line3ItemName { get; init; } = "Компонент 3";
        public double Line3Qty { get; init; } = 30;
    }
}
