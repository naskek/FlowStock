using FlowStock.Core.Models;
using FlowStock.Server;

namespace FlowStock.Server.Tests.Orders;

public sealed class OperationOrderCandidatePolicyTests
{
    [Theory]
    [InlineData(OrderType.Customer, OrderStatus.Accepted, true, true)]
    [InlineData(OrderType.Customer, OrderStatus.InProgress, true, true)]
    [InlineData(OrderType.Customer, OrderStatus.Accepted, false, false)]
    [InlineData(OrderType.Customer, OrderStatus.Shipped, true, false)]
    [InlineData(OrderType.Internal, OrderStatus.Accepted, true, false)]
    public void Outbound_RequiresCustomerOrderWithShipmentRemaining(
        OrderType type,
        OrderStatus status,
        bool hasShipmentRemaining,
        bool expected)
    {
        var order = CreateOrder(type, status, hasShipmentRemaining: hasShipmentRemaining, needsProductionPalletPlan: false);

        var actual = OperationOrderCandidatePolicy.IsCandidate(order, DocType.Outbound);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(OrderStatus.Accepted, true, true)]
    [InlineData(OrderStatus.InProgress, true, true)]
    [InlineData(OrderStatus.Accepted, false, false)]
    [InlineData(OrderStatus.Shipped, true, false)]
    [InlineData(OrderStatus.Cancelled, true, false)]
    [InlineData(OrderStatus.Merged, true, false)]
    public void ProductionReceipt_RequiresNeedsProductionPalletPlan(
        OrderStatus status,
        bool needsProductionPalletPlan,
        bool expected)
    {
        var order = CreateOrder(OrderType.Customer, status, hasShipmentRemaining: true, needsProductionPalletPlan);

        var actual = OperationOrderCandidatePolicy.IsCandidate(order, DocType.ProductionReceipt);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Inbound_IsNeverCandidate()
    {
        var order = CreateOrder(OrderType.Customer, OrderStatus.Accepted, hasShipmentRemaining: true, needsProductionPalletPlan: true);

        Assert.False(OperationOrderCandidatePolicy.IsCandidate(order, DocType.Inbound));
    }

    private static Order CreateOrder(
        OrderType type,
        OrderStatus status,
        bool hasShipmentRemaining,
        bool needsProductionPalletPlan)
    {
        return new Order
        {
            Id = 1,
            OrderRef = "001",
            Type = type,
            Status = status,
            HasShipmentRemaining = hasShipmentRemaining,
            NeedsProductionPalletPlan = needsProductionPalletPlan,
            ListMetricsLoaded = true
        };
    }
}
