using System.Net;
using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.UpdateOrder.Infrastructure;

namespace FlowStock.Server.Tests.UpdateOrder;

[Collection("UpdateOrder")]
public sealed class InternalOrderPalletQtyApiIntegrationTests
{
    [Fact]
    public async Task DecreaseBelowFilled_ReturnsBadRequest_AndKeepsQtyAndFilledPallets()
    {
        var fixture = InternalOrderPalletQtyUpdateScenario.Create(
            orderedQty: 1200,
            filledPalletCount: 2,
            openPalletCount: 0,
            filledPalletsWithoutComponentLines: true);
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        using var response = await UpdateOrderHttpApi.PutRawAsync(
            host.Client,
            fixture.OrderId,
            BuildJson(fixture.ItemId, 600));

        var payload = await UpdateOrderHttpApi.ReadApiErrorResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("ORDER_LINE_QTY_BELOW_COVERAGE", payload.Error);
        Assert.Contains("заполнено 1200", payload.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var line = Assert.Single(fixture.Harness.Store.GetOrderLines(fixture.OrderId));
        Assert.Equal(1200, line.QtyOrdered, 3);
        var pallets = fixture.Harness.Store.GetProductionPalletsByDoc(fixture.PrdDocId);
        Assert.Equal(2, pallets.Count(pallet => pallet.Status == ProductionPalletStatus.Filled));
        Assert.Equal(1200, fixture.Harness.Store.GetFilledProductionPalletQtyByOrderLine(fixture.OrderLineId), 3);
    }

    [Fact]
    public async Task Decrease4800To2400_Succeeds_TrimsSurplusOpenPallets_AndKeepsFilled()
    {
        var fixture = InternalOrderPalletQtyUpdateScenario.Create(
            orderedQty: 4800,
            filledPalletCount: 2,
            openPalletCount: 6);
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        var payload = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            fixture.OrderId,
            InternalOrderPalletQtyUpdateScenario.BuildUpdateRequest(2400));

        Assert.True(payload.Ok);
        var line = Assert.Single(fixture.Harness.Store.GetOrderLines(fixture.OrderId));
        Assert.Equal(2400, line.QtyOrdered, 3);

        var pallets = fixture.Harness.Store.GetProductionPalletsByDoc(fixture.PrdDocId);
        Assert.Equal(2, pallets.Count(pallet => pallet.Status == ProductionPalletStatus.Filled));
        Assert.Equal(1200, pallets.Where(pallet => pallet.Status == ProductionPalletStatus.Filled).Sum(pallet => pallet.PlannedQty), 3);
        Assert.Equal(2, pallets.Count(pallet =>
            pallet.Status == ProductionPalletStatus.Planned
            || pallet.Status == ProductionPalletStatus.Printed));
        Assert.Equal(4, pallets.Count(pallet => pallet.Status == ProductionPalletStatus.Cancelled));
        Assert.Equal(1200, ActiveOpenQty(pallets, fixture.OrderLineId), 3);
    }

    [Fact]
    public async Task Decrease4800To1200_Succeeds_RemovesActiveOpenPlanBeyondFilled()
    {
        var fixture = InternalOrderPalletQtyUpdateScenario.Create(
            orderedQty: 4800,
            filledPalletCount: 2,
            openPalletCount: 6,
            openPalletsArePrinted: true);
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        var payload = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            fixture.OrderId,
            InternalOrderPalletQtyUpdateScenario.BuildUpdateRequest(1200));

        Assert.True(payload.Ok);
        var pallets = fixture.Harness.Store.GetProductionPalletsByDoc(fixture.PrdDocId);
        Assert.Equal(2, pallets.Count(pallet => pallet.Status == ProductionPalletStatus.Filled));
        Assert.Equal(0, ActiveOpenQty(pallets, fixture.OrderLineId), 3);
        Assert.Equal(6, pallets.Count(pallet => pallet.Status == ProductionPalletStatus.Cancelled));

