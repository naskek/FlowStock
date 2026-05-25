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
    public void FillPallet_IsIdempotent_WhenRequestUsesOriginalPlanningPrdAfterPalletMovedToClosedPrd()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = CreatePalletService(harness);
        var plan = service.PlanOrder(10);
        var first = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)
            .OrderBy(pallet => pallet.Id)
            .First();

        var fill = service.Fill(first.HuCode, "TSD-01", orderId: 10, prdDocId: plan.PrdDocId);
        var closedPrdDocId = fill.ClosedPrdDocId!.Value;
        var prdCountBeforeRepeat = harness.Store.GetDocsByOrder(10)
            .Count(doc => doc.Type == DocType.ProductionReceipt);

        var repeat = service.Fill(first.HuCode, "TSD-01", orderId: 10, prdDocId: plan.PrdDocId);
        var huLedger = harness.LedgerEntries
            .Where(entry => string.Equals(entry.HuCode, first.HuCode, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(fill.Success);
        Assert.True(fill.PrdAutoClosed);
        Assert.NotEqual(plan.PrdDocId, closedPrdDocId);
        Assert.True(repeat.Success);
        Assert.True(repeat.AlreadyFilled);
        Assert.True(repeat.PrdAutoClosed);
        Assert.Equal(closedPrdDocId, repeat.ClosedPrdDocId);
        Assert.Equal(closedPrdDocId, repeat.Pallet?.PrdDocId);
        Assert.Single(huLedger);
        Assert.Equal(first.PlannedQty, huLedger[0].QtyDelta);
        Assert.Equal(prdCountBeforeRepeat, harness.Store.GetDocsByOrder(10).Count(doc => doc.Type == DocType.ProductionReceipt));
    }

    [Fact]
    public void RepeatFill_WithCurrentClosedPrd_ReturnsAlreadyFilledAndDoesNotDuplicateLedger()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 600, maxQtyPerHu: 600);
        var service = CreatePalletService(harness);
        var plan = service.PlanOrder(10);
        var hu = Assert.Single(harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)).HuCode;
        var fill = service.Fill(hu, "TSD-01", orderId: 10, prdDocId: plan.PrdDocId);
        var closedPrdDocId = fill.ClosedPrdDocId!.Value;

        var repeat = service.Fill(hu, "TSD-01", orderId: 10, prdDocId: closedPrdDocId);

        Assert.True(fill.Success);
        Assert.True(repeat.Success);
        Assert.True(repeat.AlreadyFilled);
        Assert.True(repeat.PrdAutoClosed);
        Assert.Equal(closedPrdDocId, repeat.ClosedPrdDocId);
        Assert.Single(harness.LedgerEntries.Where(entry =>
            string.Equals(entry.HuCode, hu, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void GetFillingContext_IncludesFilledPalletMovedToClosedPrdInOrderProgress()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = CreatePalletService(harness);
        var plan = service.PlanOrder(10);
        var first = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)
            .OrderBy(pallet => pallet.Id)
            .First();
        var fill = service.Fill(first.HuCode, "TSD-01", orderId: 10, prdDocId: plan.PrdDocId);
        var closedPrdDocId = fill.ClosedPrdDocId!.Value;

        var context = service.GetFillingContext(10);
        var line = Assert.Single(context.Document.Lines);
        var filledPallet = Assert.Single(context.Document.Pallets.Where(pallet =>
            string.Equals(pallet.HuCode, first.HuCode, StringComparison.OrdinalIgnoreCase)));

        Assert.Equal(plan.PrdDocId, context.PrdDocId);
        Assert.Equal(2, context.Document.Summary.PlannedPalletCount);
        Assert.Equal(1200, context.Document.Summary.PlannedQty);
        Assert.Equal(1, context.Document.Summary.FilledPalletCount);
        Assert.Equal(first.PlannedQty, context.Document.Summary.FilledQty);
        Assert.Equal(1, context.Document.Summary.RemainingPalletCount);
        Assert.Equal(600, context.Document.Summary.RemainingQty);
        Assert.Equal(101, line.OrderLineId);
        Assert.Equal(1200, line.OrderedQty);
        Assert.Equal(2, line.PlannedPalletCount);
        Assert.Equal(1200, line.PlannedQty);
        Assert.Equal(1, line.FilledPalletCount);
        Assert.Equal(first.PlannedQty, line.FilledQty);
        Assert.Equal(1, line.RemainingPalletCount);
        Assert.Equal(600, line.RemainingQty);
        Assert.Equal(2, context.Document.Pallets.Count);
        Assert.Equal(ProductionPalletStatus.Filled, filledPallet.Status);
        Assert.Equal(closedPrdDocId, filledPallet.PrdDocId);
        Assert.Contains(context.Document.Pallets, pallet =>
            pallet.PrdDocId == plan.PrdDocId
            && string.Equals(pallet.Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FillPallet_RejectsHuFromAnotherOrder()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 600, maxQtyPerHu: 600);
        var service = CreatePalletService(harness);
        var plan = service.PlanOrder(10);
        var hu = Assert.Single(harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)).HuCode;

        var result = service.Fill(hu, "TSD-01", orderId: 999, prdDocId: plan.PrdDocId);

        Assert.False(result.Success);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void FillPallet_RejectsUnknownHu()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 600, maxQtyPerHu: 600);
        var service = CreatePalletService(harness);
        var plan = service.PlanOrder(10);

        var result = service.Fill("HU-UNKNOWN", "TSD-01", orderId: 10, prdDocId: plan.PrdDocId);

        Assert.False(result.Success);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void FillPallet_DoesNotAcceptUnplannedHu()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = CreatePalletService(harness);
        var plan = service.PlanOrder(10);
        var second = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)
            .OrderBy(pallet => pallet.Id)
            .Skip(1)
            .First();

        var result = service.Fill(second.HuCode, "TSD-01", orderId: 10, prdDocId: plan.PrdDocId + 1000);

        Assert.False(result.Success);
        Assert.Empty(harness.LedgerEntries);
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
