using FlowStock.App;
using FlowStock.Core.Models;

namespace FlowStock.Server.Tests.CreateOrder;

public sealed class OrderWpfPolicyTests
{
    [Fact]
    public void MarkingExportButtonPolicy_DisablesShippedOrders()
    {
        var lines = new[]
        {
            new OrderLineView { Id = 1, ItemId = 10, Gtin = "04601234567890", QtyOrdered = 10 }
        };

        Assert.False(OrderMarkingExportUiPolicy.CanExport(CreateOrder(OrderStatus.Shipped), lines));
        Assert.True(OrderMarkingExportUiPolicy.CanExport(CreateOrder(OrderStatus.Accepted), lines));
        Assert.True(OrderMarkingExportUiPolicy.CanExport(CreateOrder(OrderStatus.Draft, OrderType.Internal), lines));
        Assert.True(OrderMarkingExportUiPolicy.CanExport(CreateOrder(OrderStatus.InProgress, OrderType.Internal), lines));
    }

    [Fact]
    public void HuBindPromptPolicy_DoesNotPromptWhenOrderAlreadyHasBoundHu()
    {
        var lines = new[]
        {
            new OrderLineView { Id = 1, ItemId = 10, QtyOrdered = 10, QtyRemaining = 10 }
        };
        var huRows = new[]
        {
            new HuStockContextRow
            {
                Hu = "HU-BOUND-001",
                ItemId = 10,
                LocationId = 1,
                Qty = 10,
                OriginInternalOrderId = 50,
                ReservedCustomerOrderId = 20
            }
        };

        var shouldPrompt = OrderReservationPromptPolicy.ShouldPrompt(
            lines,
            huRows,
            new HashSet<long> { 10 },
            orderId: 20,
            out var freeHuCodes);

        Assert.False(shouldPrompt);
        Assert.Empty(freeHuCodes);
    }

    [Fact]
    public void HuBindPromptPolicy_DoesNotPromptAgainAfterSuccessfulBindRefresh()
    {
        var lines = new[]
        {
            new OrderLineView { Id = 1, ItemId = 10, QtyOrdered = 5, QtyRemaining = 5 }
        };
        var refreshedRows = new[]
        {
            new HuStockContextRow
            {
                Hu = "HU-AFTER-BIND",
                ItemId = 10,
                LocationId = 1,
                Qty = 5,
                OriginInternalOrderId = 50,
                ReservedCustomerOrderId = 20
            },
            new HuStockContextRow
            {
                Hu = "HU-FREE-EXTRA",
                ItemId = 10,
                LocationId = 1,
                Qty = 5,
                OriginInternalOrderId = 51
            }
        };

        var shouldPrompt = OrderReservationPromptPolicy.ShouldPrompt(
            lines,
            refreshedRows,
            new HashSet<long> { 10 },
            orderId: 20,
            out _);

        Assert.False(shouldPrompt);
    }

    [Fact]
    public void HuBindPromptPolicy_PromptsWhenNoBoundHuAndFreeHuExists()
    {
        var lines = new[]
        {
            new OrderLineView { Id = 1, ItemId = 10, QtyOrdered = 5, QtyRemaining = 5 }
        };
        var huRows = new[]
        {
            new HuStockContextRow
            {
                Hu = "HU-FREE-001",
                ItemId = 10,
                LocationId = 1,
                Qty = 5,
                OriginInternalOrderId = 50
            }
        };

        var shouldPrompt = OrderReservationPromptPolicy.ShouldPrompt(
            lines,
            huRows,
            new HashSet<long> { 10 },
            orderId: 20,
            out var freeHuCodes);

        Assert.True(shouldPrompt);
        Assert.Equal(new[] { "HU-FREE-001" }, freeHuCodes);
    }

    private static Order CreateOrder(OrderStatus status, OrderType type = OrderType.Customer)
    {
        return new Order
        {
            Id = 20,
            OrderRef = "ORD-20",
            Type = type,
            Status = status,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0)
        };
    }
}
