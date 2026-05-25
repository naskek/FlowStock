using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Diagnostics;

public sealed class NewLedgerTransitionTests
{
    [Fact]
    public void DryRun_ReportsStaleActiveCustomerReservationWithoutMutating()
    {
        var harness = CreateStaleReservationHarness();
        var service = new NewLedgerTransitionService(harness.Store);

        var report = service.DryRun();

        Assert.False(report.Applied);
        Assert.Equal(report.LedgerRowsBefore, report.LedgerRowsAfter);
        var stale = Assert.Single(report.StaleReservations);
        Assert.Equal(87, stale.OrderId);
        Assert.Equal(8701, stale.OrderLineId);
        Assert.Equal(6, stale.ItemId);
        Assert.Equal("HU-0000478", stale.ToHu);
        Assert.Equal(600, stale.Qty);
        Assert.Equal(0, stale.CurrentBalance);
        Assert.Single(harness.Store.GetOrderReceiptPlanLines(87));
        Assert.Contains(report.PlannedActions, action => action.ActionCode == NewLedgerTransitionActionCodes.RemoveStaleReservation);
        Assert.Contains(report.PlannedActions, action => action.ActionCode == NewLedgerTransitionActionCodes.RebuildActiveCustomerReservation);
    }

    [Fact]
    public void Apply_RemovesOnlyStaleReservationsAndKeepsLedgerUnchanged()
    {
        var harness = CreateStaleReservationHarness();
        var service = new NewLedgerTransitionService(harness.Store);

        var first = service.Apply();
        var second = service.Apply();

        Assert.True(first.Applied);
        Assert.Equal(first.LedgerRowsBefore, first.LedgerRowsAfter);
        Assert.Empty(harness.Store.GetOrderReceiptPlanLines(87));
        Assert.True(second.Applied);
        Assert.Equal(0, second.StaleReservationCount);
        Assert.Equal(second.LedgerRowsBefore, second.LedgerRowsAfter);
    }

