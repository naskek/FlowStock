using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Orders;

public sealed class CustomerMixedHuReservationApplyTests
{
    [Fact]
    public void CreateCustomerOrder_AutoBindsFreeHu_AndBecomesAcceptedAfterMixedProductionFill()
    {
        const long itemTypeId = 1;
        const long itemId = 6;
        const long partnerId = 100;
        const long locationId = 1;
        const double palletQty = 600;
        const string warehouseHu = "HU-0000725";

        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = locationId, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItemType(new ItemType { Id = itemTypeId, Name = "Готовая продукция", EnableOrderReservation = true });
        harness.SeedItem(new Item
        {
            Id = itemId,
            Name = "Товар",
            BaseUom = "шт",
            ItemTypeId = itemTypeId,
            MaxQtyPerHu = palletQty
        });
        harness.SeedPartner(new Partner { Id = partnerId, Code = "CUST", Name = "Покупатель" });
        harness.SeedBalance(itemId, locationId, palletQty, warehouseHu);

        var orderService = new OrderService(harness.Store);
        var customerOrderId = orderService.CreateOrder(
            "003",
            partnerId,
            dueDate: null,
            comment: null,
            lines:
            [
                new OrderLineView
                {
                    ItemId = itemId,
                    ItemName = "Товар",
                    QtyOrdered = palletQty * 2
                }
            ],
            OrderType.Customer,
            bindReservedStockForCustomer: false);
        var customerLine = Assert.Single(harness.Store.GetOrderLines(customerOrderId));

        var reservation = Assert.Single(harness.Store.GetOrderReceiptPlanLines(customerOrderId));
        Assert.Equal(customerLine.Id, reservation.OrderLineId);
        Assert.Equal(itemId, reservation.ItemId);
        Assert.Equal(palletQty, reservation.QtyPlanned, 3);
        Assert.Equal(warehouseHu, reservation.ToHu);
        Assert.True(harness.Store.GetOrder(customerOrderId)!.UseReservedStock);

        var plan = new ProductionPalletService(harness.Store).PlanOrder(customerOrderId);
        Assert.True(plan.ProductionRequired);
        var plannedPallet = Assert.Single(harness.Store.GetProductionPalletsByDoc(plan.PrdDocId));
        Assert.Equal(palletQty, plannedPallet.PlannedQty, 3);
        Assert.False(string.Equals(warehouseHu, plannedPallet.HuCode, StringComparison.OrdinalIgnoreCase));

        var documentService = harness.CreateService();
        var fillService = new ProductionPalletService(
            harness.Store,
            new ProductionFillCloseService(harness.Store, documentService, new FlowStockLedgerFlowOptions()));
        var fill = fillService.Fill(plannedPallet.HuCode, "TSD-01", customerOrderId, plan.PrdDocId);
        Assert.True(fill.Success, fill.Error);

        var status = orderService.RefreshPersistedStatus(customerOrderId);
        Assert.Equal(OrderStatus.Accepted, status);
        Assert.Equal(OrderStatus.Accepted, harness.Store.GetOrder(customerOrderId)!.Status);

        var outbound = new OutboundPickingService(harness.Store, documentService).GetOrders();
        var outboundOrder = Assert.Single(outbound, row => row.OrderId == customerOrderId);
        Assert.Equal(2, outboundOrder.ExpectedHuCount);

        var details = new OutboundPickingService(harness.Store, documentService).GetDetails(customerOrderId);
        Assert.Equal(2, details.ExpectedHuCount);
        Assert.Contains(details.Hus, hu => string.Equals(hu.HuCode, warehouseHu, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(details.Hus, hu => string.Equals(hu.HuCode, plannedPallet.HuCode, StringComparison.OrdinalIgnoreCase));
    }
}
