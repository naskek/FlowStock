using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.UpdateOrder.Infrastructure;

internal sealed class InternalOrderPalletQtyFixture
{
    public required CloseDocumentHarness Harness { get; init; }
    public required InMemoryApiDocStore ApiStore { get; init; }
    public required long OrderId { get; init; }
    public required long OrderLineId { get; init; }
    public required long ItemId { get; init; }
    public required long PrdDocId { get; init; }
}

internal static class InternalOrderPalletQtyUpdateScenario
{
    public const long DefaultOrderId = 72;
    public const long DefaultOrderLineId = 7201;
    public const long DefaultItemId = 7200;
    public const long DefaultPrdDocId = 7200;
    public const string DefaultOrderRef = "072";

    public static InternalOrderPalletQtyFixture Create(
        double orderedQty,
        int filledPalletCount,
        int openPalletCount,
        bool openPalletsArePrinted = false,
        bool filledPalletsWithoutComponentLines = false)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = DefaultItemId,
            Name = "Горчица Печагин, 1 кг",
            Brand = "Печагин",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        harness.SeedOrder(new Order
        {
            Id = DefaultOrderId,
            OrderRef = DefaultOrderRef,
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = DefaultOrderLineId,
            OrderId = DefaultOrderId,
            ItemId = DefaultItemId,
            QtyOrdered = orderedQty,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });
        harness.SeedDoc(new Doc
        {
            Id = DefaultPrdDocId,
            DocRef = "PRD-2026-000072",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = DefaultOrderId,
            OrderRef = DefaultOrderRef,
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        });

        var nextPalletId = 1L;
        var nextDocLineId = 72001L;
        for (var i = 0; i < filledPalletCount; i++)
        {
            var huCode = $"HU-FILLED-{i + 1:000}";
            harness.SeedLine(new DocLine
            {
                Id = nextDocLineId++,
                DocId = DefaultPrdDocId,
                OrderLineId = DefaultOrderLineId,
                ItemId = DefaultItemId,
                Qty = 600,
                ToLocationId = 1,
                ToHu = huCode,
                PackSingleHu = true
            });
            harness.SeedProductionPallet(BuildPallet(
                id: nextPalletId++,
                huCode: huCode,
                status: ProductionPalletStatus.Filled,
                includeComponentLines: !filledPalletsWithoutComponentLines,
                docLineId: nextDocLineId - 1));
        }

        var openStatus = openPalletsArePrinted
            ? ProductionPalletStatus.Printed
            : ProductionPalletStatus.Planned;
        for (var i = 0; i < openPalletCount; i++)
        {
            var huCode = $"HU-OPEN-{i + 1:000}";
            harness.SeedLine(new DocLine
            {
                Id = nextDocLineId++,
                DocId = DefaultPrdDocId,
                OrderLineId = DefaultOrderLineId,
                ItemId = DefaultItemId,
                Qty = 600,
                ToLocationId = 1,
                ToHu = huCode,
                PackSingleHu = true
            });
            harness.SeedProductionPallet(BuildPallet(
                id: nextPalletId++,
                huCode: huCode,
                status: openStatus,
                includeComponentLines: true,
                docLineId: nextDocLineId - 1));
        }

        return new InternalOrderPalletQtyFixture
        {
            Harness = harness,
            ApiStore = new InMemoryApiDocStore(),
            OrderId = DefaultOrderId,
            OrderLineId = DefaultOrderLineId,
            ItemId = DefaultItemId,
            PrdDocId = DefaultPrdDocId
        };
    }

    public static UpdateOrderHttpApi.UpdateOrderRequest BuildUpdateRequest(double qtyOrdered)
    {
        return new UpdateOrderHttpApi.UpdateOrderRequest
        {
            OrderRef = DefaultOrderRef,
            Type = "INTERNAL",
            Lines =
            [
                new UpdateOrderHttpApi.UpdateOrderLineRequest
                {
                    ItemId = DefaultItemId,
                    QtyOrdered = qtyOrdered,
                    ProductionPurpose = "INTERNAL_STOCK"
                }
            ]
        };
    }

    private static ProductionPallet BuildPallet(
        long id,
        string huCode,
        string status,
        bool includeComponentLines,
        long docLineId)
    {
        return new ProductionPallet
        {
            Id = id,
            PrdDocId = DefaultPrdDocId,
            DocLineId = docLineId,
            OrderId = DefaultOrderId,
            OrderLineId = DefaultOrderLineId,
            ItemId = DefaultItemId,
            ItemName = "Горчица Печагин, 1 кг",
            HuCode = huCode,
            PlannedQty = 600,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            Status = status,
            FilledAt = status == ProductionPalletStatus.Filled ? new DateTime(2026, 5, 13, 10, 0, 0) : null,
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0),
            Lines = includeComponentLines
                ?
                [
                    new ProductionPalletComponentLine
                    {
                        Id = id * 10,
                        ProductionPalletId = id,
                        DocLineId = docLineId,
                        OrderLineId = DefaultOrderLineId,
                        ItemId = DefaultItemId,
                        ItemName = "Горчица Печагин, 1 кг",
                        PlannedQty = 600,
                        FilledQty = status == ProductionPalletStatus.Filled ? 600 : 0,
                        CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
                    }
                ]
                : Array.Empty<ProductionPalletComponentLine>()
        };
    }
}
