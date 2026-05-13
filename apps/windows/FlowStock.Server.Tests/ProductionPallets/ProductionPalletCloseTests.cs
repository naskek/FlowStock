using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class ProductionPalletCloseTests
{
    [Fact]
    public void CloseProductionReceipt_WithUnfilledProductionPallets_Fails()
    {
        var harness = CreateHarness();
        harness.SeedProductionPallet(BuildPallet(ProductionPalletStatus.Planned));

        var result = harness.CreateService().TryCloseDoc(20, allowNegative: false);

        Assert.False(result.Success);
        Assert.Contains("Нельзя закрыть выпуск: есть ненаполненные паллеты", result.Errors);
        Assert.Empty(harness.LedgerEntries);
        Assert.Equal(DocStatus.Draft, harness.GetDoc(20).Status);
    }

    [Fact]
    public void CloseProductionReceipt_WithFilledProductionPallets_DoesNotDuplicateLedger()
    {
        var harness = CreateHarness();
        harness.SeedProductionPallet(BuildPallet(ProductionPalletStatus.Planned));
        var palletService = new ProductionPalletService(harness.Store);
        var fill = palletService.Fill("HU-000001", "TSD-01");
        Assert.True(fill.Success);
        Assert.Single(harness.LedgerEntries);

        var close = harness.CreateService().TryCloseDoc(20, allowNegative: false);

        Assert.True(close.Success);
        Assert.Single(harness.LedgerEntries);
        Assert.Equal(DocStatus.Closed, harness.GetDoc(20).Status);
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(10).Status);
    }

    [Fact]
    public void FillPallet_UsesPlannedHuOnly()
    {
        var harness = CreateTwoPalletHarness();
        var palletService = new ProductionPalletService(harness.Store);

        var fill = palletService.Fill("HU-PLAN-001", "TSD-01");

        Assert.True(fill.Success);
        var entry = Assert.Single(harness.LedgerEntries);
        Assert.Equal("HU-PLAN-001", entry.HuCode);
        Assert.Equal(600, entry.QtyDelta);
        Assert.DoesNotContain(harness.LedgerEntries, row => string.Equals(row.HuCode, "HU-PLAN-002", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CloseProductionReceipt_WithAllFilledProductionPallets_KeepsStockOnPlannedHusWithoutDuplicate()
    {
        var harness = CreateTwoPalletHarness();
        var palletService = new ProductionPalletService(harness.Store);
        Assert.True(palletService.Fill("HU-PLAN-001", "TSD-01").Success);
        Assert.True(palletService.Fill("HU-PLAN-002", "TSD-01").Success);
        Assert.Equal(2, harness.LedgerEntries.Count);

        var close = harness.CreateService().TryCloseDoc(20, allowNegative: false);

        Assert.True(close.Success);
        Assert.Equal(2, harness.LedgerEntries.Count);
        Assert.Equal(600, harness.LedgerEntries.Where(row => row.HuCode == "HU-PLAN-001").Sum(row => row.QtyDelta));
        Assert.Equal(600, harness.LedgerEntries.Where(row => row.HuCode == "HU-PLAN-002").Sum(row => row.QtyDelta));
        Assert.DoesNotContain(harness.LedgerEntries, row =>
            !string.Equals(row.HuCode, "HU-PLAN-001", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(row.HuCode, "HU-PLAN-002", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(DocStatus.Closed, harness.GetDoc(20).Status);
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(10).Status);
    }

    [Fact]
    public void AutoDistributeProductionReceiptHus_WithProductionPallets_DoesNotChangePlannedHus()
    {
        var harness = CreateTwoPalletHarness();
        var beforeLines = harness.GetDocLines(20).Select(line => line.ToHu).ToArray();
        var beforePallets = harness.Store.GetProductionPalletsByDoc(20).Select(pallet => pallet.HuCode).ToArray();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            harness.CreateService().AutoDistributeProductionReceiptHus(20));

        Assert.Equal("Для выпуска с планом паллет распределение HU выполняется через план паллет", ex.Message);
        Assert.Equal(beforeLines, harness.GetDocLines(20).Select(line => line.ToHu).ToArray());
        Assert.Equal(beforePallets, harness.Store.GetProductionPalletsByDoc(20).Select(pallet => pallet.HuCode).ToArray());
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void OrderReceiptRemaining_UsesFilledProductionPalletsWithoutDoubleCounting()
    {
        var harness = CreateTwoPalletHarness();
        var documentService = harness.CreateService();
        var palletService = new ProductionPalletService(harness.Store);

        var initial = Assert.Single(documentService.GetOrderReceiptRemaining(10));
        Assert.Equal(0, initial.QtyReceived);
        Assert.Equal(1200, initial.QtyRemaining);

        Assert.True(palletService.Fill("HU-PLAN-001", "TSD-01").Success);
        var partial = Assert.Single(documentService.GetOrderReceiptRemaining(10));
        Assert.Equal(600, partial.QtyReceived);
        Assert.Equal(600, partial.QtyRemaining);

        Assert.True(palletService.Fill("HU-PLAN-002", "TSD-01").Success);
        var fullBeforeClose = Assert.Single(documentService.GetOrderReceiptRemaining(10));
        Assert.Equal(1200, fullBeforeClose.QtyReceived);
        Assert.Equal(0, fullBeforeClose.QtyRemaining);

        var close = documentService.TryCloseDoc(20, allowNegative: false);

        Assert.True(close.Success);
        var fullAfterClose = Assert.Single(documentService.GetOrderReceiptRemaining(10));
        Assert.Equal(1200, fullAfterClose.QtyReceived);
        Assert.Equal(0, fullAfterClose.QtyRemaining);
        Assert.Equal(2, harness.LedgerEntries.Count);
    }

    private static CloseDocumentHarness CreateTwoPalletHarness()
    {
        var harness = CreateHarness(orderQty: 1200, firstHu: "HU-PLAN-001");
        harness.SeedLine(new DocLine
        {
            Id = 202,
            DocId = 20,
            OrderLineId = 101,
            ItemId = 100,
            Qty = 600,
            ToLocationId = 1,
            ToHu = "HU-PLAN-002"
        });
        harness.SeedProductionPallet(BuildPallet(
            id: 1,
            docLineId: 201,
            huCode: "HU-PLAN-001",
            status: ProductionPalletStatus.Planned));
        harness.SeedProductionPallet(BuildPallet(
            id: 2,
            docLineId: 202,
            huCode: "HU-PLAN-002",
            status: ProductionPalletStatus.Planned));
        return harness;
    }

    private static CloseDocumentHarness CreateHarness(double orderQty = 600, string firstHu = "HU-000001")
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item { Id = 100, Name = "Товар" });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "056",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 100,
            QtyOrdered = orderQty
        });
        harness.SeedDoc(new Doc
        {
            Id = 20,
            DocRef = "PRD-2026-000001",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 201,
            DocId = 20,
            OrderLineId = 101,
            ItemId = 100,
            Qty = 600,
            ToLocationId = 1,
            ToHu = firstHu
        });
        return harness;
    }

    private static ProductionPallet BuildPallet(string status)
    {
        return BuildPallet(id: 1, docLineId: 201, huCode: "HU-000001", status: status);
    }

    private static ProductionPallet BuildPallet(long id, long docLineId, string huCode, string status)
    {
        return new ProductionPallet
        {
            Id = id,
            PrdDocId = 20,
            DocLineId = docLineId,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 100,
            ItemName = "Товар",
            HuCode = huCode,
            PlannedQty = 600,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            Status = status,
            FilledAt = status == ProductionPalletStatus.Filled ? new DateTime(2026, 5, 13, 10, 0, 0) : null,
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        };
    }
}
