using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.CreateOrder;

public sealed class ProductionLinePurposeTests
{
    [Fact]
    public void InternalOrder_NormalizesAllLinesToInternalStock()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = 10, Name = "Хрен столовый, Печагин, 1 кг", Gtin = "04607186951520" });
        harness.SeedItem(new Item { Id = 11, Name = "Хрен столовый, Печагин, 1 кг, запас", Gtin = "04607186951521" });
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
                    ItemId = 11,
                    ItemName = "Хрен столовый, Печагин, 1 кг, запас",
                    QtyOrdered = 1134,
                    ProductionPurpose = ProductionLinePurpose.InternalStock
                }
            ],
            type: OrderType.Internal);

        var lines = harness.GetOrderLines(orderId).ToArray();

        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.Equal(ProductionLinePurpose.InternalStock, line.ProductionPurpose));
    }

    [Fact]
    public void CustomerOrder_NormalizesAllLinesToCustomerOrder()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedPartner(new Partner { Id = 1, Name = "Клиент" });
        harness.SeedItem(new Item { Id = 10, Name = "Товар 1" });
        harness.SeedItem(new Item { Id = 11, Name = "Товар 2" });
        var service = new OrderService(harness.Store);

        var orderId = service.CreateOrder(
            orderRef: "CUST-001",
            partnerId: 1,
            dueDate: null,
            comment: null,
            lines:
            [
                new OrderLineView { ItemId = 10, ItemName = "Товар 1", QtyOrdered = 100, ProductionPurpose = ProductionLinePurpose.InternalStock },
                new OrderLineView { ItemId = 11, ItemName = "Товар 2", QtyOrdered = 200, ProductionPurpose = ProductionLinePurpose.CustomerOrder }
            ],
            type: OrderType.Customer);

        var lines = harness.GetOrderLines(orderId).ToArray();

        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.Equal(ProductionLinePurpose.CustomerOrder, line.ProductionPurpose));
    }

    [Fact]
    public void InternalOrder_MergesSameItemAfterPurposeNormalization()
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
                new OrderLineView { ItemId = 10, ItemName = "Товар", QtyOrdered = 200, ProductionPurpose = ProductionLinePurpose.CustomerOrder }
            ],
            type: OrderType.Internal);

        var lines = harness.GetOrderLines(orderId);

        Assert.Single(lines);
        Assert.Equal(300, lines[0].QtyOrdered);
        Assert.Equal(ProductionLinePurpose.InternalStock, lines[0].ProductionPurpose);
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
