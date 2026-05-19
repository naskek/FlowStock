using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderLinePalletFillPresentationTests
{
    [Fact]
    public void CustomerOrderLine_FullyShipped_HidesIncompleteFillProgress()
    {
        var order = BuildCustomerOrder(OrderStatus.InProgress);
        var line = BuildLine(qtyOrdered: 1890, qtyShipped: 1890, plannedPallets: 5, filledPallets: 2);

        var presentation = OrderLinePalletFillPresentationService.Resolve(order, line);

        Assert.Equal("SHIPPED", presentation.FulfillmentStatus);
        Assert.True(presentation.LineFullyShipped);
        Assert.True(presentation.HidePalletFillIndicator);
        Assert.False(presentation.BlockingFillRequired);
        Assert.Null(presentation.Label);
    }

    [Fact]
    public void CustomerOrder_FullyShipped_WithStaleProductionPallets_RemainsShipped()
    {
        var order = new Order
        {
            Id = 66,
            OrderRef = "066",
            Type = OrderType.Customer,
            Status = OrderStatus.Shipped,
            HasShipmentRemaining = false
        };

        var status = OrderPalletFillPresentationService.ResolveOrderPalletPlanStatus(
            order,
            needsProductionPalletPlan: false,
            hasProductionPalletPlan: true,
            new ProductionPalletSummary
            {
                PlannedPalletCount = 5,
                FilledPalletCount = 2,
                RemainingPalletCount = 3
            });

        Assert.Equal(string.Empty, status);
        Assert.True(OrderPalletFillPresentationService.HasStaleUnfilledPlanAfterShipment(
            order,
            new ProductionPalletSummary { PlannedPalletCount = 5, FilledPalletCount = 2 }));
    }

    [Fact]
    public void CustomerOrder_NotShipped_WithProductionPallets_ShowsFillProgress()
    {
        var order = BuildCustomerOrder(OrderStatus.InProgress);
        var line = BuildLine(qtyOrdered: 1890, qtyShipped: 0, plannedPallets: 5, filledPallets: 2);

        var presentation = OrderLinePalletFillPresentationService.Resolve(order, line);

        Assert.Equal("IN_FILL", presentation.FulfillmentStatus);
        Assert.False(presentation.HidePalletFillIndicator);
        Assert.Equal("Наполнено 2 / 5", presentation.Label);
    }

    [Fact]
    public void CustomerOrder_PartialShipment_ShowsFillProgressForRemaining()
    {
        var order = BuildCustomerOrder(OrderStatus.InProgress);
        var line = BuildLine(qtyOrdered: 1890, qtyShipped: 1000, plannedPallets: 5, filledPallets: 2);

        var presentation = OrderLinePalletFillPresentationService.Resolve(order, line);

        Assert.Equal("IN_FILL", presentation.FulfillmentStatus);
        Assert.False(presentation.LineFullyShipped);
        Assert.Equal("Наполнено 2 / 5", presentation.Label);
        Assert.Contains("К отгрузке: 890", presentation.Title);
    }

    [Fact]
    public void InternalOrder_WithPartialPalletFill_StillShowsBlockingFillProgress()
    {
        var order = new Order
        {
            Id = 1,
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress
        };
        var line = BuildLine(qtyOrdered: 100, qtyShipped: 0, plannedPallets: 5, filledPallets: 2);
        line.QtyProduced = 40;

        var presentation = OrderLinePalletFillPresentationService.Resolve(order, line);

        Assert.Equal("IN_FILL", presentation.FulfillmentStatus);
        Assert.True(presentation.BlockingFillRequired);
        Assert.Equal("Наполнено 2 / 5", presentation.Label);
    }

    private static Order BuildCustomerOrder(OrderStatus status)
    {
        return new Order
        {
            Id = 66,
            OrderRef = "066",
            Type = OrderType.Customer,
            Status = status
        };
    }

    private static OrderLineView BuildLine(
        double qtyOrdered,
        double qtyShipped,
        int plannedPallets,
        int filledPallets)
    {
        return new OrderLineView
        {
            Id = 1,
            OrderId = 66,
            ItemId = 10,
            ItemName = "Хрен столовый",
            QtyOrdered = qtyOrdered,
            QtyShipped = qtyShipped,
            QtyRemaining = Math.Max(0, qtyOrdered - qtyShipped),
            PlannedPalletCount = plannedPallets,
            FilledPalletCount = filledPallets,
            PlannedPalletQty = qtyOrdered,
            FilledPalletQty = filledPallets * (qtyOrdered / Math.Max(1, plannedPallets))
        };
    }
}
