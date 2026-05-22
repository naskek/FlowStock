using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.CloseDocument;

public sealed class OutboundPickingServiceTests
{
    [Fact]
    public void ListOnlyAcceptedCustomerOrders()
    {
        var harness = CreateBasicPickingHarness();
        SeedOrder(harness, 30, 301, "SO-DRAFT", OrderType.Customer, OrderStatus.InProgress, "HU-000030", 3);
        SeedOrder(harness, 40, 401, "INT-READY", OrderType.Internal, OrderStatus.Accepted, "HU-000040", 4);

        var rows = CreatePickingService(harness).GetOrders();

        var row = Assert.Single(rows);
        Assert.Equal(20, row.OrderId);
        Assert.Equal("SO-020", row.OrderRef);
        Assert.Equal(1, row.ExpectedHuCount);
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
        SeedOrder(harness, 30, 301, "SO-030", OrderType.Customer, OrderStatus.InProgress, "HU-000030", 3);
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
    public void CompleteMarksReadyButDoesNotClose()
    {
        var harness = CreateBasicPickingHarness();
        var service = CreatePickingService(harness, autoClose: false);
        service.Scan(20, "HU-000001", "TSD-01");

        var result = service.Complete(20);

        Assert.True(result.Success);
        Assert.Equal("Все паллеты подобраны. Ожидает проведения в WPF.", result.Message);
        var details = result.Order;
        Assert.NotNull(details);
        var draftId = details.DraftOutboundDocId!.Value;
        var draft = harness.GetDoc(draftId);
        Assert.Equal(DocStatus.Draft, draft.Status);
        Assert.Equal("TSD OUTBOUND PICKING READY", draft.Comment);
        Assert.Empty(harness.LedgerEntries);
        Assert.Equal(OrderStatus.Accepted, harness.GetOrder(20).Status);
    }

    [Fact]
    public void ScanWithAutoCloseWritesLedgerAndShipsOrder()
    {
        var harness = CreateBasicPickingHarness();
        var picking = CreatePickingService(harness, autoClose: true);
        var scan = picking.Scan(20, "HU-000001", "TSD-01");

        Assert.True(scan.Success, $"{scan.ErrorCode}: {scan.Message}");
        Assert.True(scan.OutboundClosed);
        Assert.NotNull(scan.ClosedOutboundDocRef);
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
        picking.Complete(20);
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

        var result = CreatePickingService(harness).Scan(20, "HU-000001", "TSD-01");

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
