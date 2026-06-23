using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderHuBindingApplyFinalServiceTests
{
    [Fact]
    public void ApplyFinal_CreatesReservationOnlyAfterExplicitCommand()
    {
        var scenario = CreateScenario(orderQty: 600);
        scenario.Harness.SeedBalance(Scenario.ItemId, Scenario.LocationId, 600, "HU-NEW");
        var ledgerBefore = scenario.Harness.Store.CountLedgerEntries();

        var result = Apply(scenario.Harness, FinalLine(Scenario.LineId, [], ["HU-NEW"]));

        var line = Assert.Single(result.AppliedLines);
        Assert.Equal(["HU-NEW"], line.FinalHuCodes);
        Assert.Equal(["HU-NEW"], line.BoundHuCodes);
        Assert.Equal(600, line.ReservedQty, 3);
        Assert.Equal(ledgerBefore, scenario.Harness.Store.CountLedgerEntries());
        Assert.Contains(scenario.Harness.GetOrderReceiptPlanLines(Scenario.OrderId), plan =>
            plan.OrderLineId == Scenario.LineId && plan.ToHu == "HU-NEW" && Math.Abs(plan.QtyPlanned - 600) < 0.001);
    }

    [Fact]
    public void ApplyFinal_EmptyFinalHuCodes_DetachesOnlyAffectedLine()
    {
        var scenario = CreateScenario(orderQty: 1200);
        scenario.Harness.SeedOrderLine(new OrderLine
        {
            Id = Scenario.OtherLineId,
            OrderId = Scenario.OrderId,
            ItemId = Scenario.OtherItemId,
            QtyOrdered = 300
        });
        scenario.Harness.SeedOrderReceiptPlanLines(
            Scenario.OrderId,
            PlanLine(Scenario.LineId, Scenario.ItemId, "HU-OLD", 600, sortOrder: 0),
            PlanLine(Scenario.OtherLineId, Scenario.OtherItemId, "HU-KEEP", 300, sortOrder: 1));

        var result = Apply(scenario.Harness, FinalLine(Scenario.LineId, ["hu-old"], []));

        var line = Assert.Single(result.AppliedLines);
        Assert.Equal(["HU-OLD"], line.DetachedHuCodes);
        var plan = scenario.Harness.GetOrderReceiptPlanLines(Scenario.OrderId);
        Assert.DoesNotContain(plan, row => row.OrderLineId == Scenario.LineId);
        Assert.Contains(plan, row => row.OrderLineId == Scenario.OtherLineId && row.ToHu == "HU-KEEP");
    }

    [Fact]
    public void ApplyFinal_ExpectedBoundHuCodesComparedAsNormalizedSet()
    {
        var scenario = CreateScenario(orderQty: 600);
        scenario.Harness.SeedBalance(Scenario.ItemId, Scenario.LocationId, 600, "HU-NEW");
        scenario.Harness.SeedBalance(Scenario.ItemId, Scenario.LocationId, 600, "HU-OLD");
        scenario.Harness.SeedOrderReceiptPlanLines(
            Scenario.OrderId,
            PlanLine(Scenario.LineId, Scenario.ItemId, "hu-old", 600, sortOrder: 0));

        Apply(scenario.Harness, FinalLine(Scenario.LineId, [" HU-OLD "], ["HU-NEW"]));

        var plan = Assert.Single(scenario.Harness.GetOrderReceiptPlanLines(Scenario.OrderId));
        Assert.Equal("HU-NEW", plan.ToHu);
    }

    [Fact]
    public void ApplyFinal_FinalHuCodesOrderBecomesSortOrder()
    {
        var scenario = CreateScenario(orderQty: 1200);
        scenario.Harness.SeedBalance(Scenario.ItemId, Scenario.LocationId, 600, "HU-A");
        scenario.Harness.SeedBalance(Scenario.ItemId, Scenario.LocationId, 600, "HU-B");

        Apply(scenario.Harness, FinalLine(Scenario.LineId, [], ["HU-B", "HU-A"]));

        var plan = scenario.Harness.GetOrderReceiptPlanLines(Scenario.OrderId);
        Assert.Equal("HU-B", plan[0].ToHu);
        Assert.Equal(0, plan[0].SortOrder);
        Assert.Equal("HU-A", plan[1].ToHu);
        Assert.Equal(1, plan[1].SortOrder);
    }

    [Fact]
    public void ApplyFinal_StaleExpectedBoundHuCodesFailsWithoutPartialApply()
    {
        var scenario = CreateScenario(orderQty: 600);
        scenario.Harness.SeedOrderReceiptPlanLines(
            Scenario.OrderId,
            PlanLine(Scenario.LineId, Scenario.ItemId, "HU-OLD", 600, sortOrder: 0));

        var ex = Assert.Throws<OrderHuBindingApplyFinalException>(() =>
            Apply(scenario.Harness, FinalLine(Scenario.LineId, [], [])));

        Assert.Equal("HU_BINDING_STALE", ex.ErrorCode);
        Assert.Equal("HU-OLD", Assert.Single(scenario.Harness.GetOrderReceiptPlanLines(Scenario.OrderId)).ToHu);
    }

    [Fact]
    public void ApplyFinal_FinalBoundQtyExceedsRemainingFails()
    {
        var scenario = CreateScenario(orderQty: 600, shippedQty: 200);
        scenario.Harness.SeedBalance(Scenario.ItemId, Scenario.LocationId, 500, "HU-BIG");

        var ex = Assert.Throws<OrderHuBindingApplyFinalException>(() =>
            Apply(scenario.Harness, FinalLine(Scenario.LineId, [], ["HU-BIG"])));

        Assert.Equal("HU_QTY_EXCEEDS_REMAINING", ex.ErrorCode);
        Assert.Empty(scenario.Harness.GetOrderReceiptPlanLines(Scenario.OrderId));
    }

    [Fact]
    public void ApplyFinal_HuAlreadyBoundByOtherOrderFails()
    {
        var scenario = CreateScenario(orderQty: 600);
        scenario.Harness.SeedBalance(Scenario.ItemId, Scenario.LocationId, 600, "HU-BUSY");
        scenario.Harness.SeedOrder(new Order
        {
            Id = Scenario.OtherOrderId,
            OrderRef = "SO-OTHER",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });
        scenario.Harness.SeedOrderLine(new OrderLine
        {
            Id = Scenario.OtherLineId,
            OrderId = Scenario.OtherOrderId,
            ItemId = Scenario.ItemId,
            QtyOrdered = 600
        });
        scenario.Harness.SeedOrderReceiptPlanLines(
            Scenario.OtherOrderId,
            new OrderReceiptPlanLine
            {
                OrderId = Scenario.OtherOrderId,
                OrderLineId = Scenario.OtherLineId,
                ItemId = Scenario.ItemId,
                QtyPlanned = 600,
                ToHu = "HU-BUSY"
            });

        var ex = Assert.Throws<OrderHuBindingApplyFinalException>(() =>
            Apply(scenario.Harness, FinalLine(Scenario.LineId, [], ["HU-BUSY"])));

        Assert.Equal("HU_RESERVED_BY_OTHER_ORDER", ex.ErrorCode);
    }

    [Fact]
    public void ApplyFinal_DoesNotChangeHuOriginOrSourcePrd()
    {
        var scenario = CreateScenario(orderQty: 600);
        scenario.Harness.SeedBalance(Scenario.ItemId, Scenario.LocationId, 600, "HU-INTERNAL");
        scenario.Harness.SeedOrder(new Order
        {
            Id = Scenario.InternalOrderId,
            OrderRef = "INT-001",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });
        scenario.Harness.SeedDoc(new Doc
        {
            Id = Scenario.InternalPrdId,
            DocRef = "PRD-INT",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = Scenario.InternalOrderId,
            CreatedAt = DateTime.UtcNow
        });
        scenario.Harness.SeedLine(new DocLine
        {
            Id = Scenario.InternalDocLineId,
            DocId = Scenario.InternalPrdId,
            ItemId = Scenario.ItemId,
            Qty = 600,
            ToLocationId = Scenario.LocationId,
            ToHu = "HU-INTERNAL"
        });

        Apply(scenario.Harness, FinalLine(Scenario.LineId, [], ["HU-INTERNAL"]));

        var originDoc = Assert.Single(scenario.Harness.GetDocLines(Scenario.InternalPrdId));
        Assert.Equal("HU-INTERNAL", originDoc.ToHu);
        Assert.Equal(Scenario.InternalOrderId, scenario.Harness.GetDoc(Scenario.InternalPrdId).OrderId);
    }

    [Fact]
    public void ApplyFinal_ReadyHuCancelsWholePlannedCustomerPallet()
    {
        var scenario = CreateScenario(orderQty: 600);
        scenario.Harness.SeedBalance(Scenario.ItemId, Scenario.LocationId, 600, "HU-READY");
        SeedPlannedPallet(scenario.Harness, qty: 600);

        var result = Apply(scenario.Harness, FinalLine(Scenario.LineId, [], ["HU-READY"]));

        Assert.Equal(1, Assert.Single(result.AppliedLines).CancelledPlannedPalletCount);
        var pallet = Assert.Single(scenario.Harness.Store.GetProductionPalletsByDoc(Scenario.PrdDocId));
        Assert.Equal(ProductionPalletStatus.Cancelled, pallet.Status);
        Assert.Equal("replaced_by_ready_hu", pallet.CancelReason);
        Assert.NotNull(pallet.CancelledAt);
        Assert.NotEmpty(scenario.Harness.GetDocLines(Scenario.PrdDocId));
    }

    [Fact]
    public void ApplyFinal_PrintedPalletConflictFails()
    {
        var scenario = CreateScenario(orderQty: 600);
        scenario.Harness.SeedBalance(Scenario.ItemId, Scenario.LocationId, 600, "HU-READY");
        SeedPlannedPallet(
            scenario.Harness,
            qty: 600,
            status: ProductionPalletStatus.Printed,
            printedAt: DateTime.UtcNow);

        var ex = Assert.Throws<OrderHuBindingApplyFinalException>(() =>
            Apply(scenario.Harness, FinalLine(Scenario.LineId, [], ["HU-READY"])));

        Assert.Equal("HU_BINDING_PLAN_CONFLICT", ex.ErrorCode);
        // Новое сообщение про избыточный будущий план; printed-паллета больше не маскируется как "status=PRINTED expected=PLANNED".
        Assert.DoesNotContain(ex.Problems ?? [], problem =>
            problem.Contains("status=PRINTED", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ex.Problems ?? [], problem =>
            problem.Contains("surplus_qty", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(scenario.Harness.GetOrderReceiptPlanLines(Scenario.OrderId));
    }

    [Fact]
    public void ApplyFinal_PartialPalletCancellationConflictFails()
    {
        var scenario = CreateScenario(orderQty: 600);
        scenario.Harness.SeedBalance(Scenario.ItemId, Scenario.LocationId, 500, "HU-READY");
        SeedPlannedPallet(scenario.Harness, qty: 600);

        var ex = Assert.Throws<OrderHuBindingApplyFinalException>(() =>
            Apply(scenario.Harness, FinalLine(Scenario.LineId, [], ["HU-READY"])));

        Assert.Equal("HU_BINDING_PLAN_CONFLICT", ex.ErrorCode);
        Assert.Empty(scenario.Harness.GetOrderReceiptPlanLines(Scenario.OrderId));
    }

    [Fact]
    public void ApplyFinal_ReadyHuCancelsPlannedCustomerPallet_IgnoresOrderMarkingStatus()
    {
        var markingPrintedAt = DateTime.UtcNow;
        var scenario = CreateScenario(
            orderQty: 600,
            orderMarkingStatus: MarkingStatus.Printed,
            markingExcelGeneratedAt: markingPrintedAt,
            markingPrintedAt: markingPrintedAt);
        scenario.Harness.SeedBalance(Scenario.ItemId, Scenario.LocationId, 600, "HU-READY");
        scenario.Harness.SeedOrder(new Order
        {
            Id = Scenario.InternalOrderId,
            OrderRef = "INT-001",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });
        scenario.Harness.SeedDoc(new Doc
        {
            Id = Scenario.InternalPrdId,
            DocRef = "PRD-INT",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = Scenario.InternalOrderId,
            CreatedAt = DateTime.UtcNow
        });
        scenario.Harness.SeedLine(new DocLine
        {
            Id = Scenario.InternalDocLineId,
            DocId = Scenario.InternalPrdId,
            ItemId = Scenario.ItemId,
            Qty = 600,
            ToLocationId = Scenario.LocationId,
            ToHu = "HU-READY"
        });
        SeedPlannedPallet(scenario.Harness, qty: 600);
        var ledgerBefore = scenario.Harness.Store.CountLedgerEntries();

        var result = Apply(scenario.Harness, FinalLine(Scenario.LineId, [], ["HU-READY"]));

        Assert.Equal(1, Assert.Single(result.AppliedLines).CancelledPlannedPalletCount);
        var pallet = Assert.Single(scenario.Harness.Store.GetProductionPalletsByDoc(Scenario.PrdDocId));
        Assert.Equal(ProductionPalletStatus.Cancelled, pallet.Status);
        Assert.Equal("replaced_by_ready_hu", pallet.CancelReason);
        Assert.NotNull(pallet.CancelledAt);
        Assert.Equal(Scenario.InternalOrderId, scenario.Harness.GetDoc(Scenario.InternalPrdId).OrderId);
        Assert.Equal("HU-READY", Assert.Single(scenario.Harness.GetDocLines(Scenario.InternalPrdId)).ToHu);
        Assert.Equal(ledgerBefore, scenario.Harness.Store.CountLedgerEntries());
    }

    [Fact]
    public void ApplyFinal_DetachDoesNotRestoreCustomerPlannedNeed_WithoutReopeningCancelledPallet()
    {
        var scenario = CreateScenario(orderQty: 600);
        scenario.Harness.SeedBalance(Scenario.ItemId, Scenario.LocationId, 600, "HU-OLD");
        SeedPlannedPallet(scenario.Harness, qty: 600, status: ProductionPalletStatus.Cancelled, cancelReason: "replaced_by_ready_hu");
        scenario.Harness.SeedOrderReceiptPlanLines(
            Scenario.OrderId,
            PlanLine(Scenario.LineId, Scenario.ItemId, "HU-OLD", 600, sortOrder: 0));

        var result = Apply(scenario.Harness, FinalLine(Scenario.LineId, ["HU-OLD"], []));

        Assert.Empty(scenario.Harness.GetOrderReceiptPlanLines(Scenario.OrderId));
        var pallets = scenario.Harness.Store.GetDocsByOrder(Scenario.OrderId)
            .SelectMany(doc => scenario.Harness.Store.GetProductionPalletsByDoc(doc.Id))
            .ToArray();
        Assert.Contains(pallets, pallet => pallet.Status == ProductionPalletStatus.Cancelled
                                          && pallet.CancelReason == "replaced_by_ready_hu");
        Assert.DoesNotContain(pallets, pallet => pallet.Status == ProductionPalletStatus.Planned);
        Assert.Equal(0, Assert.Single(result.AppliedLines).RestoredPlannedQty);
        Assert.DoesNotContain(scenario.Harness.Store.GetOrders(), order => order.Type == OrderType.Internal);
    }

    [Fact]
    public void ApplyFinal_FilledPalletPlusWarehouseHu_DoesNotConflictAndKeepsFilled()
    {
        // Bug 2 / Сценарий A: ordered 1800, FILLED 600, склад 600+600 → surplus 0, успех, FILLED не отменяется.
        var scenario = CreateScenario(orderQty: 1800);
        scenario.Harness.SeedBalance(Scenario.ItemId, Scenario.LocationId, 600, "HU-963");
        scenario.Harness.SeedBalance(Scenario.ItemId, Scenario.LocationId, 600, "HU-965");
        SeedFilledPallet(scenario.Harness, qty: 600, huCode: "HU-FILLED", postLedger: false);

        var result = Apply(scenario.Harness, FinalLine(Scenario.LineId, [], ["HU-963", "HU-965"]));

        var line = Assert.Single(result.AppliedLines);
        Assert.Equal(0, line.CancelledPlannedPalletCount);
        var planHus = scenario.Harness.GetOrderReceiptPlanLines(Scenario.OrderId).Select(plan => plan.ToHu).OrderBy(hu => hu).ToArray();
        Assert.Equal(new[] { "HU-963", "HU-965" }, planHus);
        var filled = Assert.Single(scenario.Harness.Store.GetProductionPalletsByDoc(Scenario.PrdDocId + 1));
        Assert.Equal(ProductionPalletStatus.Filled, filled.Status);
    }

    [Fact]
    public void ApplyFinal_ConfirmedFilledHuAlsoFinalBound_DeduplicatesCoverage()
    {
        // Bug 2 / анти-двойной-счёт: один HU подтверждён FILLED и выбран как final → coverage=600, surplus=0, успех.
        var scenario = CreateScenario(orderQty: 600);
        SeedFilledPallet(scenario.Harness, qty: 600, huCode: "HU-DUP", postLedger: true);
        scenario.Harness.SeedBalance(Scenario.ItemId, Scenario.LocationId, 600, "HU-DUP");

        var result = Apply(scenario.Harness, FinalLine(Scenario.LineId, [], ["HU-DUP"]));

        var line = Assert.Single(result.AppliedLines);
        Assert.Equal(0, line.CancelledPlannedPalletCount);
        Assert.Equal("HU-DUP", Assert.Single(scenario.Harness.GetOrderReceiptPlanLines(Scenario.OrderId)).ToHu);
        var filled = Assert.Single(scenario.Harness.Store.GetProductionPalletsByDoc(Scenario.PrdDocId + 1));
        Assert.Equal(ProductionPalletStatus.Filled, filled.Status);
    }

    private static OrderHuBindingApplyFinalResult Apply(
        CloseDocumentHarness harness,
        params OrderHuBindingApplyFinalLineRequest[] lines)
    {
        return new OrderHuBindingApplyFinalService(harness.Store).ApplyFinal(
            Scenario.OrderId,
            new OrderHuBindingApplyFinalRequest
            {
                Mode = OrderHuBindingApplyFinalRequest.ReplaceFinalSelectionMode,
                Lines = lines
            });
    }

    private static OrderHuBindingApplyFinalLineRequest FinalLine(
        long orderLineId,
        IReadOnlyList<string> expected,
        IReadOnlyList<string> final) =>
        new()
        {
            OrderLineId = orderLineId,
            ExpectedBoundHuCodes = expected,
            FinalHuCodes = final
        };

    private static Scenario CreateScenario(
        double orderQty,
        double shippedQty = 0,
        MarkingStatus orderMarkingStatus = MarkingStatus.NotRequired,
        DateTime? markingExcelGeneratedAt = null,
        DateTime? markingPrintedAt = null)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = Scenario.LocationId, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItemType(new ItemType { Id = Scenario.ItemTypeId, Name = "Готовая продукция", EnableOrderReservation = true });
        harness.SeedItem(new Item
        {
            Id = Scenario.ItemId,
            Name = "Товар",
            BaseUom = "шт",
            ItemTypeId = Scenario.ItemTypeId,
            MaxQtyPerHu = 600
        });
        harness.SeedItem(new Item
        {
            Id = Scenario.OtherItemId,
            Name = "Другой товар",
            BaseUom = "шт",
            ItemTypeId = Scenario.ItemTypeId,
            MaxQtyPerHu = 300
        });
        harness.SeedOrder(new Order
        {
            Id = Scenario.OrderId,
            OrderRef = "SO-001",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            MarkingStatus = orderMarkingStatus,
            MarkingExcelGeneratedAt = markingExcelGeneratedAt,
            MarkingPrintedAt = markingPrintedAt,
            CreatedAt = DateTime.UtcNow
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = Scenario.LineId,
            OrderId = Scenario.OrderId,
            ItemId = Scenario.ItemId,
            QtyOrdered = orderQty
        });
        if (shippedQty > 0)
        {
            harness.SeedShippedTotalsByOrderLine(Scenario.OrderId, new Dictionary<long, double>
            {
                [Scenario.LineId] = shippedQty
            });
        }

        return new Scenario(harness);
    }

    private static OrderReceiptPlanLine PlanLine(long orderLineId, long itemId, string huCode, double qty, int sortOrder) =>
        new()
        {
            OrderId = Scenario.OrderId,
            OrderLineId = orderLineId,
            ItemId = itemId,
            QtyPlanned = qty,
            ToHu = huCode,
            SortOrder = sortOrder
        };

    private static void SeedPlannedPallet(
        CloseDocumentHarness harness,
        double qty,
        string status = ProductionPalletStatus.Planned,
        DateTime? printedAt = null,
        string? cancelReason = null)
    {
        harness.SeedDoc(new Doc
        {
            Id = Scenario.PrdDocId,
            DocRef = "PRD-SO-001",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = Scenario.OrderId,
            CreatedAt = DateTime.UtcNow
        });
        harness.SeedLine(new DocLine
        {
            Id = Scenario.PrdDocLineId,
            DocId = Scenario.PrdDocId,
            OrderLineId = Scenario.LineId,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder,
            ItemId = Scenario.ItemId,
            Qty = qty,
            ToLocationId = Scenario.LocationId,
            ToHu = "HU-PLANNED"
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = Scenario.PalletId,
            PrdDocId = Scenario.PrdDocId,
            DocLineId = Scenario.PrdDocLineId,
            OrderId = Scenario.OrderId,
            OrderLineId = Scenario.LineId,
            ItemId = Scenario.ItemId,
            ItemName = "Товар",
            HuCode = "HU-PLANNED",
            PlannedQty = qty,
            ToLocationId = Scenario.LocationId,
            Status = status,
            PrintedAt = printedAt,
            CancelReason = cancelReason,
            CancelledAt = cancelReason == null ? null : DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            Lines =
            [
                new ProductionPalletComponentLine
                {
                    Id = Scenario.PalletLineId,
                    ProductionPalletId = Scenario.PalletId,
                    DocLineId = Scenario.PrdDocLineId,
                    OrderLineId = Scenario.LineId,
                    ItemId = Scenario.ItemId,
                    ItemName = "Товар",
                    PlannedQty = qty,
                    FilledQty = 0,
                    CreatedAt = DateTime.UtcNow
                }
            ]
        });
    }

    private static void SeedFilledPallet(
        CloseDocumentHarness harness,
        double qty,
        string huCode,
        bool postLedger)
    {
        const long docId = Scenario.PrdDocId + 1;
        const long docLineId = Scenario.PrdDocLineId + 1;
        const long palletId = Scenario.PalletId + 1;
        const long palletLineId = Scenario.PalletLineId + 1;

        harness.SeedDoc(new Doc
        {
            Id = docId,
            DocRef = "PRD-FILLED",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = Scenario.OrderId,
            CreatedAt = DateTime.UtcNow
        });
        harness.SeedLine(new DocLine
        {
            Id = docLineId,
            DocId = docId,
            OrderLineId = Scenario.LineId,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder,
            ItemId = Scenario.ItemId,
            Qty = qty,
            ToLocationId = Scenario.LocationId,
            ToHu = huCode
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = palletId,
            PrdDocId = docId,
            DocLineId = docLineId,
            OrderId = Scenario.OrderId,
            OrderLineId = Scenario.LineId,
            ItemId = Scenario.ItemId,
            ItemName = "Товар",
            HuCode = huCode,
            PlannedQty = qty,
            ToLocationId = Scenario.LocationId,
            Status = ProductionPalletStatus.Filled,
            FilledAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            Lines =
            [
                new ProductionPalletComponentLine
                {
                    Id = palletLineId,
                    ProductionPalletId = palletId,
                    DocLineId = docLineId,
                    OrderLineId = Scenario.LineId,
                    ItemId = Scenario.ItemId,
                    ItemName = "Товар",
                    PlannedQty = qty,
                    FilledQty = qty,
                    CreatedAt = DateTime.UtcNow
                }
            ]
        });

        if (postLedger)
        {
            harness.SeedLedgerEntry(docId, Scenario.ItemId, Scenario.LocationId, qty, huCode);
        }
    }

    private sealed record Scenario(CloseDocumentHarness Harness)
    {
        public const long OrderId = 10;
        public const long OtherOrderId = 11;
        public const long LineId = 101;
        public const long OtherLineId = 102;
        public const long ItemTypeId = 1;
        public const long ItemId = 6;
        public const long OtherItemId = 7;
        public const long LocationId = 1;
        public const long PrdDocId = 201;
        public const long PrdDocLineId = 301;
        public const long PalletId = 401;
        public const long PalletLineId = 501;
        public const long InternalOrderId = 701;
        public const long InternalPrdId = 702;
        public const long InternalDocLineId = 703;
    }
}
