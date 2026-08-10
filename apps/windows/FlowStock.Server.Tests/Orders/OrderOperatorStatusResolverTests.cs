using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderOperatorStatusResolverTests
{
    [Theory]
    [InlineData(OrderStatus.InProgress, "IN_PROGRESS", "В работе")]
    [InlineData(OrderStatus.Accepted, "ACCEPTED", "Готов к отгрузке")]
    [InlineData(OrderStatus.Shipped, "SHIPPED", "Выполнен")]
    [InlineData(OrderStatus.Cancelled, "CANCELLED", "Отменён")]
    [InlineData(OrderStatus.Draft, "DRAFT", "Черновик")]
    [InlineData(OrderStatus.Merged, "MERGED", "Объединён")]
    public void CustomerStatus_ReturnsCanonicalPresentation(
        OrderStatus status,
        string expectedCode,
        string expectedLabel)
    {
        var presentation = OrderOperatorStatusResolver.Resolve(status, OrderType.Customer, null);

        Assert.Equal(expectedCode, presentation.Code);
        Assert.Equal(expectedLabel, presentation.Label);
    }

    [Theory]
    [InlineData(OrderStatus.InProgress)]
    [InlineData(OrderStatus.Accepted)]
    public void ActiveCustomerWithShippedAndRemainingQty_UsesPartialOverlay(OrderStatus status)
    {
        var presentation = OrderOperatorStatusResolver.Resolve(
            status,
            OrderType.Customer,
            new OrderShipmentProgress { OrderedQty = 15, ShippedQty = 5, RemainingQty = 10 });

        Assert.Equal("PARTIALLY_SHIPPED", presentation.Code);
        Assert.Equal("Частично отгружен", presentation.Label);
    }

    [Theory]
    [InlineData(OrderStatus.Shipped, "SHIPPED")]
    [InlineData(OrderStatus.Cancelled, "CANCELLED")]
    [InlineData(OrderStatus.Merged, "MERGED")]
    [InlineData(OrderStatus.Draft, "DRAFT")]
    public void NonActiveCustomerStatus_DoesNotReceivePartialOverlay(OrderStatus status, string expectedCode)
    {
        var presentation = OrderOperatorStatusResolver.Resolve(
            status,
            OrderType.Customer,
            new OrderShipmentProgress { OrderedQty = 15, ShippedQty = 5, RemainingQty = 10 });

        Assert.Equal(expectedCode, presentation.Code);
    }

    [Fact]
    public void InternalOrder_PreservesCompatibilitySemanticsWithoutPartialOverlay()
    {
        var presentation = OrderOperatorStatusResolver.Resolve(
            OrderStatus.Accepted,
            OrderType.Internal,
            new OrderShipmentProgress { OrderedQty = 15, ShippedQty = 5, RemainingQty = 10 });

        Assert.Equal("ACCEPTED", presentation.Code);
        Assert.Equal("Готов", presentation.Label);
    }

    [Fact]
    public void UnexpectedStatus_ReturnsSafeCompatibilityPresentation()
    {
        var presentation = OrderOperatorStatusResolver.Resolve((OrderStatus)999, OrderType.Customer, null);

        Assert.Equal("UNKNOWN", presentation.Code);
        Assert.Equal("Неизвестно", presentation.Label);
    }
}
