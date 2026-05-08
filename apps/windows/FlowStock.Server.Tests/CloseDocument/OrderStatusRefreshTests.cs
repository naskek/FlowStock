using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.CloseDocument;

public sealed class OrderStatusRefreshTests
{
    [Fact]
    public void CloseProductionReceipt_FullInternalOrder_BecomesShipped()
    {
        var harness = CreateInternalOrderHarness();
        harness.SeedDoc(new Doc
        {
            Id = 100,
            DocRef = "PRD-2026-000100",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 50,
            OrderRef = "INT-001",
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 101,
            DocId = 100,
            OrderLineId = 501,
            ItemId = 1001,
            Qty = 5,
            ToLocationId = 1,
            ToHu = "HU-INT-001"
        });

        var result = harness.CreateService().TryCloseDoc(100, allowNegative: false);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(50).Status);
    }

    [Fact]
    public void CloseProductionReceipt_PartialInternalOrder_RemainsInProgress_UntilLastLineClosed()
    {
        var harness = CreateInternalOrderHarness();
        harness.SeedOrderLine(new OrderLine
        {
            Id = 502,
            OrderId = 50,
            ItemId = 1002,
            QtyOrdered = 7,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });
        harness.SeedItem(new Item
        {
            Id = 1002,
            Name = "Кетчуп",
            Gtin = "04607186951521",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMarking = false
        });

        harness.SeedDoc(new Doc
        {
            Id = 100,
            DocRef = "PRD-2026-000100",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 50,
            OrderRef = "INT-001",
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 101,
            DocId = 100,
            OrderLineId = 501,
            ItemId = 1001,
            Qty = 5,
            ToLocationId = 1,
            ToHu = "HU-INT-001"
        });

        var firstResult = harness.CreateService().TryCloseDoc(100, allowNegative: false);

        Assert.True(firstResult.Success, string.Join("; ", firstResult.Errors));
        Assert.Equal(OrderStatus.InProgress, harness.GetOrder(50).Status);

        harness.SeedDoc(new Doc
        {
            Id = 200,
            DocRef = "PRD-2026-000200",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 50,
            OrderRef = "INT-001",
            CreatedAt = new DateTime(2026, 5, 8, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 201,
            DocId = 200,
            OrderLineId = 502,
            ItemId = 1002,
            Qty = 7,
            ToLocationId = 1,
            ToHu = "HU-INT-002"
        });

        var secondResult = harness.CreateService().TryCloseDoc(200, allowNegative: false);

        Assert.True(secondResult.Success, string.Join("; ", secondResult.Errors));
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(50).Status);
    }

    [Fact]
    public void CloseProductionReceipt_FullCustomerOrder_BecomesAccepted()
    {
        var harness = CreateCustomerOrderHarness();
        harness.SeedDoc(new Doc
        {
            Id = 1,
            DocRef = "PRD-2026-000001",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            OrderRef = "SO-001",
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 11,
            DocId = 1,
            OrderLineId = 101,
            ItemId = 1001,
            Qty = 5,
            ToLocationId = 1,
            ToHu = "HU-PRD-001"
        });

        var result = harness.CreateService().TryCloseDoc(1, allowNegative: false);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(OrderStatus.Accepted, harness.GetOrder(10).Status);
    }

    [Fact]
    public void CloseProductionReceipt_PartialCustomerOrder_RemainsInProgress_UntilLastLineClosed()
    {
        var harness = CreateCustomerOrderHarness();
        harness.SeedOrderLine(new OrderLine
        {
            Id = 102,
            OrderId = 10,
            ItemId = 1002,
            QtyOrdered = 7,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedItem(new Item
        {
            Id = 1002,
            Name = "Кетчуп",
            Gtin = "04607186951521",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMarking = false
        });

        harness.SeedDoc(new Doc
        {
            Id = 1,
            DocRef = "PRD-2026-000001",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            OrderRef = "SO-001",
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 11,
            DocId = 1,
            OrderLineId = 101,
            ItemId = 1001,
            Qty = 5,
            ToLocationId = 1,
            ToHu = "HU-PRD-001"
        });

        var firstResult = harness.CreateService().TryCloseDoc(1, allowNegative: false);

        Assert.True(firstResult.Success);
        Assert.Equal(OrderStatus.InProgress, harness.GetOrder(10).Status);

        harness.SeedDoc(new Doc
        {
            Id = 2,
            DocRef = "PRD-2026-000002",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            OrderRef = "SO-001",
            CreatedAt = new DateTime(2026, 5, 8, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 21,
            DocId = 2,
            OrderLineId = 102,
            ItemId = 1002,
            Qty = 7,
            ToLocationId = 1,
            ToHu = "HU-PRD-002"
        });

        var secondResult = harness.CreateService().TryCloseDoc(2, allowNegative: false);

        Assert.True(secondResult.Success);
        Assert.Equal(OrderStatus.Accepted, harness.GetOrder(10).Status);
    }

    [Fact]
    public void CloseOutbound_FullCustomerOrder_BecomesShipped()
    {
        var harness = CreateCustomerOrderHarness();
        harness.SeedOrder(new Order
        {
            Id = 20,
            OrderRef = "SO-020",
            Type = OrderType.Customer,
            Status = OrderStatus.Accepted,
            PartnerId = 200,
            CreatedAt = new DateTime(2026, 5, 8, 9, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 201,
            OrderId = 20,
            ItemId = 1001,
            QtyOrdered = 5,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedOrderReceiptPlanLines(20, new OrderReceiptPlanLine
        {
            Id = 1,
            OrderId = 20,
            OrderLineId = 201,
            ItemId = 1001,
            QtyPlanned = 5,
            ToLocationId = 1,
            ToHu = "HU-OUT-001",
            SortOrder = 1
        });
        harness.SeedBalance(itemId: 1001, locationId: 1, qty: 5, huCode: "HU-OUT-001");
        harness.SeedDoc(new Doc
        {
            Id = 3,
            DocRef = "OUT-2026-000003",
            Type = DocType.Outbound,
            Status = DocStatus.Draft,
            OrderId = 20,
            OrderRef = "SO-020",
            PartnerId = 200,
            CreatedAt = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 31,
            DocId = 3,
            OrderLineId = 201,
            ItemId = 1001,
            Qty = 5,
            FromLocationId = 1,
            FromHu = "HU-OUT-001"
        });

        var result = harness.CreateService().TryCloseDoc(3, allowNegative: false);

        if (!result.Success)
        {
            throw new Xunit.Sdk.XunitException(string.Join("; ", result.Errors));
        }

        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(20).Status);
    }

    private static CloseDocumentHarness CreateCustomerOrderHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location
        {
            Id = 1,
            Code = "FG-01",
            Name = "Готовая продукция"
        });
        harness.SeedPartner(new Partner
        {
            Id = 200,
            Code = "CUST-200",
            Name = "Тестовый клиент",
            CreatedAt = new DateTime(2026, 5, 8, 8, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Горчица",
            Gtin = "04607186951520",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMarking = false
        });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "SO-001",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            PartnerId = 200,
            CreatedAt = new DateTime(2026, 5, 8, 9, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 1001,
            QtyOrdered = 5,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });

        return harness;
    }

    private static CloseDocumentHarness CreateInternalOrderHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location
        {
            Id = 1,
            Code = "FG-01",
            Name = "Готовая продукция"
        });
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Горчица",
            Gtin = "04607186951520",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMarking = false
        });
        harness.SeedOrder(new Order
        {
            Id = 50,
            OrderRef = "INT-001",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 8, 9, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 501,
            OrderId = 50,
            ItemId = 1001,
            QtyOrdered = 5,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });

        return harness;
    }
}
