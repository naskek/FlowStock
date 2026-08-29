using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class ProductionPalletLabelBackfillTests
{
    [Fact]
    public void DryRunAndApply_BackfillOnlyEligiblePrintedRows_AndRemainIdempotent()
    {
        var harness = CreateHarness(includeAnomalies: true);
        var service = new ProductionPalletLabelBackfillService(harness.Store);

        var dryRun = service.Run(apply: false);

        Assert.Equal("DRY_RUN", dryRun.Mode);
        Assert.Equal(3, dryRun.CandidateCount);
        Assert.Equal(0, dryRun.BackfilledCount);
        Assert.Equal(2, dryRun.BlockerCount);
        Assert.Contains(dryRun.Rows, row => row.PalletId == 1
                                            && row.Action == ProductionPalletLabelBackfillAction.WouldBackfill
                                            && row.HasPrintedAt
                                            && !string.IsNullOrWhiteSpace(row.CurrentLabelFingerprint));
        Assert.Contains(dryRun.Rows, row => row.PalletId == 2
                                            && row.BlockerReason == "PRINTED_AT_MISSING");
        Assert.Contains(dryRun.Rows, row => row.PalletId == 3
                                            && row.BlockerReason == "PLANNED_HAS_PRINT_EVIDENCE");
        Assert.Null(harness.Store.GetProductionPalletByHu("HU-ELIGIBLE")!.PrintedLabelFingerprint);

        var applied = service.Run(apply: true);

        Assert.Equal(1, applied.BackfilledCount);
        Assert.Equal(2, applied.BlockerCount);
        var eligible = harness.Store.GetProductionPalletByHu("HU-ELIGIBLE")!;
        Assert.NotNull(eligible.PrintedLabelFingerprint);
        Assert.Equal(ProductionPalletLabelContract.FingerprintVersion, eligible.PrintedLabelFingerprintVersion);
        Assert.Equal(new DateTime(2026, 8, 29, 10, 0, 0), eligible.PrintedAt);
        Assert.Null(harness.Store.GetProductionPalletByHu("HU-NO-TIMESTAMP")!.PrintedLabelFingerprint);
        Assert.Equal(ProductionPalletStatus.Planned, harness.Store.GetProductionPalletByHu("HU-PLANNED-EVIDENCE")!.Status);
        var anomalousFill = new ProductionPalletService(harness.Store).Scan(10, 20, "HU-NO-TIMESTAMP");
        Assert.False(anomalousFill.Success);
        Assert.Equal(ProductionFillingErrorCodes.LabelStateUnverified, anomalousFill.Error);

        var secondApply = service.Run(apply: true);

        Assert.Equal(0, secondApply.BackfilledCount);
        Assert.Contains(secondApply.Rows, row => row.PalletId == 1
                                                 && row.Action == ProductionPalletLabelBackfillAction.AlreadyVerified);
    }

    [Fact]
    public void LegacyUnverifiedFill_IsBlockedUntilBaselineBackfill_WhenPayloadDidNotChange()
    {
        var harness = CreateHarness(includeAnomalies: false);
        var palletService = new ProductionPalletService(harness.Store);

        var blocked = palletService.Scan(10, 20, "HU-ELIGIBLE");
        Assert.False(blocked.Success);
        Assert.Equal(ProductionFillingErrorCodes.LabelStateUnverified, blocked.Error);

        var report = new ProductionPalletLabelBackfillService(harness.Store).Run(apply: true);
        Assert.Equal(1, report.BackfilledCount);

        var accepted = palletService.Scan(10, 20, "HU-ELIGIBLE");
        Assert.True(accepted.Success, $"{accepted.Error}: {accepted.ErrorMessage}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_DoesNotBackfillOrderThatBecomesCandidateAfterLockSetWasCaptured(bool useExplicitScope)
    {
        var harness = CreateHarness(includeAnomalies: false);
        harness.RunAfterNextLockOrdersForUpdate(() =>
        {
            harness.SeedOrder(new Order
            {
                Id = 11,
                OrderRef = "LBL-011",
                Type = OrderType.Customer,
                Status = OrderStatus.InProgress,
                PartnerName = "Клиент 2",
                CreatedAt = new DateTime(2026, 8, 29, 8, 10, 0)
            });
            harness.SeedOrderLine(new OrderLine
            {
                Id = 111,
                OrderId = 11,
                ItemId = 100,
                QtyOrdered = 378,
                ProductionPurpose = ProductionLinePurpose.CustomerOrder
            });
            harness.SeedDoc(new Doc
            {
                Id = 21,
                DocRef = "PRD-LABEL-LATE-CANDIDATE",
                Type = DocType.ProductionReceipt,
                Status = DocStatus.Draft,
                OrderId = 11,
                CreatedAt = new DateTime(2026, 8, 29, 9, 10, 0)
            });
            SeedPallet(
                harness,
                id: 4,
                huCode: "HU-LATE-CANDIDATE",
                status: ProductionPalletStatus.Printed,
                printedAt: new DateTime(2026, 8, 29, 10, 10, 0),
                orderId: 11,
                orderLineId: 111,
                docId: 21);
        });

        IReadOnlyCollection<long>? scope = useExplicitScope ? [10, 11] : null;
        var report = new ProductionPalletLabelBackfillService(harness.Store).Run(apply: true, scope);

        Assert.Equal(1, report.BackfilledCount);
        Assert.DoesNotContain(report.Rows, row => row.OrderId == 11);
        Assert.Null(harness.Store.GetProductionPalletByHu("HU-LATE-CANDIDATE")!.PrintedLabelFingerprint);
        Assert.NotNull(harness.Store.GetProductionPalletByHu("HU-ELIGIBLE")!.PrintedLabelFingerprint);
    }

    private static CloseDocumentHarness CreateHarness(bool includeAnomalies)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item { Id = 100, Name = "Товар", Brand = "Бренд", BaseUom = "шт", MaxQtyPerHu = 378 });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "LBL-010",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            PartnerName = "Клиент",
            CreatedAt = new DateTime(2026, 8, 29, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 100,
            QtyOrdered = includeAnomalies ? 1134 : 378,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedDoc(new Doc
        {
            Id = 20,
            DocRef = "PRD-LABEL-BACKFILL",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            CreatedAt = new DateTime(2026, 8, 29, 9, 0, 0)
        });
        SeedPallet(harness, 1, "HU-ELIGIBLE", ProductionPalletStatus.Printed, new DateTime(2026, 8, 29, 10, 0, 0));
        if (includeAnomalies)
        {
            SeedPallet(harness, 2, "HU-NO-TIMESTAMP", ProductionPalletStatus.Printed, null);
            SeedPallet(harness, 3, "HU-PLANNED-EVIDENCE", ProductionPalletStatus.Planned, new DateTime(2026, 8, 29, 10, 5, 0));
        }

        return harness;
    }

    private static void SeedPallet(
        CloseDocumentHarness harness,
        long id,
        string huCode,
        string status,
        DateTime? printedAt,
        long orderId = 10,
        long orderLineId = 101,
        long docId = 20)
    {
        harness.SeedLine(new DocLine
        {
            Id = 200 + id,
            DocId = docId,
            OrderLineId = orderLineId,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder,
            ItemId = 100,
            Qty = 378,
            ToLocationId = 1,
            ToHu = huCode,
            PackSingleHu = true
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = id,
            PrdDocId = docId,
            DocLineId = 200 + id,
            OrderId = orderId,
            OrderLineId = orderLineId,
            ItemId = 100,
            ItemName = "Товар",
            HuCode = huCode,
            PlannedQty = 378,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            Status = status,
            PrintedAt = printedAt,
            CreatedAt = new DateTime(2026, 8, 29, 9, 0, 0).AddMinutes(id)
        });
    }
}
