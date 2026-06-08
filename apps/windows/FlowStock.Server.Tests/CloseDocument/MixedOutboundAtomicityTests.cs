using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.CloseDocument;

public sealed class MixedOutboundAtomicityTests
{
    [Fact]
    public void CloseMixedHu_WithMissingComponent_IsRejectedWithoutLedger()
    {
        var (harness, draftId) = CreateMixedOutboundDraft();
        var missingLine = harness.GetDocLines(draftId).Single(line => line.ItemId == 1002);
        harness.Store.DeleteDocLine(missingLine.Id);

        var close = harness.CreateService().TryCloseDoc(draftId, allowNegative: false);

        Assert.False(close.Success);
        Assert.Contains("MIXED_HU_SHIP_AS_WHOLE_REQUIRED", close.Errors);
        Assert.Contains(close.Errors, error => error.Contains("Микс-паллета HU-MIX-001 должна отгружаться целиком.", StringComparison.Ordinal));
        Assert.Empty(harness.LedgerEntries);
        Assert.Equal(DocStatus.Draft, harness.GetDoc(draftId).Status);
    }

    [Fact]
    public void CloseMixedHu_WithPartialComponentQuantity_IsRejectedWithoutLedger()
    {
        var (harness, draftId) = CreateMixedOutboundDraft();
        var partialLine = harness.GetDocLines(draftId).Single(line => line.ItemId == 1002);
        harness.Store.DeleteDocLine(partialLine.Id);
        harness.SeedLine(new DocLine
        {
            Id = partialLine.Id,
            DocId = partialLine.DocId,
            OrderLineId = partialLine.OrderLineId,
            ProductionPurpose = partialLine.ProductionPurpose,
            ItemId = partialLine.ItemId,
            Qty = 1,
            QtyInput = 1,
            UomCode = partialLine.UomCode,
            FromLocationId = partialLine.FromLocationId,
            ToLocationId = partialLine.ToLocationId,
            FromHu = partialLine.FromHu,
            ToHu = partialLine.ToHu
        });

        var close = harness.CreateService().TryCloseDoc(draftId, allowNegative: false);

        Assert.False(close.Success);
        Assert.Contains("MIXED_HU_SHIP_AS_WHOLE_REQUIRED", close.Errors);
        Assert.Contains(close.Errors, error => error.Contains("Микс-паллета HU-MIX-001 должна отгружаться целиком.", StringComparison.Ordinal));
        Assert.Empty(harness.LedgerEntries);
        Assert.Equal(DocStatus.Draft, harness.GetDoc(draftId).Status);
    }

    [Fact]
    public void CloseMixedHu_WithFullComposition_WritesAllComponentLedger()
    {
        var (harness, draftId) = CreateMixedOutboundDraft();

        var close = harness.CreateService().TryCloseDoc(draftId, allowNegative: false);

        Assert.True(close.Success, string.Join("; ", close.Errors));
        var ledger = harness.LedgerEntries.OrderBy(entry => entry.ItemId).ToArray();
        Assert.Equal(2, ledger.Length);
        Assert.Equal(new double[] { -5d, -2d }, ledger.Select(entry => entry.QtyDelta).ToArray());
        Assert.All(ledger, entry => Assert.Equal("HU-MIX-001", entry.HuCode));
    }

    private static (CloseDocumentHarness Harness, long DraftId) CreateMixedOutboundDraft()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "FG-01", Name = "Готовая продукция" });
        harness.SeedPartner(new Partner
        {
            Id = 200,
            Code = "CUST-200",
            Name = "Тестовый клиент",
            CreatedAt = DateTime.UtcNow
        });
        harness.SeedItem(new Item { Id = 1001, Name = "Горчица", ItemTypeName = "Готовая продукция" });
        harness.SeedItem(new Item { Id = 1002, Name = "Соус", ItemTypeName = "Готовая продукция" });
        harness.SeedOrder(new Order
        {
            Id = 20,
            OrderRef = "SO-MIX",
            Type = OrderType.Customer,
            Status = OrderStatus.Accepted,
            PartnerId = 200,
            CreatedAt = DateTime.UtcNow
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 201,
            OrderId = 20,
            ItemId = 1001,
            QtyOrdered = 5,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 202,
            OrderId = 20,
            ItemId = 1002,
            QtyOrdered = 2,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedBalance(1001, 1, 5, "HU-MIX-001");
        harness.SeedBalance(1002, 1, 2, "HU-MIX-001");
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 1,
            OrderId = 20,
            HuCode = "HU-MIX-001",
            Status = ProductionPalletStatus.Filled,
            Lines =
            [
                new ProductionPalletComponentLine
                {
                    Id = 11, ProductionPalletId = 1, OrderLineId = 201, ItemId = 1001,
                    ItemName = "Горчица", PlannedQty = 5, FilledQty = 5, CreatedAt = DateTime.UtcNow
                },
                new ProductionPalletComponentLine
                {
                    Id = 12, ProductionPalletId = 1, OrderLineId = 202, ItemId = 1002,
                    ItemName = "Соус", PlannedQty = 2, FilledQty = 2, CreatedAt = DateTime.UtcNow
                }
            ]
        });
        harness.SeedOrderReceiptPlanLines(20,
            new OrderReceiptPlanLine
            {
                Id = 1, OrderId = 20, OrderLineId = 201, ItemId = 1001, ItemName = "Горчица",
                QtyPlanned = 5, ToLocationId = 1, ToLocationCode = "FG-01", ToHu = "HU-MIX-001"
            },
            new OrderReceiptPlanLine
            {
                Id = 2, OrderId = 20, OrderLineId = 202, ItemId = 1002, ItemName = "Соус",
                QtyPlanned = 2, ToLocationId = 1, ToLocationCode = "FG-01", ToHu = "HU-MIX-001"
            });

        var picking = new OutboundPickingService(
            harness.Store,
            harness.CreateService(),
            new FlowStockLedgerFlowOptions { OutboundAutoCloseOnComplete = false });
        var scan = picking.Scan(20, "HU-MIX-001", "TSD-01");
        Assert.True(scan.Success, $"{scan.ErrorCode}: {scan.Message}");
        return (harness, scan.Order!.DraftOutboundDocId!.Value);
    }
}
