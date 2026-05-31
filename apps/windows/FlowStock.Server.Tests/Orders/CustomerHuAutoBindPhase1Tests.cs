using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Orders;

public sealed class CustomerHuAutoBindPhase1Tests
{
    [Fact]
    public void CreateCustomerOrder_DoesNotAutoBindAvailableHu_AndCandidatesStillReturnIt()
    {
        var (harness, itemId, partnerId, locationId, huCode) = CreateReadyHuScenario();

        var orderId = CreateCustomerOrder(harness, itemId, partnerId, qty: 600, bindReservedStock: true);

        Assert.Empty(harness.Store.GetOrderReceiptPlanLines(orderId));

        var line = Assert.Single(harness.Store.GetOrderLines(orderId));
        var candidates = new HuReservationCandidatesService(harness.Store).Build(new HuReservationCandidatesQuery
        {
            OrderId = orderId,
            Lines =
            [
                new HuReservationCandidatesLineQuery
                {
                    ClientLineKey = "line-1",
                    OrderLineId = line.Id,
                    ItemId = itemId,
                    QtyOrdered = 600
                }
            ],
            ExcludeHuCodes = Array.Empty<string>()
        });

        var candidate = Assert.Single(Assert.Single(candidates.Lines).Candidates);
        Assert.Equal(huCode, candidate.HuCode);
        Assert.Equal(locationId, harness.Store.GetHuStockRows().Single(row => row.HuCode == huCode).LocationId);
    }

    [Fact]
    public void RefreshCustomerReceiptPlan_DoesNotCreateNewHuReservations()
    {
        var (harness, itemId, partnerId, _, _) = CreateReadyHuScenario();
        var orderId = CreateCustomerOrder(harness, itemId, partnerId, qty: 600, bindReservedStock: true);

        new OrderService(harness.Store).RefreshCustomerReceiptPlans();

        Assert.Empty(harness.Store.GetOrderReceiptPlanLines(orderId));
    }

    [Fact]
    public void DetachHu_NotReboundByRefreshOrUpdate()
    {
        var (harness, itemId, partnerId, _, huCode) = CreateReadyHuScenario();
        var orderId = CreateCustomerOrder(harness, itemId, partnerId, qty: 600, bindReservedStock: true);
        var line = Assert.Single(harness.Store.GetOrderLines(orderId));
        var orderService = new OrderService(harness.Store);
        var applyService = new OrderHuReservationApplyService(harness.Store);

        applyService.Apply(orderId, new OrderHuReservationApplyRequest
        {
            Lines =
            [
                new OrderHuReservationApplyLineRequest
                {
                    OrderLineId = line.Id,
                    SelectedHuCodes = [huCode]
                }
            ]
        });
        Assert.Single(harness.Store.GetOrderReceiptPlanLines(orderId));

        applyService.Apply(orderId, new OrderHuReservationApplyRequest
        {
            Lines =
            [
                new OrderHuReservationApplyLineRequest
                {
                    OrderLineId = line.Id,
                    SelectedHuCodes = Array.Empty<string>()
                }
            ]
        });
        Assert.Empty(harness.Store.GetOrderReceiptPlanLines(orderId));

        orderService.RefreshCustomerReceiptPlans();
        Assert.Empty(harness.Store.GetOrderReceiptPlanLines(orderId));

        orderService.UpdateOrder(
            orderId,
            "CUST-001",
            partnerId,
            dueDate: null,
            comment: "refresh without auto-bind",
            lines:
            [
                new OrderLineView
                {
                    Id = line.Id,
                    ItemId = itemId,
                    ItemName = "Товар",
                    QtyOrdered = 600
                }
            ],
            OrderType.Customer,
            bindReservedStockForCustomer: true);

        Assert.Empty(harness.Store.GetOrderReceiptPlanLines(orderId));
    }

    private static (CloseDocumentHarness Harness, long ItemId, long PartnerId, long LocationId, string HuCode) CreateReadyHuScenario()
    {
        const long itemTypeId = 1;
        const long itemId = 6;
        const long partnerId = 100;
        const long locationId = 1;
        const string huCode = "HU-READY-001";

        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = locationId, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItemType(new ItemType { Id = itemTypeId, Name = "Готовая продукция", EnableOrderReservation = true });
        harness.SeedItem(new Item
        {
            Id = itemId,
            Name = "Товар",
            BaseUom = "шт",
            ItemTypeId = itemTypeId,
            MaxQtyPerHu = 600
        });
        harness.SeedPartner(new Partner { Id = partnerId, Code = "CUST", Name = "Покупатель" });
        harness.SeedBalance(itemId, locationId, 600, huCode);

        return (harness, itemId, partnerId, locationId, huCode);
    }

    private static long CreateCustomerOrder(
        CloseDocumentHarness harness,
        long itemId,
        long partnerId,
        double qty,
        bool bindReservedStock)
    {
        return new OrderService(harness.Store).CreateOrder(
            "CUST-001",
            partnerId,
            dueDate: null,
            comment: null,
            lines:
            [
                new OrderLineView
                {
                    ItemId = itemId,
                    ItemName = "Товар",
                    QtyOrdered = qty
                }
            ],
            OrderType.Customer,
            bindReservedStockForCustomer: bindReservedStock);
    }
}
