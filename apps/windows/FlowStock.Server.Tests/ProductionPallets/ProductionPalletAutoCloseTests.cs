using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class ProductionPalletAutoCloseTests
{
    [Fact]
    public void FillPallet_WithAutoClose_WritesLedgerAndClosesDedicatedPrd()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = CreatePalletService(harness);
        var plan = service.PlanOrder(10);
        var pallets = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId);
        var first = pallets.OrderBy(p => p.HuCode, StringComparer.OrdinalIgnoreCase).First();

        var fill = service.Fill(first.HuCode, "TSD-01", orderId: 10);

        Assert.True(fill.Success);
        Assert.True(fill.PrdAutoClosed);
        Assert.NotNull(fill.ClosedPrdDocRef);
        Assert.Single(harness.LedgerEntries);
        Assert.Equal(DocStatus.Closed, harness.GetDoc(fill.ClosedPrdDocId!.Value).Status);
        Assert.Equal(OrderStatus.InProgress, harness.GetOrder(10).Status);
    }

    [Fact]
    public void RepeatedFill_IsIdempotent_NoDuplicateLedger()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 600, maxQtyPerHu: 600);
        var service = CreatePalletService(harness);
        var plan = service.PlanOrder(10);
        var hu = Assert.Single(harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)).HuCode;

        Assert.True(service.Fill(hu, "TSD-01", orderId: 10).Success);
        var second = service.Fill(hu, "TSD-01", orderId: 10);

        Assert.True(second.Success);
        Assert.True(second.AlreadyFilled);
        Assert.Single(harness.LedgerEntries);
    }

    [Fact]
    public void FillAllPallets_InternalOrderBecomesShipped()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = CreatePalletService(harness);
        var plan = service.PlanOrder(10);
        foreach (var pallet in harness.Store.GetProductionPalletsByDoc(plan.PrdDocId))
        {
            Assert.True(service.Fill(pallet.HuCode, "TSD-01", orderId: 10).Success);
        }

        Assert.Equal(2, harness.LedgerEntries.Count);
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(10).Status);
    }

    private static ProductionPalletService CreatePalletService(CloseDocumentHarness harness)
    {
        var documents = harness.CreateService();
        var options = new FlowStockLedgerFlowOptions { ProductionAutoCloseOnFill = true };
        var fillClose = new ProductionFillCloseService(harness.Store, documents, options);
        return new ProductionPalletService(harness.Store, fillClose);
    }

    private static CloseDocumentHarness CreateHarnessWithOrderOnly(double orderQty, double maxQtyPerHu)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Товар",
            Brand = "Печагин",
            BaseUom = "шт",
            MaxQtyPerHu = maxQtyPerHu
        });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "056",
            Type = OrderType.Internal,
            PartnerName = "ПЕЧАГИН ПРОДУКТ",
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
        return harness;
    }
}
