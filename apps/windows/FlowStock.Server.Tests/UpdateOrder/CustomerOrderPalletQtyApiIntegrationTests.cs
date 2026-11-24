using System.Net;
using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.UpdateOrder.Infrastructure;

namespace FlowStock.Server.Tests.UpdateOrder;

[Collection("UpdateOrder")]
public sealed class CustomerOrderPalletQtyApiIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteThreeOfFive1800Pallets_ThenDecrease9000To3600_ReopenAndReplan(bool printed)
    {
        var fixture = CreateCustomerFixture(9000, capacity: 1800);
        var pallets = fixture.Harness.CreateAtomicProductionPalletService();
        var plan = pallets.PlanOrder(fixture.OrderId);
        var original = fixture.Harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).OrderBy(x => x.Id).ToArray();
        Assert.Equal(5, original.Length);
        if (printed) pallets.MarkPrinted(fixture.OrderId, original.Select(x => x.Id).ToArray(), DateTime.UtcNow);
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);
        using var deleteResponse = await host.Client.PostAsync(
            $"/api/orders/{fixture.OrderId}/production-pallets/cancel-plan",
            new StringContent(JsonSerializer.Serialize(new { pallet_ids = original.Skip(2).Select(x => x.Id) }), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        Assert.Equal(9000, Assert.Single(fixture.Harness.Store.GetOrderLines(fixture.OrderId)).QtyOrdered);

        for (var save = 0; save < 2; save++)
        {
            var result = await UpdateOrderHttpApi.UpdateAsync(host.Client, fixture.OrderId, BuildUpdateRequest(fixture, 3600));
            Assert.True(result.Ok);
            using var response = await host.Client.GetAsync($"/api/orders/{fixture.OrderId}/lines");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var line = Assert.Single(json.RootElement.EnumerateArray());
            Assert.Equal(3600, line.GetProperty("qty_ordered").GetDouble());
            var remaining = fixture.Harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)
                .Where(x => x.Status != ProductionPalletStatus.Cancelled).OrderBy(x => x.Id).ToArray();
            Assert.Equal(original.Take(2).Select(x => x.Id), remaining.Select(x => x.Id));
            Assert.Equal(3600, remaining.Sum(x => x.PlannedQty));
        }
        pallets.PlanOrder(fixture.OrderId);
        var active = fixture.Harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)
            .Where(x => x.Status != ProductionPalletStatus.Cancelled).OrderBy(x => x.Id).ToArray();
        Assert.Equal(original.Take(2).Select(x => x.Id), active.Select(x => x.Id));
        Assert.Equal(3600, active.Sum(x => x.PlannedQty));
        Assert.Equal(3600, fixture.Harness.Store.GetDocLines(plan.PrdDocId).Sum(x => x.Qty));
        Assert.Empty(fixture.Harness.Store.GetOrderReceiptPlanLines(fixture.OrderId));
        Assert.Equal(0, fixture.Harness.Store.CountLedgerEntriesByDocId(plan.PrdDocId));
    }
    [Fact]
    public async Task PartialDelete_ThenDecreaseBelowFilledQty_RejectsWithoutChangingRemainingPlan()
    {
        var fixture = CreateCustomerFixture(9000, capacity: 1800);
        var pallets = fixture.Harness.CreateAtomicProductionPalletService();
        var plan = pallets.PlanOrder(fixture.OrderId);
        var original = fixture.Harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).OrderBy(x => x.Id).ToArray();
        pallets.CancelOrderPlan(fixture.OrderId, original.Skip(2).Select(x => x.Id).ToArray());
        Assert.True(pallets.Fill(original[0].HuCode, "TSD-test").Success);
        var docs = fixture.Harness.Store.GetDocsByOrder(fixture.OrderId).OrderBy(x => x.Id).ToArray();
        var before = JsonSerializer.Serialize(docs.Select(doc => new
        {
            doc, lines = fixture.Harness.Store.GetDocLines(doc.Id),
            ledger = fixture.Harness.Store.CountLedgerEntriesByDocId(doc.Id)
        }));
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);
        using var response = await UpdateOrderHttpApi.PutRawAsync(host.Client, fixture.OrderId, BuildRawJson(fixture, 1000));
        var error = await UpdateOrderHttpApi.ReadApiErrorResultAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("ORDER_LINE_QTY_BELOW_COVERAGE", error.Error);
        Assert.Equal(9000, Assert.Single(fixture.Harness.Store.GetOrderLines(fixture.OrderId)).QtyOrdered);
        var remaining = docs.SelectMany(doc => fixture.Harness.Store.GetProductionPalletsByDoc(doc.Id)).OrderBy(x => x.Id).ToArray();
        Assert.Equal(original.Take(2).Select(x => x.Id), remaining.Select(x => x.Id));
        Assert.Equal(ProductionPalletStatus.Filled, remaining[0].Status);
        Assert.Equal(ProductionPalletStatus.Planned, remaining[1].Status);
        Assert.Equal(before, JsonSerializer.Serialize(docs.Select(doc => new
        {
            doc, lines = fixture.Harness.Store.GetDocLines(doc.Id),
            ledger = fixture.Harness.Store.CountLedgerEntriesByDocId(doc.Id)
        })));
    }

    [Fact]
    public async Task DecreaseWithUnfilledProductionPlan_PersistsQtyAndCancelsObsoletePallets()
    {
        var fixture = CreateCustomerFixture(orderedQty: 1200);
        var palletService = fixture.Harness.CreateAtomicProductionPalletService();
        var plan = palletService.PlanOrder(fixture.OrderId);
        Assert.Equal(2, fixture.Harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).Count);
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        var payload = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            fixture.OrderId,
            BuildUpdateRequest(fixture, qtyOrdered: 600));

        Assert.True(payload.Ok);
        Assert.Equal(600, Assert.Single(fixture.Harness.Store.GetOrderLines(fixture.OrderId)).QtyOrdered, 3);
        var pallets = fixture.Harness.Store.GetProductionPalletsByDoc(plan.PrdDocId);
        var active = pallets
            .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Single(active);
        Assert.Equal(600, active.Sum(pallet => pallet.PlannedQty), 3);
        Assert.Single(pallets, pallet => string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase));

        using var linesResponse = await host.Client.GetAsync($"/api/orders/{fixture.OrderId}/lines");
        Assert.Equal(HttpStatusCode.OK, linesResponse.StatusCode);
        using var document = JsonDocument.Parse(await linesResponse.Content.ReadAsStringAsync());
        var line = document.RootElement.EnumerateArray().Single();
        Assert.Equal(600, line.GetProperty("qty_ordered").GetDouble(), 3);
    }

    [Fact]
    public async Task DecreaseBelowFilledCustomerPallet_ReturnsValidationWithBlockingHu()
    {
        var fixture = CreateCustomerFixture(orderedQty: 1200);
        var palletService = fixture.Harness.CreateAtomicProductionPalletService();
        var plan = palletService.PlanOrder(fixture.OrderId);
        var filledHu = fixture.Harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)
            .OrderBy(pallet => pallet.Id)
            .First()
            .HuCode;
        Assert.True(palletService.Fill(filledHu, "TSD-01").Success);
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        using var response = await UpdateOrderHttpApi.PutRawAsync(
            host.Client,
            fixture.OrderId,
            BuildRawJson(fixture, qtyOrdered: 500));

        var error = await UpdateOrderHttpApi.ReadApiErrorResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(error.Ok);
        Assert.Equal("ORDER_LINE_QTY_BELOW_COVERAGE", error.Error);
        Assert.Contains("защищено 600", error.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("новое количество 500", error.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1200, Assert.Single(fixture.Harness.Store.GetOrderLines(fixture.OrderId)).QtyOrdered, 3);
    }

    [Fact]
    public async Task PutCustomerQtyChange_LinesApiDoesNotSilentlyReturnOldQty()
    {
        var fixture = CreateCustomerFixture(orderedQty: 1134);
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        var payload = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            fixture.OrderId,
            BuildUpdateRequest(fixture, qtyOrdered: 756));

        Assert.True(payload.Ok);
        using var linesResponse = await host.Client.GetAsync($"/api/orders/{fixture.OrderId}/lines");
        Assert.Equal(HttpStatusCode.OK, linesResponse.StatusCode);
        using var document = JsonDocument.Parse(await linesResponse.Content.ReadAsStringAsync());
        var line = document.RootElement.EnumerateArray().Single();
        Assert.Equal(756, line.GetProperty("qty_ordered").GetDouble(), 3);
        Assert.Equal(756, Assert.Single(fixture.Harness.Store.GetOrderLines(fixture.OrderId)).QtyOrdered, 3);
    }

    [Fact]
    public async Task DecreaseCustomerQty_WithFourReservedHuTo2300_NormalizesToThreeHu()
    {
        var fixture = CreateCustomerFixture(orderedQty: 2400);
        SeedReservedWarehouseHuReservations(fixture, "HU-000009", "HU-000010", "HU-000011", "HU-000012");
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        var payload = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            fixture.OrderId,
            BuildUpdateRequest(fixture, qtyOrdered: 2300, ["HU-000009", "HU-000010", "HU-000011"]));

        Assert.True(payload.Ok);
        Assert.Equal(1800, Assert.Single(fixture.Harness.Store.GetOrderLines(fixture.OrderId)).QtyOrdered, 3);
        var planLines = fixture.Harness.Store.GetOrderReceiptPlanLines(fixture.OrderId);
        Assert.Equal(3, planLines.Count);
        Assert.Equal(1800, planLines.Sum(line => line.QtyPlanned), 3);
        Assert.DoesNotContain(planLines, line => string.Equals(line.ToHu, "HU-000012", StringComparison.OrdinalIgnoreCase));
        AssertNoActiveProductionPalletsForOrderLine(fixture);
    }

    [Fact]
    public async Task DecreaseCustomerQty_ToCurrentReservedHuTotal_PersistsQtyWithoutProductionPallets()
    {
        var fixture = CreateCustomerFixture(orderedQty: 2400);
        SeedReservedWarehouseHuReservations(fixture, "HU-000009", "HU-000010", "HU-000011");
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        var payload = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            fixture.OrderId,
            BuildUpdateRequest(fixture, qtyOrdered: 1800, ["HU-000009", "HU-000010", "HU-000011"]));

        Assert.True(payload.Ok);
        Assert.Equal(1800, Assert.Single(fixture.Harness.Store.GetOrderLines(fixture.OrderId)).QtyOrdered, 3);
        var planLines = fixture.Harness.Store.GetOrderReceiptPlanLines(fixture.OrderId);
        Assert.Equal(3, planLines.Count);
        Assert.Equal(1800, planLines.Sum(line => line.QtyPlanned), 3);
        Assert.Equal(new[] { "HU-000009", "HU-000010", "HU-000011" }, planLines.Select(line => line.ToHu).ToArray());
        AssertNoActiveProductionPalletsForOrderLine(fixture);

        using var linesResponse = await host.Client.GetAsync($"/api/orders/{fixture.OrderId}/lines");
        Assert.Equal(HttpStatusCode.OK, linesResponse.StatusCode);
        using var document = JsonDocument.Parse(await linesResponse.Content.ReadAsStringAsync());
        var line = document.RootElement.EnumerateArray().Single();
        Assert.Equal(1800, line.GetProperty("qty_ordered").GetDouble(), 3);
        Assert.Equal(0, line.GetProperty("pallet_planned_qty").GetDouble(), 3);
    }

    [Fact]
    public async Task DecreaseCustomerQty_WithSelectedReservedHu_AppliesQtyAndHuAtomically()
    {
        var fixture = CreateCustomerFixture(orderedQty: 2400);
        SeedReservedWarehouseHuReservations(fixture, "HU-000009", "HU-000010", "HU-000011", "HU-000012");
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        var payload = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            fixture.OrderId,
            BuildUpdateRequest(fixture, qtyOrdered: 2300, ["HU-000010", "HU-000011", "HU-000012"]));

        Assert.True(payload.Ok);
        Assert.Equal(1800, Assert.Single(fixture.Harness.Store.GetOrderLines(fixture.OrderId)).QtyOrdered, 3);
        var planLines = fixture.Harness.Store.GetOrderReceiptPlanLines(fixture.OrderId);
        Assert.Equal(3, planLines.Count);
        Assert.Equal(new[] { "HU-000010", "HU-000011", "HU-000012" }, planLines.Select(line => line.ToHu).ToArray());
        Assert.Equal(1800, planLines.Sum(line => line.QtyPlanned), 3);
        AssertNoActiveProductionPalletsForOrderLine(fixture);
    }

    [Fact]
    public async Task DecreaseCustomerQty_WhenQtyUpdateFails_RollsBackSelectedHuReservations()
    {
        var fixture = CreateCustomerFixture(orderedQty: 2400);
        SeedReservedWarehouseHuReservations(fixture, "HU-000009", "HU-000010", "HU-000011", "HU-000012");
        fixture.Harness.FailNextUpdateOrderLineQty();
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        using var response = await UpdateOrderHttpApi.PutRawAsync(
            host.Client,
            fixture.OrderId,
            BuildRawJson(fixture, qtyOrdered: 2300, ["HU-000010", "HU-000011", "HU-000012"]));

        var error = await UpdateOrderHttpApi.ReadApiErrorResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(error.Ok);
        Assert.Equal(2400, Assert.Single(fixture.Harness.Store.GetOrderLines(fixture.OrderId)).QtyOrdered, 3);
        var planLines = fixture.Harness.Store.GetOrderReceiptPlanLines(fixture.OrderId);
        Assert.Equal(4, planLines.Count);
        Assert.Equal(new[] { "HU-000009", "HU-000010", "HU-000011", "HU-000012" }, planLines.Select(line => line.ToHu).ToArray());
    }

    [Fact]
    public async Task DecreaseCustomerQty_WithFourReservedHuTo1200_KeepsTwoHu()
    {
        var fixture = CreateCustomerFixture(orderedQty: 2400);
        SeedReservedWarehouseHuReservations(fixture, "HU-000009", "HU-000010", "HU-000011", "HU-000012");
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        var payload = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            fixture.OrderId,
            BuildUpdateRequest(fixture, qtyOrdered: 1200, ["HU-000009", "HU-000010"]));

        Assert.True(payload.Ok);
        Assert.Equal(1200, Assert.Single(fixture.Harness.Store.GetOrderLines(fixture.OrderId)).QtyOrdered, 3);
        var planLines = fixture.Harness.Store.GetOrderReceiptPlanLines(fixture.OrderId);
        Assert.Equal(2, planLines.Count);
        Assert.Equal(1200, planLines.Sum(line => line.QtyPlanned), 3);
        Assert.Equal(new[] { "HU-000009", "HU-000010" }, planLines.Select(line => line.ToHu).ToArray());
        AssertNoActiveProductionPalletsForOrderLine(fixture);
    }

    [Fact]
    public async Task IncreaseCustomerQty_WithExactFreeWarehouseHu_DoesNotAutoBindHuOnSave()
    {
        var fixture = CreateCustomerFixture(orderedQty: 1800);
        SeedReservedWarehouseHuReservations(fixture, "HU-000009", "HU-000010", "HU-000011");
        fixture.Harness.SeedBalance(fixture.ItemId, 1, 600, "HU-000012");
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        var payload = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            fixture.OrderId,
            BuildUpdateRequest(fixture, qtyOrdered: 2400));

        Assert.True(payload.Ok);
        Assert.Equal(2400, Assert.Single(fixture.Harness.Store.GetOrderLines(fixture.OrderId)).QtyOrdered, 3);
        var planLines = fixture.Harness.Store.GetOrderReceiptPlanLines(fixture.OrderId);
        Assert.Equal(3, planLines.Count);
        Assert.Equal(1800, planLines.Sum(line => line.QtyPlanned), 3);
        Assert.DoesNotContain(planLines, line => string.Equals(line.ToHu, "HU-000012", StringComparison.OrdinalIgnoreCase));
        AssertNoActiveProductionPalletsForOrderLine(fixture);
    }

    [Fact]
    public async Task IncreaseCustomerQty_WithPartialFreeWarehouseHu_DoesNotAutoBindHuOnSave()
    {
        var fixture = CreateCustomerFixture(orderedQty: 1800);
        SeedReservedWarehouseHuReservations(fixture, "HU-000009", "HU-000010", "HU-000011");
        fixture.Harness.SeedBalance(fixture.ItemId, 1, 600, "HU-000012");
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        var payload = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            fixture.OrderId,
            BuildUpdateRequest(fixture, qtyOrdered: 3000));

        Assert.True(payload.Ok);
        Assert.Equal(3000, Assert.Single(fixture.Harness.Store.GetOrderLines(fixture.OrderId)).QtyOrdered, 3);
        var planLines = fixture.Harness.Store.GetOrderReceiptPlanLines(fixture.OrderId);
        Assert.Equal(3, planLines.Count);
        Assert.Equal(1800, planLines.Sum(line => line.QtyPlanned), 3);
        Assert.DoesNotContain(planLines, line => string.Equals(line.ToHu, "HU-000012", StringComparison.OrdinalIgnoreCase));
        AssertNoActiveProductionPalletsForOrderLine(fixture);
    }

    [Fact]
    public async Task IncreaseCustomerQty_WhenFreeWarehouseHuExceedsShortage_DoesNotAutoPlanResidual()
    {
        var fixture = CreateCustomerFixture(orderedQty: 1800);
        SeedReservedWarehouseHuReservations(fixture, "HU-000009", "HU-000010", "HU-000011");
        fixture.Harness.SeedBalance(fixture.ItemId, 1, 600, "HU-000012");
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        var payload = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            fixture.OrderId,
            BuildUpdateRequest(fixture, qtyOrdered: 2200));

        Assert.True(payload.Ok);
        Assert.Equal(2200, Assert.Single(fixture.Harness.Store.GetOrderLines(fixture.OrderId)).QtyOrdered, 3);
        var planLines = fixture.Harness.Store.GetOrderReceiptPlanLines(fixture.OrderId);
        Assert.Equal(3, planLines.Count);
        Assert.DoesNotContain(planLines, line => string.Equals(line.ToHu, "HU-000012", StringComparison.OrdinalIgnoreCase));
        AssertNoActiveProductionPalletsForOrderLine(fixture);
    }

    [Fact]
    public async Task CustomerQtyZero_IsRejectedWithDeleteLineMessage()
    {
        var fixture = CreateCustomerFixture(orderedQty: 2400);
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        using var response = await UpdateOrderHttpApi.PutRawAsync(
            host.Client,
            fixture.OrderId,
            BuildRawJson(fixture, qtyOrdered: 0));

        var error = await UpdateOrderHttpApi.ReadApiErrorResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(error.Ok);
        Assert.Contains("Количество строки не может быть 0. Удалите строку заказа.", error.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(2400, Assert.Single(fixture.Harness.Store.GetOrderLines(fixture.OrderId)).QtyOrdered, 3);
    }

    [Fact]
    public async Task ReservedHuUnbinding_DoesNotCancelWarehouseHuProductionPallets()
    {
        var fixture = CreateCustomerFixture(orderedQty: 2400);
        SeedReservedWarehouseHuReservations(fixture, "HU-000009", "HU-000010", "HU-000011", "HU-000012");
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        var payload = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            fixture.OrderId,
            BuildUpdateRequest(fixture, qtyOrdered: 1200, ["HU-000009", "HU-000010"]));

        Assert.True(payload.Ok);
        var sourcePallets = fixture.Harness.Store.GetProductionPalletsByDoc(9000);
        Assert.Equal(4, sourcePallets.Count);
        Assert.All(sourcePallets, pallet => Assert.Equal(ProductionPalletStatus.Filled, pallet.Status));
        Assert.Equal(600, fixture.Harness.Store.GetLedgerBalance(fixture.ItemId, 1, "HU-000011"), 3);
        Assert.Equal(600, fixture.Harness.Store.GetLedgerBalance(fixture.ItemId, 1, "HU-000012"), 3);
    }

    [Fact]
    public async Task CustomerQtyReductionBlockedMessage_DoesNotListReservedHu()
    {
        var fixture = CreateCustomerFixture(orderedQty: 2400);
        SeedReservedWarehouseHuReservations(fixture, "HU-RESERVED-001");
        fixture.Harness.SeedDoc(new Doc
        {
            Id = 9100,
            DocRef = "PRD-CUSTOMER-FILLED",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = fixture.OrderId,
            CreatedAt = new DateTime(2026, 5, 25, 11, 0, 0, DateTimeKind.Utc),
            ClosedAt = new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc)
        });
        fixture.Harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 9101,
            PrdDocId = 9100,
            DocLineId = 91001,
            OrderId = fixture.OrderId,
            OrderLineId = fixture.OrderLineId,
            ItemId = fixture.ItemId,
            HuCode = "HU-FILLED-CUSTOMER",
            PlannedQty = 600,
            ToLocationId = 1,
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc),
            CreatedAt = new DateTime(2026, 5, 25, 11, 0, 0, DateTimeKind.Utc)
        });
        fixture.Harness.SeedLedgerEntry(9100, fixture.ItemId, 1, 600, "HU-FILLED-CUSTOMER");
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        using var response = await UpdateOrderHttpApi.PutRawAsync(
            host.Client,
            fixture.OrderId,
            BuildRawJson(fixture, qtyOrdered: 500));

        var error = await UpdateOrderHttpApi.ReadApiErrorResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(error.Ok);
        Assert.Contains("защищено 1200", error.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("новое количество 500", error.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HU-RESERVED-001", error.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static UpdateOrderHttpApi.UpdateOrderRequest BuildUpdateRequest(
        CustomerFixture fixture,
        double qtyOrdered,
        IReadOnlyList<string>? selectedHuCodes = null)
    {
        return new UpdateOrderHttpApi.UpdateOrderRequest
        {
            OrderRef = fixture.OrderRef,
            Type = "CUSTOMER",
            PartnerId = fixture.PartnerId,
            Status = "IN_PROGRESS",
            Lines =
            [
                new UpdateOrderHttpApi.UpdateOrderLineRequest
                {
                    OrderLineId = fixture.OrderLineId,
                    ItemId = fixture.ItemId,
                    QtyOrdered = qtyOrdered,
                    SelectedHuCodes = selectedHuCodes
                }
            ]
        };
    }

    private static void SeedReservedWarehouseHuReservations(CustomerFixture fixture, params string[] huCodes)
    {
        fixture.Harness.SeedDoc(new Doc
        {
            Id = 9000,
            DocRef = "PRD-WAREHOUSE-HU",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = 900,
            CreatedAt = new DateTime(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc),
            ClosedAt = new DateTime(2026, 5, 24, 11, 0, 0, DateTimeKind.Utc)
        });

        var planLines = huCodes
            .Select((huCode, index) =>
            {
                fixture.Harness.SeedBalance(fixture.ItemId, 1, 600, huCode);
                fixture.Harness.SeedProductionPallet(new ProductionPallet
                {
                    Id = 9001 + index,
                    PrdDocId = 9000,
                    DocLineId = 90001 + index,
                    OrderId = 900,
                    ItemId = fixture.ItemId,
                    HuCode = huCode,
                    PlannedQty = 600,
                    ToLocationId = 1,
                    Status = ProductionPalletStatus.Filled,
                    FilledAt = new DateTime(2026, 5, 24, 11, 0, 0, DateTimeKind.Utc),
                    CreatedAt = new DateTime(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc)
                });

                return new OrderReceiptPlanLine
                {
                    Id = 8001 + index,
                    OrderId = fixture.OrderId,
                    OrderLineId = fixture.OrderLineId,
                    ItemId = fixture.ItemId,
                    ItemName = "Аджика",
                    QtyPlanned = 600,
                    ToLocationId = 1,
                    ToHu = huCode,
                    SortOrder = index
                };
            })
            .ToArray();

        fixture.Harness.SeedOrderReceiptPlanLines(fixture.OrderId, planLines);
    }

    private static void AssertNoActiveProductionPalletsForOrderLine(CustomerFixture fixture)
    {
        var activePallets = fixture.Harness.Store.GetDocsByOrder(fixture.OrderId)
            .Where(doc => doc.Type == DocType.ProductionReceipt)
            .SelectMany(doc => fixture.Harness.Store.GetProductionPalletsByDoc(doc.Id))
            .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            .Where(pallet => pallet.OrderLineId == fixture.OrderLineId
                             || pallet.Lines.Any(line => line.OrderLineId == fixture.OrderLineId))
            .ToArray();
        Assert.Empty(activePallets);
    }

    private static void AssertActiveProductionPalletsForOrderLine(
        CustomerFixture fixture,
        int expectedCount,
        double expectedQty)
    {
        var activePallets = fixture.Harness.Store.GetDocsByOrder(fixture.OrderId)
            .Where(doc => doc.Type == DocType.ProductionReceipt)
            .SelectMany(doc => fixture.Harness.Store.GetProductionPalletsByDoc(doc.Id))
            .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            .Where(pallet => pallet.OrderLineId == fixture.OrderLineId
                             || pallet.Lines.Any(line => line.OrderLineId == fixture.OrderLineId))
            .ToArray();
        Assert.Equal(expectedCount, activePallets.Length);
        Assert.Equal(expectedQty, activePallets.Sum(pallet => ResolvePalletQtyForOrderLine(pallet, fixture.OrderLineId)), 3);
        Assert.All(activePallets, pallet => Assert.Equal(ProductionPalletStatus.Planned, pallet.Status));
    }

    private static double ResolvePalletQtyForOrderLine(ProductionPallet pallet, long orderLineId)
    {
        var componentQty = pallet.Lines
            .Where(line => line.OrderLineId == orderLineId)
            .Sum(line => Math.Max(0, line.PlannedQty));
        if (componentQty > 0)
        {
            return componentQty;
        }

        return pallet.OrderLineId == orderLineId
            ? Math.Max(0, pallet.PlannedQty)
            : 0;
    }

    private static string BuildRawJson(CustomerFixture fixture, double qtyOrdered, IReadOnlyList<string>? selectedHuCodes = null)
    {
        var selectedHuJson = selectedHuCodes == null
            ? string.Empty
            : $"""
                       ,
                       "selected_hu_codes": [{string.Join(", ", selectedHuCodes.Select(code => $"\"{code}\""))}]
              """;
        return $$"""
                 {
                   "order_ref": "{{fixture.OrderRef}}",
                   "type": "CUSTOMER",
                   "partner_id": {{fixture.PartnerId}},
                   "status": "IN_PROGRESS",
                   "lines": [
                     {
                       "order_line_id": {{fixture.OrderLineId}},
                       "item_id": {{fixture.ItemId}},
                       "qty_ordered": {{qtyOrdered.ToString(System.Globalization.CultureInfo.InvariantCulture)}}{{selectedHuJson}}
                     }
                   ]
                 }
                 """;
    }

    private static CustomerFixture CreateCustomerFixture(double orderedQty, double capacity = 600)
    {
        var harness = new CloseDocumentHarness();
        const long orderId = 55;
        const long orderLineId = 143;
        const long itemId = 1001;
        const long partnerId = 200;
        harness.SeedPartner(new Partner
        {
            Id = partnerId,
            Code = "CUST-200",
            Name = "Покупатель",
            CreatedAt = new DateTime(2026, 5, 25, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = itemId,
            Name = "Аджика",
            BaseUom = "шт",
            MaxQtyPerHu = capacity
        });
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = "055",
            Type = OrderType.Customer,
            PartnerId = partnerId,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 25, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = orderLineId,
            OrderId = orderId,
            ItemId = itemId,
            QtyOrdered = orderedQty,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        return new CustomerFixture(harness, new InMemoryApiDocStore(), orderId, orderLineId, itemId, partnerId, "055");
    }

    private sealed record CustomerFixture(
        CloseDocumentHarness Harness,
        InMemoryApiDocStore ApiStore,
        long OrderId,
        long OrderLineId,
        long ItemId,
        long PartnerId,
        string OrderRef);
}
