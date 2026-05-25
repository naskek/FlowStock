using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Orders;

public sealed class ProductionOrderLineHuCodesTests
{
    [Fact]
    public void BuildByOrder_CustomerOrder083Like_ShowsPlannedHuPerLine()
    {
        var harness = CreateCustomerTwoLineHarness();
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(83);
        var pallets = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)
            .OrderBy(pallet => pallet.OrderLineId)
            .ToArray();

        var huByLine = ProductionOrderLineHuCodes.BuildByOrder(harness.Store, 83);

        Assert.Equal(2, huByLine.Count);
        Assert.Equal(new[] { pallets[0].HuCode }, huByLine[222]);
        Assert.Equal(new[] { pallets[1].HuCode }, huByLine[223]);
    }

    [Fact]
    public void BuildByOrder_CustomerWithoutProductionPallets_UsesBoundHuOnly()
    {
        var harness = CreateCustomerTwoLineHarness();
        harness.SeedOrderReceiptPlanLines(83,
        [
            new OrderReceiptPlanLine
            {
                Id = 501,
                OrderId = 83,
                OrderLineId = 222,
                ItemId = 29,
                QtyPlanned = 120,
                ToHu = "HU-BOUND-1",
                SortOrder = 1
            }
        ]);
        harness.SeedLedgerEntry(docId: 900, itemId: 29, locationId: 1, qtyDelta: 120, huCode: "HU-BOUND-1");

        var huByLine = ProductionOrderLineHuCodes.BuildByOrder(harness.Store, 83);

        Assert.Single(huByLine);
        Assert.Equal(new[] { "HU-BOUND-1" }, huByLine[222]);
    }

    private static CloseDocumentHarness CreateCustomerTwoLineHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item { Id = 29, Name = "Товар A", BaseUom = "шт", MaxQtyPerHu = 600 });
        harness.SeedItem(new Item { Id = 13, Name = "Товар B", BaseUom = "шт", MaxQtyPerHu = 600 });
        harness.SeedOrder(new Order
        {
            Id = 83,
            OrderRef = "083",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 22, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine { Id = 222, OrderId = 83, ItemId = 29, QtyOrdered = 120 });
        harness.SeedOrderLine(new OrderLine { Id = 223, OrderId = 83, ItemId = 13, QtyOrdered = 80 });
        return harness;
    }
}
