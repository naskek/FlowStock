using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class ProductionPalletCloseTests
{
    [Fact]
    public void CloseProductionReceipt_WithUnfilledProductionPallets_Fails()
    {
        var harness = CreateHarness();
        harness.SeedProductionPallet(BuildPallet(ProductionPalletStatus.Planned));

        var result = harness.CreateService().TryCloseDoc(20, allowNegative: false);

        Assert.False(result.Success);
        Assert.Contains("Нельзя закрыть выпуск: есть ненаполненные паллеты", result.Errors);
        Assert.Empty(harness.LedgerEntries);
        Assert.Equal(DocStatus.Draft, harness.GetDoc(20).Status);
    }

    [Fact]
    public void CloseProductionReceipt_WithFilledProductionPallets_WritesLedgerOnce()
    {
        var harness = CreateHarness();
        harness.SeedProductionPallet(BuildPallet(ProductionPalletStatus.Planned));
        var palletService = new ProductionPalletService(harness.Store);
        var fill = palletService.Fill("HU-000001", "TSD-01");
        Assert.True(fill.Success);
        Assert.Empty(harness.LedgerEntries);

        var close = harness.CreateService().TryCloseDoc(20, allowNegative: false);

        Assert.True(close.Success);
        Assert.Single(harness.LedgerEntries);
        Assert.Equal(DocStatus.Closed, harness.GetDoc(20).Status);
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(10).Status);
    }

    [Fact]
    public void CloseProductionReceipt_WithFilledProductionPallets_SkipsLegacyHuDistributionValidation()
    {
        var harness = CreateHarness(orderQty: 1200, firstHu: null, firstLineQty: 1200, maxQtyPerHu: 600);
        harness.SeedProductionPallet(BuildPallet(
            id: 1,
            docLineId: 201,
            huCode: "HU-PLAN-001",
            status: ProductionPalletStatus.Planned,
            plannedQty: 1200));
        var palletService = new ProductionPalletService(harness.Store);
        Assert.True(palletService.Fill("HU-PLAN-001", "TSD-01").Success);

        var close = harness.CreateService().TryCloseDoc(20, allowNegative: false);

        Assert.True(close.Success);
        Assert.Single(harness.LedgerEntries);
        Assert.Equal("HU-PLAN-001", harness.LedgerEntries.Single().HuCode);
        Assert.Equal(DocStatus.Closed, harness.GetDoc(20).Status);
    }

    [Fact]
    public void ProductionOrder_PlanFillCloseAndShip_UsesPlannedHu()
    {
        var harness = CreateOrderPlanningHarness();
        var palletService = new ProductionPalletService(harness.Store);
        var documentService = harness.CreateService();

        var plan = palletService.PlanOrder(10);
        var pallet = Assert.Single(harness.Store.GetProductionPalletsByDoc(plan.PrdDocId));
        Assert.True(palletService.Fill(pallet.HuCode, "TSD-01").Success);
        Assert.True(documentService.TryCloseDoc(plan.PrdDocId, allowNegative: false).Success);

        harness.SeedPartner(new Partner { Id = 1, Name = "Клиент" });
        harness.SeedDoc(new Doc
        {
            Id = 30,
            DocRef = "OUT-2026-000001",
            Type = DocType.Outbound,
            Status = DocStatus.Draft,
            PartnerId = 1,
            OrderId = 10,
            CreatedAt = new DateTime(2026, 5, 13, 12, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 301,
            DocId = 30,
            OrderLineId = 101,
            ItemId = 100,
            Qty = 600,
            FromLocationId = 1,
            FromHu = pallet.HuCode
        });

        var outboundClose = documentService.TryCloseDoc(30, allowNegative: false);

        Assert.True(outboundClose.Success);
        Assert.Equal(DocStatus.Closed, harness.GetDoc(30).Status);
        Assert.Equal(2, harness.LedgerEntries.Count);
        Assert.Equal(0, harness.LedgerEntries.Where(entry => entry.HuCode == pallet.HuCode).Sum(entry => entry.QtyDelta));
        Assert.Contains(harness.LedgerEntries, entry => entry.DocId == plan.PrdDocId && entry.QtyDelta == 600 && entry.HuCode == pallet.HuCode);
        Assert.Contains(harness.LedgerEntries, entry => entry.DocId == 30 && entry.QtyDelta == -600 && entry.HuCode == pallet.HuCode);
    }

    [Fact]
    public void FillPallet_UsesPlannedHuOnly_WithoutWritingLedger()
    {
        var harness = CreateTwoPalletHarness();
        var palletService = new ProductionPalletService(harness.Store);

        var fill = palletService.Fill("HU-PLAN-001", "TSD-01");

        Assert.True(fill.Success);
        Assert.Empty(harness.LedgerEntries);
        var filled = harness.Store.GetProductionPalletsByDoc(20).Single(row => row.HuCode == "HU-PLAN-001");
        Assert.Equal(ProductionPalletStatus.Filled, filled.Status);
    }

    [Fact]
    public void CloseProductionReceipt_WithAllFilledProductionPallets_KeepsStockOnPlannedHusWithoutDuplicate()
    {
        var harness = CreateTwoPalletHarness();
        var palletService = new ProductionPalletService(harness.Store);
        Assert.True(palletService.Fill("HU-PLAN-001", "TSD-01").Success);
        Assert.True(palletService.Fill("HU-PLAN-002", "TSD-01").Success);
        Assert.Empty(harness.LedgerEntries);

        var close = harness.CreateService().TryCloseDoc(20, allowNegative: false);

        Assert.True(close.Success);
        Assert.Equal(2, harness.LedgerEntries.Count);
        Assert.Equal(600, harness.LedgerEntries.Where(row => row.HuCode == "HU-PLAN-001").Sum(row => row.QtyDelta));
        Assert.Equal(600, harness.LedgerEntries.Where(row => row.HuCode == "HU-PLAN-002").Sum(row => row.QtyDelta));
        Assert.DoesNotContain(harness.LedgerEntries, row =>
            !string.Equals(row.HuCode, "HU-PLAN-001", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(row.HuCode, "HU-PLAN-002", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(DocStatus.Closed, harness.GetDoc(20).Status);
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(10).Status);
    }

    [Fact]
    public void CloseProductionReceipt_WithDraftPalletLedgerRows_DoesNotDuplicateReceiptLedger()
    {
        var harness = CreateHarness();
        harness.SeedProductionPallet(BuildPallet(ProductionPalletStatus.Planned));
        var palletService = new ProductionPalletService(harness.Store);
        Assert.True(palletService.Fill("HU-000001", "TSD-01").Success);
        harness.Store.AddLedgerEntry(new LedgerEntry
        {
            Timestamp = new DateTime(2026, 5, 13, 10, 0, 0),
            DocId = 20,
            ItemId = 100,
            LocationId = 1,
            QtyDelta = 600,
            HuCode = "HU-000001"
        });

        var result = harness.CreateService().TryCloseDoc(20, allowNegative: false);

        Assert.True(result.Success);
        Assert.Equal(DocStatus.Closed, harness.GetDoc(20).Status);
        Assert.Single(harness.LedgerEntries);
        Assert.Equal(600, harness.LedgerEntries.Single().QtyDelta);
    }

    [Fact]
    public void AutoDistributeProductionReceiptHus_WithProductionPallets_DoesNotChangePlannedHus()
    {
        var harness = CreateTwoPalletHarness();
        var beforeLines = harness.GetDocLines(20).Select(line => line.ToHu).ToArray();
        var beforePallets = harness.Store.GetProductionPalletsByDoc(20).Select(pallet => pallet.HuCode).ToArray();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            harness.CreateService().AutoDistributeProductionReceiptHus(20));

        Assert.Equal("Для выпуска с планом паллет распределение HU выполняется через план паллет", ex.Message);
        Assert.Equal(beforeLines, harness.GetDocLines(20).Select(line => line.ToHu).ToArray());
        Assert.Equal(beforePallets, harness.Store.GetProductionPalletsByDoc(20).Select(pallet => pallet.HuCode).ToArray());
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void OrderReceiptRemaining_UsesFilledProductionPalletsWithoutDoubleCounting()
    {
        var harness = CreateTwoPalletHarness();
        var documentService = harness.CreateService();
        var palletService = new ProductionPalletService(harness.Store);

        var initial = Assert.Single(documentService.GetOrderReceiptRemaining(10));
        Assert.Equal(0, initial.QtyReceived);
        Assert.Equal(1200, initial.QtyRemaining);

        Assert.True(palletService.Fill("HU-PLAN-001", "TSD-01").Success);
        var partial = Assert.Single(documentService.GetOrderReceiptRemaining(10));
        Assert.Equal(600, partial.QtyReceived);
        Assert.Equal(600, partial.QtyRemaining);

        Assert.True(palletService.Fill("HU-PLAN-002", "TSD-01").Success);
        var fullBeforeClose = Assert.Single(documentService.GetOrderReceiptRemaining(10));
        Assert.Equal(1200, fullBeforeClose.QtyReceived);
        Assert.Equal(0, fullBeforeClose.QtyRemaining);

        var close = documentService.TryCloseDoc(20, allowNegative: false);

        Assert.True(close.Success);
        var fullAfterClose = Assert.Single(documentService.GetOrderReceiptRemaining(10));
        Assert.Equal(1200, fullAfterClose.QtyReceived);
        Assert.Equal(0, fullAfterClose.QtyRemaining);
        Assert.Equal(2, harness.LedgerEntries.Count);
    }

    [Fact]
    public void OutboundClose_RejectsHuFromOpenProductionReceiptEvenIfBrokenLedgerExists()
    {
        var harness = CreateHarness();
        harness.SeedProductionPallet(BuildPallet(ProductionPalletStatus.Planned));
        var palletService = new ProductionPalletService(harness.Store);
        Assert.True(palletService.Fill("HU-000001", "TSD-01").Success);
        harness.Store.AddLedgerEntry(new LedgerEntry
        {
            Timestamp = new DateTime(2026, 5, 13, 10, 0, 0),
            DocId = 20,
            ItemId = 100,
            LocationId = 1,
            QtyDelta = 600,
            HuCode = "HU-000001"
        });
        harness.SeedDoc(new Doc
        {
            Id = 30,
            DocRef = "OUT-2026-000001",
            Type = DocType.Outbound,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 5, 13, 12, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 301,
            DocId = 30,
            ItemId = 100,
            Qty = 600,
            FromLocationId = 1,
            FromHu = "HU-000001"
        });

        var result = harness.CreateService().TryCloseDoc(30, allowNegative: false);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("ожидает закрытия PRD", StringComparison.OrdinalIgnoreCase));
    }

    private static CloseDocumentHarness CreateTwoPalletHarness()
    {
        var harness = CreateHarness(orderQty: 1200, firstHu: "HU-PLAN-001");
        harness.SeedLine(new DocLine
        {
            Id = 202,
            DocId = 20,
            OrderLineId = 101,
            ItemId = 100,
            Qty = 600,
            ToLocationId = 1,
            ToHu = "HU-PLAN-002"
        });
        harness.SeedProductionPallet(BuildPallet(
            id: 1,
            docLineId: 201,
            huCode: "HU-PLAN-001",
            status: ProductionPalletStatus.Planned));
        harness.SeedProductionPallet(BuildPallet(
            id: 2,
            docLineId: 202,
            huCode: "HU-PLAN-002",
            status: ProductionPalletStatus.Planned));
        return harness;
    }

    private static CloseDocumentHarness CreateOrderPlanningHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Товар",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "056",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 100,
            QtyOrdered = 600
        });
        return harness;
    }

    private static CloseDocumentHarness CreateHarness(
        double orderQty = 600,
        string? firstHu = "HU-000001",
        double firstLineQty = 600,
        double? maxQtyPerHu = null)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item { Id = 100, Name = "Товар", MaxQtyPerHu = maxQtyPerHu });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "056",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 100,
            QtyOrdered = orderQty
        });
        harness.SeedDoc(new Doc
        {
            Id = 20,
            DocRef = "PRD-2026-000001",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 201,
            DocId = 20,
            OrderLineId = 101,
            ItemId = 100,
            Qty = firstLineQty,
            ToLocationId = 1,
            ToHu = firstHu
        });
        return harness;
    }

    private static ProductionPallet BuildPallet(string status)
    {
        return BuildPallet(id: 1, docLineId: 201, huCode: "HU-000001", status: status);
    }

    private static ProductionPallet BuildPallet(long id, long docLineId, string huCode, string status, double plannedQty = 600)
    {
        return new ProductionPallet
        {
            Id = id,
            PrdDocId = 20,
            DocLineId = docLineId,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 100,
            ItemName = "Товар",
            HuCode = huCode,
            PlannedQty = plannedQty,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            Status = status,
            FilledAt = status == ProductionPalletStatus.Filled ? new DateTime(2026, 5, 13, 10, 0, 0) : null,
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        };
    }
}
