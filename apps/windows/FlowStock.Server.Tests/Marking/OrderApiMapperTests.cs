using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Server;

namespace FlowStock.Server.Tests.Marking;

public sealed class OrderApiMapperTests
{
    [Fact]
    public void MapOrder_ReturnsCanonicalCancelledOrderStatus()
    {
        var order = new Order
        {
            Id = 1,
            OrderRef = "CO-1",
            Type = OrderType.Customer,
            Status = OrderStatus.Cancelled,
            CreatedAt = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc)
        };

        var json = JsonSerializer.SerializeToElement(OrderApiMapper.MapOrder(order));

        Assert.Equal("CANCELLED", json.GetProperty("order_status").GetString());
        Assert.Equal("Отменён", json.GetProperty("order_status_display").GetString());
        Assert.Equal("Отменён", json.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(OrderStatus.InProgress, MarkingStatus.Printed, false, false, "PRINTED", "Маркировка проведена")]
    [InlineData(OrderStatus.InProgress, MarkingStatus.NotRequired, true, false, "REQUIRED", "Маркировка не проведена")]
    [InlineData(OrderStatus.InProgress, MarkingStatus.NotRequired, false, false, "NOT_REQUIRED", "")]
    [InlineData(OrderStatus.Cancelled, MarkingStatus.NotRequired, true, false, "NOT_REQUIRED", "")]
    [InlineData(OrderStatus.Cancelled, MarkingStatus.Printed, true, false, "NOT_REQUIRED", "")]
    public void MapOrder_ReturnsEffectiveMarkingStatusForOrderApi(
        OrderStatus orderStatus,
        MarkingStatus storedStatus,
        bool markingRequired,
        bool markingCompleted,
        string expectedStatus,
        string expectedDisplay)
    {
        var order = new Order
        {
            Id = 1,
            OrderRef = "CO-1",
            Type = OrderType.Customer,
            Status = orderStatus,
            CreatedAt = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc),
            MarkingStatus = storedStatus,
            MarkingRequired = markingRequired,
            MarkingApplies = markingRequired,
            MarkingCodeCovered = markingCompleted
        };

        var json = JsonSerializer.SerializeToElement(OrderApiMapper.MapOrder(order));

