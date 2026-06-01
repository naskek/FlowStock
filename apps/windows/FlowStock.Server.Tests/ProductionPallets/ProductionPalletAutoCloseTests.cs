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
    public void FillMixedPallet_WithAutoClose_WritesComponentLedgerAndFeedsOutbound()
    {
        var harness = CreateCustomerMixedPalletHarness();
        var service = CreatePalletService(harness);
        var plan = service.PlanOrder(102);
        var plannedPallets = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId);
        var mixed = Assert.Single(plannedPallets.Where(pallet => pallet.IsMixedPallet));
        var single = Assert.Single(plannedPallets.Where(pallet => !pallet.IsMixedPallet));

        var mixedFill = service.Fill(mixed.HuCode, "TSD-01", orderId: 102, prdDocId: plan.PrdDocId);

        Assert.True(mixedFill.Success, mixedFill.Error);
        Assert.True(mixedFill.PrdAutoClosed);
        Assert.NotEqual(plan.PrdDocId, mixedFill.ClosedPrdDocId);
        var filledMixed = harness.Store.GetProductionPalletByHu(mixed.HuCode);
        Assert.NotNull(filledMixed);
        Assert.Equal(ProductionPalletStatus.Filled, filledMixed.Status);
        Assert.All(filledMixed.Lines, line => Assert.Equal(line.PlannedQty, line.FilledQty));
        Assert.Equal(DocStatus.Closed, harness.GetDoc(mixedFill.ClosedPrdDocId!.Value).Status);
        Assert.Equal(DocStatus.Draft, harness.GetDoc(plan.PrdDocId).Status);

        var mixedDocLines = harness.GetDocLines(mixedFill.ClosedPrdDocId.Value)
            .OrderBy(line => line.ItemId)
            .ToArray();
        Assert.Equal([44, 47], mixedDocLines.Select(line => line.ItemId).ToArray());
        Assert.All(mixedDocLines, line => Assert.Equal(mixed.HuCode, line.ToHu));

        var mixedLedger = harness.LedgerEntries
            .Where(entry => entry.DocId == mixedFill.ClosedPrdDocId.Value)
            .OrderBy(entry => entry.ItemId)
            .ToArray();
        Assert.Equal(2, mixedLedger.Length);
        Assert.Equal([44, 47], mixedLedger.Select(entry => entry.ItemId).ToArray());
        Assert.All(mixedLedger, entry =>
        {
            Assert.Equal(900, entry.QtyDelta);
            Assert.Equal(mixed.HuCode, entry.HuCode);
        });

        var singleFill = service.Fill(single.HuCode, "TSD-01", orderId: 102);
        Assert.True(singleFill.Success, singleFill.Error);
        Assert.Equal(OrderStatus.Accepted, harness.GetOrder(102).Status);

        var orderLines = new OrderService(harness.Store).GetOrderLineViews(102)
            .OrderBy(line => line.ItemId)
            .ToArray();
        Assert.Equal([42, 44, 47], orderLines.Select(line => line.ItemId).ToArray());
        Assert.All(orderLines, line =>
        {
            Assert.Equal(900, line.QtyProduced);
            Assert.Equal(900, line.QtyAvailable);
        });

        var outbound = new OutboundPickingService(harness.Store, harness.CreateService());
        var details = outbound.GetDetails(102);
        Assert.Equal(2, details.ExpectedHuCount);
        var outboundMixed = Assert.Single(details.Hus.Where(hu =>
            string.Equals(hu.HuCode, mixed.HuCode, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("Микс Хрен столовый, Микс Хрен ядреный", outboundMixed.ItemSummary);
        Assert.Equal([44, 47], outboundMixed.Lines.OrderBy(line => line.ItemId).Select(line => line.ItemId).ToArray());
        Assert.All(outboundMixed.Lines, line => Assert.Equal(900, line.Qty));

        var scan = outbound.Scan(102, mixed.HuCode, "TSD-OUT");
        var repeatScan = outbound.Scan(102, mixed.HuCode, "TSD-OUT");

        Assert.True(scan.Success, $"{scan.ErrorCode}: {scan.Message}");
        Assert.True(repeatScan.Success, $"{repeatScan.ErrorCode}: {repeatScan.Message}");
        Assert.True(repeatScan.AlreadyPicked);
        var outboundDocId = scan.Order!.DraftOutboundDocId!.Value;
        var outboundLines = harness.GetDocLines(outboundDocId)
            .OrderBy(line => line.ItemId)
            .ToArray();
        Assert.Equal(2, outboundLines.Length);
        Assert.Equal([44, 47], outboundLines.Select(line => line.ItemId).ToArray());
        Assert.All(outboundLines, line => Assert.Equal(mixed.HuCode, line.FromHu));
    }

    [Fact]
    public void FillPallet_WhenAutoCloseFails_RollsBackFilledState()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600, itemIsActive: false);
        var service = CreatePalletService(harness);
        var plan = service.PlanOrder(10);
        var first = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)
            .OrderBy(pallet => pallet.Id)
            .First();

        var fill = service.Fill(first.HuCode, "TSD-01", orderId: 10, prdDocId: plan.PrdDocId);

        Assert.False(fill.Success);
        Assert.Contains("карточка товара заблокирована", fill.Error, StringComparison.OrdinalIgnoreCase);
        var palletAfter = harness.Store.GetProductionPalletByHu(first.HuCode);
        Assert.NotNull(palletAfter);
        Assert.Equal(ProductionPalletStatus.Planned, palletAfter.Status);
        Assert.All(palletAfter.Lines, line => Assert.Equal(0, line.FilledQty));
        Assert.Empty(harness.LedgerEntries);
        Assert.Equal(DocStatus.Draft, harness.GetDoc(plan.PrdDocId).Status);
        Assert.Single(harness.Store.GetDocsByOrder(10).Where(doc => doc.Type == DocType.ProductionReceipt));
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

    private static CloseDocumentHarness CreateHarnessWithOrderOnly(double orderQty, double maxQtyPerHu, bool itemIsActive = true)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Товар",
            IsActive = itemIsActive,
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

    private static CloseDocumentHarness CreateCustomerMixedPalletHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedPartner(new Partner
        {
            Id = 501,
            Code = "CUST-102",
            Name = "Клиент 102",
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedItem(new Item { Id = 42, Name = "Хрен со свеклой", BaseUom = "шт", MaxQtyPerHu = 900 });
        harness.SeedItem(new Item { Id = 44, Name = "Микс Хрен столовый", BaseUom = "шт" });
        harness.SeedItem(new Item { Id = 47, Name = "Микс Хрен ядреный", BaseUom = "шт" });
        harness.SeedOrder(new Order
        {
            Id = 102,
            OrderRef = "102",
            Type = OrderType.Customer,
            PartnerId = 501,
            PartnerName = "Клиент 102",
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 280,
            OrderId = 102,
            ItemId = 42,
            QtyOrdered = 900,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 281,
            OrderId = 102,
            ItemId = 44,
            QtyOrdered = 900,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder,
            ProductionPalletGroup = "MIX-1"
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 282,
            OrderId = 102,
            ItemId = 47,
            QtyOrdered = 900,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder,
            ProductionPalletGroup = "MIX-1"
        });
        return harness;
    }
}
