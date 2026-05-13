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

    private static CloseDocumentHarness CreateHarness()
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
            QtyOrdered = 600
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
            ToHu = "HU-000001"
        });
        return harness;
    }

    private static ProductionPallet BuildPallet(string status)
    {
        return new ProductionPallet
        {
            Id = 1,
            PrdDocId = 20,
            DocLineId = 201,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 100,
            ItemName = "Товар",
            HuCode = "HU-000001",
            PlannedQty = 600,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            Status = status,
            FilledAt = status == ProductionPalletStatus.Filled ? new DateTime(2026, 5, 13, 10, 0, 0) : null,
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        };
    }
}
