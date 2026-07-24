using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.CreateOrder.Infrastructure;

internal static class CreateOrderHttpScenario
{
    public static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore) CreateCustomerScenario()
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
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Горчица",
            Barcode = "4660011933641",
            DefaultSalePriceGross = 100m,
            DefaultSaleVatRateId = 1,
            DefaultSaleVatRate = 22m,
            DefaultSaleVatRateIsActive = true
        });
        harness.SeedItem(new Item
        {
            Id = 1002,
            Name = "Кетчуп",
            Barcode = "4660011933642",
            DefaultSalePriceGross = 120m,
            DefaultSaleVatRateId = 1,
            DefaultSaleVatRate = 22m,
            DefaultSaleVatRateIsActive = true
        });

        return (harness, new InMemoryApiDocStore());
    }

    public static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore) CreateInternalScenario()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Горчица",
            Barcode = "4660011933641"
        });
        harness.SeedItem(new Item
        {
            Id = 1002,
            Name = "Кетчуп",
            Barcode = "4660011933642"
        });

        return (harness, new InMemoryApiDocStore());
    }

    public static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore) CreateOrderRefCollisionScenario(string existingOrderRef)
    {
        var (harness, apiStore) = CreateCustomerScenario();
        harness.SeedOrder(new Order
        {
            Id = 1,
            OrderRef = existingOrderRef,
            Type = OrderType.Customer,
            PartnerId = 200,
            Status = OrderStatus.Accepted,
            CreatedAt = new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 1,
            OrderId = 1,
            ItemId = 1001,
            QtyOrdered = 10
        });

        return (harness, apiStore);
    }
}