        Assert.Equal(expectedStatus, json.GetProperty("marking_status").GetString());
        Assert.Equal(expectedStatus, json.GetProperty("marking_effective_status").GetString());
        Assert.Equal(markingRequired, json.GetProperty("marking_required").GetBoolean());
        Assert.Equal(expectedDisplay, json.GetProperty("marking_status_display").GetString());
    }

    [Fact]
    public void MapOrder_ReturnsPrintedForLegacyExcelGeneratedRawStatus()
    {
        var order = new Order
        {
            Id = 1,
            OrderRef = "CO-1",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc),
            MarkingStatus = MarkingStatusMapper.FromString("EXCEL_GENERATED"),
            MarkingRequired = false
        };

        var json = JsonSerializer.SerializeToElement(OrderApiMapper.MapOrder(order));

        Assert.Equal("PRINTED", json.GetProperty("marking_status").GetString());
        Assert.Equal("PRINTED", json.GetProperty("marking_effective_status").GetString());
        Assert.Equal("Маркировка проведена", json.GetProperty("marking_status_display").GetString());
        Assert.True(json.GetProperty("marking_completed").GetBoolean());
        Assert.Equal("Маркировка проведена", json.GetProperty("marking_label").GetString());
    }

    [Fact]
    public void MapOrder_ReturnsBinaryCompletedLabel_WhenRealCodesCoverMarkableLines()
    {
        var order = new Order
        {
            Id = 1,
            OrderRef = "CO-1",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc),
            MarkingStatus = MarkingStatus.NotRequired,
            MarkingRequired = false,
            MarkingApplies = true,
            MarkingCodeCovered = true
        };

        var json = JsonSerializer.SerializeToElement(OrderApiMapper.MapOrder(order));

        Assert.True(json.GetProperty("marking_applies").GetBoolean());
        Assert.True(json.GetProperty("marking_completed").GetBoolean());
        Assert.Equal("PRINTED", json.GetProperty("marking_effective_status").GetString());
        Assert.Equal("Маркировка проведена", json.GetProperty("marking_label").GetString());
    }

    [Fact]
    public void MapOrder_ReturnsBinaryNotCompletedLabel_WhenMarkableLinesStillNeedMarking()
    {
        var order = new Order
        {
            Id = 1,
            OrderRef = "CO-1",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc),
            MarkingStatus = MarkingStatus.NotRequired,
            MarkingRequired = true,
            MarkingApplies = true
        };

        var json = JsonSerializer.SerializeToElement(OrderApiMapper.MapOrder(order));

        Assert.True(json.GetProperty("marking_applies").GetBoolean());
        Assert.False(json.GetProperty("marking_completed").GetBoolean());
        Assert.Equal("REQUIRED", json.GetProperty("marking_effective_status").GetString());
        Assert.Equal("Маркировка не проведена", json.GetProperty("marking_label").GetString());
    }

    [Fact]
    public void MapOrder_DoesNotTreatMarkingTaskWithoutCodesAsCompleted()
    {
        var order = new Order
        {
            Id = 1,
            OrderRef = "INT-001",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc),
            MarkingStatus = MarkingStatus.NotRequired,
            MarkingApplies = true,
            MarkingRequired = false,
            MarkingCodeCovered = false
        };

        var json = JsonSerializer.SerializeToElement(OrderApiMapper.MapOrder(order));

        Assert.False(json.GetProperty("marking_completed").GetBoolean());
        Assert.Equal("REQUIRED", json.GetProperty("marking_effective_status").GetString());
        Assert.Equal("Маркировка не проведена", json.GetProperty("marking_label").GetString());
    }

    [Theory]
    [InlineData(OrderStatus.InProgress, "IN_PROGRESS", "В работе")]
    [InlineData(OrderStatus.Shipped, "SHIPPED", "Выполнен")]
    [InlineData(OrderStatus.Cancelled, "CANCELLED", "Отменён")]
    public void MapOrder_ReturnsConsistentOrderStatusFields_ForInternalOrders(
        OrderStatus status,
        string expectedStatusCode,
        string expectedDisplay)
    {
        var order = new Order
        {
            Id = 56,
            OrderRef = "056",
            Type = OrderType.Internal,
            Status = status,
            CreatedAt = new DateTime(2026, 5, 13, 10, 0, 0, DateTimeKind.Utc)
        };

        var json = JsonSerializer.SerializeToElement(OrderApiMapper.MapOrder(order));

        Assert.Equal(expectedStatusCode, json.GetProperty("order_status").GetString());
        Assert.Equal(expectedDisplay, json.GetProperty("order_status_display").GetString());
        Assert.Equal(expectedDisplay, json.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(OrderStatus.InProgress, "IN_PROGRESS", "В работе")]
    [InlineData(OrderStatus.Accepted, "ACCEPTED", "Готов")]
    [InlineData(OrderStatus.Shipped, "SHIPPED", "Выполнен")]
    [InlineData(OrderStatus.Cancelled, "CANCELLED", "Отменён")]
    public void MapOrder_ReturnsConsistentOrderStatusFields_ForCustomerOrders(
        OrderStatus status,
        string expectedStatusCode,
        string expectedDisplay)
    {
        var order = new Order
        {
            Id = 57,
            OrderRef = "057",
            Type = OrderType.Customer,
            Status = status,
            CreatedAt = new DateTime(2026, 5, 13, 10, 0, 0, DateTimeKind.Utc)
        };

        var json = JsonSerializer.SerializeToElement(OrderApiMapper.MapOrder(order));

        Assert.Equal(expectedStatusCode, json.GetProperty("order_status").GetString());
        Assert.Equal(expectedDisplay, json.GetProperty("order_status_display").GetString());
        Assert.Equal(expectedDisplay, json.GetProperty("status").GetString());
    }
}
