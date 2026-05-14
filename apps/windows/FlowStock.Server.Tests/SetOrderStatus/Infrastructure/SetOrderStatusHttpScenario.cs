using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.SetOrderStatus.Infrastructure;

internal static class SetOrderStatusHttpScenario
{
    public static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore, long OrderId) CreateDraftCustomerScenario()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedPartner(new Partner
        {
            Id = 200,
            Code = "CUST-200",
            Name = "Тестовый покупатель",
            CreatedAt = new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc)
        });

        const long orderId = 30;
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = "030",
            Type = OrderType.Customer,
            PartnerId = 200,
            Status = OrderStatus.Draft,
            CreatedAt = new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine { Id = 501, OrderId = orderId, ItemId = 1001, QtyOrdered = 10 });

        return (harness, new InMemoryApiDocStore(), orderId);
    }

    public static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore, long OrderId) CreateShippedCustomerScenario()
    {
        var (harness, apiStore, orderId) = CreateDraftCustomerScenario();
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = "030",
            Type = OrderType.Customer,
            PartnerId = 200,
            Status = OrderStatus.Shipped,
            CreatedAt = new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc),
            ShippedAt = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc)
        });

        return (harness, apiStore, orderId);
    }

    public static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore, long OrderId, long DraftPrdDocId) CreateCustomerScenarioWithDraftProductionReceipt()
    {
        var (harness, apiStore, orderId) = CreateDraftCustomerScenario();
        harness.SeedLocation(new Location
        {
            Id = 1,
            Code = "FG-01",
            Name = "Готовая продукция"
        });
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Тестовый товар",
            BaseUom = "шт",
            MaxQtyPerHu = 10
        });

        const long docId = 900;
        harness.SeedDoc(new Doc
        {
            Id = docId,
            DocRef = "PRD-2026-000900",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = orderId,
            OrderRef = "030",
            CreatedAt = new DateTime(2026, 3, 10, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 901,
            DocId = docId,
            OrderLineId = 501,
            ItemId = 1001,
            Qty = 10,
            ToLocationId = 1,
            ToHu = "HU-000900"
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 902,
            PrdDocId = docId,
            DocLineId = 901,
            OrderId = orderId,
            OrderLineId = 501,
            ItemId = 1001,
            ItemName = "Тестовый товар",
            HuCode = "HU-000900",
            PlannedQty = 10,
            ToLocationId = 1,
            ToLocationCode = "FG-01",
            Status = ProductionPalletStatus.Planned,
            CreatedAt = new DateTime(2026, 3, 10, 11, 5, 0, DateTimeKind.Utc)
        });

        return (harness, apiStore, orderId, docId);
    }
}
