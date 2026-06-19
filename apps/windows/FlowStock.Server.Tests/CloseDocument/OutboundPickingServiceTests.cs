using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.CloseDocument;

public sealed class OutboundPickingServiceTests
{
    [Fact]
    public void ListIncludesAcceptedAndHuCoveredCustomerOrders()
    {
        var harness = CreateBasicPickingHarness();
        SeedOrder(harness, 30, 301, "SO-COVERED", OrderType.Customer, OrderStatus.InProgress, "HU-000030", 3);
        SeedOrder(harness, 40, 401, "INT-READY", OrderType.Internal, OrderStatus.Accepted, "HU-000040", 4);

        var rows = CreatePickingService(harness).GetOrders();

        Assert.Equal([20, 30], rows.Select(row => row.OrderId).OrderBy(id => id).ToArray());
        Assert.All(rows, row => Assert.Equal(1, row.ExpectedHuCount));
    }

    [Fact]
    public void GetOrderReturnsExpectedHu()
    {
        var details = CreatePickingService(CreateBasicPickingHarness()).GetDetails(20);

        var hu = Assert.Single(details.Hus);
        Assert.Equal("HU-000001", hu.HuCode);
        Assert.Equal(OutboundPickingHuStatus.Pending, hu.Status);
        Assert.Equal(5, hu.Qty);
        Assert.Equal("Горчица", hu.ItemSummary);
    }

