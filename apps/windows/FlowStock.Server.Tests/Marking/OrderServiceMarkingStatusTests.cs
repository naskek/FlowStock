using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Marking;

public sealed class OrderServiceMarkingStatusTests
{
    [Fact]
    public void GetOrders_PreservesPersistedPrintedMarkingStatusAndTimestamps()
    {
        var printedAt = new DateTime(2026, 4, 30, 10, 30, 0, DateTimeKind.Utc);
        var order = CreatePrintedOrder(printedAt);
        var store = CreateStore(order);

        var result = Assert.Single(new OrderService(store.Object).GetOrders());

        Assert.Equal(MarkingStatus.Printed, result.MarkingStatus);
        Assert.Equal(MarkingStatus.Printed, result.EffectiveMarkingStatus);
        Assert.False(result.MarkingRequired);
        Assert.Equal(printedAt, result.MarkingExcelGeneratedAt);
        Assert.Equal(printedAt, result.MarkingPrintedAt);
        Assert.Equal("ЧЗ готов к нанесению", result.MarkingStatusDisplay);
    }

    [Fact]
    public void GetOrder_PreservesPersistedPrintedMarkingStatusAndTimestamps()
    {
        var printedAt = new DateTime(2026, 4, 30, 10, 30, 0, DateTimeKind.Utc);
        var order = CreatePrintedOrder(printedAt);
        var store = CreateStore(order);

        var result = new OrderService(store.Object).GetOrder(order.Id);

        Assert.NotNull(result);
        Assert.Equal(MarkingStatus.Printed, result.MarkingStatus);
        Assert.Equal(MarkingStatus.Printed, result.EffectiveMarkingStatus);
        Assert.False(result.MarkingRequired);
        Assert.Equal(printedAt, result.MarkingExcelGeneratedAt);
        Assert.Equal(printedAt, result.MarkingPrintedAt);
        Assert.Equal("ЧЗ готов к нанесению", result.MarkingStatusDisplay);
    }

    private static Mock<IDataStore> CreateStore(Order order)
    {
        var store = new Mock<IDataStore>(MockBehavior.Loose);
        store.Setup(s => s.GetOrders()).Returns(new[] { order });
        store.Setup(s => s.GetOrder(order.Id)).Returns(order);
        store.Setup(s => s.GetOrderLines(order.Id)).Returns(Array.Empty<OrderLine>());
        store.Setup(s => s.GetShippedTotalsByOrderLine(order.Id)).Returns(new Dictionary<long, double>());
        store.Setup(s => s.GetOrderReceiptRemaining(order.Id)).Returns(Array.Empty<OrderReceiptLine>());
        return store;
    }

    private static Order CreatePrintedOrder(DateTime printedAt)
    {
        return new Order
        {
            Id = 2,
            OrderRef = "002",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc),
            MarkingStatus = MarkingStatus.Printed,
            MarkingRequired = false,
            MarkingExcelGeneratedAt = printedAt,
            MarkingPrintedAt = printedAt
        };
    }
}
