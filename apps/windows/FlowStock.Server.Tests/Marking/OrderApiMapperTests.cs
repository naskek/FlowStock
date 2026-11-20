using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Server;

namespace FlowStock.Server.Tests.Marking;

public sealed class OrderApiMapperTests
{
    [Fact]
    public void OrderStatusDisplay_AcceptedRemainsLegacyWhenCanonicalPresentationIsLoaded()
    {
        var order = new Order
        {
            Type = OrderType.Customer,
            Status = OrderStatus.Accepted,
            OperatorStatusPresentation = new OrderOperatorStatusPresentation("ACCEPTED", "Готов к отгрузке")
        };

        Assert.Equal("Готов", order.StatusDisplay);
        Assert.Equal("Готов к отгрузке", order.OperatorStatusPresentation.Label);
    }

    [Fact]
    public void OrderStatusDisplay_PartialShipmentRemainsLegacyWhenCanonicalPresentationIsLoaded()
    {
        var order = new Order
        {
            Type = OrderType.Customer,
            Status = OrderStatus.Accepted,
            IsPartiallyShipped = true,
            OperatorStatusPresentation = new OrderOperatorStatusPresentation(
                "PARTIALLY_SHIPPED",
                "Частично отгружен")
        };

        Assert.Equal("Частично отгружено", order.StatusDisplay);
        Assert.Equal("Частично отгружен", order.OperatorStatusPresentation.Label);
    }

    [Theory]
    [InlineData(OrderType.Internal, OrderStatus.InProgress, false)]
    [InlineData(OrderType.Customer, OrderStatus.Draft, false)]
    [InlineData(OrderType.Customer, OrderStatus.InProgress, true)]
    [InlineData(OrderType.Customer, OrderStatus.Accepted, true)]
    [InlineData(OrderType.Customer, OrderStatus.Shipped, false)]
    [InlineData(OrderType.Customer, OrderStatus.Cancelled, false)]
    [InlineData(OrderType.Customer, OrderStatus.Merged, false)]
    public void MapOrder_ClampsLegacyPartialOutboundPermissionFailClosed(
        OrderType type,
        OrderStatus status,
        bool expected)
    {
        var json = JsonSerializer.SerializeToElement(OrderApiMapper.MapOrder(new Order
        {
            Id = 1,
            OrderRef = "PERMISSION-1",
            Type = type,
            Status = status,
            AllowPartialOutbound = true,
            CreatedAt = DateTime.UtcNow
        }));

        Assert.Equal(expected, json.GetProperty("allow_partial_outbound").GetBoolean());
    }

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

    [Fact]
    public void MapOrder_DoesNotExposeHistoricalMarkingState()
    {
        var order = new Order { Id = 1, MarkingStatus = MarkingStatus.Printed,
            MarkingRequired = true, MarkingApplies = true, MarkingCodeCovered = true,
            MarkingPrintedAt = DateTime.UtcNow };
        var json = JsonSerializer.SerializeToElement(OrderApiMapper.MapOrder(order));
        Assert.DoesNotContain(json.EnumerateObject(), property => property.Name.StartsWith("marking_"));
        Assert.False(order.MarkingCompleted);
        Assert.Empty(order.MarkingLabel);
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

    [Fact]
    public void MapOrder_AcceptedKeepsLegacyAliasesAndAddsCanonicalPresentation()
    {
        var order = new Order
        {
            Id = 57,
            OrderRef = "057",
            Type = OrderType.Customer,
            Status = OrderStatus.Accepted,
            OperatorStatusPresentation = new OrderOperatorStatusPresentation("ACCEPTED", "Готов к отгрузке"),
            CreatedAt = new DateTime(2026, 5, 13, 10, 0, 0, DateTimeKind.Utc)
        };

        var json = JsonSerializer.SerializeToElement(OrderApiMapper.MapOrder(order));

        Assert.Equal("ACCEPTED", json.GetProperty("order_status").GetString());
        Assert.Equal("Готов", json.GetProperty("order_status_display").GetString());
        Assert.Equal("Готов", json.GetProperty("status").GetString());
        Assert.Equal(
            "ACCEPTED",
            json.GetProperty("order_status_presentation").GetProperty("code").GetString());
        Assert.Equal(
            "Готов к отгрузке",
            json.GetProperty("order_status_presentation").GetProperty("label").GetString());
    }

    [Fact]
    public void MapOrder_ReturnsDerivedPartiallyShippedDisplay_WithoutChangingPersistedStatus()
    {
        var order = new Order
        {
            Id = 58,
            OrderRef = "058",
            Type = OrderType.Customer,
            Status = OrderStatus.Accepted,
            CreatedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.SerializeToElement(OrderApiMapper.MapOrder(
            order,
            hasShipmentRemaining: true,
            shipmentProgress: new OrderShipmentProgress
            {
                OrderedQty = 15,
                ShippedQty = 5,
                RemainingQty = 10
            }));

        Assert.Equal("ACCEPTED", json.GetProperty("order_status").GetString());
        Assert.Equal("Частично отгружено", json.GetProperty("order_status_display").GetString());
        Assert.Equal("Частично отгружено", json.GetProperty("status").GetString());
        Assert.Equal(
            "PARTIALLY_SHIPPED",
            json.GetProperty("order_status_presentation").GetProperty("code").GetString());
        Assert.Equal(
            "Частично отгружен",
            json.GetProperty("order_status_presentation").GetProperty("label").GetString());
        Assert.True(json.GetProperty("is_partially_shipped").GetBoolean());
        Assert.Equal(10, json.GetProperty("shipment_remaining_qty").GetDouble());
    }

    [Fact]
    public void MapOrder_ReturnsProductionPalletPlanFields()
    {
        var order = new Order
        {
            Id = 80,
            OrderRef = "080",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc)
        };

        var json = JsonSerializer.SerializeToElement(OrderApiMapper.MapOrder(
            order,
            hasShipmentRemaining: false,
            hasProductionPalletPlan: true,
            needsProductionPalletPlan: true,
            palletSummary: new ProductionPalletSummary
            {
                PlannedPalletCount = 3,
                FilledPalletCount = 1,
                PlannedQty = 1800,
                FilledQty = 600,
                RemainingPalletCount = 2,
                RemainingQty = 1200
            },
            palletPlanStatus: "Наполнение идёт: 1 / 3"));

        Assert.True(json.GetProperty("has_production_pallet_plan").GetBoolean());
        Assert.True(json.GetProperty("needs_production_pallet_plan").GetBoolean());
        Assert.Equal(3, json.GetProperty("planned_pallet_count").GetInt32());
        Assert.Equal(1, json.GetProperty("filled_pallet_count").GetInt32());
        Assert.Equal(1800, json.GetProperty("planned_qty").GetDouble());
        Assert.Equal(600, json.GetProperty("filled_qty").GetDouble());
        Assert.Equal("Наполнение идёт: 1 / 3", json.GetProperty("pallet_plan_status").GetString());
    }
}
