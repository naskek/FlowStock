using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderHuBindingManageApplyServiceTests
{
    private const long ItemTypeId = 1;
    private const long Loc = 1;
    private const long ItemA = 6;
    private const long ItemB = 7;
    private const long OrderA = 10;
    private const long OrderB = 11;
    private const long OrderC = 12;
    private const long LineA = 101;
    private const long LineA2 = 102;
    private const long LineB = 111;
    private const long LineC = 121;

    [Fact]
    public void Apply_BindFreeHu_CreatesReservation()
    {
        var harness = NewHarness();
        SeedOrder(harness, OrderA, "SO-A");
        SeedLine(harness, LineA, OrderA, 600);
        harness.SeedBalance(ItemA, Loc, 600, "HU-1");

        var result = Apply(harness, NoExpected, Line(OrderA, LineA, [], ["HU-1"]));

        var line = Assert.Single(Assert.Single(result.Orders).AppliedLines);
        Assert.Equal(["HU-1"], line.BoundHuCodes);
        Assert.Equal("HU-1", Assert.Single(harness.GetOrderReceiptPlanLines(OrderA)).ToHu);
    }

    [Fact]
    public void Apply_Detach_RemovesReservation()
    {
        var harness = NewHarness();
        SeedOrder(harness, OrderA, "SO-A");
        SeedLine(harness, LineA, OrderA, 600);
        harness.SeedBalance(ItemA, Loc, 600, "HU-1");
        harness.SeedOrderReceiptPlanLines(OrderA, Plan(OrderA, LineA, "HU-1", 600));

        var result = Apply(harness, NoExpected, Line(OrderA, LineA, ["HU-1"], []));

        var line = Assert.Single(Assert.Single(result.Orders).AppliedLines);
        Assert.Equal(["HU-1"], line.DetachedHuCodes);
        Assert.Empty(harness.GetOrderReceiptPlanLines(OrderA));
    }

    [Fact]
    public void Apply_Detach_DoesNotRestoreProductionPlan()
    {
        var harness = NewHarness();
        SeedOrder(harness, OrderA, "SO-A");
        SeedLine(harness, LineA, OrderA, 600);
        harness.SeedBalance(ItemA, Loc, 600, "HU-1");
        harness.SeedOrderReceiptPlanLines(OrderA, Plan(OrderA, LineA, "HU-1", 600));
        SeedPlannedPallet(harness, OrderA, LineA, 600, ProductionPalletStatus.Cancelled);

        var result = Apply(harness, NoExpected, Line(OrderA, LineA, ["HU-1"], []));

        var line = Assert.Single(Assert.Single(result.Orders).AppliedLines);
        Assert.Equal(0, line.RestoredPlannedQty);
        Assert.Empty(harness.GetOrderReceiptPlanLines(OrderA));
        var pallets = harness.Store.GetDocsByOrder(OrderA)
            .SelectMany(doc => harness.Store.GetProductionPalletsByDoc(doc.Id))
            .ToArray();
        Assert.Contains(pallets, pallet => pallet.Status == ProductionPalletStatus.Cancelled);
        Assert.DoesNotContain(pallets, pallet => pallet.Status == ProductionPalletStatus.Planned);
    }

    [Fact]
    public void Apply_MoveBetweenLinesOfOneOrder()
    {
        var harness = NewHarness();
        SeedOrder(harness, OrderA, "SO-A");
        SeedLine(harness, LineA, OrderA, 600);
        SeedLine(harness, LineA2, OrderA, 600);
        harness.SeedBalance(ItemA, Loc, 600, "HU-1");
        harness.SeedOrderReceiptPlanLines(OrderA, Plan(OrderA, LineA, "HU-1", 600));

        Apply(harness, NoExpected,
            Line(OrderA, LineA, ["HU-1"], []),
            Line(OrderA, LineA2, [], ["HU-1"]));

        var bound = Assert.Single(harness.GetOrderReceiptPlanLines(OrderA));
        Assert.Equal(LineA2, bound.OrderLineId);
        Assert.Equal("HU-1", bound.ToHu);
    }

    [Fact]
    public void Apply_MoveBetweenTwoOrders()
    {
        var harness = NewHarness();
        SeedOrder(harness, OrderA, "SO-A");
        SeedOrder(harness, OrderB, "SO-B");
        SeedLine(harness, LineA, OrderA, 600);
        SeedLine(harness, LineB, OrderB, 600);
        harness.SeedBalance(ItemA, Loc, 600, "HU-1");
        harness.SeedOrderReceiptPlanLines(OrderA, Plan(OrderA, LineA, "HU-1", 600));

        Apply(harness, NoExpected,
            Line(OrderA, LineA, ["HU-1"], []),
            Line(OrderB, LineB, [], ["HU-1"]));

        Assert.Empty(harness.GetOrderReceiptPlanLines(OrderA));
        Assert.Equal("HU-1", Assert.Single(harness.GetOrderReceiptPlanLines(OrderB)).ToHu);
    }

    [Fact]
    public void Apply_SwapBetweenTwoOrders()
    {
        var harness = NewHarness();
        SeedOrder(harness, OrderA, "SO-A");
        SeedOrder(harness, OrderB, "SO-B");
        SeedLine(harness, LineA, OrderA, 600);
        SeedLine(harness, LineB, OrderB, 600);
        harness.SeedBalance(ItemA, Loc, 600, "HU-1");
        harness.SeedBalance(ItemA, Loc, 600, "HU-2");
        harness.SeedOrderReceiptPlanLines(OrderA, Plan(OrderA, LineA, "HU-1", 600));
        harness.SeedOrderReceiptPlanLines(OrderB, Plan(OrderB, LineB, "HU-2", 600));

        Apply(harness, NoExpected,
            Line(OrderA, LineA, ["HU-1"], ["HU-2"]),
            Line(OrderB, LineB, ["HU-2"], ["HU-1"]));

        Assert.Equal("HU-2", Assert.Single(harness.GetOrderReceiptPlanLines(OrderA)).ToHu);
        Assert.Equal("HU-1", Assert.Single(harness.GetOrderReceiptPlanLines(OrderB)).ToHu);
    }

    [Fact]
    public void Apply_CycleThreeOrders()
    {
        var harness = NewHarness();
        SeedOrder(harness, OrderA, "SO-A");
        SeedOrder(harness, OrderB, "SO-B");
        SeedOrder(harness, OrderC, "SO-C");
        SeedLine(harness, LineA, OrderA, 600);
        SeedLine(harness, LineB, OrderB, 600);
        SeedLine(harness, LineC, OrderC, 600);
        harness.SeedBalance(ItemA, Loc, 600, "HU-1");
        harness.SeedBalance(ItemA, Loc, 600, "HU-2");
        harness.SeedBalance(ItemA, Loc, 600, "HU-3");
        harness.SeedOrderReceiptPlanLines(OrderA, Plan(OrderA, LineA, "HU-1", 600));
        harness.SeedOrderReceiptPlanLines(OrderB, Plan(OrderB, LineB, "HU-2", 600));
        harness.SeedOrderReceiptPlanLines(OrderC, Plan(OrderC, LineC, "HU-3", 600));

        Apply(harness, NoExpected,
            Line(OrderA, LineA, ["HU-1"], ["HU-3"]),
            Line(OrderB, LineB, ["HU-2"], ["HU-1"]),
            Line(OrderC, LineC, ["HU-3"], ["HU-2"]));

        Assert.Equal("HU-3", Assert.Single(harness.GetOrderReceiptPlanLines(OrderA)).ToHu);
        Assert.Equal("HU-1", Assert.Single(harness.GetOrderReceiptPlanLines(OrderB)).ToHu);
        Assert.Equal("HU-2", Assert.Single(harness.GetOrderReceiptPlanLines(OrderC)).ToHu);
    }

    [Fact]
    public void Apply_PreservesOtherHuOnTargetLine()
    {
        var harness = NewHarness();
        SeedOrder(harness, OrderA, "SO-A");
        SeedOrder(harness, OrderB, "SO-B");
        SeedLine(harness, LineA, OrderA, 100);
        SeedLine(harness, LineB, OrderB, 300);
        harness.SeedBalance(ItemA, Loc, 100, "HU-1");
        harness.SeedBalance(ItemA, Loc, 100, "HU-10");
        harness.SeedBalance(ItemA, Loc, 100, "HU-11");
        harness.SeedOrderReceiptPlanLines(OrderA, Plan(OrderA, LineA, "HU-1", 100));
        harness.SeedOrderReceiptPlanLines(
            OrderB,
            Plan(OrderB, LineB, "HU-10", 100, 0),
            Plan(OrderB, LineB, "HU-11", 100, 1));

        Apply(harness, NoExpected,
            Line(OrderA, LineA, ["HU-1"], []),
            Line(OrderB, LineB, ["HU-10", "HU-11"], ["HU-10", "HU-11", "HU-1"]));

        var planB = harness.GetOrderReceiptPlanLines(OrderB).Select(line => line.ToHu).OrderBy(hu => hu).ToArray();
        Assert.Equal(new[] { "HU-1", "HU-10", "HU-11" }, planB);
    }

    [Fact]
    public void Apply_DuplicateHuInFinalState_Throws()
    {
        var harness = NewHarness();
        SeedOrder(harness, OrderA, "SO-A");
        SeedLine(harness, LineA, OrderA, 600);
        SeedLine(harness, LineA2, OrderA, 600);
        harness.SeedBalance(ItemA, Loc, 600, "HU-1");

        var ex = Assert.Throws<OrderHuBindingApplyFinalException>(() =>
            Apply(harness, NoExpected,
                Line(OrderA, LineA, [], ["HU-1"]),
                Line(OrderA, LineA2, [], ["HU-1"])));

        Assert.Equal("DUPLICATE_HU_IN_REQUEST", ex.ErrorCode);
        Assert.Empty(harness.GetOrderReceiptPlanLines(OrderA));
    }

    [Fact]
    public void Apply_StaleLineSet_Throws()
    {
        var harness = NewHarness();
        SeedOrder(harness, OrderA, "SO-A");
        SeedLine(harness, LineA, OrderA, 600);
        harness.SeedBalance(ItemA, Loc, 600, "HU-1");
        harness.SeedOrderReceiptPlanLines(OrderA, Plan(OrderA, LineA, "HU-1", 600));

        var ex = Assert.Throws<OrderHuBindingApplyFinalException>(() =>
            Apply(harness, NoExpected, Line(OrderA, LineA, [], [])));

        Assert.Equal("HU_BINDING_STALE", ex.ErrorCode);
        Assert.Equal("HU-1", Assert.Single(harness.GetOrderReceiptPlanLines(OrderA)).ToHu);
    }

    [Fact]
    public void Apply_StaleOwner_Throws()
    {
        var harness = NewHarness();
        SeedOrder(harness, OrderA, "SO-A");
        SeedOrder(harness, OrderC, "SO-C");
        SeedLine(harness, LineA, OrderA, 600);
        SeedLine(harness, LineC, OrderC, 600);
        harness.SeedBalance(ItemA, Loc, 600, "HU-1");
        harness.SeedBalance(ItemA, Loc, 600, "HU-9");
        // HU-1 фактически принадлежит заказу C (вне batch), клиент думал, что он свободен.
        harness.SeedOrderReceiptPlanLines(OrderC, Plan(OrderC, LineC, "HU-1", 600));
        harness.SeedOrderReceiptPlanLines(OrderA, Plan(OrderA, LineA, "HU-9", 600));

        var ex = Assert.Throws<OrderHuBindingApplyFinalException>(() =>
            Apply(harness, [Free("HU-1", ItemA, 600)], Line(OrderA, LineA, ["HU-9"], ["HU-9"])));

        Assert.Equal("HU_OWNER_CHANGED", ex.ErrorCode);
    }

    [Fact]
    public void Apply_ChangedHuQuantity_Throws()
    {
        var harness = NewHarness();
        SeedOrder(harness, OrderA, "SO-A");
        SeedLine(harness, LineA, OrderA, 600);
        harness.SeedBalance(ItemA, Loc, 500, "HU-1");

        var ex = Assert.Throws<OrderHuBindingApplyFinalException>(() =>
            Apply(harness, [Free("HU-1", ItemA, 600)], Line(OrderA, LineA, [], [])));

        Assert.Equal("HU_QTY_CHANGED", ex.ErrorCode);
    }

    [Fact]
    public void Apply_MixedHu_Throws()
    {
        var harness = NewHarness();
        SeedOrder(harness, OrderA, "SO-A");
        SeedLine(harness, LineA, OrderA, 600);
        harness.SeedBalance(ItemA, Loc, 300, "HU-MIX");
        harness.SeedBalance(ItemB, Loc, 200, "HU-MIX");

        var ex = Assert.Throws<OrderHuBindingApplyFinalException>(() =>
            Apply(harness, NoExpected, Line(OrderA, LineA, [], ["HU-MIX"])));

        Assert.Equal("HU_MIXED_NOT_SUPPORTED", ex.ErrorCode);
        Assert.Empty(harness.GetOrderReceiptPlanLines(OrderA));
    }

    [Fact]
    public void Apply_WrongItem_Throws()
    {
        var harness = NewHarness();
        SeedOrder(harness, OrderA, "SO-A");
        SeedLine(harness, LineA, OrderA, 600);
        harness.SeedBalance(ItemB, Loc, 600, "HU-B");

        var ex = Assert.Throws<OrderHuBindingApplyFinalException>(() =>
            Apply(harness, NoExpected, Line(OrderA, LineA, [], ["HU-B"])));

        Assert.Equal("HU_ITEM_MISMATCH", ex.ErrorCode);
    }

    [Fact]
    public void Apply_ShippedOrder_Throws()
    {
        var harness = NewHarness();
        SeedOrder(harness, OrderA, "SO-A", OrderStatus.Shipped);
        SeedLine(harness, LineA, OrderA, 600);
        harness.SeedBalance(ItemA, Loc, 600, "HU-1");

        var ex = Assert.Throws<OrderHuBindingApplyFinalException>(() =>
            Apply(harness, NoExpected, Line(OrderA, LineA, [], ["HU-1"])));

        Assert.Equal("ORDER_CLOSED", ex.ErrorCode);
    }

    [Fact]
    public void Apply_CancelledOrder_Throws()
    {
        var harness = NewHarness();
        SeedOrder(harness, OrderA, "SO-A", OrderStatus.Cancelled);
        SeedLine(harness, LineA, OrderA, 600);
        harness.SeedBalance(ItemA, Loc, 600, "HU-1");

        var ex = Assert.Throws<OrderHuBindingApplyFinalException>(() =>
            Apply(harness, NoExpected, Line(OrderA, LineA, [], ["HU-1"])));

        Assert.Equal("ORDER_CLOSED", ex.ErrorCode);
    }

    [Fact]
    public void Apply_HuOnUntouchedLineOfSameOrder_Throws()
    {
        var harness = NewHarness();
        SeedOrder(harness, OrderA, "SO-A");
        SeedLine(harness, LineA, OrderA, 600);
        SeedLine(harness, LineA2, OrderA, 600);
        harness.SeedBalance(ItemA, Loc, 600, "HU-7");
        // HU-7 на строке LineA2, которая НЕ входит в batch.
        harness.SeedOrderReceiptPlanLines(OrderA, Plan(OrderA, LineA2, "HU-7", 600));

        var ex = Assert.Throws<OrderHuBindingApplyFinalException>(() =>
            Apply(harness, NoExpected, Line(OrderA, LineA, [], ["HU-7"])));

        Assert.Equal("HU_RESERVED_BY_OTHER_ORDER", ex.ErrorCode);
    }

    [Fact]
    public void Apply_HuOwnedByOrderOutsideBatch_Throws()
    {
        var harness = NewHarness();
        SeedOrder(harness, OrderA, "SO-A");
        SeedOrder(harness, OrderC, "SO-C");
        SeedLine(harness, LineA, OrderA, 600);
        SeedLine(harness, LineC, OrderC, 600);
        harness.SeedBalance(ItemA, Loc, 600, "HU-1");
        harness.SeedOrderReceiptPlanLines(OrderC, Plan(OrderC, LineC, "HU-1", 600));

        var ex = Assert.Throws<OrderHuBindingApplyFinalException>(() =>
            Apply(harness, NoExpected, Line(OrderA, LineA, [], ["HU-1"])));

        Assert.Equal("HU_RESERVED_BY_OTHER_ORDER", ex.ErrorCode);
    }

    [Fact]
    public void Apply_ProductionPlanConflict_Throws()
    {
        var harness = NewHarness();
        SeedOrder(harness, OrderA, "SO-A");
        SeedLine(harness, LineA, OrderA, 600);
        harness.SeedBalance(ItemA, Loc, 600, "HU-1");
        SeedPlannedPallet(harness, OrderA, LineA, 600, ProductionPalletStatus.Printed, DateTime.UtcNow);

        var ex = Assert.Throws<OrderHuBindingApplyFinalException>(() =>
            Apply(harness, NoExpected, Line(OrderA, LineA, [], ["HU-1"])));

        Assert.Equal("HU_BINDING_PLAN_CONFLICT", ex.ErrorCode);
        Assert.Empty(harness.GetOrderReceiptPlanLines(OrderA));
    }

    [Fact]
    public void Apply_FullRollbackOnErrorAfterChangesBegan()
    {
        var harness = NewHarness();
        SeedOrder(harness, OrderA, "SO-A");
        SeedLine(harness, LineA, OrderA, 600);
        SeedLine(harness, LineA2, OrderA, 600);
        harness.SeedBalance(ItemA, Loc, 600, "HU-1");
        harness.SeedBalance(ItemA, Loc, 600, "HU-2");
        // Вторая строка имеет напечатанную плановую паллету → конфликт уже после batch-записи.
        SeedPlannedPallet(harness, OrderA, LineA2, 600, ProductionPalletStatus.Printed, DateTime.UtcNow);

        var ex = Assert.Throws<OrderHuBindingApplyFinalException>(() =>
            Apply(harness, NoExpected,
                Line(OrderA, LineA, [], ["HU-1"]),
                Line(OrderA, LineA2, [], ["HU-2"])));

        Assert.Equal("HU_BINDING_PLAN_CONFLICT", ex.ErrorCode);
        // Полный откат: ни HU-1, ни HU-2 не сохранены.
        Assert.Empty(harness.GetOrderReceiptPlanLines(OrderA));
    }

    [Fact]
    public void Apply_RefreshesStatusOfAllAffectedOrders()
    {
        var harness = NewHarness();
        SeedOrder(harness, OrderA, "SO-A");
        SeedOrder(harness, OrderB, "SO-B");
        SeedLine(harness, LineA, OrderA, 600);
        SeedLine(harness, LineB, OrderB, 600);
        harness.SeedBalance(ItemA, Loc, 600, "HU-1");
        harness.SeedOrderReceiptPlanLines(OrderA, Plan(OrderA, LineA, "HU-1", 600));

        Apply(harness, NoExpected,
            Line(OrderA, LineA, ["HU-1"], []),
            Line(OrderB, LineB, [], ["HU-1"]));

        harness.VerifyOrderStatusRefreshed(OrderA, Times.AtLeastOnce());
        harness.VerifyOrderStatusRefreshed(OrderB, Times.AtLeastOnce());
    }

    [Fact]
    public void Apply_FilledPalletPlusWarehouseHu_DoesNotConflictAndKeepsFilled()
    {
        // Bug 2 / Сценарий A (order-scoped + manage): ordered 1800, FILLED 600, склад 600+600 → surplus 0, успех.
        var harness = NewHarness();
        SeedOrder(harness, OrderA, "SO-A");
        SeedLine(harness, LineA, OrderA, 1800);
        harness.SeedBalance(ItemA, Loc, 600, "HU-963");
        harness.SeedBalance(ItemA, Loc, 600, "HU-965");
        SeedFilledPallet(harness, OrderA, LineA, 600, "HU-FILLED", postLedger: false);

        var result = Apply(harness, NoExpected, Line(OrderA, LineA, [], ["HU-963", "HU-965"]));

        var line = Assert.Single(Assert.Single(result.Orders).AppliedLines);
        Assert.Equal(0, line.CancelledPlannedPalletCount);
        var planHus = harness.GetOrderReceiptPlanLines(OrderA).Select(plan => plan.ToHu).OrderBy(hu => hu).ToArray();
        Assert.Equal(new[] { "HU-963", "HU-965" }, planHus);
    }

    private static readonly IReadOnlyList<ManageExpectedHuState> NoExpected = Array.Empty<ManageExpectedHuState>();

    private static CloseDocumentHarness NewHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = Loc, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItemType(new ItemType { Id = ItemTypeId, Name = "Готовая продукция", EnableOrderReservation = true });
        harness.SeedItem(new Item { Id = ItemA, Name = "Товар A", BaseUom = "шт", ItemTypeId = ItemTypeId, MaxQtyPerHu = 600 });
        harness.SeedItem(new Item { Id = ItemB, Name = "Товар B", BaseUom = "шт", ItemTypeId = ItemTypeId, MaxQtyPerHu = 600 });
        return harness;
    }

    private static void SeedOrder(CloseDocumentHarness harness, long orderId, string orderRef, OrderStatus status = OrderStatus.InProgress)
    {
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = orderRef,
            Type = OrderType.Customer,
            Status = status,
            CreatedAt = DateTime.UtcNow
        });
    }

    private static void SeedLine(CloseDocumentHarness harness, long lineId, long orderId, double qty, long itemId = ItemA)
    {
        harness.SeedOrderLine(new OrderLine { Id = lineId, OrderId = orderId, ItemId = itemId, QtyOrdered = qty });
    }

    private static OrderReceiptPlanLine Plan(long orderId, long lineId, string huCode, double qty, int sortOrder = 0) =>
        new()
        {
            OrderId = orderId,
            OrderLineId = lineId,
            ItemId = ItemA,
            QtyPlanned = qty,
            ToHu = huCode,
            SortOrder = sortOrder
        };

    private static void SeedPlannedPallet(
        CloseDocumentHarness harness,
        long orderId,
        long lineId,
        double qty,
        string status = ProductionPalletStatus.Planned,
        DateTime? printedAt = null)
    {
        var docId = 9000 + lineId;
        var docLineId = 8000 + lineId;
        var palletId = 7000 + lineId;
        var palletLineId = 6000 + lineId;
        var huPlan = $"HU-PLAN-{lineId}";

        harness.SeedDoc(new Doc
        {
            Id = docId,
            DocRef = $"PRD-{lineId}",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = orderId,
            CreatedAt = DateTime.UtcNow
        });
        harness.SeedLine(new DocLine
        {
            Id = docLineId,
            DocId = docId,
            OrderLineId = lineId,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder,
            ItemId = ItemA,
            Qty = qty,
            ToLocationId = Loc,
            ToHu = huPlan
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = palletId,
            PrdDocId = docId,
            DocLineId = docLineId,
            OrderId = orderId,
            OrderLineId = lineId,
            ItemId = ItemA,
            ItemName = "Товар A",
            HuCode = huPlan,
            PlannedQty = qty,
            ToLocationId = Loc,
            Status = status,
            PrintedAt = printedAt,
            CreatedAt = DateTime.UtcNow,
            Lines =
            [
                new ProductionPalletComponentLine
                {
                    Id = palletLineId,
                    ProductionPalletId = palletId,
                    DocLineId = docLineId,
                    OrderLineId = lineId,
                    ItemId = ItemA,
                    ItemName = "Товар A",
                    PlannedQty = qty,
                    FilledQty = 0,
                    CreatedAt = DateTime.UtcNow
                }
            ]
        });
    }

    private static void SeedFilledPallet(
        CloseDocumentHarness harness,
        long orderId,
        long lineId,
        double qty,
        string huCode,
        bool postLedger)
    {
        var docId = 4000 + lineId;
        var docLineId = 3000 + lineId;
        var palletId = 2000 + lineId;
        var palletLineId = 1000 + lineId;

        harness.SeedDoc(new Doc
        {
            Id = docId,
            DocRef = $"PRD-FILLED-{lineId}",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = orderId,
            CreatedAt = DateTime.UtcNow
        });
        harness.SeedLine(new DocLine
        {
            Id = docLineId,
            DocId = docId,
            OrderLineId = lineId,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder,
            ItemId = ItemA,
            Qty = qty,
            ToLocationId = Loc,
            ToHu = huCode
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = palletId,
            PrdDocId = docId,
            DocLineId = docLineId,
            OrderId = orderId,
            OrderLineId = lineId,
            ItemId = ItemA,
            ItemName = "Товар A",
            HuCode = huCode,
            PlannedQty = qty,
            ToLocationId = Loc,
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
                    OrderLineId = lineId,
                    ItemId = ItemA,
                    ItemName = "Товар A",
                    PlannedQty = qty,
                    FilledQty = qty,
                    CreatedAt = DateTime.UtcNow
                }
            ]
        });

        if (postLedger)
        {
            harness.SeedLedgerEntry(docId, ItemA, Loc, qty, huCode);
        }
    }

    private static OrderHuBindingManageApplyResult Apply(
        CloseDocumentHarness harness,
        IReadOnlyList<ManageExpectedHuState> expectedHuStates,
        params OrderHuBindingManageApplyLineRequest[] lines)
    {
        return new OrderHuBindingManageApplyService(harness.Store).ApplyFinal(new OrderHuBindingManageApplyRequest
        {
            Mode = OrderHuBindingManageApplyRequest.ReplaceFinalSelectionMode,
            ExpectedHuStates = expectedHuStates,
            Lines = lines
        });
    }

    private static OrderHuBindingManageApplyLineRequest Line(
        long orderId,
        long orderLineId,
        IReadOnlyList<string> expected,
        IReadOnlyList<string> final) =>
        new()
        {
            OrderId = orderId,
            OrderLineId = orderLineId,
            ExpectedBoundHuCodes = expected,
            FinalHuCodes = final
        };

    private static ManageExpectedHuState Free(string huCode, long itemId, double qty) =>
        new() { HuCode = huCode, ItemId = itemId, ExpectedQty = qty };
}
