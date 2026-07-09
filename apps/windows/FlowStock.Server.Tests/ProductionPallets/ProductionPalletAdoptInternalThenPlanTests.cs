using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class ProductionPalletAdoptInternalThenPlanTests
{
    [Fact]
    public void Preview_ProjectsPlannedAndPrintedInternalHu_ForAdoption()
    {
        var harness = CreateHarness(customerQty: 756, sourceQty: 756);
        SeedSourcePallet(harness, 4001, 401, "HU-INT-PRINTED", 378, ProductionPalletStatus.Printed);
        SeedSourcePallet(harness, 4002, 402, "HU-INT-PLANNED", 378, ProductionPalletStatus.Planned);
        var service = new ProductionPalletService(harness.Store);

        var preview = service.GetCustomerPrePlanCoveragePreview(10);

        Assert.True(preview.HasWarning);
        Assert.Equal(2, preview.ProjectedAdoptedPalletCount);
        Assert.Equal(756, preview.ProjectedAdoptedQty);
        Assert.Equal(0, preview.ProjectedRemainingQtyAfterAdoption);
        Assert.Contains(preview.AdoptableInternalPlannedHus, row =>
            row.HuCode == "HU-INT-PRINTED"
            && row.Status == ProductionPalletStatus.Printed
            && !row.WillRequireReprint);
        Assert.Contains(preview.AdoptableInternalPlannedHus, row =>
            row.HuCode == "HU-INT-PLANNED"
            && row.Status == ProductionPalletStatus.Planned
            && !row.WillRequireReprint);
    }

    [Fact]
    public void AdoptInternalThenPlan_TransfersPrintedAsPrinted_ReducesSourceQty_AndClearsWarning()
    {
        var harness = CreateHarness(customerQty: 378, sourceQty: 756);
        SeedSourcePallet(harness, 4001, 401, "HU-INT-PRINTED", 378, ProductionPalletStatus.Printed);
        var service = new ProductionPalletService(harness.Store);

        var result = service.PlanOrder(10, ProductionPalletPlanMode.AdoptInternalThenPlan);

        Assert.Equal(1, result.AdoptedPalletCount);
        Assert.Equal(378, result.AdoptedQty);
        var adopted = Assert.Single(result.AdoptedInternalPlannedHus);
        Assert.False(adopted.WillRequireReprint);
        Assert.Empty(result.ReprintRequiredHus);

        var pallet = Assert.Single(harness.Store.GetProductionPalletsByDoc(result.PrdDocId));
        Assert.Equal(10, pallet.OrderId);
        Assert.Equal(101, pallet.OrderLineId);
        Assert.Equal(ProductionPalletStatus.Printed, pallet.Status);
        Assert.Equal(new DateTime(2026, 7, 2, 8, 0, 0), pallet.PrintedAt);
        Assert.Equal(378, harness.Store.GetOrderLines(30).Single().QtyOrdered);
        Assert.Empty(harness.LedgerEntries);
        Assert.Empty(harness.Store.GetOrderReceiptPlanLines(10));

        var printRow = Assert.Single(service.GetPrintRows(10));
        Assert.Equal("HU-INT-PRINTED", printRow.HuCode);
        Assert.Equal(10, printRow.OrderId);
        Assert.Equal(ProductionPalletStatus.Printed, printRow.Status);

        var previewAfter = service.GetCustomerPrePlanCoveragePreview(10);
        Assert.False(previewAfter.HasWarning);
        Assert.Empty(previewAfter.Lines);
    }

    [Theory]
    [InlineData(ProductionPalletStatus.Filled)]
    [InlineData(ProductionPalletStatus.Cancelled)]
    public void AdoptInternalThenPlan_WhenPalletBecomesIneligibleDuringTransfer_FailsAndRollsBack(string staleStatus)
    {
        var harness = CreateHarness(customerQty: 378, sourceQty: 756);
        SeedSourcePallet(harness, 4001, 401, "HU-INT-STALE", 378, ProductionPalletStatus.Planned);
        harness.ChangeNextSelectedAdoptionPalletStatusBeforeTransfer(4001, staleStatus);
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<InvalidOperationException>(
            () => service.PlanOrder(10, ProductionPalletPlanMode.AdoptInternalThenPlan));

        Assert.Contains("planned HU", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(756, harness.Store.GetOrderLines(30).Single().QtyOrdered);
        var sourceDocLines = harness.Store.GetDocLines(40);
        var sourceLine = Assert.Single(sourceDocLines);
        Assert.Equal(40, sourceLine.DocId);
        Assert.Equal(301, sourceLine.OrderLineId);
        Assert.Equal(ProductionLinePurpose.InternalStock, sourceLine.ProductionPurpose);
        var sourcePallet = Assert.Single(harness.Store.GetProductionPalletsByDoc(40));
        Assert.Equal(30, sourcePallet.OrderId);
        Assert.Equal(301, sourcePallet.OrderLineId);
        Assert.Equal(ProductionPalletStatus.Planned, sourcePallet.Status);
        Assert.Empty(harness.Store.GetDocsByOrder(10).SelectMany(doc => harness.Store.GetProductionPalletsByDoc(doc.Id)));
        Assert.Empty(harness.LedgerEntries);
        Assert.Empty(harness.Store.GetOrderReceiptPlanLines(10));
    }

    [Theory]
    [InlineData(SelectedAdoptionComponentLineMutation.Remove)]
    [InlineData(SelectedAdoptionComponentLineMutation.Change)]
    public void AdoptInternalThenPlan_WhenComponentLineDisappearsOrChangesDuringTransfer_FailsAndRollsBack(
        SelectedAdoptionComponentLineMutation mutation)
    {
        var harness = CreateHarness(customerQty: 378, sourceQty: 756);
        SeedSourcePallet(harness, 4001, 401, "HU-INT-LINE-RACE", 378, ProductionPalletStatus.Planned);
        harness.ChangeNextSelectedAdoptionComponentLinesBeforeTransfer(4001, mutation);
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<InvalidOperationException>(
            () => service.PlanOrder(10, ProductionPalletPlanMode.AdoptInternalThenPlan));

        Assert.Contains("состав", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(756, harness.Store.GetOrderLines(30).Single().QtyOrdered);
        Assert.Single(harness.Store.GetDocLines(40));
        var sourcePallet = Assert.Single(harness.Store.GetProductionPalletsByDoc(40));
        Assert.Equal(30, sourcePallet.OrderId);
        Assert.Equal(301, sourcePallet.OrderLineId);
        var sourceComponent = Assert.Single(sourcePallet.Lines);
        Assert.Equal(301, sourceComponent.OrderLineId);
        Assert.Equal(401, sourceComponent.DocLineId);
        Assert.Empty(harness.Store.GetDocsByOrder(10).SelectMany(doc => harness.Store.GetProductionPalletsByDoc(doc.Id)));
        Assert.Empty(harness.LedgerEntries);
        Assert.Empty(harness.Store.GetOrderReceiptPlanLines(10));
    }

    [Fact]
    public void AdoptInternalThenPlan_WhenExtraComponentLineAppearsDuringTransfer_FailsAndRollsBack()
    {
        var harness = CreateHarness(customerQty: 378, sourceQty: 756);
        SeedSourcePallet(harness, 4001, 401, "HU-INT-EXTRA-LINE", 378, ProductionPalletStatus.Planned);
        harness.ChangeNextSelectedAdoptionComponentLinesBeforeTransfer(4001, SelectedAdoptionComponentLineMutation.AddExtra);
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<InvalidOperationException>(
            () => service.PlanOrder(10, ProductionPalletPlanMode.AdoptInternalThenPlan));

        Assert.Contains("состав", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(756, harness.Store.GetOrderLines(30).Single().QtyOrdered);
        var sourcePallet = Assert.Single(harness.Store.GetProductionPalletsByDoc(40));
        Assert.Equal(30, sourcePallet.OrderId);
        Assert.Single(sourcePallet.Lines);
        Assert.Single(harness.Store.GetDocLines(40));
        Assert.Empty(harness.Store.GetDocsByOrder(10).SelectMany(doc => harness.Store.GetProductionPalletsByDoc(doc.Id)));
        Assert.Empty(harness.LedgerEntries);
        Assert.Empty(harness.Store.GetOrderReceiptPlanLines(10));
    }

    [Fact]
    public void AdoptInternalThenPlan_SuccessfulTransferKeepsPalletLinesAndDocLinesConsistent()
    {
        var harness = CreateHarness(customerQty: 378, sourceQty: 756);
        SeedSourcePallet(harness, 4001, 401, "HU-INT-PLANNED", 378, ProductionPalletStatus.Planned);
        var service = new ProductionPalletService(harness.Store);

        var result = service.PlanOrder(10, ProductionPalletPlanMode.AdoptInternalThenPlan);

        var pallet = Assert.Single(harness.Store.GetProductionPalletsByDoc(result.PrdDocId));
        Assert.Equal(result.PrdDocId, pallet.PrdDocId);
        Assert.Equal(10, pallet.OrderId);
        Assert.Equal(101, pallet.OrderLineId);
        Assert.Equal(ProductionPalletStatus.Planned, pallet.Status);
        Assert.Null(pallet.PrintedAt);
        var component = Assert.Single(pallet.Lines);
        Assert.Equal(101, component.OrderLineId);
        var targetDocLine = Assert.Single(harness.Store.GetDocLines(result.PrdDocId));
        Assert.Equal(401, targetDocLine.Id);
        Assert.Equal(result.PrdDocId, targetDocLine.DocId);
        Assert.Equal(101, targetDocLine.OrderLineId);
        Assert.Equal(ProductionLinePurpose.CustomerOrder, targetDocLine.ProductionPurpose);
        Assert.Empty(harness.Store.GetDocLines(40));
    }

    [Fact]
    public void AdoptInternalThenPlan_WhenFullyAdoptedSourceLineHasNoBlockers_RemovesSourceOrderLine()
    {
        var harness = CreateHarness(customerQty: 378, sourceQty: 378);
        SeedSourcePallet(harness, 4001, 401, "HU-INT-FULL", 378, ProductionPalletStatus.Planned);
        var service = new ProductionPalletService(harness.Store);

        var result = service.PlanOrder(10, ProductionPalletPlanMode.AdoptInternalThenPlan);

        Assert.Equal(1, result.AdoptedPalletCount);
        Assert.Empty(harness.Store.GetOrderLines(30));
        Assert.Empty(harness.Store.GetDocsByOrder(30).SelectMany(doc => harness.Store.GetProductionPalletsByDoc(doc.Id)));
        Assert.Empty(harness.Store.GetOrderReceiptPlanLines(30));
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void AdoptInternalThenPlan_WhenFullyAdoptedSourceLineHasOnlyStaleInternalReceiptPlanRows_CleansRowsAndRemovesLine()
    {
        var harness = CreateHarness(customerQty: 378, sourceQty: 378);
        SeedSourcePallet(harness, 4001, 401, "HU-INT-FULL-STALE-PLAN", 378, ProductionPalletStatus.Planned);
        harness.SeedOrderReceiptPlanLines(30, new OrderReceiptPlanLine
        {
            Id = 9001,
            OrderId = 30,
            OrderLineId = 301,
            ItemId = 100,
            ItemName = "Аджика",
            QtyPlanned = 378,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            ToHu = "HU-STALE-INTERNAL",
            SortOrder = 1
        });
        var service = new ProductionPalletService(harness.Store);

        _ = service.PlanOrder(10, ProductionPalletPlanMode.AdoptInternalThenPlan);

        Assert.Empty(harness.Store.GetOrderLines(30));
        Assert.Empty(harness.Store.GetOrderReceiptPlanLines(30));
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void AdoptInternalThenPlan_WhenDepletedSourceLineHasRemainingDocLineBlocker_DoesNotDeleteSourceOrderLine()
    {
        var harness = CreateHarness(customerQty: 378, sourceQty: 378);
        SeedSourcePallet(harness, 4001, 401, "HU-INT-FULL-BLOCKED", 378, ProductionPalletStatus.Planned);
        harness.SeedLine(new DocLine
        {
            Id = 499,
            DocId = 40,
            OrderLineId = 301,
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            ItemId = 100,
            Qty = 1,
            ToLocationId = 1,
            ToHu = "HU-BLOCKER",
            PackSingleHu = true
        });
        var service = new ProductionPalletService(harness.Store);

        _ = service.PlanOrder(10, ProductionPalletPlanMode.AdoptInternalThenPlan);

        var sourceLine = Assert.Single(harness.Store.GetOrderLines(30));
        Assert.Equal(301, sourceLine.Id);
        Assert.Equal(0, sourceLine.QtyOrdered);
        Assert.Contains(harness.Store.GetDocLines(40), line => line.Id == 499 && line.OrderLineId == 301);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void SourceInternalPlanOrder_AfterAdoption_CreatesOnlyRemainingQuantity()
    {
        var harness = CreateHarness(customerQty: 378, sourceQty: 756);
        SeedSourcePallet(harness, 4001, 401, "HU-INT-PLANNED", 378, ProductionPalletStatus.Planned);
        var service = new ProductionPalletService(harness.Store);

        _ = service.PlanOrder(10, ProductionPalletPlanMode.AdoptInternalThenPlan);

        var internalReplan = service.PlanOrder(30);

        var sourcePallets = harness.Store.GetProductionPalletsByDoc(internalReplan.PrdDocId);
        Assert.Single(sourcePallets);
        Assert.Equal(378, sourcePallets.Sum(pallet => pallet.PlannedQty));
        Assert.Equal(378, harness.Store.GetOrderLines(30).Single().QtyOrdered);
    }

    [Fact]
    public void AdoptInternalThenPlan_DoesNotAdoptSourceOrderThatAppearsAfterLockSnapshot()
    {
        var harness = CreateHarness(customerQty: 378, sourceQty: 0);
        harness.RunAfterNextLockOrdersForUpdate(() =>
        {
            SeedSourceOrderWithPallet(
                harness,
                sourceOrderId: 31,
                sourceOrderRef: "105",
                sourceOrderLineId: 311,
                sourcePrdDocId: 41,
                sourceDocLineId: 411,
                palletId: 4101,
                huCode: "HU-INT-LATE",
                qty: 378,
                status: ProductionPalletStatus.Planned);
        });
        var service = new ProductionPalletService(harness.Store);

        var result = service.PlanOrder(10, ProductionPalletPlanMode.AdoptInternalThenPlan);

        Assert.Equal(0, result.AdoptedPalletCount);
        Assert.Empty(result.AdoptedInternalPlannedHus);
        Assert.Equal(1, result.NewlyPlannedPalletCount);
        var lateSourcePallet = Assert.Single(harness.Store.GetProductionPalletsByDoc(41));
        Assert.Equal(31, lateSourcePallet.OrderId);
        Assert.Equal(311, lateSourcePallet.OrderLineId);
        Assert.Equal(ProductionPalletStatus.Planned, lateSourcePallet.Status);
        Assert.Equal(378, harness.Store.GetOrderLines(31).Single().QtyOrdered);
        Assert.Empty(harness.LedgerEntries);
        Assert.Empty(harness.Store.GetOrderReceiptPlanLines(10));
    }

    [Fact]
    public void SourceInternalPlanOrder_WhenAdoptionFullyDepletesExpectedOutput_DoesNotRecreateTransferredHu()
    {
        var harness = CreateHarness(customerQty: 378, sourceQty: 378);
        SeedSourcePallet(harness, 4001, 401, "HU-INT-FULL", 378, ProductionPalletStatus.Planned);
        var service = new ProductionPalletService(harness.Store);

        _ = service.PlanOrder(10, ProductionPalletPlanMode.AdoptInternalThenPlan);

        Assert.Empty(harness.Store.GetOrderLines(30));
        var sourceRemaining = OrderReceiptRemainingForTest(harness, 30);
        Assert.DoesNotContain(sourceRemaining, line => line.QtyRemaining > 0.0001);

        Assert.Throws<InvalidOperationException>(() => service.PlanOrder(30));
        Assert.Empty(harness.Store.GetDocsByOrder(30).SelectMany(doc => harness.Store.GetProductionPalletsByDoc(doc.Id)));
    }

    [Fact]
    public void PartialAdoption_LeavesOnlyRemainingInternalExpectedOutput()
    {
        var harness = CreateHarness(customerQty: 378, sourceQty: 756);
        SeedSourcePallet(harness, 4001, 401, "HU-INT-PLANNED", 378, ProductionPalletStatus.Planned);
        var service = new ProductionPalletService(harness.Store);

        _ = service.PlanOrder(10, ProductionPalletPlanMode.AdoptInternalThenPlan);

        var sourceRemaining = OrderReceiptRemainingForTest(harness, 30);
        Assert.Equal(378, sourceRemaining.Single().QtyRemaining);
        var previewForSecondCustomer = service.GetCustomerPrePlanCoveragePreview(11);
        Assert.True(previewForSecondCustomer.HasWarning);
        Assert.Equal(378, Assert.Single(previewForSecondCustomer.Lines).ExpectedQty);
    }

    private static CloseDocumentHarness CreateHarness(double customerQty, double sourceQty)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Аджика",
            BaseUom = "шт",
            MaxQtyPerHu = 378
        });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "086",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            PartnerName = "Клиент",
            CreatedAt = new DateTime(2026, 7, 1, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 100,
            QtyOrdered = customerQty,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedOrder(new Order
        {
            Id = 11,
            OrderRef = "087",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            PartnerName = "Клиент 2",
            CreatedAt = new DateTime(2026, 7, 1, 8, 30, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 111,
            OrderId = 11,
            ItemId = 100,
            QtyOrdered = 378,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedOrder(new Order
        {
            Id = 30,
            OrderRef = "104",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 7, 1, 9, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 301,
            OrderId = 30,
            ItemId = 100,
            QtyOrdered = sourceQty
        });
        harness.SeedDoc(new Doc
        {
            Id = 40,
            DocRef = "PRD-2026-000040",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 30,
            OrderRef = "104",
            CreatedAt = new DateTime(2026, 7, 1, 10, 0, 0)
        });
        return harness;
    }

    private static void SeedSourcePallet(
        CloseDocumentHarness harness,
        long palletId,
        long docLineId,
        string huCode,
        double qty,
        string status)
    {
        SeedSourceOrderWithPallet(
            harness,
            sourceOrderId: 30,
            sourceOrderRef: "104",
            sourceOrderLineId: 301,
            sourcePrdDocId: 40,
            sourceDocLineId: docLineId,
            palletId: palletId,
            huCode: huCode,
            qty: qty,
            status: status,
            seedOrder: false);
    }

    private static void SeedSourceOrderWithPallet(
        CloseDocumentHarness harness,
        long sourceOrderId,
        string sourceOrderRef,
        long sourceOrderLineId,
        long sourcePrdDocId,
        long sourceDocLineId,
        long palletId,
        string huCode,
        double qty,
        string status,
        bool seedOrder = true)
    {
        if (seedOrder)
        {
            harness.SeedOrder(new Order
            {
                Id = sourceOrderId,
                OrderRef = sourceOrderRef,
                Type = OrderType.Internal,
                Status = OrderStatus.InProgress,
                CreatedAt = new DateTime(2026, 7, 1, 9, 30, 0)
            });
            harness.SeedOrderLine(new OrderLine
            {
                Id = sourceOrderLineId,
                OrderId = sourceOrderId,
                ItemId = 100,
                QtyOrdered = qty
            });
            harness.SeedDoc(new Doc
            {
                Id = sourcePrdDocId,
                DocRef = $"PRD-2026-{sourcePrdDocId:000000}",
                Type = DocType.ProductionReceipt,
                Status = DocStatus.Draft,
                OrderId = sourceOrderId,
                OrderRef = sourceOrderRef,
                CreatedAt = new DateTime(2026, 7, 1, 10, 30, 0)
            });
        }

        harness.SeedLine(new DocLine
        {
            Id = sourceDocLineId,
            DocId = sourcePrdDocId,
            OrderLineId = sourceOrderLineId,
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            ItemId = 100,
            Qty = qty,
            ToLocationId = 1,
            ToHu = huCode,
            PackSingleHu = true
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = palletId,
            PrdDocId = sourcePrdDocId,
            DocLineId = sourceDocLineId,
            OrderId = sourceOrderId,
            OrderLineId = sourceOrderLineId,
            ItemId = 100,
            ItemName = "Аджика",
            HuCode = huCode,
            PlannedQty = qty,
            ToLocationId = 1,
            Status = status,
            PrintedAt = status == ProductionPalletStatus.Printed ? new DateTime(2026, 7, 2, 8, 0, 0) : null,
            CreatedAt = new DateTime(2026, 7, 1, 10, 0, 0),
            Lines = new[]
            {
                new ProductionPalletComponentLine
                {
                    Id = palletId * 1000 + 1,
                    ProductionPalletId = palletId,
                    DocLineId = sourceDocLineId,
                    OrderLineId = sourceOrderLineId,
                    ItemId = 100,
                    ItemName = "Аджика",
                    PlannedQty = qty,
                    CreatedAt = new DateTime(2026, 7, 1, 10, 0, 0)
                }
            }
        });
    }

    private static IReadOnlyList<OrderReceiptLine> OrderReceiptRemainingForTest(CloseDocumentHarness harness, long orderId)
    {
        var service = new DocumentService(harness.Store);
        return service.GetOrderReceiptRemaining(orderId);
    }
}
