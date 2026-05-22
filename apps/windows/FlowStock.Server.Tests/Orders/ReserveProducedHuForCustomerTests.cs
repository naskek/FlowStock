using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Orders;

public sealed class ReserveProducedHuForCustomerTests
{
    [Fact]
    public void ReserveProducedHu_DoesNotChangeInternalQtyOrProduced_AndLinksCustomerPlan()
    {
        const long internalOrderId = 10;
        const long customerOrderId = 20;
        const long internalLineId = 101;
        const long customerLineId = 201;
        const long itemId = 100;
        const long locationId = 1;
        const long prdDocId = 1000;
        const double palletQty = 600;
        const double orderedQty = palletQty * 5;
        const string huCode1 = "HU-0000001";
        const string huCode2 = "HU-0000002";

        var harness = BuildProducedInternalCustomerHarness(
            internalOrderId,
            customerOrderId,
            internalLineId,
            customerLineId,
            itemId,
            locationId,
            prdDocId,
            orderedQty,
            palletQty,
            huCode1,
            huCode2);

        var producedQtyBefore = harness.Store.GetOrderReceiptRemaining(internalOrderId)
            .Single(line => line.OrderLineId == internalLineId)
            .QtyReceived;

        var service = new OrderProducedHuReservationService(harness.Store);
        var result = service.Reserve(new OrderProducedHuReservationRequest
        {
            SourceInternalOrderId = internalOrderId,
            TargetCustomerOrderId = customerOrderId,
            ItemId = itemId,
            TargetOrderLineId = customerLineId,
            HuCodes = new[] { huCode1, huCode2 }
        });

        Assert.Equal(2, result.ReservedHuCodes.Count);
        Assert.Contains(result.ReservedHuCodes, code => string.Equals(code, huCode1, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.ReservedHuCodes, code => string.Equals(code, huCode2, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(palletQty * 2, result.QtyReserved, 3);
        Assert.Equal(orderedQty, result.SourceQtyOrdered, 3);
        Assert.Equal(producedQtyBefore, result.SourceProducedQty, 3);

        var internalLineAfter = harness.GetOrderLines(internalOrderId).Single();
        Assert.Equal(orderedQty, internalLineAfter.QtyOrdered, 3);

        var producedAfter = harness.Store.GetOrderReceiptRemaining(internalOrderId)
            .Single(line => line.OrderLineId == internalLineId)
            .QtyReceived;
        Assert.Equal(producedQtyBefore, producedAfter, 3);

        foreach (var pallet in harness.Store.GetProductionPalletsByDoc(prdDocId))
        {
            Assert.Equal(internalOrderId, pallet.OrderId);
            Assert.Equal(ProductionPalletStatus.Filled, pallet.Status, ignoreCase: true);
        }

        var customerPlan = harness.GetOrderReceiptPlanLines(customerOrderId);
        Assert.Equal(2, customerPlan.Count);
        Assert.All(customerPlan, line =>
        {
            Assert.Equal(customerOrderId, line.OrderId);
            Assert.Equal(customerLineId, line.OrderLineId);
            Assert.Equal(itemId, line.ItemId);
            Assert.Equal(palletQty, line.QtyPlanned, 3);
        });
        Assert.Contains(customerPlan, line => string.Equals(line.ToHu, huCode1, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(customerPlan, line => string.Equals(line.ToHu, huCode2, StringComparison.OrdinalIgnoreCase));

        var contextByHu = harness.Store.GetHuOrderContextRows()
            .Where(row => row.ItemId == itemId)
            .ToDictionary(row => row.HuCode, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(internalOrderId, contextByHu[huCode1].OriginInternalOrderId);
        Assert.Equal(internalOrderId, contextByHu[huCode2].OriginInternalOrderId);
        Assert.Equal(customerOrderId, contextByHu[huCode1].ReservedCustomerOrderId);
        Assert.Equal(customerOrderId, contextByHu[huCode2].ReservedCustomerOrderId);
    }

    [Fact]
    public void InternalFilledHuMovedToCustomerCreatesReplacementPlannedHu()
    {
        var harness = BuildOpenInternalCustomerHarness();
        var service = new OrderProducedHuReservationService(harness.Store);

        service.Reserve(new OrderProducedHuReservationRequest
        {
            SourceInternalOrderId = 10,
            TargetCustomerOrderId = 20,
            ItemId = 100,
            TargetOrderLineId = 201,
            HuCodes = new[] { "HU-OLD" }
        });

        var pallets = harness.Store.GetProductionPalletsByDoc(1000);
        Assert.Contains(pallets, pallet =>
            string.Equals(pallet.HuCode, "HU-OLD", StringComparison.OrdinalIgnoreCase)
            && string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase));
        var replacement = Assert.Single(pallets, pallet =>
            !string.Equals(pallet.HuCode, "HU-OLD", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProductionPalletStatus.Planned, replacement.Status, ignoreCase: true);
        Assert.Equal(10, replacement.OrderId);
        Assert.Equal(101, replacement.OrderLineId);
        Assert.Equal(600, replacement.PlannedQty, 3);
    }

    [Fact]
    public void InternalOrderQuantityIsNotReducedWhenHuIsTakenByCustomer()
    {
        var harness = BuildOpenInternalCustomerHarness();
        var qtyBefore = harness.GetOrderLines(10).Single().QtyOrdered;

        new OrderProducedHuReservationService(harness.Store).Reserve(new OrderProducedHuReservationRequest
        {
            SourceInternalOrderId = 10,
            TargetCustomerOrderId = 20,
            ItemId = 100,
            TargetOrderLineId = 201,
            HuCodes = new[] { "HU-OLD" }
        });

        Assert.Equal(qtyBefore, harness.GetOrderLines(10).Single().QtyOrdered, 3);
    }

    [Fact]
    public void InternalCannotCompleteUntilReplacementHuFilled()
    {
        var harness = BuildOpenInternalCustomerHarness();
        new OrderProducedHuReservationService(harness.Store).Reserve(new OrderProducedHuReservationRequest
        {
            SourceInternalOrderId = 10,
            TargetCustomerOrderId = 20,
            ItemId = 100,
            TargetOrderLineId = 201,
            HuCodes = new[] { "HU-OLD" }
        });

        var close = harness.CreateService().TryCloseDoc(1000, allowNegative: false);

        Assert.False(close.Success);
        Assert.Contains(close.Errors, error => error.Contains("ненаполненные паллеты", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InternalCloseAfterCustomerOutDoesNotDuplicateLedger()
    {
        var harness = BuildOpenInternalCustomerHarness();
        var documentService = harness.CreateService();
        new OrderProducedHuReservationService(harness.Store).Reserve(new OrderProducedHuReservationRequest
        {
            SourceInternalOrderId = 10,
            TargetCustomerOrderId = 20,
            ItemId = 100,
            TargetOrderLineId = 201,
            HuCodes = new[] { "HU-OLD" }
        });
        var outDocId = documentService.CreateDoc(
            DocType.Outbound,
            "OUT-2026-000900",
            null,
            500,
            "SO-20",
            null,
            20,
            hydrateOrderLines: true);
        var closeOut = documentService.TryCloseDoc(outDocId, allowNegative: false);
        Assert.True(closeOut.Success, string.Join("; ", closeOut.Errors));

        var replacement = harness.Store.GetProductionPalletsByDoc(1000)
            .Single(pallet => string.Equals(pallet.Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase));
        var fill = new ProductionPalletService(harness.Store).Fill(replacement.HuCode, "TSD-01");
        Assert.True(fill.Success, fill.Error);
        var closePrd = documentService.TryCloseDoc(1000, allowNegative: false);

        Assert.True(closePrd.Success, string.Join("; ", closePrd.Errors));
        Assert.Single(harness.LedgerEntries, entry => entry.HuCode == "HU-OLD" && entry.QtyDelta > 0);
        Assert.Single(harness.LedgerEntries, entry => entry.HuCode == "HU-OLD" && entry.QtyDelta < 0);
        Assert.Single(harness.LedgerEntries, entry => entry.HuCode == replacement.HuCode && entry.QtyDelta > 0);
    }

    [Fact]
    public void Redistribute_WhenOnlyProducedStock_DoesNotDecreaseInternalQtyOrdered()
    {
        const long internalOrderId = 10;
        const long customerOrderId = 20;
        const long internalLineId = 101;
        const long customerLineId = 201;
        const long itemId = 100;
        const long locationId = 1;
        const long prdDocId = 1000;
        const double palletQty = 600;
        const double orderedQty = palletQty * 5;
        const string huCode1 = "HU-0000001";
        const string huCode2 = "HU-0000002";

        var harness = BuildProducedInternalCustomerHarness(
            internalOrderId,
            customerOrderId,
            internalLineId,
            customerLineId,
            itemId,
            locationId,
            prdDocId,
            orderedQty,
            palletQty,
            huCode1,
            huCode2);

        var internalQtyBefore = harness.GetOrderLines(internalOrderId).Single().QtyOrdered;
        var producedBefore = harness.Store.GetOrderReceiptRemaining(internalOrderId)
            .Single(line => line.OrderLineId == internalLineId)
            .QtyReceived;

        var service = new OrderRedistributionService(harness.Store);
        var result = service.Redistribute(
            internalOrderId,
            customerOrderId,
            itemId,
            palletQty * 2);

        Assert.Equal(palletQty * 2, result.QtyFromProducedStock, 3);
        Assert.Equal(0, result.QtyFromUnproduced, 3);
        Assert.Equal(internalQtyBefore, result.SourceQtyOrderedAfter, 3);
        Assert.Equal(internalQtyBefore, harness.GetOrderLines(internalOrderId).Single().QtyOrdered, 3);
        var producedAfter = harness.Store.GetOrderReceiptRemaining(internalOrderId)
            .Single(line => line.OrderLineId == internalLineId)
            .QtyReceived;
        Assert.Equal(producedBefore, producedAfter, 3);
        Assert.All(harness.Store.GetProductionPalletsByDoc(prdDocId), pallet => Assert.Equal(internalOrderId, pallet.OrderId));
        Assert.Contains(result.TransferredHuCodes, code => string.Equals(code, huCode1, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.TransferredHuCodes, code => string.Equals(code, huCode2, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReserveProducedHu_RejectsPlannedPalletWithoutFill()
    {
        const long internalOrderId = 10;
        const long customerOrderId = 20;
        const long itemId = 100;
        const string plannedHu = "HU-PLANNED";

        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItemType(new ItemType { Id = 1, Name = "Товар", EnableOrderReservation = true });
        harness.SeedItem(new Item { Id = itemId, Name = "Товар", ItemTypeId = 1, MaxQtyPerHu = 600 });
        harness.SeedOrder(new Order
        {
            Id = internalOrderId,
            OrderRef = "INT-10",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 1, 1)
        });
        harness.SeedOrderLine(new OrderLine { Id = 101, OrderId = internalOrderId, ItemId = itemId, QtyOrdered = 3000 });
        harness.SeedOrder(new Order
        {
            Id = customerOrderId,
            OrderRef = "CUST-20",
            Type = OrderType.Customer,
            PartnerId = 500,
            Status = OrderStatus.InProgress,
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 1, 2)
        });
        harness.SeedOrderLine(new OrderLine { Id = 201, OrderId = customerOrderId, ItemId = itemId, QtyOrdered = 3000 });
        harness.SeedDoc(new Doc
        {
            Id = 1000,
            DocRef = "PRD-INT-10",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = internalOrderId,
            CreatedAt = new DateTime(2026, 1, 1)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 1,
            PrdDocId = 1000,
            DocLineId = 1,
            OrderId = internalOrderId,
            OrderLineId = 101,
            ItemId = itemId,
            HuCode = plannedHu,
            PlannedQty = 600,
            ToLocationId = 1,
            Status = ProductionPalletStatus.Planned,
            CreatedAt = new DateTime(2026, 1, 1)
        });

        var service = new OrderProducedHuReservationService(harness.Store);
        var ex = Assert.Throws<InvalidOperationException>(() => service.Reserve(new OrderProducedHuReservationRequest
        {
            SourceInternalOrderId = internalOrderId,
            TargetCustomerOrderId = customerOrderId,
            ItemId = itemId,
            HuCodes = new[] { plannedHu }
        }));

        Assert.Contains("FILLED", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.GetOrderReceiptPlanLines(customerOrderId));
    }

    private static CloseDocumentHarness BuildProducedInternalCustomerHarness(
        long internalOrderId,
        long customerOrderId,
        long internalLineId,
        long customerLineId,
        long itemId,
        long locationId,
        long prdDocId,
        double orderedQty,
        double palletQty,
        string huCode1,
        string huCode2)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = locationId, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItemType(new ItemType { Id = 1, Name = "Товар", EnableOrderReservation = true });
        harness.SeedItem(new Item
        {
            Id = itemId,
            Name = "Товар",
            ItemTypeId = 1,
            BaseUom = "шт",
            MaxQtyPerHu = palletQty
        });
        harness.SeedOrder(new Order
        {
            Id = internalOrderId,
            OrderRef = "INT-10",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 1, 1)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = internalLineId,
            OrderId = internalOrderId,
            ItemId = itemId,
            QtyOrdered = orderedQty
        });
        harness.SeedOrder(new Order
        {
            Id = customerOrderId,
            OrderRef = "CUST-20",
            Type = OrderType.Customer,
            PartnerId = 500,
            PartnerName = "Клиент",
            Status = OrderStatus.InProgress,
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 1, 2)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = customerLineId,
            OrderId = customerOrderId,
            ItemId = itemId,
            QtyOrdered = orderedQty
        });
        harness.SeedDoc(new Doc
        {
            Id = prdDocId,
            DocRef = "PRD-INT-10",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = internalOrderId,
            OrderRef = "INT-10",
            CreatedAt = new DateTime(2026, 1, 1, 8, 0, 0),
            ClosedAt = new DateTime(2026, 1, 1, 9, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 10001,
            DocId = prdDocId,
            OrderLineId = internalLineId,
            ItemId = itemId,
            Qty = palletQty,
            ToLocationId = locationId,
            ToHu = huCode1
        });
        harness.SeedLine(new DocLine
        {
            Id = 10002,
            DocId = prdDocId,
            OrderLineId = internalLineId,
            ItemId = itemId,
            Qty = palletQty,
            ToLocationId = locationId,
            ToHu = huCode2
        });
        harness.SeedProductionPallet(BuildFilledPallet(1, prdDocId, internalOrderId, internalLineId, itemId, huCode1, palletQty, locationId));
        harness.SeedProductionPallet(BuildFilledPallet(2, prdDocId, internalOrderId, internalLineId, itemId, huCode2, palletQty, locationId));
        harness.SeedBalance(itemId, locationId, palletQty, huCode1);
        harness.SeedBalance(itemId, locationId, palletQty, huCode2);
        return harness;
    }

    private static CloseDocumentHarness BuildOpenInternalCustomerHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад", AutoHuDistributionEnabled = true });
        harness.SeedPartner(new Partner { Id = 500, Code = "CUST", Name = "Клиент" });
        harness.SeedItemType(new ItemType { Id = 1, Name = "Товар", EnableOrderReservation = true });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Товар",
            ItemTypeId = 1,
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "INT-10",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 1, 1)
        });
        harness.SeedOrderLine(new OrderLine { Id = 101, OrderId = 10, ItemId = 100, QtyOrdered = 600 });
        harness.SeedOrder(new Order
        {
            Id = 20,
            OrderRef = "SO-20",
            Type = OrderType.Customer,
            Status = OrderStatus.Accepted,
            PartnerId = 500,
            PartnerName = "Клиент",
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 1, 2)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 201,
            OrderId = 20,
            ItemId = 100,
            QtyOrdered = 600,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedDoc(new Doc
        {
            Id = 1000,
            DocRef = "PRD-INT-10",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            OrderRef = "INT-10",
            CreatedAt = new DateTime(2026, 1, 1, 8, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 10001,
            DocId = 1000,
            OrderLineId = 101,
            ItemId = 100,
            Qty = 600,
            ToLocationId = 1,
            ToHu = "HU-OLD"
        });
        harness.SeedProductionPallet(BuildFilledPallet(1, 1000, 10, 101, 100, "HU-OLD", 600, 1));
        harness.SeedLedgerEntry(1000, 100, 1, 600, "HU-OLD");
        return harness;
    }

    private static ProductionPallet BuildFilledPallet(
        long id,
        long prdDocId,
        long orderId,
        long orderLineId,
        long itemId,
        string huCode,
        double plannedQty,
        long locationId)
    {
        return new ProductionPallet
        {
            Id = id,
            PrdDocId = prdDocId,
            DocLineId = 10001,
            OrderId = orderId,
            OrderLineId = orderLineId,
            ItemId = itemId,
            ItemName = "Товар",
            HuCode = huCode,
            PlannedQty = plannedQty,
            ToLocationId = locationId,
            ToLocationCode = "MAIN",
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 1, 1, 10, 0, 0),
            CreatedAt = new DateTime(2026, 1, 1, 9, 0, 0)
        };
    }
}
