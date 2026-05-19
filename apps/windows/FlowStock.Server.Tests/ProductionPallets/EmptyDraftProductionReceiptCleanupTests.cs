using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class EmptyDraftProductionReceiptCleanupTests
{
    [Fact]
    public void TryDelete_DeletesEmptyDraftProductionReceipt()
    {
        var harness = CreateHarnessWithEmptyDraftPrd();
        var result = EmptyDraftProductionReceiptCleanup.TryDeleteEmptyDraftProductionReceiptIfSafe(
            harness.Store,
            orderId: 66,
            prdDocId: 162);

        Assert.True(result.Deleted);
        Assert.Null(harness.Store.GetDoc(162));
    }

    [Fact]
    public void TryDelete_SkipsWhenLedgerExists()
    {
        var harness = CreateHarnessWithEmptyDraftPrd();
        harness.Store.AddLedgerEntry(new LedgerEntry
        {
            DocId = 162,
            ItemId = 100,
            LocationId = 1,
            QtyDelta = 1,
            Timestamp = DateTime.Now
        });

        var result = EmptyDraftProductionReceiptCleanup.TryDeleteEmptyDraftProductionReceiptIfSafe(
            harness.Store,
            66,
            162);

        Assert.False(result.Deleted);
        Assert.Equal("HAS_LEDGER", result.SkipReasonCode);
        Assert.NotNull(harness.Store.GetDoc(162));
    }

    [Fact]
    public void TryDelete_SkipsWhenDocClosed()
    {
        var harness = CreateHarnessWithEmptyDraftPrd(docStatus: DocStatus.Closed);

        var result = EmptyDraftProductionReceiptCleanup.TryDeleteEmptyDraftProductionReceiptIfSafe(
            harness.Store,
            66,
            162);

        Assert.False(result.Deleted);
        Assert.Equal("DOC_CLOSED", result.SkipReasonCode);
        Assert.NotNull(harness.Store.GetDoc(162));
    }

    [Fact]
    public void TryDelete_SkipsWhenActivePalletsRemain()
    {
        var harness = CreateHarnessWithPlannedPalletOnDraftPrd();

        var result = EmptyDraftProductionReceiptCleanup.TryDeleteEmptyDraftProductionReceiptIfSafe(
            harness.Store,
            66,
            162);

        Assert.False(result.Deleted);
        Assert.Equal("HAS_ACTIVE_PALLETS", result.SkipReasonCode);
    }

    private static CloseDocumentHarness CreateHarnessWithEmptyDraftPrd(DocStatus docStatus = DocStatus.Draft)
    {
        var harness = CreateHarnessForAdoptMinimal();
        harness.SeedDoc(new Doc
        {
            Id = 162,
            DocRef = "PRD-2026-000156",
            Type = DocType.ProductionReceipt,
            Status = docStatus,
            OrderId = 66,
            OrderRef = "066",
            CreatedAt = DateTime.Now
        });
        return harness;
    }

    private static CloseDocumentHarness CreateHarnessForAdoptMinimal()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Склад" });
        harness.SeedItem(new Item { Id = 100, Name = "Товар", MaxQtyPerHu = 600 });
        harness.SeedOrder(new Order
        {
            Id = 66,
            OrderRef = "066",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = DateTime.Now
        });
        harness.SeedOrderLine(new OrderLine { Id = 171, OrderId = 66, ItemId = 100, QtyOrdered = 0 });
        return harness;
    }

    private static CloseDocumentHarness CreateHarnessWithPlannedPalletOnDraftPrd()
    {
        var harness = CreateHarnessWithEmptyDraftPrd();
        harness.SeedLine(new DocLine
        {
            Id = 1752,
            DocId = 162,
            OrderLineId = 171,
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            ItemId = 100,
            Qty = 600,
            ToLocationId = 1,
            ToHu = "HU-0000462",
            PackSingleHu = true
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 35,
            PrdDocId = 162,
            DocLineId = 1752,
            OrderId = 66,
            OrderLineId = 171,
            ItemId = 100,
            HuCode = "HU-0000462",
            PlannedQty = 600,
            Status = ProductionPalletStatus.Planned,
            CreatedAt = DateTime.Now
        });
        return harness;
    }
}
