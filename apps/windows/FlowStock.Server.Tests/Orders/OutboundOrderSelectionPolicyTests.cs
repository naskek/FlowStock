using FlowStock.App;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Orders;

[Collection("CreateOrder")]
public sealed class OutboundOrderSelectionPolicyTests
{
    [Theory]
    [InlineData(OrderStatus.Accepted, true, true)]
    [InlineData(OrderStatus.InProgress, true, true)]
    [InlineData(OrderStatus.Accepted, false, false)]
    [InlineData(OrderStatus.Shipped, true, false)]
    [InlineData(OrderStatus.Cancelled, true, false)]
    public void IsCandidate_UsesCustomerStatusAndShipmentRemaining(OrderStatus status, bool hasShipmentRemaining, bool expected)
    {
        var actual = OutboundOrderSelectionPolicy.IsCandidate(OrderType.Customer, status, hasShipmentRemaining);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsCandidate_RejectsInternalOrders()
    {
        Assert.False(OutboundOrderSelectionPolicy.IsCandidate(OrderType.Internal, OrderStatus.Accepted, hasShipmentRemaining: true));
    }

    [Fact]
    public void ShipmentRemainingService_ReturnsOrderedQty_WhenNoOutboundExists()
    {
        var harness = CreateOutboundCandidateHarness(status: OrderStatus.Accepted);
        var row = Assert.Single(new DocumentService(harness.Store).GetOrderShipmentRemaining(10));

        Assert.Equal(5, row.QtyOrdered);
        Assert.Equal(0, row.QtyShipped);
        Assert.Equal(5, row.QtyRemaining);
    }

    [Fact]
    public void ShipmentRemainingService_ReturnsZero_AfterFullOutbound()
    {
        var harness = CreateOutboundCandidateHarness(status: OrderStatus.Accepted);
        harness.SeedOrderReceiptPlanLines(10, new OrderReceiptPlanLine
        {
            Id = 1,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 1001,
            QtyPlanned = 5,
            ToLocationId = 1,
            ToHu = "HU-OUT-010",
            SortOrder = 1
        });
        harness.SeedBalance(itemId: 1001, locationId: 1, qty: 5, huCode: "HU-OUT-010");
        harness.SeedDoc(new Doc
        {
            Id = 1,
            DocRef = "OUT-2026-000001",
            Type = DocType.Outbound,
            Status = DocStatus.Draft,
            OrderId = 10,
            OrderRef = "SO-010",
            PartnerId = 200,
            CreatedAt = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 11,
            DocId = 1,
            OrderLineId = 101,
            ItemId = 1001,
            Qty = 5,
            FromLocationId = 1,
            FromHu = "HU-OUT-010"
        });

        var closeResult = harness.CreateService().TryCloseDoc(1, allowNegative: false);
        Assert.True(closeResult.Success);
        Assert.Empty(new DocumentService(harness.Store).GetOrderShipmentRemaining(10));
    }

    private static CloseDocumentHarness CreateOutboundCandidateHarness(OrderStatus status)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location
        {
            Id = 1,
            Code = "FG-01",
            Name = "Готовая продукция"
        });
        harness.SeedPartner(new Partner
        {
            Id = 200,
            Code = "CUST-200",
            Name = "Тестовый клиент",
            CreatedAt = new DateTime(2026, 5, 8, 8, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Горчица",
            Gtin = "04607186951520",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMarking = false
        });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "SO-010",
            Type = OrderType.Customer,
            Status = status,
            PartnerId = 200,
            CreatedAt = new DateTime(2026, 5, 8, 9, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 1001,
            QtyOrdered = 5,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });

        return harness;
    }
}