    [Fact]
    public void DryRun_ReportsFilledPalletWithoutLedgerAndDraftPrdWithLedgerAsDiagnostics()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = 6, Name = "Горчица" });
        harness.SeedLocation(new Location { Id = 10, Code = "FG", Name = "FG" });
        harness.SeedDoc(new Doc
        {
            Id = 172,
            DocRef = "PRD-2026-000172",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 490,
            PrdDocId = 172,
            DocLineId = 17201,
            ItemId = 6,
            HuCode = "HU-0000490",
            PlannedQty = 600,
            ToLocationId = 10,
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 13, 9, 0, 0)
        });
        harness.SeedLedgerEntry(172, 6, 10, 600, "HU-OTHER");

        var report = new NewLedgerTransitionService(harness.Store).DryRun();

        var missingLedger = Assert.Single(report.FilledPalletsWithoutLedger);
        Assert.Equal("HU-0000490", missingLedger.HuCode);
        var draftWithLedger = Assert.Single(report.DraftPrdsWithLedger);
        Assert.Equal(172, draftWithLedger.PrdDocId);
        Assert.Contains(report.PlannedActions, action => action.ActionCode == NewLedgerTransitionActionCodes.ReportFilledWithoutLedger);
        Assert.Contains(report.PlannedActions, action => action.ActionCode == NewLedgerTransitionActionCodes.ReportDraftPrdWithLedger);
    }

    [Fact]
    public async Task Endpoint_ApplyRequiresConfirmApply()
    {
        var harness = CreateStaleReservationHarness();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var response = await host.Client.PostAsJsonAsync(
            "/api/admin/maintenance/new-ledger-transition/apply",
            new { confirm = "NO" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(harness.Store.GetOrderReceiptPlanLines(87));
    }

    [Fact]
    public async Task OrderLinesEndpoint_ExcludesStaleReservedHuFromProducedQtyAndProductionHuCodes()
    {
        var harness = CreateStaleReservationHarness();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var response = await host.Client.GetAsync("/api/orders/87/lines");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var line = document.RootElement.EnumerateArray().Single();
        Assert.Equal(0, line.GetProperty("qty_produced").GetDouble());
        Assert.Equal(0, line.GetProperty("can_ship_now").GetDouble());
        Assert.Equal(600, line.GetProperty("shortage").GetDouble());
        Assert.Empty(line.GetProperty("production_hu_codes").EnumerateArray());
    }

    [Fact]
    public void OutboundClose_RejectsStaleReservedHuWithZeroLedgerBalance()
    {
        var harness = CreateStaleReservationHarness();
        harness.SeedDoc(new Doc
        {
            Id = 187,
            DocRef = "OUT-2026-000187",
            Type = DocType.Outbound,
            Status = DocStatus.Draft,
            OrderId = 87,
            PartnerId = 1,
            CreatedAt = new DateTime(2026, 5, 14, 8, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 18701,
            DocId = 187,
            OrderLineId = 8701,
            ItemId = 6,
            Qty = 600,
            FromLocationId = 10,
            FromHu = "HU-0000478"
        });

        var result = harness.CreateService().TryCloseDoc(187, allowNegative: false);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("HU-0000478", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(harness.LedgerEntries, entry => entry.DocId == 187);
    }

    [Fact]
    public async Task InternalOrderLines_UseGrossReceiptLedgerForProducedQty_AndDoNotExposeZeroBalanceHu()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = 6, Name = "Горчица" });
        harness.SeedLocation(new Location { Id = 10, Code = "FG", Name = "FG" });
        harness.SeedOrder(new Order
        {
            Id = 72,
            OrderRef = "072",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 189,
            OrderId = 72,
            ItemId = 6,
            QtyOrdered = 600
        });
        harness.SeedDoc(new Doc
        {
            Id = 720,
            DocRef = "PRD-2026-000172",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 72,
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 7201,
            DocId = 720,
            OrderLineId = 189,
            ItemId = 6,
            Qty = 600,
            ToLocationId = 10,
            ToHu = "HU-0000478"
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 478,
            PrdDocId = 720,
            DocLineId = 7201,
            OrderId = 72,
            OrderLineId = 189,
            ItemId = 6,
            HuCode = "HU-0000478",
            PlannedQty = 600,
            ToLocationId = 10,
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 13, 10, 0, 0)
        });
        harness.SeedLedgerEntry(720, 6, 10, 600, "HU-0000478");
        harness.SeedLedgerEntry(901, 6, 10, -600, "HU-0000478");

        var status = new OrderService(harness.Store).RefreshPersistedStatus(72);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var response = await host.Client.GetAsync("/api/orders/72/lines");

        Assert.Equal(OrderStatus.Shipped, status);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var line = document.RootElement.EnumerateArray().Single();
        Assert.Equal(600, line.GetProperty("qty_produced").GetDouble(), 3);
        Assert.Equal(0, line.GetProperty("qty_left").GetDouble(), 3);
        Assert.Empty(line.GetProperty("production_hu_codes").EnumerateArray());
    }

    [Fact]
    public void FilledLedgerRepairDryRun_ReportsOnlyRequestedPalletsAndDoesNotMutate()
    {
        var harness = CreateFilledLedgerRepairHarness();
        var service = new NewLedgerTransitionService(harness.Store);

        var report = service.DryRunFilledLedgerRepair(new FilledLedgerRepairRequest
        {
            PalletIds = [105]
        });

        var candidate = Assert.Single(report.Candidates);
        Assert.True(report.DryRun);
        Assert.Equal(105, candidate.PalletId);
        Assert.Equal(FilledLedgerRepairDecisions.SafeToBackfill, candidate.Decision);
        Assert.Empty(harness.LedgerEntries);
        Assert.Equal(0, harness.Store.GetLedgerQtyByDocItemHu(181, 6, "HU-0000105"));
    }

    [Fact]
    public void FilledLedgerRepairApply_WritesOnlyRequestedSafePalletsAndIsIdempotent()
    {
        var harness = CreateFilledLedgerRepairHarness();
        var service = new NewLedgerTransitionService(harness.Store);

        var first = service.ApplyFilledLedgerRepair(new FilledLedgerRepairRequest
        {
            PalletIds = [105]
        });
        var second = service.ApplyFilledLedgerRepair(new FilledLedgerRepairRequest
        {
            PalletIds = [105]
        });

        Assert.False(first.DryRun);
        Assert.Equal(1, first.LedgerRowsWritten);
        Assert.Equal([105L], first.AppliedPalletIds);
        Assert.Equal(0, second.LedgerRowsWritten);
        Assert.Equal(600, harness.Store.GetLedgerQtyByDocItemHu(181, 6, "HU-0000105"), 3);
        Assert.Equal(0, harness.Store.GetLedgerQtyByDocItemHu(181, 6, "HU-0000106"), 3);
    }

    [Fact]
    public void FilledLedgerRepairDryRun_SkipsCancelledPrintedAndAlreadyReceiptedPallets()
    {
        var harness = CreateFilledLedgerRepairHarness();
        harness.SeedProductionPallet(BuildPallet(107, "HU-0000107", ProductionPalletStatus.Cancelled));
        harness.SeedProductionPallet(BuildPallet(108, "HU-0000108", ProductionPalletStatus.Printed));
        harness.SeedProductionPallet(BuildPallet(109, "HU-0000109", ProductionPalletStatus.Filled));
        harness.SeedLedgerEntry(181, 6, 10, 600, "HU-0000109");

        var report = new NewLedgerTransitionService(harness.Store).DryRunFilledLedgerRepair(new FilledLedgerRepairRequest
        {
            PalletIds = [107, 108, 109]
        });

        Assert.Contains(report.Candidates, row => row.PalletId == 107 && row.Decision == FilledLedgerRepairDecisions.SkipCancelled);
        Assert.Contains(report.Candidates, row => row.PalletId == 108 && row.Decision == FilledLedgerRepairDecisions.SkipNotFilled);
        Assert.Contains(report.Candidates, row => row.PalletId == 109 && row.Decision == FilledLedgerRepairDecisions.SkipAlreadyHasReceiptLedger);
        Assert.All(report.Candidates, row => Assert.NotEqual(FilledLedgerRepairDecisions.SafeToBackfill, row.Decision));
    }

    [Fact]
    public void FilledLedgerRepairApply_BackfillsAndClosesSafeInternalDraftPrd()
    {
        var harness = CreateFilledLedgerRepairHarness();
        var service = new NewLedgerTransitionService(harness.Store);

        var report = service.ApplyFilledLedgerRepair(new FilledLedgerRepairRequest
        {
            OrderIds = [72],
            CloseStaleInternalPrdDrafts = true
        });
        var repeat = service.ApplyFilledLedgerRepair(new FilledLedgerRepairRequest
        {
            OrderIds = [72],
            CloseStaleInternalPrdDrafts = true
        });

        Assert.Equal(2, report.LedgerRowsWritten);
        Assert.Equal([181L], report.ClosedPrdDocIds);
        Assert.Equal([72L], report.RefreshedOrderIds);
        Assert.Equal(DocStatus.Closed, harness.GetDoc(181).Status);
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(72).Status);
        Assert.Equal(0, repeat.LedgerRowsWritten);
        Assert.Empty(repeat.ClosedPrdDocIds);
        Assert.Equal(1200, harness.LedgerEntries.Where(entry => entry.DocId == 181).Sum(entry => entry.QtyDelta), 3);
    }

    [Fact]
    public void FilledLedgerRepairApply_CustomerPalletCanBeBackfilledButOrderStatusIsNotForced()
    {
        var harness = CreateFilledLedgerRepairHarness();
        SeedCustomerRepairCandidate(harness);

        var report = new NewLedgerTransitionService(harness.Store).ApplyFilledLedgerRepair(new FilledLedgerRepairRequest
        {
            OrderIds = [75],
            PalletIds = [65],
            CloseStaleInternalPrdDrafts = true
        });

        Assert.Equal(1, report.LedgerRowsWritten);
        Assert.Equal([65L], report.AppliedPalletIds);
        Assert.Empty(report.ClosedPrdDocIds);
        Assert.Equal(OrderStatus.Accepted, harness.GetOrder(75).Status);
    }

    private static CloseDocumentHarness CreateStaleReservationHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItemType(new ItemType { Id = 1, Name = "Готовая продукция", EnableOrderReservation = true });
        harness.SeedItem(new Item { Id = 6, Name = "Горчица", ItemTypeId = 1 });
        harness.SeedLocation(new Location { Id = 10, Code = "FG", Name = "FG" });
        harness.SeedPartner(new Partner { Id = 1, Name = "Клиент" });
        harness.SeedOrder(new Order
        {
            Id = 87,
            OrderRef = "086",
            Type = OrderType.Customer,
            Status = OrderStatus.Accepted,
            PartnerId = 1,
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 8701,
            OrderId = 87,
            ItemId = 6,
            QtyOrdered = 600
        });
        harness.SeedOrderReceiptPlanLines(87, new OrderReceiptPlanLine
        {
            Id = 1,
            OrderId = 87,
            OrderLineId = 8701,
            ItemId = 6,
            QtyPlanned = 600,
            ToLocationId = 10,
            ToHu = "HU-0000478"
        });
        return harness;
    }

    private static CloseDocumentHarness CreateFilledLedgerRepairHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = 6, Name = "Горчица" });
        harness.SeedLocation(new Location { Id = 10, Code = "FG", Name = "FG" });
        harness.SeedOrder(new Order
        {
            Id = 72,
            OrderRef = "072",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine { Id = 189, OrderId = 72, ItemId = 6, QtyOrdered = 1200 });
        harness.SeedDoc(new Doc
        {
            Id = 181,
            DocRef = "PRD-2026-000181",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 72,
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 18101,
            DocId = 181,
            OrderLineId = 189,
            ItemId = 6,
            Qty = 600,
            ToLocationId = 10,
            ToHu = "HU-0000105"
        });
        harness.SeedLine(new DocLine
        {
            Id = 18102,
            DocId = 181,
            OrderLineId = 189,
            ItemId = 6,
            Qty = 600,
            ToLocationId = 10,
            ToHu = "HU-0000106"
        });
        harness.SeedProductionPallet(BuildPallet(105, "HU-0000105", ProductionPalletStatus.Filled, docLineId: 18101));
        harness.SeedProductionPallet(BuildPallet(106, "HU-0000106", ProductionPalletStatus.Filled, docLineId: 18102));
        return harness;
    }

    private static void SeedCustomerRepairCandidate(CloseDocumentHarness harness)
    {
        harness.SeedItem(new Item { Id = 64, Name = "Customer item" });
        harness.SeedPartner(new Partner { Id = 75, Name = "Customer" });
        harness.SeedOrder(new Order
        {
            Id = 75,
            OrderRef = "075",
            Type = OrderType.Customer,
            Status = OrderStatus.Accepted,
            PartnerId = 75,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine { Id = 7501, OrderId = 75, ItemId = 64, QtyOrdered = 840 });
        harness.SeedDoc(new Doc
        {
            Id = 190,
            DocRef = "PRD-2026-000190",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 75,
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 19001,
            DocId = 190,
            OrderLineId = 7501,
            ItemId = 64,
            Qty = 840,
            ToLocationId = 10,
            ToHu = "HU-0000065"
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 65,
            PrdDocId = 190,
            DocLineId = 19001,
            OrderId = 75,
            OrderLineId = 7501,
            ItemId = 64,
            HuCode = "HU-0000065",
            PlannedQty = 840,
            ToLocationId = 10,
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 13, 10, 0, 0)
        });
    }

    private static ProductionPallet BuildPallet(
        long id,
        string huCode,
        string status,
        long docLineId = 18101) =>
        new()
        {
            Id = id,
            PrdDocId = 181,
            DocLineId = docLineId,
            OrderId = 72,
            OrderLineId = 189,
            ItemId = 6,
            HuCode = huCode,
            PlannedQty = 600,
            ToLocationId = 10,
            Status = status,
            FilledAt = status == ProductionPalletStatus.Filled ? new DateTime(2026, 5, 13, 10, 0, 0) : null
        };
}
