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
}
