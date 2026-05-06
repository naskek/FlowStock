using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.CreateOrder;

public sealed class ProductionLinePurposeTests
{
    [Fact]
    public void InternalOrder_AllowsSameItemWithDifferentProductionPurposes()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = 10, Name = "Хрен столовый, Печагин, 1 кг", Gtin = "04607186951520" });
        var service = new OrderService(harness.Store);

        var orderId = service.CreateOrder(
            orderRef: "INT-001",
            partnerId: null,
            dueDate: null,
            comment: null,
            lines:
            [
                new OrderLineView
                {
                    ItemId = 10,
                    ItemName = "Хрен столовый, Печагин, 1 кг",
                    QtyOrdered = 756,
                    ProductionPurpose = ProductionLinePurpose.CustomerOrder
                },
                new OrderLineView
                {
                    ItemId = 10,
                    ItemName = "Хрен столовый, Печагин, 1 кг",
                    QtyOrdered = 1134,
                    ProductionPurpose = ProductionLinePurpose.InternalStock
                }
            ],
            type: OrderType.Internal);

        var lines = harness.GetOrderLines(orderId).OrderBy(line => line.ProductionPurpose).ToArray();

        Assert.Equal(2, lines.Length);
        Assert.Contains(lines, line => line.ItemId == 10
                                      && line.QtyOrdered == 756
                                      && line.ProductionPurpose == ProductionLinePurpose.CustomerOrder);
        Assert.Contains(lines, line => line.ItemId == 10
                                      && line.QtyOrdered == 1134
                                      && line.ProductionPurpose == ProductionLinePurpose.InternalStock);
    }

    [Fact]
    public void InternalOrder_MergesOnlySameItemAndSameProductionPurpose()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = 10, Name = "Товар" });
        var service = new OrderService(harness.Store);

        var orderId = service.CreateOrder(
            orderRef: "INT-002",
            partnerId: null,
            dueDate: null,
            comment: null,
            lines:
            [
                new OrderLineView { ItemId = 10, ItemName = "Товар", QtyOrdered = 100, ProductionPurpose = ProductionLinePurpose.InternalStock },
                new OrderLineView { ItemId = 10, ItemName = "Товар", QtyOrdered = 200, ProductionPurpose = ProductionLinePurpose.InternalStock },
                new OrderLineView { ItemId = 10, ItemName = "Товар", QtyOrdered = 300, ProductionPurpose = ProductionLinePurpose.CustomerOrder }
            ],
            type: OrderType.Internal);

        var lines = harness.GetOrderLines(orderId);

        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, line => line.QtyOrdered == 300 && line.ProductionPurpose == ProductionLinePurpose.InternalStock);
        Assert.Contains(lines, line => line.QtyOrdered == 300 && line.ProductionPurpose == ProductionLinePurpose.CustomerOrder);
    }

    [Fact]
    public void MissingDocLinePurpose_FallsBackByOrderLineLink()
    {
        Assert.Equal(
            ProductionLinePurpose.CustomerOrder,
            ProductionLinePurposeMapper.FromDbValue(null, orderLineId: 100));
        Assert.Equal(
            ProductionLinePurpose.InternalStock,
            ProductionLinePurposeMapper.FromDbValue(null, orderLineId: null));
    }
}