    [Fact]
    public void ScanCreatesDraftOutboundAndDocLinesWithoutLedger()
    {
        var harness = CreateBasicPickingHarness();
        var service = CreatePickingService(harness);

        var result = service.Scan(20, "HU-000001", "TSD-01");

        Assert.True(result.Success);
        var details = result.Order;
        Assert.NotNull(details);
        Assert.NotNull(details.DraftOutboundDocId);
        var draft = harness.GetDoc(details.DraftOutboundDocId.Value);
        Assert.Equal(DocType.Outbound, draft.Type);
        Assert.Equal(DocStatus.Draft, draft.Status);
        Assert.Equal(20, draft.OrderId);
        var line = Assert.Single(harness.GetDocLines(draft.Id));
        Assert.Equal(201, line.OrderLineId);
        Assert.Equal(1001, line.ItemId);
        Assert.Equal(5, line.Qty);
        Assert.Equal("HU-000001", line.FromHu);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void RepeatedScanIsIdempotent()
    {
        var harness = CreateBasicPickingHarness();
        var service = CreatePickingService(harness);

        var first = service.Scan(20, "HU-000001", "TSD-01");
        var second = service.Scan(20, "HU-000001", "TSD-01");

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.True(second.AlreadyPicked);
        var secondDetails = second.Order;
        Assert.NotNull(secondDetails);
        var draftId = secondDetails.DraftOutboundDocId!.Value;
        Assert.Single(harness.GetDocLines(draftId));
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void ScanRejectsWrongHuOrderAndStatus()
    {
        var harness = CreateBasicPickingHarness();
        SeedOrder(harness, 30, 301, "SO-030", OrderType.Customer, OrderStatus.Draft, "HU-000030", 3);
        var service = CreatePickingService(harness);

        var wrongHu = service.Scan(20, "HU-999999", "TSD-01");
        var wrongStatus = service.Scan(30, "HU-000030", "TSD-01");

        Assert.False(wrongHu.Success);
        Assert.Equal("HU_NOT_EXPECTED", wrongHu.ErrorCode);
        Assert.False(wrongStatus.Success);
        Assert.Equal("VALIDATION_ERROR", wrongStatus.ErrorCode);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void ScanAllowsInProgressCustomerOrderFullyCoveredByWarehouseHu()
    {
        var harness = CreateBasicPickingHarness();
        SeedOrder(harness, 30, 301, "SO-030", OrderType.Customer, OrderStatus.InProgress, "HU-000030", 3);
        var service = CreatePickingService(harness);

        var result = service.Scan(30, "HU-000030", "TSD-01");

        Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
        Assert.NotNull(result.Order);
        Assert.Equal(30, result.Order.OrderId);
        Assert.Equal(1, result.Order.PickedHuCount);
    }

    [Fact]
    public void ReservationOnlyHu_IsNotListedAndScanReturnsPhysicalStockError()
    {
        var harness = CreateBasicPickingHarness();
        harness.SeedOrderReceiptPlanLines(20, new OrderReceiptPlanLine
        {
            Id = 900,
            OrderId = 20,
            OrderLineId = 201,
            ItemId = 1001,
            ItemName = "Горчица",
            QtyPlanned = 5,
            ToLocationId = 1,
            ToLocationCode = "FG-01",
            ToHu = "HU-RESERVATION-ONLY"
        });
        var service = CreatePickingService(harness);

        var details = service.GetDetails(20);
        var scan = service.Scan(20, "HU-RESERVATION-ONLY", "TSD-01");

        Assert.Empty(details.Hus);
        Assert.False(scan.Success);
        Assert.Equal("HU_BOUND_WITHOUT_STOCK", scan.ErrorCode);
        Assert.Contains("физически", scan.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(harness.Store.GetDocsByOrder(20), doc => doc.Type == DocType.Outbound);
    }

    [Fact]
    public void ScanRejectsHuPickedInOtherOpenOutbound()
    {
        var harness = CreateBasicPickingHarness();
        harness.SeedDoc(new Doc
        {
            Id = 500,
            DocRef = "OUT-OTHER",
            Type = DocType.Outbound,
            Status = DocStatus.Draft,
            OrderId = 99,
            CreatedAt = DateTime.UtcNow
        });
        harness.SeedLine(new DocLine
        {
            Id = 501,
            DocId = 500,
            ItemId = 1001,
            Qty = 5,
            FromLocationId = 1,
            FromHu = "HU-000001"
        });

        var result = CreatePickingService(harness).Scan(20, "HU-000001", "TSD-01");

        Assert.False(result.Success);
        Assert.Equal("HU_PICKED_IN_OTHER_OUTBOUND", result.ErrorCode);
    }

    [Fact]
    public void CloseRejectsDuplicateHuLinesInsideTransaction()
    {
        var harness = CreateBasicPickingHarness();
        harness.SeedDoc(new Doc
        {
            Id = 510,
            DocRef = "OUT-DUPLICATE",
            Type = DocType.Outbound,
            Status = DocStatus.Draft,
            OrderId = 20,
            PartnerId = 200,
            CreatedAt = DateTime.UtcNow
        });
        harness.SeedLine(new DocLine
        {
            Id = 511, DocId = 510, OrderLineId = 201, ItemId = 1001, Qty = 2,
            FromLocationId = 1, FromHu = "HU-000001"
        });
        harness.SeedLine(new DocLine
        {
            Id = 512, DocId = 510, OrderLineId = 201, ItemId = 1001, Qty = 2,
            FromLocationId = 1, FromHu = "HU-000001"
        });

        var close = harness.CreateService().TryCloseDoc(510, allowNegative: false);

        Assert.False(close.Success);
        Assert.Contains(close.Errors, error => error.Contains("повторная строка", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(harness.LedgerEntries);
        Assert.Equal(DocStatus.Draft, harness.GetDoc(510).Status);
    }

    [Fact]
    public void ScanRejectsHuReservedForAnotherCustomerOrder()
    {
        var harness = CreateBasicPickingHarness();
        SeedOrder(harness, 30, 301, "SO-030", OrderType.Customer, OrderStatus.Accepted, "HU-000030", 3);

        var result = CreatePickingService(harness).Scan(30, "HU-000001", "TSD-01");

        Assert.False(result.Success);
        Assert.Equal("HU_NOT_EXPECTED", result.ErrorCode);
        Assert.DoesNotContain(harness.Store.GetDocsByOrder(30), doc => doc.Type == DocType.Outbound);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void CompleteExplicitlyClosesEvenWhenLegacyAutoCloseOptionIsDisabled()
    {
        var harness = CreateBasicPickingHarness();
        var service = CreatePickingService(harness, autoClose: false);
        service.Scan(20, "HU-000001", "TSD-01");

        var result = service.Complete(20);

        Assert.True(result.Success);
        Assert.True(result.OutboundClosed);
        var details = result.Order;
        Assert.NotNull(details);
        var draftId = details.DraftOutboundDocId!.Value;
        var draft = harness.GetDoc(draftId);
        Assert.Equal(DocStatus.Closed, draft.Status);
        Assert.Single(harness.LedgerEntries);
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(20).Status);
    }

    [Fact]
    public void LastScanDoesNotCloseUntilExplicitComplete()
    {
        var harness = CreateBasicPickingHarness();
        var picking = CreatePickingService(harness, autoClose: true);
        var scan = picking.Scan(20, "HU-000001", "TSD-01");

        Assert.True(scan.Success, $"{scan.ErrorCode}: {scan.Message}");
        Assert.False(scan.OutboundClosed);
        Assert.True(scan.Order!.CanClose);
        Assert.Empty(harness.LedgerEntries);
        Assert.True(picking.Complete(20).OutboundClosed);
        var ledger = Assert.Single(harness.LedgerEntries);
        Assert.Equal(-5, ledger.QtyDelta);
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(20).Status);
    }

    [Fact]
    public void CompleteAutoClose_IsIdempotentWhenOutboundAlreadyClosed()
    {
        var harness = CreateBasicPickingHarness();
        var picking = CreatePickingService(harness, autoClose: true);
        Assert.True(picking.Scan(20, "HU-000001", "TSD-01").Success);

        var result = picking.Complete(20);

        Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
        Assert.True(result.OutboundClosed);
        Assert.Single(harness.LedgerEntries);
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(20).Status);
    }

    [Fact]
    public void WpfCloseAfterPickingWritesLedgerAndShipsOrder()
    {
        var harness = CreateBasicPickingHarness();
        var picking = CreatePickingService(harness, autoClose: false);
        picking.Scan(20, "HU-000001", "TSD-01");
        var draftId = picking.GetDetails(20).DraftOutboundDocId!.Value;

        var close = harness.CreateService().TryCloseDoc(draftId, allowNegative: false);

        Assert.True(close.Success);
        Assert.Empty(close.Errors);
        var ledger = Assert.Single(harness.LedgerEntries);
        Assert.Equal(1001, ledger.ItemId);
        Assert.Equal(1, ledger.LocationId);
        Assert.Equal("HU-000001", ledger.HuCode);
        Assert.Equal(-5, ledger.QtyDelta);
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(20).Status);
    }

    [Fact]
    public void PalletizedCustomerOrder080_IncludesFilledProductionPalletInOutboundList()
    {
        var harness = CreateOrder080PalletizedHarness();
        var service = CreatePickingService(harness);

        var rows = service.GetOrders();
        var row = Assert.Single(rows);
        Assert.Equal(79, row.OrderId);
        Assert.Equal("080", row.OrderRef);
        Assert.Equal(1, row.ExpectedHuCount);

        var details = service.GetDetails(79);
        Assert.Equal(1, details.ExpectedHuCount);
        Assert.Equal(0, details.PickedHuCount);
        var hu = Assert.Single(details.Hus);
        Assert.Equal("HU-0000506", hu.HuCode);
        Assert.Equal(OutboundPickingHuStatus.Pending, hu.Status);
        Assert.Equal(1890, hu.Qty);
        Assert.Equal(208, hu.Lines[0].OrderLineId);
        Assert.Equal(30, hu.Lines[0].ItemId);
    }

    [Fact]
    public void PalletizedCustomerOrder080_RepeatScanIsRejected_AndRepeatCompleteIsIdempotent()
    {
        var harness = CreateOrder080PalletizedHarness();
        var picking = CreatePickingService(harness, autoClose: true);

        var first = picking.Scan(79, "HU-0000506", "TSD-01");
        Assert.True(first.Success, $"{first.ErrorCode}: {first.Message}");
        Assert.False(first.OutboundClosed);
        var line = Assert.Single(harness.GetDocLines(first.Order!.DraftOutboundDocId!.Value));
        Assert.Equal(208, line.OrderLineId);
        Assert.Equal(30, line.ItemId);
        Assert.Equal(1890, line.Qty);
        Assert.Equal(1, line.FromLocationId);
        Assert.Equal("HU-0000506", line.FromHu);
        Assert.DoesNotContain(harness.LedgerEntries, entry => entry.QtyDelta < 0);

        var repeatScan = picking.Scan(79, "HU-0000506", "TSD-01");
        Assert.True(repeatScan.Success);
        Assert.True(repeatScan.AlreadyPicked);

        Assert.True(new ProductionPalletService(harness.Store).CompleteFilling(79, "TSD-01").Success);
        var repeatComplete = picking.Complete(79);
        Assert.True(repeatComplete.Success, $"{repeatComplete.ErrorCode}: {repeatComplete.Message}");
        Assert.True(repeatComplete.OutboundClosed);
        Assert.Single(harness.LedgerEntries.Where(entry => entry.QtyDelta < 0));
    }

    [Fact]
    public void PalletizedOutbound_RequiresFillingFinalize_WithoutPostingOrShipping()
    {
        var harness = CreateOrder080PalletizedHarness();
        var picking = CreatePickingService(harness, autoClose: true);
        var scan = picking.Scan(79, "HU-0000506", "TSD-01");
        var draftId = scan.Order!.DraftOutboundDocId!.Value;

        var blocked = picking.Complete(79);

        Assert.False(blocked.Success);
        Assert.Equal("FILLING_NOT_FINALIZED", blocked.ErrorCode);
        Assert.Equal(DocStatus.Draft, harness.GetDoc(draftId).Status);
        Assert.Equal(OrderStatus.Accepted, harness.GetOrder(79).Status);
        Assert.DoesNotContain(harness.LedgerEntries, entry => entry.DocId == draftId);

        Assert.True(new ProductionPalletService(harness.Store).CompleteFilling(79, "TSD-01").Success);
        Assert.True(picking.Complete(79).Success);
        Assert.True(picking.Complete(79).Success);
        Assert.Single(harness.LedgerEntries.Where(entry => entry.DocId == draftId));
    }

    [Fact]
    public void FillingFingerprint_DoesNotChangeWhenOnlyPrdDocumentChanges()
    {
        var harness = CreateOrder080PalletizedHarness();
        var before = ProductionPalletService.TryGetFillingOperationProgress(harness.Store, 79);
        var pallet = Assert.Single(harness.Store.GetProductionPalletsByDoc(201));
        harness.SeedDoc(new Doc { Id = 202, DocRef = "PRD-202", Type = DocType.ProductionReceipt, Status = DocStatus.Closed, OrderId = 79 });
        harness.SeedProductionPallet(CopyPallet(pallet, prdDocId: 202));

        var after = ProductionPalletService.TryGetFillingOperationProgress(harness.Store, 79);

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(before.OperationFingerprint, after.OperationFingerprint);
    }

    [Fact]
    public void PalletizedOutbound_RejectsStaleFillingCompletion()
    {
        var harness = CreateOrder080PalletizedHarness();
        Assert.True(new ProductionPalletService(harness.Store).CompleteFilling(79, "TSD-01").Success);
        var pallet = Assert.Single(harness.Store.GetProductionPalletsByDoc(201));
        harness.SeedProductionPallet(CopyPallet(pallet, plannedQty: 1800));
        var picking = CreatePickingService(harness, autoClose: true);
        Assert.True(picking.Scan(79, "HU-0000506", "TSD-01").Success);

        var blocked = picking.Complete(79);

        Assert.False(blocked.Success);
        Assert.Equal("FILLING_NOT_FINALIZED", blocked.ErrorCode);
        Assert.Equal(OrderStatus.Accepted, harness.GetOrder(79).Status);
    }

    [Fact]
    public void NonPalletizedOutbound_IsNotBlockedByFillingFinalizeGuard()
    {
        var harness = CreateBasicPickingHarness();

        // Order 20 ships ordinary warehouse HU and has no production pallets, so the
        // filling guard must hit the null-path of TryGetFillingOperationProgress.
        Assert.Null(ProductionPalletService.TryGetFillingOperationProgress(harness.Store, 20));

        var picking = CreatePickingService(harness, autoClose: true);
        Assert.True(picking.Scan(20, "HU-000001", "TSD-01").Success);

        var complete = picking.Complete(20);

        Assert.True(complete.Success, $"{complete.ErrorCode}: {complete.Message}");
        Assert.NotEqual("FILLING_NOT_FINALIZED", complete.ErrorCode);
        Assert.True(complete.OutboundClosed);
        var ledger = Assert.Single(harness.LedgerEntries);
        Assert.Equal(-5, ledger.QtyDelta);
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(20).Status);
    }

    [Fact]
    public void PartialComplete_ClosesOnlyScannedHu_AndNextPickingShowsRemaining()
    {
        var harness = CreateThreeHuPickingHarness();
        var picking = CreatePickingService(harness, autoClose: true);

        var scan = picking.Scan(20, "HU-000001", "TSD-01");
        Assert.True(scan.Success, $"{scan.ErrorCode}: {scan.Message}");
        Assert.False(scan.OutboundClosed);
        Assert.Equal(5, scan.Order!.ScannedQty);
        Assert.Equal(15, scan.Order.RemainingQty);

        var withoutConfirmation = picking.Complete(20);
        Assert.False(withoutConfirmation.Success);
        Assert.Equal("PARTIAL_CONFIRMATION_REQUIRED", withoutConfirmation.ErrorCode);

        var completed = picking.Complete(20, allowPartial: true);
        Assert.True(completed.Success, $"{completed.ErrorCode}: {completed.Message}");
        Assert.True(completed.OutboundClosed);
        Assert.NotEqual(OrderStatus.Shipped, harness.GetOrder(20).Status);
        Assert.Equal(-5, Assert.Single(harness.LedgerEntries).QtyDelta);

        var next = picking.GetDetails(20);
        Assert.Equal("Частично отгружено", next.Status);
        Assert.Equal(2, next.ExpectedHuCount);
        Assert.Equal(5, next.ShippedQty);
        Assert.Equal(10, next.RemainingQty);
        Assert.DoesNotContain(next.Hus, hu => hu.HuCode == "HU-000001");

        var repeated = picking.Scan(20, "HU-000001", "TSD-01");
        Assert.False(repeated.Success);
        Assert.Equal("HU_ALREADY_SHIPPED", repeated.ErrorCode);
    }

    [Fact]
    public void FullCompletionAfterPartial_ShipsOrder()
    {
        var harness = CreateThreeHuPickingHarness();
        var picking = CreatePickingService(harness, autoClose: true);
        Assert.True(picking.Scan(20, "HU-000001", "TSD-01").Success);
        Assert.True(picking.Complete(20, allowPartial: true).Success);

        var secondScan = picking.Scan(20, "HU-000002", "TSD-01");
        Assert.True(secondScan.Success, $"{secondScan.ErrorCode}: {secondScan.Message}");
        var finalScan = picking.Scan(20, "HU-000003", "TSD-01");

        Assert.True(finalScan.Success, $"{finalScan.ErrorCode}: {finalScan.Message}");
        Assert.False(finalScan.OutboundClosed);
        Assert.True(finalScan.Order!.CanClose);
        Assert.True(picking.Complete(20).OutboundClosed);
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(20).Status);
        Assert.Equal(-15, harness.LedgerEntries.Sum(entry => entry.QtyDelta));
    }

    [Fact]
    public void PalletizedCustomerOrder080_RepeatedScanBeforeClose_IsAlreadyPicked()
    {
        var harness = CreateOrder080PalletizedHarness();
        var picking = CreatePickingService(harness, autoClose: false);

        var first = picking.Scan(79, "HU-0000506", "TSD-01");
        var second = picking.Scan(79, "HU-0000506", "TSD-01");

        Assert.True(first.Success, $"{first.ErrorCode}: {first.Message}");
        Assert.True(second.Success, $"{second.ErrorCode}: {second.Message}");
        Assert.True(second.AlreadyPicked);
        Assert.Single(harness.GetDocLines(first.Order!.DraftOutboundDocId!.Value));
        Assert.DoesNotContain(harness.LedgerEntries, entry => entry.QtyDelta < 0);
    }

    [Fact]
    public void MixedPalletCreatesComponentLines()
    {
        var harness = CreateBasicPickingHarness();
        harness.SeedItem(new Item { Id = 1002, Name = "Соус" });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 202,
            OrderId = 20,
            ItemId = 1002,
            QtyOrdered = 2,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedBalance(1002, 1, 2, "HU-000001");
        harness.SeedOrderReceiptPlanLines(20,
            new OrderReceiptPlanLine
            {
                Id = 1,
                OrderId = 20,
                OrderLineId = 201,
                ItemId = 1001,
                ItemName = "Горчица",
                QtyPlanned = 5,
                ToLocationId = 1,
                ToLocationCode = "FG-01",
                ToHu = "HU-000001"
            },
            new OrderReceiptPlanLine
            {
                Id = 2,
                OrderId = 20,
                OrderLineId = 202,
                ItemId = 1002,
                ItemName = "Соус",
                QtyPlanned = 2,
                ToLocationId = 1,
                ToLocationCode = "FG-01",
                ToHu = "HU-000001"
            });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 1,
            OrderId = 20,
            HuCode = "HU-000001",
            Status = ProductionPalletStatus.Filled,
            Lines =
            [
                new ProductionPalletComponentLine
                {
                    Id = 11,
                    ProductionPalletId = 1,
                    OrderLineId = 201,
                    ItemId = 1001,
                    ItemName = "Горчица",
                    PlannedQty = 5,
                    FilledQty = 5,
                    CreatedAt = DateTime.UtcNow
                },
                new ProductionPalletComponentLine
                {
                    Id = 12,
                    ProductionPalletId = 1,
                    OrderLineId = 202,
                    ItemId = 1002,
                    ItemName = "Соус",
                    PlannedQty = 2,
                    FilledQty = 2,
                    CreatedAt = DateTime.UtcNow
                }
            ]
        });