        var printRows = new ProductionPalletService(fixture.Harness.Store).GetPrintRows(fixture.OrderId);
        Assert.Equal(2, printRows.Count);
        Assert.DoesNotContain(printRows, row =>
            row.Status == ProductionPalletStatus.Planned
            || row.Status == ProductionPalletStatus.Printed);
        Assert.Empty(PalletLabelPrintSelectionService.ResolveDefaultSelectedPalletIds(printRows));
    }

    [Fact]
    public async Task Increase1200To2400_Succeeds_AppendsMissingPlannedWithoutDuplicates()
    {
        var fixture = InternalOrderPalletQtyUpdateScenario.Create(
            orderedQty: 1200,
            filledPalletCount: 2,
            openPalletCount: 0);
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        var first = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            fixture.OrderId,
            InternalOrderPalletQtyUpdateScenario.BuildUpdateRequest(2400));

        Assert.True(first.Ok);
        var afterFirst = fixture.Harness.Store.GetProductionPalletsByDoc(fixture.PrdDocId);
        Assert.Equal(2, afterFirst.Count(pallet => pallet.Status == ProductionPalletStatus.Filled));
        Assert.Equal(2, afterFirst.Count(pallet => pallet.Status == ProductionPalletStatus.Planned));
        Assert.Equal(2400, ActivePalletQty(afterFirst, fixture.OrderLineId), 3);

        var second = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            fixture.OrderId,
            InternalOrderPalletQtyUpdateScenario.BuildUpdateRequest(2400));

        Assert.True(second.Ok);
        var afterSecond = fixture.Harness.Store.GetProductionPalletsByDoc(fixture.PrdDocId);
        Assert.Equal(
            afterFirst.Select(pallet => pallet.Id).OrderBy(id => id),
            afterSecond.Select(pallet => pallet.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task DecreaseWithPrintedSurplus_AllowsTrim_AndDoesNotCountPrintedAsFilled()
    {
        var fixture = InternalOrderPalletQtyUpdateScenario.Create(
            orderedQty: 4800,
            filledPalletCount: 2,
            openPalletCount: 6,
            openPalletsArePrinted: true);
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        var payload = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            fixture.OrderId,
            InternalOrderPalletQtyUpdateScenario.BuildUpdateRequest(2400));

        Assert.True(payload.Ok);
        Assert.Equal(1200, fixture.Harness.Store.GetFilledProductionPalletQtyByOrderLine(fixture.OrderLineId), 3);
        var pallets = fixture.Harness.Store.GetProductionPalletsByDoc(fixture.PrdDocId);
        Assert.Equal(2, pallets.Count(pallet => pallet.Status == ProductionPalletStatus.Printed));
        Assert.Equal(4, pallets.Count(pallet => pallet.Status == ProductionPalletStatus.Cancelled));
    }

    [Fact]
    public async Task DecreaseThenIncrease4800_FullCycle_CreatesNewPlannedWithoutDuplicateDocLine()
    {
        var fixture = InternalOrderPalletQtyUpdateScenario.Create(
            orderedQty: 4800,
            filledPalletCount: 2,
            openPalletCount: 6);
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        var stepA = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            fixture.OrderId,
            InternalOrderPalletQtyUpdateScenario.BuildUpdateRequest(1200));
        Assert.True(stepA.Ok);

        var afterDecrease = fixture.Harness.Store.GetProductionPalletsByDoc(fixture.PrdDocId);
        Assert.Equal(2, afterDecrease.Count(pallet => pallet.Status == ProductionPalletStatus.Filled));
        Assert.Equal(0, ActiveOpenQty(afterDecrease, fixture.OrderLineId), 3);
        Assert.Equal(6, afterDecrease.Count(pallet => pallet.Status == ProductionPalletStatus.Cancelled));

        var stepB = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            fixture.OrderId,
            InternalOrderPalletQtyUpdateScenario.BuildUpdateRequest(4800));
        Assert.True(stepB.Ok);

        var afterIncrease = fixture.Harness.Store.GetProductionPalletsByDoc(fixture.PrdDocId);
        Assert.Equal(2, afterIncrease.Count(pallet => pallet.Status == ProductionPalletStatus.Filled));
        Assert.Equal(6, afterIncrease.Count(pallet => pallet.Status == ProductionPalletStatus.Planned));
        Assert.Equal(6, afterIncrease.Count(pallet => pallet.Status == ProductionPalletStatus.Cancelled));
        Assert.Equal(4800, ActivePalletQty(afterIncrease, fixture.OrderLineId), 3);

        var activeDocLineIds = afterIncrease
            .Where(pallet => pallet.Status != ProductionPalletStatus.Cancelled)
            .Select(pallet => pallet.DocLineId)
            .ToArray();
        Assert.Equal(activeDocLineIds.Length, activeDocLineIds.Distinct().Count());
    }

    [Fact]
    public async Task RepeatedUpdateWithSameQty_IsIdempotent()
    {
        var fixture = InternalOrderPalletQtyUpdateScenario.Create(
            orderedQty: 4800,
            filledPalletCount: 2,
            openPalletCount: 6);
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            fixture.OrderId,
            InternalOrderPalletQtyUpdateScenario.BuildUpdateRequest(1200));

        var afterFirst = fixture.Harness.Store.GetProductionPalletsByDoc(fixture.PrdDocId)
            .Select(pallet => pallet.Id)
            .OrderBy(id => id)
            .ToArray();

        await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            fixture.OrderId,
            InternalOrderPalletQtyUpdateScenario.BuildUpdateRequest(1200));

        var afterSecond = fixture.Harness.Store.GetProductionPalletsByDoc(fixture.PrdDocId)
            .Select(pallet => pallet.Id)
            .OrderBy(id => id)
            .ToArray();

        Assert.Equal(afterFirst, afterSecond);
        Assert.Equal(0, ActiveOpenQty(fixture.Harness.Store.GetProductionPalletsByDoc(fixture.PrdDocId), fixture.OrderLineId), 3);
    }

    [Fact]
    public async Task PutIncrease1200To2400_LinesApi_ReturnsFilledAndNewPlannedHuCodes()
    {
        var fixture = InternalOrderPalletQtyUpdateScenario.Create(
            orderedQty: 1200,
            filledPalletCount: 2,
            openPalletCount: 0);
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        var update = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            fixture.OrderId,
            InternalOrderPalletQtyUpdateScenario.BuildUpdateRequest(2400));
        Assert.True(update.Ok);

        using var linesResponse = await host.Client.GetAsync($"/api/orders/{fixture.OrderId}/lines");
        Assert.Equal(HttpStatusCode.OK, linesResponse.StatusCode);
        using var document = JsonDocument.Parse(await linesResponse.Content.ReadAsStringAsync());
        var line = document.RootElement.EnumerateArray().Single();
        var huCodes = line.GetProperty("production_hu_codes")
            .EnumerateArray()
            .Select(element => element.GetString())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToArray();

        Assert.Equal(2400, line.GetProperty("qty_ordered").GetDouble(), 3);
        Assert.Equal(1200, line.GetProperty("qty_produced").GetDouble(), 3);
        var plannedHuCodes = fixture.Harness.Store.GetProductionPalletsByDoc(fixture.PrdDocId)
            .Where(pallet => pallet.Status == ProductionPalletStatus.Planned)
            .Select(pallet => pallet.HuCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToArray();
        Assert.Equal(2, plannedHuCodes.Length);
        Assert.Equal(plannedHuCodes.Length, huCodes.Length);
        Assert.All(plannedHuCodes, code => Assert.Contains(code, huCodes));
        Assert.False(string.IsNullOrWhiteSpace(line.GetProperty("production_hu_codes_display").GetString()));
    }

    [Fact]
    public async Task DecreaseBelowFilled_HeadOnlyFilledPallets_StillBlockedByApi()
    {
        var fixture = InternalOrderPalletQtyUpdateScenario.Create(
            orderedQty: 1200,
            filledPalletCount: 2,
            openPalletCount: 0,
            filledPalletsWithoutComponentLines: true);
        Assert.Equal(
            1200,
            fixture.Harness.Store.GetFilledProductionPalletQtyByOrderLine(fixture.OrderLineId),
            3);

        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);
        using var response = await UpdateOrderHttpApi.PutRawAsync(
            host.Client,
            fixture.OrderId,
            BuildJson(fixture.ItemId, 600));

        var payload = await UpdateOrderHttpApi.ReadApiErrorResultAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("ORDER_LINE_QTY_BELOW_COVERAGE", payload.Error);
    }

    private static string BuildJson(long itemId, double qtyOrdered)
    {
        return $$"""
                   {
                     "order_ref": "{{InternalOrderPalletQtyUpdateScenario.DefaultOrderRef}}",
                     "type": "INTERNAL",
                     "lines": [
                       {
                         "item_id": {{itemId}},
                         "qty_ordered": {{qtyOrdered.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                         "production_purpose": "INTERNAL_STOCK"
                       }
                     ]
                   }
                   """;
    }

    private static double ActiveOpenQty(IReadOnlyList<ProductionPallet> pallets, long orderLineId)
    {
        return pallets
            .Where(pallet => pallet.Status is ProductionPalletStatus.Planned or ProductionPalletStatus.Printed)
            .Sum(pallet => ResolveQty(pallet, orderLineId));
    }

    private static double ActivePalletQty(IReadOnlyList<ProductionPallet> pallets, long orderLineId)
    {
        return pallets
            .Where(pallet => pallet.Status != ProductionPalletStatus.Cancelled)
            .Sum(pallet => ResolveQty(pallet, orderLineId));
    }

    private static double ResolveQty(ProductionPallet pallet, long orderLineId)
    {
        var componentQty = pallet.Lines
            .Where(line => line.OrderLineId == orderLineId)
            .Sum(line => line.PlannedQty);
        return componentQty > 0.000001 ? componentQty : pallet.PlannedQty;
    }
}
