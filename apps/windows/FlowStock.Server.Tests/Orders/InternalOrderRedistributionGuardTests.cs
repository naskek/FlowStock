using FlowStock.Core.Models;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.Diagnostics;

namespace FlowStock.Server.Tests.Orders;

public sealed class InternalOrderRedistributionGuardTests
{
    [Fact]
    public void Evaluate_BlocksWhenDraftPrdAndActivePalletsExist()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedOrder(new Order
        {
            Id = 67,
            OrderRef = "067",
            Type = OrderType.Internal,
            Status = OrderStatus.Shipped,
            CreatedAt = new DateTime(2026, 5, 1)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 6701,
            OrderId = 67,
            ItemId = 6,
            QtyOrdered = 0
        });
        harness.SeedDoc(new Doc
        {
            Id = 171,
            DocRef = "PRD-2026-000163",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 67,
            CreatedAt = new DateTime(2026, 5, 2)
        });
        ProductionPlanConsistencyDiagnosticsEndpointTests.SeedProductionPalletPrd(
            harness,
            orderId: 67,
            orderLineId: 6701,
            prdDocId: 171,
            palletCount: 2,
            palletQty: 600,
            palletStatus: ProductionPalletStatus.Printed);

        var guard = InternalOrderRedistributionGuard.Evaluate(harness.Store, 67);

        Assert.True(guard.IsBlocked);
        Assert.Contains(guard.DraftPrdDocs, doc => doc.DocRef.StartsWith("PRD-", StringComparison.Ordinal));
        Assert.Equal(2, guard.ActivePallets.Count);
    }

    [Fact]
    public void Redistribute_ThrowsWhenGuardBlocksAndDoesNotChangeOrderLines()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = 6, Name = "Горчица", BaseUom = "шт", IsActive = true });
        harness.SeedOrder(new Order
        {
            Id = 67,
            OrderRef = "067",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 1)
        });
        harness.SeedOrder(new Order
        {
            Id = 77,
            OrderRef = "077",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 5, 3)
        });
        harness.SeedOrderLine(new OrderLine { Id = 6701, OrderId = 67, ItemId = 6, QtyOrdered = 1200 });
        harness.SeedOrderLine(new OrderLine { Id = 7701, OrderId = 77, ItemId = 6, QtyOrdered = 1200 });
        harness.SeedDoc(new Doc
        {
            Id = 171,
            DocRef = "PRD-2026-000163",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 67,
            CreatedAt = new DateTime(2026, 5, 2)
        });
        ProductionPlanConsistencyDiagnosticsEndpointTests.SeedProductionPalletPrd(
            harness,
            orderId: 67,
            orderLineId: 6701,
            prdDocId: 171,
            palletCount: 1,
            palletQty: 600);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new OrderRedistributionService(harness.Store).Redistribute(67, 77, 6, 600));

        Assert.Equal(InternalOrderRedistributionGuardResult.BlockedMessage, ex.Message);
        Assert.Equal(1200, harness.GetOrderLines(67).Single().QtyOrdered);
        Assert.Equal(1200, harness.GetOrderLines(77).Single().QtyOrdered);
    }

    [Fact]
    public void AutoRedistribution_AddsBlockWithoutChangingCustomerQty()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItemType(new ItemType { Id = 1, Name = "Продукция", EnableOrderReservation = true });
        harness.SeedItem(new Item { Id = 6, Name = "Горчица", BaseUom = "шт", IsActive = true, ItemTypeId = 1 });
        harness.SeedOrder(new Order
        {
            Id = 67,
            OrderRef = "067",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 1)
        });
        harness.SeedOrder(new Order
        {
            Id = 77,
            OrderRef = "077",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 5, 3)
        });
        harness.SeedOrderLine(new OrderLine { Id = 6701, OrderId = 67, ItemId = 6, QtyOrdered = 1200 });
        harness.SeedOrderLine(new OrderLine { Id = 7701, OrderId = 77, ItemId = 6, QtyOrdered = 1200 });
        harness.SeedDoc(new Doc
        {
            Id = 171,
            DocRef = "PRD-2026-000163",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 67,
            CreatedAt = new DateTime(2026, 5, 2)
        });
        ProductionPlanConsistencyDiagnosticsEndpointTests.SeedProductionPalletPrd(
            harness,
            orderId: 67,
            orderLineId: 6701,
            prdDocId: 171,
            palletCount: 1,
            palletQty: 600);
        var markingOrderId = Guid.NewGuid();
        harness.SeedMarkingOrder(new MarkingOrder
        {
            Id = markingOrderId,
            OrderId = 67,
            ItemId = 6,
            RequestedQuantity = 1200,
            RequestNumber = "MO-067",
            Status = MarkingOrderStatus.CodesBound,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        harness.SeedMarkingCodes(markingOrderId, count: 1200);

        var result = new OrderAutoRedistributionService(harness.Store).ApplyFromOpenInternalOrders(77);

        Assert.False(result.HasTransfers);
        var blocked = Assert.Single(result.IgnoredAttempts);
        Assert.NotNull(blocked.Guard);
        Assert.True(blocked.Guard!.IsBlocked);
        Assert.Equal(1200, harness.GetOrderLines(77).Single().QtyOrdered);
    }
}
