using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Orders;

public sealed class CustomerOutboundBoundHuTests
{
    [Fact]
    public void GetUnshippedBoundHuLines_ReturnsAllPlanLines_WhenNothingShipped()
    {
        var harness = CreateOrder078Harness();

        var lines = CustomerOutboundBoundHuService.GetUnshippedBoundHuLines(harness.Store, 78);

        Assert.Equal(8, lines.Count);
        Assert.Contains(lines, line => string.Equals(line.HuCode, "HU-0000478", StringComparison.OrdinalIgnoreCase) && line.Qty == 600);
        Assert.Contains(lines, line => string.Equals(line.HuCode, "HU-0000530", StringComparison.OrdinalIgnoreCase) && line.Qty == 1824);
    }

    [Fact]
    public void GetUnshippedBoundHuLines_ExcludesReservationOnlyHuWithoutPhysicalStock()
    {
        var harness = CreateOrder078Harness();
        harness.SeedOrderReceiptPlanLines(
            78,
            PlanLine(1, 203, 1001, "Горчица 1 кг", 600, "HU-RESERVATION-ONLY"));

        var lines = CustomerOutboundBoundHuService.GetUnshippedBoundHuLines(harness.Store, 78);

        Assert.Empty(lines);
    }

    [Fact]
    public void SyncDraftOutbound_AddsMissingBoundHu_ToExistingDraftWithoutDuplicates()
    {
        var harness = CreateOrder078Harness();
        harness.SeedDoc(new Doc
        {
            Id = 213,
            DocRef = "OUT-2026-000197",
            Type = DocType.Outbound,
            Status = DocStatus.Draft,
            OrderId = 78,
            PartnerId = 500,
            CreatedAt = new DateTime(2026, 5, 20, 9, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 9001,
            DocId = 213,
            OrderLineId = 204,
            ItemId = 1002,
            Qty = 1824,
            FromLocationId = 1,
            FromHu = "HU-0000453"
        });
        harness.SeedLine(new DocLine
        {
            Id = 9002,
            DocId = 213,
            OrderLineId = 206,
            ItemId = 1004,
            Qty = 1824,
            FromLocationId = 1,
            FromHu = "HU-0000461"
        });
        harness.SeedLine(new DocLine
        {
            Id = 9003,
            DocId = 213,
            OrderLineId = 206,
            ItemId = 1004,
            Qty = 1824,
            FromLocationId = 1,
            FromHu = "HU-0000481"
        });

        var firstSync = CustomerOutboundBoundHuService.SyncDraftOutboundFromBoundHu(harness.Store, 213, replaceAll: false);
        var secondSync = CustomerOutboundBoundHuService.SyncDraftOutboundFromBoundHu(harness.Store, 213, replaceAll: false);

        Assert.Equal(5, firstSync);
        Assert.Equal(0, secondSync);
        var docLines = harness.GetDocLines(213);
        Assert.Equal(8, docLines.Count);
        Assert.Equal(8, docLines.Select(line => (line.OrderLineId, line.FromHu)).Distinct().Count());
    }

    [Fact]
    public void GetUnshippedBoundHuLines_ExcludesHuAlreadyShippedInClosedOutbound()
    {
        var harness = CreateOrder078Harness();
        harness.SeedDoc(new Doc
        {
            Id = 300,
            DocRef = "OUT-2026-000100",
            Type = DocType.Outbound,
            Status = DocStatus.Closed,
            OrderId = 78,
            PartnerId = 500,
            ClosedAt = new DateTime(2026, 5, 19, 12, 0, 0),
            CreatedAt = new DateTime(2026, 5, 19, 10, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 3001,
            DocId = 300,
            OrderLineId = 203,
            ItemId = 1001,
            Qty = 600,
            FromLocationId = 1,
            FromHu = "HU-0000478"
        });

        var lines = CustomerOutboundBoundHuService.GetUnshippedBoundHuLines(harness.Store, 78);

        Assert.Equal(7, lines.Count);
        Assert.DoesNotContain(lines, line => string.Equals(line.HuCode, "HU-0000478", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateOutboundDoc_HydratesAllBoundHuLines()
    {
        var harness = CreateOrder078Harness();
        var documentService = harness.CreateService();

        var docId = documentService.CreateDoc(
            DocType.Outbound,
            "OUT-2026-000300",
            null,
            500,
            "078",
            null,
            78,
            hydrateOrderLines: true);

        var lines = harness.GetDocLines(docId);
        Assert.Equal(8, lines.Count);
        Assert.Empty(harness.LedgerEntries);
        Assert.False(harness.Store.HasProductionPallets(docId));
    }

    [Fact]
    public void HasReceiptProductionNeed_IsFalse_WhenCustomerFullyCovered()
    {
        var harness = CreateOrder078Harness();

        Assert.False(CustomerOutboundBoundHuService.HasReceiptProductionNeed(harness.Store, 78));
    }

    [Fact]
    public void CustomerOutboundLifecycle_InternalFillBindShipThenClosePrd_NoDuplicateReceiptLedger()
    {
        var harness = CreateLifecycleHarness();
        var palletService = new ProductionPalletService(harness.Store);
        var documentService = harness.CreateService();

        var plan = palletService.PlanOrder(66);
        var pallet = Assert.Single(harness.Store.GetProductionPalletsByDoc(plan.PrdDocId));
        Assert.True(palletService.Fill(pallet.HuCode, "TSD-01").Success);
        harness.SeedLedgerEntry(
            plan.PrdDocId,
            100,
            1,
            600,
            pallet.HuCode);

        harness.SeedOrderReceiptPlanLines(78, new OrderReceiptPlanLine
        {
            Id = 900,
            OrderId = 78,
            OrderLineId = 172,
            ItemId = 100,
            ItemName = "Товар",
            QtyPlanned = 600,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            ToHu = pallet.HuCode,
            SortOrder = 1
        });

        var outDocId = documentService.CreateDoc(
            DocType.Outbound,
            "OUT-2026-000400",
            null,
            500,
            "078",
            null,
            78,
            hydrateOrderLines: true);
        Assert.Single(harness.GetDocLines(outDocId), line => string.Equals(line.FromHu, pallet.HuCode, StringComparison.OrdinalIgnoreCase));
        Assert.True(documentService.TryCloseDoc(outDocId, allowNegative: false).Success);

        var closePrd = documentService.TryCloseDoc(plan.PrdDocId, allowNegative: false);
        Assert.True(closePrd.Success);
        Assert.Equal(DocStatus.Closed, harness.GetDoc(plan.PrdDocId).Status);
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(66).Status);

        var inbound = harness.LedgerEntries.Where(entry => entry.QtyDelta > 0 && entry.HuCode == pallet.HuCode).ToArray();
        var outbound = harness.LedgerEntries.Where(entry => entry.QtyDelta < 0 && entry.HuCode == pallet.HuCode).ToArray();
        Assert.Single(inbound);
        Assert.Single(outbound);
        Assert.Equal(600, inbound[0].QtyDelta, 3);
        Assert.Equal(-600, outbound[0].QtyDelta, 3);
        Assert.Equal(0, harness.Store.GetLedgerBalance(100, 1, pallet.HuCode), 3);
    }

    private static CloseDocumentHarness CreateOrder078Harness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedPartner(new Partner { Id = 500, Code = "CUST", Name = "Клиент" });
        harness.SeedItem(new Item { Id = 1001, Name = "Горчица 1 кг", BaseUom = "шт" });
        harness.SeedItem(new Item { Id = 1002, Name = "Горчица 200 гр", BaseUom = "шт" });
        harness.SeedItem(new Item { Id = 1003, Name = "Хрен 1 кг", BaseUom = "шт" });
        harness.SeedItem(new Item { Id = 1004, Name = "Хрен 200 гр", BaseUom = "шт" });
        harness.SeedItem(new Item { Id = 1005, Name = "Аджика 200 гр", BaseUom = "шт" });
        harness.SeedOrder(new Order
        {
            Id = 78,
            OrderRef = "078",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            PartnerId = 500,
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 5, 20, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine { Id = 203, OrderId = 78, ItemId = 1001, QtyOrdered = 1800 });
        harness.SeedOrderLine(new OrderLine { Id = 204, OrderId = 78, ItemId = 1002, QtyOrdered = 1824 });
        harness.SeedOrderLine(new OrderLine { Id = 205, OrderId = 78, ItemId = 1003, QtyOrdered = 378 });
        harness.SeedOrderLine(new OrderLine { Id = 206, OrderId = 78, ItemId = 1004, QtyOrdered = 3648 });
        harness.SeedOrderLine(new OrderLine { Id = 229, OrderId = 78, ItemId = 1005, QtyOrdered = 1824 });

        harness.SeedOrderReceiptPlanLines(
            78,
            PlanLine(1, 203, 1001, "Горчица 1 кг", 600, "HU-0000478"),
            PlanLine(2, 203, 1001, "Горчица 1 кг", 600, "HU-0000479"),
            PlanLine(3, 203, 1001, "Горчица 1 кг", 600, "HU-0000532"),
            PlanLine(4, 204, 1002, "Горчица 200 гр", 1824, "HU-0000453"),
            PlanLine(5, 205, 1003, "Хрен 1 кг", 378, "HU-0000482"),
            PlanLine(6, 206, 1004, "Хрен 200 гр", 1824, "HU-0000461"),
            PlanLine(7, 206, 1004, "Хрен 200 гр", 1824, "HU-0000481"),
            PlanLine(8, 229, 1005, "Аджика 200 гр", 1824, "HU-0000530"));

        foreach (var planLine in harness.GetOrderReceiptPlanLines(78))
        {
            harness.SeedBalance(planLine.ItemId, 1, planLine.QtyPlanned, planLine.ToHu!);
        }

        return harness;
    }

    private static CloseDocumentHarness CreateLifecycleHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад", AutoHuDistributionEnabled = true });
        harness.SeedPartner(new Partner { Id = 500, Code = "CUST", Name = "Клиент" });
        harness.SeedItem(new Item { Id = 100, Name = "Товар", BaseUom = "шт", MaxQtyPerHu = 600 });
        harness.SeedOrder(new Order
        {
            Id = 66,
            OrderRef = "066",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 18, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 171,
            OrderId = 66,
            ItemId = 100,
            QtyOrdered = 600,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });
        harness.SeedOrder(new Order
        {
            Id = 78,
            OrderRef = "078",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            PartnerId = 500,
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 5, 18, 9, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 172,
            OrderId = 78,
            ItemId = 100,
            QtyOrdered = 600,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        return harness;
    }

    private static OrderReceiptPlanLine PlanLine(
        int sortOrder,
        long orderLineId,
        long itemId,
        string itemName,
        double qty,
        string hu) =>
        new()
        {
            Id = sortOrder + 100,
            OrderId = 78,
            OrderLineId = orderLineId,
            ItemId = itemId,
            ItemName = itemName,
            QtyPlanned = qty,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            ToHu = hu,
            SortOrder = sortOrder
        };
}