        var service = CreatePickingService(harness);
        var expectedHu = Assert.Single(service.GetDetails(20).Hus);
        Assert.Equal([1001, 1002], expectedHu.Lines.OrderBy(line => line.ItemId).Select(line => line.ItemId).ToArray());

        var result = service.Scan(20, "HU-000001", "TSD-01");

        Assert.True(result.Success);
        var details = result.Order;
        Assert.NotNull(details);
        var draftId = details.DraftOutboundDocId!.Value;
        var lines = harness.GetDocLines(draftId).OrderBy(line => line.ItemId).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal(201, lines[0].OrderLineId);
        Assert.Equal(5, lines[0].Qty);
        Assert.Equal(202, lines[1].OrderLineId);
        Assert.Equal(2, lines[1].Qty);
        Assert.All(lines, line => Assert.Equal("HU-000001", line.FromHu));
        Assert.Empty(harness.LedgerEntries);
    }

    private static OutboundPickingService CreatePickingService(CloseDocumentHarness harness, bool autoClose = false)
    {
        return new OutboundPickingService(
            harness.Store,
            harness.CreateService(),
            new FlowStockLedgerFlowOptions { OutboundAutoCloseOnComplete = autoClose });
    }

    private static ProductionPallet CopyPallet(ProductionPallet pallet, long? prdDocId = null, double? plannedQty = null)
    {
        return new ProductionPallet
        {
            Id = pallet.Id,
            PrdDocId = prdDocId ?? pallet.PrdDocId,
            DocLineId = pallet.DocLineId,
            OrderId = pallet.OrderId,
            OrderLineId = pallet.OrderLineId,
            ItemId = pallet.ItemId,
            ItemName = pallet.ItemName,
            HuCode = pallet.HuCode,
            PlannedQty = plannedQty ?? pallet.PlannedQty,
            ToLocationId = pallet.ToLocationId,
            ToLocationCode = pallet.ToLocationCode,
            Status = pallet.Status,
            PalletNo = pallet.PalletNo,
            PalletCount = pallet.PalletCount,
            PrintedAt = pallet.PrintedAt,
            FilledAt = pallet.FilledAt,
            FilledByDeviceId = pallet.FilledByDeviceId,
            CancelReason = pallet.CancelReason,
            CancelledAt = pallet.CancelledAt,
            CreatedAt = pallet.CreatedAt,
            Lines = pallet.Lines
        };
    }

    private static CloseDocumentHarness CreateOrder080PalletizedHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location
        {
            Id = 1,
            Code = "FG-01",
            Name = "Готовая продукция"
        });
        harness.SeedPartner(new Partner
        {
            Id = 500,
            Code = "CUST-080",
            Name = "Клиент 080",
            CreatedAt = new DateTime(2026, 5, 8, 8, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedItem(new Item
        {
            Id = 30,
            Name = "Продукция 080",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMarking = false
        });
        harness.SeedOrder(new Order
        {
            Id = 79,
            OrderRef = "080",
            Type = OrderType.Customer,
            Status = OrderStatus.Accepted,
            PartnerId = 500,
            CreatedAt = new DateTime(2026, 5, 8, 9, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 208,
            OrderId = 79,
            ItemId = 30,
            QtyOrdered = 1890,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedHu(new HuRecord
        {
            Id = 79,
            Code = "HU-0000506",
            Status = "ACTIVE",
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedBalance(30, 1, 1890, "HU-0000506");
        harness.SeedDoc(new Doc
        {
            Id = 201,
            DocRef = "PRD-201",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = 79,
            ClosedAt = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc),
            CreatedAt = new DateTime(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 79,
            PrdDocId = 201,
            DocLineId = 20101,
            OrderId = 79,
            OrderLineId = 208,
            ItemId = 30,
            ItemName = "Продукция 080",
            HuCode = "HU-0000506",
            PlannedQty = 1890,
            ToLocationId = 1,
            ToLocationCode = "FG-01",
            Status = ProductionPalletStatus.Filled,
            PalletNo = 1,
            PalletCount = 1,
            FilledAt = new DateTime(2026, 5, 20, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLedgerEntry(201, 30, 1, 1890, "HU-0000506");
        return harness;
    }

    private static CloseDocumentHarness CreateBasicPickingHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location
        {
            Id = 1,
            Code = "FG-01",
            Name = "Готовая продукция"
        });
        harness.SeedPartner(new Partner
        {
            Id = 200,
            Code = "CUST-200",
            Name = "Тестовый клиент",
            CreatedAt = new DateTime(2026, 5, 8, 8, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Горчица",
            Gtin = "04607186951520",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMarking = false
        });
        SeedOrder(harness, 20, 201, "SO-020", OrderType.Customer, OrderStatus.Accepted, "HU-000001", 5);
        return harness;
    }

    private static CloseDocumentHarness CreateThreeHuPickingHarness()
    {
        var harness = CreateBasicPickingHarness();
        harness.SeedOrderLine(new OrderLine
        {
            Id = 202,
            OrderId = 20,
            ItemId = 1001,
            QtyOrdered = 5,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 203,
            OrderId = 20,
            ItemId = 1001,
            QtyOrdered = 5,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedHu(new HuRecord { Id = 21, Code = "HU-000002", Status = "ACTIVE", CreatedAt = DateTime.UtcNow });
        harness.SeedHu(new HuRecord { Id = 22, Code = "HU-000003", Status = "ACTIVE", CreatedAt = DateTime.UtcNow });
        harness.SeedBalance(1001, 1, 5, "HU-000002");
        harness.SeedBalance(1001, 1, 5, "HU-000003");
        harness.SeedOrderReceiptPlanLines(20,
            new OrderReceiptPlanLine
            {
                Id = 1, OrderId = 20, OrderLineId = 201, ItemId = 1001, ItemName = "Горчица",
                QtyPlanned = 5, ToLocationId = 1, ToLocationCode = "FG-01", ToHu = "HU-000001"
            },
            new OrderReceiptPlanLine
            {
                Id = 2, OrderId = 20, OrderLineId = 202, ItemId = 1001, ItemName = "Горчица",
                QtyPlanned = 5, ToLocationId = 1, ToLocationCode = "FG-01", ToHu = "HU-000002"
            },
            new OrderReceiptPlanLine
            {
                Id = 3, OrderId = 20, OrderLineId = 203, ItemId = 1001, ItemName = "Горчица",
                QtyPlanned = 5, ToLocationId = 1, ToLocationCode = "FG-01", ToHu = "HU-000003"
            });
        return harness;
    }

    private static void SeedOrder(
        CloseDocumentHarness harness,
        long orderId,
        long orderLineId,
        string orderRef,
        OrderType type,
        OrderStatus status,
        string huCode,
        double qty)
    {
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = orderRef,
            Type = type,
            Status = status,
            PartnerId = 200,
            CreatedAt = new DateTime(2026, 5, 8, 9, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = orderLineId,
            OrderId = orderId,
            ItemId = 1001,
            QtyOrdered = qty,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedHu(new HuRecord
        {
            Id = orderId,
            Code = huCode,
            Status = "ACTIVE",
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedBalance(1001, 1, qty, huCode);
        harness.SeedOrderReceiptPlanLines(orderId, new OrderReceiptPlanLine
        {
            Id = orderId,
            OrderId = orderId,
            OrderLineId = orderLineId,
            ItemId = 1001,
            ItemName = "Горчица",
            QtyPlanned = qty,
            ToLocationId = 1,
            ToLocationCode = "FG-01",
            ToHu = huCode
        });
    }
}
