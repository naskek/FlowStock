using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.UpdateOrder.Infrastructure;

internal static class UpdateOrderHttpScenario
{
    public static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore, long OrderId) CreateCustomerScenario()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedPartner(new Partner
        {
            Id = 200,
            Code = "CUST-200",
            Name = "Тестовый покупатель",
            CreatedAt = new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedPartner(new Partner
        {
            Id = 201,
            Code = "SUP-201",
            Name = "Тестовый поставщик",
            CreatedAt = new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedPartner(new Partner
        {
            Id = 202,
            Code = "CUST-202",
            Name = "Покупатель 202",
            CreatedAt = new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedItem(new Item { Id = 1001, Name = "Горчица", Barcode = "4660011933641" });
        harness.SeedItem(new Item { Id = 1002, Name = "Кетчуп", Barcode = "4660011933642" });
        harness.SeedItem(new Item { Id = 1003, Name = "Соус", Barcode = "4660011933643" });

        const long orderId = 10;
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = "001",
            Type = OrderType.Customer,
            PartnerId = 200,
            DueDate = new DateTime(2026, 3, 20),
            Status = OrderStatus.Accepted,
            Comment = "Исходный заказ",
            CreatedAt = new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine { Id = 101, OrderId = orderId, ItemId = 1001, QtyOrdered = 10 });
        harness.SeedOrderLine(new OrderLine { Id = 102, OrderId = orderId, ItemId = 1002, QtyOrdered = 5 });

        return (harness, new InMemoryApiDocStore(), orderId);
    }

    public static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore, long OrderId) CreateShippedCustomerScenario()
    {
        var (harness, apiStore, orderId) = CreateCustomerScenario();
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = "001",
            Type = OrderType.Customer,
            PartnerId = 200,
            DueDate = new DateTime(2026, 3, 20),
            Status = OrderStatus.Shipped,
            Comment = "Отгружен",
            CreatedAt = new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc)
        });
        return (harness, apiStore, orderId);
    }

    public static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore, long OrderId) CreateOrderRefCollisionScenario()
    {
        var (harness, apiStore, orderId) = CreateCustomerScenario();
        harness.SeedOrder(new Order
        {
            Id = 11,
            OrderRef = "777",
            Type = OrderType.Customer,
            PartnerId = 202,
            Status = OrderStatus.Draft,
            CreatedAt = new DateTime(2026, 3, 10, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine { Id = 201, OrderId = 11, ItemId = 1003, QtyOrdered = 2 });
        return (harness, apiStore, orderId);
    }

    public static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore, long OrderId) CreateInternalScenario()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = 1001, Name = "Горчица", Barcode = "4660011933641", Gtin = "04607186951520" });
        harness.SeedItem(new Item { Id = 1002, Name = "Кетчуп", Barcode = "4660011933642" });

        const long orderId = 20;
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = "INT-001",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            Comment = "Исходный внутренний заказ",
            CreatedAt = new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 201,
            OrderId = orderId,
            ItemId = 1001,
            QtyOrdered = 10,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });

        return (harness, new InMemoryApiDocStore(), orderId);
    }
}
