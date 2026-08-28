using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services;
using FlowStock.Data;
using FlowStock.Server;
using FlowStock.Server.Tests.Support;
using FlowStock.Server.Tests.UpdateOrder.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace FlowStock.Server.Tests.Orders;

[Collection(PostgresLocationIntegrationTestCollection.Name)]
public sealed class OrderDeletePostgresRegressionTests
{
    [Fact]
    public void DeleteOrderLine_CleansPalletPlanAndReservationChildrenBeforeOrderLineDelete()
    {
        var source = File.ReadAllText(GetPostgresDataStorePath());
        var methodBody = SliceMethod(
            source,
            "private void DeleteOrderLinesCore(NpgsqlConnection connection, long orderId, IReadOnlyCollection<long> orderLineIds)",
            "private void EnsureOrderLinesCanBeDeleted");

        Assert.Contains("EnsureOrderLinesCanBeDeleted(connection, orderId, ids);", methodBody, StringComparison.Ordinal);
        Assert.Contains("ClearRemovableProductionPalletPlanForOrderLines(connection, orderId, ids);", methodBody, StringComparison.Ordinal);
        AssertDeleteBefore(
            methodBody,
            "DELETE FROM order_receipt_plan_lines WHERE order_line_id = ANY(@order_line_ids)",
            "DELETE FROM order_lines WHERE id = ANY(@order_line_ids)");
    }

    [Fact]
    public void DeleteOrderLine_RemovesPlannedPalletsInsteadOfDetachingActiveOrderLineReferences()
    {
        var source = File.ReadAllText(GetPostgresDataStorePath());
        var methodBody = SliceMethod(
            source,
            "private void ClearRemovableProductionPalletPlanForOrderLines",
            "private long[] GetOrderLineIds");

        Assert.Contains("DELETE FROM production_pallet_lines", methodBody, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM production_pallets", methodBody, StringComparison.Ordinal);
        Assert.Contains("pp.status = @planned_status", methodBody, StringComparison.Ordinal);
        Assert.Contains("pp.status = @cancelled_status", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("pp.status IN (@planned_status, @cancelled_status)", methodBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PutUpdate_RemovingCustomerLineWithPlannedPallets_DeletesLineWithoutFkFailure()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, async scopedStore =>
        {
            EnsureAtLeastOneLocation(scopedStore);
            var fixture = SeedCustomerOrderWithTwoLines(scopedStore);
            var palletService = new ProductionPalletService(scopedStore);
            var plan = palletService.PlanOrder(fixture.OrderId);
            Assert.Contains(
                scopedStore.GetProductionPalletsByDoc(plan.PrdDocId),
                pallet => pallet.OrderLineId == fixture.DeletedOrderLineId
                          || pallet.Lines.Any(line => line.OrderLineId == fixture.DeletedOrderLineId));

            await using var host = await PostgresOrderUpdateHost.StartAsync(scopedStore);
            var payload = await UpdateOrderHttpApi.UpdateAsync(
                host.Client,
                fixture.OrderId,
                BuildDeleteFirstLineRequest(fixture));

            Assert.True(payload.Ok);
            Assert.Equal(1, payload.LineCount);

            var remainingLines = scopedStore.GetOrderLines(fixture.OrderId);
            Assert.Single(remainingLines);
            Assert.DoesNotContain(remainingLines, line => line.Id == fixture.DeletedOrderLineId);
            Assert.Contains(remainingLines, line => line.Id == fixture.RemainingOrderLineId);

            var palletsAfter = scopedStore.GetProductionPalletsByDoc(plan.PrdDocId);
            Assert.DoesNotContain(
                palletsAfter,
                pallet => pallet.OrderLineId == fixture.DeletedOrderLineId
                          || pallet.Lines.Any(line => line.OrderLineId == fixture.DeletedOrderLineId));
            Assert.Contains(
                palletsAfter,
                pallet => pallet.OrderLineId == fixture.RemainingOrderLineId
                          || pallet.Lines.Any(line => line.OrderLineId == fixture.RemainingOrderLineId));
            AssertNoActiveOrphanPallets(palletsAfter);
        });
    }

    [Fact]
    public async Task PutUpdate_RemovingCustomerLineWithCancelledPalletsOrderLineReference_DeletesLineWithoutFkFailure()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, async scopedStore =>
        {
            EnsureAtLeastOneLocation(scopedStore);
            var fixture = SeedCustomerOrderWithTwoLines(scopedStore);
            var palletService = new ProductionPalletService(scopedStore);
            var plan = palletService.PlanOrder(fixture.OrderId);
            var palletIds = scopedStore.GetProductionPalletsByDoc(plan.PrdDocId).Select(pallet => pallet.Id).ToArray();
            Assert.True(palletIds.Length > 0);
            scopedStore.CancelProductionPallets(palletIds);

            Assert.Contains(
                scopedStore.GetProductionPalletsByDoc(plan.PrdDocId),
                pallet => string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase)
                          && pallet.OrderLineId == fixture.DeletedOrderLineId);

            await using var host = await PostgresOrderUpdateHost.StartAsync(scopedStore);
            var payload = await UpdateOrderHttpApi.UpdateAsync(
                host.Client,
                fixture.OrderId,
                BuildDeleteFirstLineRequest(fixture));

            Assert.True(payload.Ok);
            Assert.DoesNotContain(scopedStore.GetOrderLines(fixture.OrderId), line => line.Id == fixture.DeletedOrderLineId);
        });
    }

    [Fact]
    public async Task PutUpdate_RemovingCustomerLineWithCancelledPalletComponentReference_DeletesLineWithoutFkFailure()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, async scopedStore =>
        {
            EnsureAtLeastOneLocation(scopedStore);
            var fixture = SeedCustomerOrderWithTwoLines(scopedStore);
            var palletService = new ProductionPalletService(scopedStore);
            var plan = palletService.PlanOrder(fixture.OrderId);
            var palletIds = scopedStore.GetProductionPalletsByDoc(plan.PrdDocId).Select(pallet => pallet.Id).ToArray();
            Assert.True(palletIds.Length > 0);
            scopedStore.CancelProductionPallets(palletIds);

            Assert.Contains(
                scopedStore.GetProductionPalletsByDoc(plan.PrdDocId),
                pallet => string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase)
                          && pallet.Lines.Any(line => line.OrderLineId == fixture.DeletedOrderLineId));

            await using var host = await PostgresOrderUpdateHost.StartAsync(scopedStore);
            var payload = await UpdateOrderHttpApi.UpdateAsync(
                host.Client,
                fixture.OrderId,
                BuildDeleteFirstLineRequest(fixture));

            Assert.True(payload.Ok);
            var palletsAfter = scopedStore.GetProductionPalletsByDoc(plan.PrdDocId);
            Assert.DoesNotContain(
                palletsAfter,
                pallet => pallet.OrderLineId == fixture.DeletedOrderLineId
                          || pallet.Lines.Any(line => line.OrderLineId == fixture.DeletedOrderLineId));
        });
    }

    [Fact]
    public async Task PutUpdate_RemovingCustomerLineWithCancelledPalletDocLineReference_DetachesFkTailsWithoutDeletingDocLine()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, async scopedStore =>
        {
            EnsureAtLeastOneLocation(scopedStore);
            var fixture = SeedCustomerOrderWithTwoLines(scopedStore);
            var plan = SeedDraftProductionReceiptPalletPlanForDeletedLine(
                scopedStore,
                fixture,
                ProductionPalletStatus.Cancelled);

            await using var host = await PostgresOrderUpdateHost.StartAsync(scopedStore);
            var payload = await UpdateOrderHttpApi.UpdateAsync(
                host.Client,
                fixture.OrderId,
                BuildDeleteFirstLineRequest(fixture));

            Assert.True(payload.Ok);
            Assert.DoesNotContain(scopedStore.GetOrderLines(fixture.OrderId), line => line.Id == fixture.DeletedOrderLineId);
            AssertDetachedPalletPlan(scopedStore, plan);
        });
    }

    [Fact]
    public async Task PutUpdate_RemovingCustomerLineWithPlannedPalletDocLineReference_RemovesActivePlannedPalletPlan()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, async scopedStore =>
        {
            EnsureAtLeastOneLocation(scopedStore);
            var fixture = SeedCustomerOrderWithTwoLines(scopedStore);
            var plan = SeedDraftProductionReceiptPalletPlanForDeletedLine(
                scopedStore,
                fixture,
                ProductionPalletStatus.Planned);

            await using var host = await PostgresOrderUpdateHost.StartAsync(scopedStore);
            var payload = await UpdateOrderHttpApi.UpdateAsync(
                host.Client,
                fixture.OrderId,
                BuildDeleteFirstLineRequest(fixture));

            Assert.True(payload.Ok);
            Assert.DoesNotContain(scopedStore.GetOrderLines(fixture.OrderId), line => line.Id == fixture.DeletedOrderLineId);
            AssertActivePalletPlanRemoved(scopedStore, plan);
            AssertNoActiveOrphanPallets(scopedStore.GetProductionPalletsByDoc(plan.PrdDocId));
        });
    }

    [Fact]
    public async Task PutUpdate_RemovingCustomerLineWithFilledPallets_ReturnsBusinessBadRequest()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, async scopedStore =>
        {
            EnsureAtLeastOneLocation(scopedStore);
            var fixture = SeedCustomerOrderWithTwoLines(scopedStore);
            var palletService = new ProductionPalletService(scopedStore);
            var plan = palletService.PlanOrder(fixture.OrderId);
            var palletToFill = scopedStore.GetProductionPalletsByDoc(plan.PrdDocId)
                .First(pallet => pallet.OrderLineId == fixture.DeletedOrderLineId
                                 || pallet.Lines.Any(line => line.OrderLineId == fixture.DeletedOrderLineId));

            scopedStore.MarkProductionPalletFilled(
                palletToFill.Id,
                new DateTime(2026, 5, 27, 12, 0, 0, DateTimeKind.Utc),
                "TEST-DEVICE");

            await using var host = await PostgresOrderUpdateHost.StartAsync(scopedStore);
            using var response = await UpdateOrderHttpApi.PutRawAsync(
                host.Client,
                fixture.OrderId,
                BuildDeleteFirstLineRawJson(fixture));

            var payload = await UpdateOrderHttpApi.ReadApiErrorResultAsync(response, HttpStatusCode.BadRequest);
            Assert.False(payload.Ok);
            Assert.Equal("ORDER_LINE_HAS_FILLED_PALLETS", payload.Error);
            Assert.Contains("нельзя удалить строку", payload.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            var remainingLines = scopedStore.GetOrderLines(fixture.OrderId);
            Assert.Equal(2, remainingLines.Count);
            Assert.Contains(remainingLines, line => line.Id == fixture.DeletedOrderLineId);
            var palletAfter = Assert.Single(
                scopedStore.GetProductionPalletsByDoc(plan.PrdDocId),
                pallet => pallet.Id == palletToFill.Id);
            Assert.Equal(fixture.DeletedOrderLineId, palletAfter.OrderLineId);
            Assert.Equal(ProductionPalletStatus.Filled, palletAfter.Status);
        });
    }

    [Fact]
    public async Task PutUpdate_RemovingCustomerLineWithPrintedPallets_RemovesActiveFuturePlan()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, async scopedStore =>
        {
            EnsureAtLeastOneLocation(scopedStore);
            var fixture = SeedCustomerOrderWithTwoLines(scopedStore);
            var plan = SeedDraftProductionReceiptPalletPlanForDeletedLine(
                scopedStore,
                fixture,
                ProductionPalletStatus.Printed);

            await using var host = await PostgresOrderUpdateHost.StartAsync(scopedStore);
            var payload = await UpdateOrderHttpApi.UpdateAsync(
                host.Client,
                fixture.OrderId,
                BuildDeleteFirstLineRequest(fixture));

            Assert.True(payload.Ok);
            Assert.DoesNotContain(scopedStore.GetOrderLines(fixture.OrderId), line => line.Id == fixture.DeletedOrderLineId);
            var palletAfter = Assert.Single(scopedStore.GetProductionPalletsByDoc(plan.PrdDocId));
            Assert.Equal(plan.PalletId, palletAfter.Id);
            Assert.Equal(ProductionPalletStatus.Cancelled, palletAfter.Status);
            AssertActivePalletPlanRemoved(scopedStore, plan);
        });
    }

    [Fact]
    public async Task PutUpdate_RemovingCustomerLineWithPrintedPartiallyFilledMixedPallet_ReturnsBusinessBadRequest()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, async scopedStore =>
        {
            EnsureAtLeastOneLocation(scopedStore);
            var fixture = SeedCustomerOrderWithTwoLines(scopedStore, deletedQty: 300, remainingQty: 300, group: "MIX-PARTIAL");
            var plan = new ProductionPalletService(scopedStore).PlanOrder(fixture.OrderId);
            var pallet = Assert.Single(scopedStore.GetProductionPalletsByDoc(plan.PrdDocId));
            Assert.Equal(2, pallet.Lines.Count);
            Assert.Equal(1, scopedStore.MarkProductionPalletsPrinted(fixture.OrderId, [pallet.Id], DateTime.UtcNow));

            var deletedLineComponent = Assert.Single(pallet.Lines, line => line.OrderLineId == fixture.DeletedOrderLineId);
            Assert.Equal(1, scopedStore.MarkProductionPalletComponentsFilled(
                pallet.Id,
                [deletedLineComponent.Id],
                new DateTime(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc)));

            var partiallyFilled = Assert.Single(scopedStore.GetProductionPalletsByDoc(plan.PrdDocId));
            Assert.Equal(ProductionPalletStatus.Printed, partiallyFilled.Status);
            Assert.True(partiallyFilled.HasComponentProgress);
            Assert.Equal(0, scopedStore.CountLedgerEntriesByDocId(plan.PrdDocId));

            await using var host = await PostgresOrderUpdateHost.StartAsync(scopedStore);
            using var response = await UpdateOrderHttpApi.PutRawAsync(
                host.Client,
                fixture.OrderId,
                BuildDeleteFirstLineRawJson(fixture));

            var payload = await UpdateOrderHttpApi.ReadApiErrorResultAsync(response, HttpStatusCode.BadRequest);
            Assert.False(payload.Ok);
            Assert.Equal("ORDER_LINE_PALLET_PLAN_NOT_PLANNED", payload.Error);
            Assert.Contains("частично наполненная микс-паллета", payload.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(scopedStore.GetOrderLines(fixture.OrderId), line => line.Id == fixture.DeletedOrderLineId);

            var palletAfter = Assert.Single(scopedStore.GetProductionPalletsByDoc(plan.PrdDocId));
            Assert.Equal(pallet.Id, palletAfter.Id);
            Assert.Equal(ProductionPalletStatus.Printed, palletAfter.Status);
            Assert.True(palletAfter.HasComponentProgress);
        });
    }

    [Fact]
    public async Task ScopedHuFate_SupersededOutboundLineAwaitsShipmentAndActiveReplacementShips()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, scopedStore =>
        {
            EnsureAtLeastOneLocation(scopedStore);
            var locationId = scopedStore.GetLocations().First().Id;
            var fixture = SeedCustomerOrderWithTwoLines(scopedStore);
            var plan = new ProductionPalletService(scopedStore).PlanOrder(fixture.OrderId);
            var pallet = scopedStore.GetProductionPalletsByDoc(plan.PrdDocId)
                .Single(candidate =>
                    candidate.OrderLineId == fixture.DeletedOrderLineId
                    || candidate.Lines.Any(line => line.OrderLineId == fixture.DeletedOrderLineId));
            scopedStore.MarkProductionPalletFilled(
                pallet.Id,
                new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc),
                "TEST");
            scopedStore.AddLedgerEntry(new LedgerEntry
            {
                Timestamp = new DateTime(2026, 7, 30, 9, 1, 0, DateTimeKind.Utc),
                DocId = pallet.PrdDocId,
                ItemId = fixture.DeletedItemId,
                LocationId = locationId,
                QtyDelta = pallet.PlannedQty,
                HuCode = pallet.HuCode
            });

            var outboundRef = $"OUT-SUP-{DateTime.UtcNow.Ticks.ToString()[^6..]}";
            var outboundId = scopedStore.AddDoc(new Doc
            {
                DocRef = outboundRef,
                Type = DocType.Outbound,
                Status = DocStatus.Closed,
                CreatedAt = new DateTime(2026, 7, 30, 10, 0, 0, DateTimeKind.Utc),
                ClosedAt = new DateTime(2026, 7, 30, 10, 5, 0, DateTimeKind.Utc),
                OrderId = fixture.OrderId,
                OrderRef = fixture.OrderRef
            });
            var predecessorId = scopedStore.AddDocLine(new DocLine
            {
                DocId = outboundId,
                OrderLineId = fixture.DeletedOrderLineId,
                ItemId = fixture.DeletedItemId,
                Qty = 41,
                FromLocationId = locationId,
                FromHu = pallet.HuCode
            });
            var tombstoneId = scopedStore.AddDocLine(new DocLine
            {
                DocId = outboundId,
                ReplacesLineId = predecessorId,
                OrderLineId = fixture.DeletedOrderLineId,
                ItemId = fixture.DeletedItemId,
                Qty = 0,
                FromLocationId = locationId,
                FromHu = pallet.HuCode
            });

            OrderLineProductionHuRow ReadProductionHu()
            {
                var sourceOrder = scopedStore.GetOrders().Single(order => order.Id == fixture.OrderId);
                var sourceLine = new OrderService(scopedStore)
                    .GetOrderLineViews(fixture.OrderId)
                    .Single(line => line.Id == fixture.DeletedOrderLineId);
                var details = OrderLineHuDetailsBuilder.BuildByOrder(
                    scopedStore,
                    sourceOrder,
                    [sourceLine])[fixture.DeletedOrderLineId];
                return Assert.Single(
                    details.ProductionHuRows,
                    row => string.Equals(row.HuCode, pallet.HuCode, StringComparison.OrdinalIgnoreCase));
            }

            var supersededPhase = ReadProductionHu();
            Assert.Equal(OrderLineHuFateDisplayBuilder.AwaitingShipmentFateCode, supersededPhase.FateCode);
            Assert.Equal(OrderLineHuFateDisplayBuilder.AwaitingShipmentFateLabel, supersededPhase.FateLabel);
            Assert.Equal(pallet.PlannedQty, supersededPhase.FateQty);
            Assert.Null(supersededPhase.FateDocRef);

            const double replacementQty = 17;
            var replacementId = scopedStore.AddDocLine(new DocLine
            {
                DocId = outboundId,
                ReplacesLineId = tombstoneId,
                OrderLineId = fixture.DeletedOrderLineId,
                ItemId = fixture.DeletedItemId,
                Qty = replacementQty,
                FromLocationId = locationId,
                FromHu = pallet.HuCode
            });

            var replacementPhase = ReadProductionHu();
            Assert.Equal(OrderLineHuFateDisplayBuilder.ShippedFateCode, replacementPhase.FateCode);
            Assert.Equal("отгружено", replacementPhase.FateLabel);
            Assert.Equal(replacementQty, replacementPhase.FateQty);
            Assert.Equal(fixture.OrderRef, replacementPhase.FateOrderRef);
            Assert.Equal(outboundRef, replacementPhase.FateDocRef);

            var activeShipment = Assert.Single(
                ((IOptimizedOrderLineHuFateStore)scopedStore)
                .GetScopedOrderLineHuFateCandidates(
                    [new ScopedOrderLineHuFateKey(fixture.DeletedItemId, pallet.HuCode)])
                .Where(candidate =>
                    string.Equals(
                        candidate.Kind,
                        ScopedOrderLineHuFateDisplayBuilder.ShipmentCandidateKind,
                        StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(replacementQty, activeShipment.Qty);
            Assert.Equal(fixture.DeletedOrderLineId, activeShipment.TargetOrderLineId);
            Assert.Equal(outboundId, activeShipment.DocId);
            Assert.Equal(outboundRef, activeShipment.DocRef);
            Assert.NotEqual(predecessorId, replacementId);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ReleaseProducedStock_WithTwoFilledSingleLinePallets_ReleasesHuAndDeletesLine()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, async scopedStore =>
        {
            EnsureAtLeastOneLocation(scopedStore);
            var locationId = scopedStore.GetLocations().First().Id;
            var fixture = SeedCustomerOrderWithTwoLines(scopedStore, deletedQty: 1200);
            var plan = new ProductionPalletService(scopedStore).PlanOrder(fixture.OrderId);
            var targetPallets = scopedStore.GetProductionPalletsByDoc(plan.PrdDocId)
                .Where(pallet => pallet.OrderLineId == fixture.DeletedOrderLineId
                                 || pallet.Lines.Any(line => line.OrderLineId == fixture.DeletedOrderLineId))
                .OrderBy(pallet => pallet.Id)
                .ToArray();
            Assert.Equal(2, targetPallets.Length);

            foreach (var pallet in targetPallets)
            {
                scopedStore.MarkProductionPalletFilled(pallet.Id, new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc), "TEST");
                scopedStore.AddLedgerEntry(new LedgerEntry
                {
                    Timestamp = new DateTime(2026, 6, 4, 9, 1, 0, DateTimeKind.Utc),
                    DocId = pallet.PrdDocId,
                    ItemId = fixture.DeletedItemId,
                    LocationId = locationId,
                    QtyDelta = pallet.PlannedQty,
                    HuCode = pallet.HuCode
                });
            }

            scopedStore.ReplaceOrderReceiptPlanLines(fixture.OrderId, targetPallets.Select((pallet, index) => new OrderReceiptPlanLine
            {
                OrderId = fixture.OrderId,
                OrderLineId = fixture.DeletedOrderLineId,
                ItemId = fixture.DeletedItemId,
                QtyPlanned = pallet.PlannedQty,
                ToLocationId = locationId,
                ToHu = pallet.HuCode,
                SortOrder = index
            }).ToArray());

            var ledgerCountBefore = scopedStore.CountLedgerEntries();
            var balancesBefore = targetPallets.ToDictionary(
                pallet => pallet.HuCode,
                pallet => scopedStore.GetLedgerBalance(fixture.DeletedItemId, locationId, pallet.HuCode),
                StringComparer.OrdinalIgnoreCase);

            await using var host = await PostgresOrderUpdateHost.StartAsync(scopedStore);
            using var response = await host.Client.PostAsync(
                $"/api/orders/{fixture.OrderId}/lines/{fixture.DeletedOrderLineId}/release-produced-stock",
                content: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<OrderProducedStockReleaseEnvelope>();
            Assert.NotNull(payload);
            Assert.True(payload!.Ok);
            Assert.Equal(2, payload.ReleasedPalletCount);
            Assert.Equal(1200, payload.ReleasedQty, 3);

            Assert.DoesNotContain(scopedStore.GetOrderLines(fixture.OrderId), line => line.Id == fixture.DeletedOrderLineId);
            Assert.DoesNotContain(scopedStore.GetOrderReceiptPlanLines(fixture.OrderId), line => line.OrderLineId == fixture.DeletedOrderLineId);
            var targetIds = targetPallets.Select(pallet => pallet.Id).ToHashSet();
            var palletsAfter = scopedStore.GetProductionPalletsByDoc(plan.PrdDocId)
                .Where(pallet => targetIds.Contains(pallet.Id))
                .ToArray();
            Assert.Equal(2, palletsAfter.Length);
            foreach (var pallet in palletsAfter)
            {
                Assert.Equal(ProductionPalletStatus.Filled, pallet.Status);
                Assert.Null(pallet.OrderId);
                Assert.Null(pallet.OrderLineId);
                Assert.All(pallet.Lines, line => Assert.Null(line.OrderLineId));
                Assert.Equal(balancesBefore[pallet.HuCode], scopedStore.GetLedgerBalance(fixture.DeletedItemId, locationId, pallet.HuCode), 3);
            }

            Assert.All(scopedStore.GetDocLines(plan.PrdDocId).Where(line => line.ItemId == fixture.DeletedItemId), line => Assert.Null(line.OrderLineId));
            Assert.Equal(ledgerCountBefore, scopedStore.CountLedgerEntries());

            var candidateOrderId = scopedStore.AddOrder(new Order
            {
                OrderRef = $"T-CAND-{DateTime.UtcNow.Ticks.ToString()[^6..]}",
                Type = OrderType.Customer,
                PartnerId = fixture.PartnerId,
                Status = OrderStatus.InProgress,
                CreatedAt = DateTime.UtcNow
            });
            var candidateLineId = scopedStore.AddOrderLine(new OrderLine
            {
                OrderId = candidateOrderId,
                ItemId = fixture.DeletedItemId,
                QtyOrdered = 600,
                ProductionPurpose = ProductionLinePurpose.CustomerOrder
            });
            var candidates = new HuReservationCandidatesService(scopedStore).Build(new HuReservationCandidatesQuery
            {
                OrderId = candidateOrderId,
                Lines =
                [
                    new HuReservationCandidatesLineQuery
                    {
                        ClientLineKey = "candidate",
                        OrderLineId = candidateLineId,
                        ItemId = fixture.DeletedItemId,
                        QtyOrdered = 600
                    }
                ]
            });
            var candidateHuCodes = candidates.Lines.Single().Candidates
                .Select(candidate => candidate.HuCode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(targetPallets, pallet => Assert.Contains(pallet.HuCode, candidateHuCodes));
        });
    }

    [Fact]
    public async Task ReleaseProducedStock_RejectsLastCustomerLine()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, async scopedStore =>
        {
            EnsureAtLeastOneLocation(scopedStore);
            var fixture = SeedCustomerOrderWithSingleLine(scopedStore);
            var plan = new ProductionPalletService(scopedStore).PlanOrder(fixture.OrderId);
            var pallet = Assert.Single(scopedStore.GetProductionPalletsByDoc(plan.PrdDocId));
            scopedStore.MarkProductionPalletFilled(pallet.Id, DateTime.UtcNow, "TEST");

            await using var host = await PostgresOrderUpdateHost.StartAsync(scopedStore);
            using var response = await host.Client.PostAsync(
                $"/api/orders/{fixture.OrderId}/lines/{fixture.OrderLineId}/release-produced-stock",
                content: null);
            var payload = await UpdateOrderHttpApi.ReadApiErrorResultAsync(response, HttpStatusCode.BadRequest);
            Assert.Equal("ORDER_RELEASE_LAST_LINE_FORBIDDEN", payload.Error);
            Assert.Contains(scopedStore.GetOrderLines(fixture.OrderId), line => line.Id == fixture.OrderLineId);
        });
    }

    [Fact]
    public async Task ReleaseProducedStock_RejectsLineWithClosedOutboundQty()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, async scopedStore =>
        {
            EnsureAtLeastOneLocation(scopedStore);
            var locationId = scopedStore.GetLocations().First().Id;
            var fixture = SeedCustomerOrderWithTwoLines(scopedStore);
            var plan = new ProductionPalletService(scopedStore).PlanOrder(fixture.OrderId);
            var pallet = scopedStore.GetProductionPalletsByDoc(plan.PrdDocId)
                .First(p => p.OrderLineId == fixture.DeletedOrderLineId || p.Lines.Any(line => line.OrderLineId == fixture.DeletedOrderLineId));
            scopedStore.MarkProductionPalletFilled(pallet.Id, DateTime.UtcNow, "TEST");
            var outboundId = scopedStore.AddDoc(new Doc
            {
                DocRef = $"OUT-T-{DateTime.UtcNow.Ticks.ToString()[^6..]}",
                Type = DocType.Outbound,
                Status = DocStatus.Closed,
                CreatedAt = DateTime.UtcNow,
                ClosedAt = DateTime.UtcNow,
                OrderId = fixture.OrderId,
                OrderRef = fixture.OrderRef
            });
            scopedStore.AddDocLine(new DocLine
            {
                DocId = outboundId,
                OrderLineId = fixture.DeletedOrderLineId,
                ItemId = fixture.DeletedItemId,
                Qty = 1,
                FromLocationId = locationId,
                FromHu = pallet.HuCode
            });

            await using var host = await PostgresOrderUpdateHost.StartAsync(scopedStore);
            using var response = await host.Client.PostAsync(
                $"/api/orders/{fixture.OrderId}/lines/{fixture.DeletedOrderLineId}/release-produced-stock",
                content: null);
            var payload = await UpdateOrderHttpApi.ReadApiErrorResultAsync(response, HttpStatusCode.BadRequest);
            Assert.Equal("ORDER_LINE_HAS_SHIPPED_QTY", payload.Error);
        });
    }

    [Theory]
    [InlineData(ProductionPalletStatus.Planned)]
    [InlineData(ProductionPalletStatus.Printed)]
    public async Task ReleaseProducedStock_RejectsNonFilledPallets(string status)
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, async scopedStore =>
        {
            EnsureAtLeastOneLocation(scopedStore);
            var fixture = SeedCustomerOrderWithTwoLines(scopedStore);
            var plan = SeedDraftProductionReceiptPalletPlanForDeletedLine(scopedStore, fixture, status);

            await using var host = await PostgresOrderUpdateHost.StartAsync(scopedStore);
            using var response = await host.Client.PostAsync(
                $"/api/orders/{fixture.OrderId}/lines/{fixture.DeletedOrderLineId}/release-produced-stock",
                content: null);
            var payload = await UpdateOrderHttpApi.ReadApiErrorResultAsync(response, HttpStatusCode.BadRequest);
            Assert.Equal("ORDER_LINE_RELEASE_REQUIRES_FILLED_PALLETS", payload.Error);
            Assert.Contains(scopedStore.GetOrderLines(fixture.OrderId), line => line.Id == fixture.DeletedOrderLineId);
            Assert.Equal(status, Assert.Single(scopedStore.GetProductionPalletsByDoc(plan.PrdDocId)).Status);
        });
    }

    [Fact]
    public async Task ReleaseProducedStock_RejectsMixedPalletWithOtherOrderLine()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, async scopedStore =>
        {
            EnsureAtLeastOneLocation(scopedStore);
            var fixture = SeedCustomerOrderWithTwoLines(scopedStore, deletedQty: 300, remainingQty: 300, group: "MIX-1");
            var plan = new ProductionPalletService(scopedStore).PlanOrder(fixture.OrderId);
            var pallet = Assert.Single(scopedStore.GetProductionPalletsByDoc(plan.PrdDocId));
            Assert.Equal(2, pallet.Lines.Count);
            scopedStore.MarkProductionPalletFilled(pallet.Id, DateTime.UtcNow, "TEST");

            await using var host = await PostgresOrderUpdateHost.StartAsync(scopedStore);
            using var response = await host.Client.PostAsync(
                $"/api/orders/{fixture.OrderId}/lines/{fixture.DeletedOrderLineId}/release-produced-stock",
                content: null);
            var payload = await UpdateOrderHttpApi.ReadApiErrorResultAsync(response, HttpStatusCode.BadRequest);
            Assert.Equal("MIXED_PALLET_RELEASE_NOT_SUPPORTED", payload.Error);
            Assert.Contains(scopedStore.GetOrderLines(fixture.OrderId), line => line.Id == fixture.DeletedOrderLineId);
        });
    }

    [Fact]
    public async Task DeleteDraftOrder_WithV0041ExportBatch_ReturnsDomainErrorAndPreservesHistory()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, scopedStore =>
        {
            var fixture = SeedCustomerOrderWithSingleLine(scopedStore);
            scopedStore.UpdateOrderStatus(fixture.OrderId, OrderStatus.Draft);
            var line = Assert.Single(scopedStore.GetOrderLines(fixture.OrderId));
            var item = scopedStore.FindItemById(line.ItemId);
            Assert.NotNull(item);
            var requestId = Guid.NewGuid();
            var batchId = Guid.NewGuid();
            var now = new DateTime(2026, 8, 27, 10, 0, 0, DateTimeKind.Utc);

            scopedStore.AddMarkingOrder(new MarkingOrder
            {
                Id = requestId,
                OrderId = fixture.OrderId,
                OrderLineId = fixture.OrderLineId,
                ItemId = line.ItemId,
                Gtin = "04607186952596",
                RequiredQuantity = 1,
                RequestedQuantity = 1,
                OriginalOrderId = fixture.OrderId,
                OriginalOrderLineId = fixture.OrderLineId,
                RequestNumber = $"DELETE-{requestId:N}",
                Status = MarkingOrderStatus.WaitingForCodes,
                SourceType = "POSTGRES_TEST",
                CreatedAt = now,
                UpdatedAt = now
            });
            ((IMarkingRequestOperationalStore)scopedStore).CreateMarkingRequestExportBatch(
                new CreateMarkingRequestExportBatchCommand(
                    batchId,
                    fixture.OrderId,
                    "delete-pre-snapshot",
                    "delete-post-snapshot",
                    0,
                    "TEST",
                    now,
                    [new MarkingRequestExportBatchRequestSnapshot(
                        requestId,
                        line.ItemId,
                        item!.Name,
                        "04607186952596",
                        1,
                        0,
                        1)]));

            var exception = Record.Exception(() => new OrderService(scopedStore).DeleteOrder(fixture.OrderId));

            var domainException = Assert.IsType<OrderMarkingHistoryDeleteException>(exception);
            Assert.Equal(OrderMarkingHistoryDeleteException.OrderErrorCode, domainException.ErrorCode);
            Assert.Contains("историю маркировки", domainException.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(scopedStore.GetOrder(fixture.OrderId));
            Assert.Single(scopedStore.GetOrderLines(fixture.OrderId));
            Assert.NotNull(((IMarkingRequestOperationalStore)scopedStore)
                .GetMarkingRequestExportBatch(fixture.OrderId, "delete-pre-snapshot"));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DeleteDraftOrder_WithoutMarkingHistory_DeletesOrderAndLines()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, scopedStore =>
        {
            var fixture = SeedCustomerOrderWithSingleLine(scopedStore);
            scopedStore.UpdateOrderStatus(fixture.OrderId, OrderStatus.Draft);

            new OrderService(scopedStore).DeleteOrder(fixture.OrderId);

            Assert.Null(scopedStore.GetOrder(fixture.OrderId));
            Assert.Empty(scopedStore.GetOrderLines(fixture.OrderId));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DeleteDraftOrder_WithMarkingRequestButNoExportBatch_ReturnsSameDomainError()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, scopedStore =>
        {
            var fixture = SeedCustomerOrderWithSingleLine(scopedStore);
            scopedStore.UpdateOrderStatus(fixture.OrderId, OrderStatus.Draft);
            var line = Assert.Single(scopedStore.GetOrderLines(fixture.OrderId));
            var requestId = AddMarkingRequest(scopedStore, fixture.OrderId, fixture.OrderLineId, line.ItemId);

            var exception = Assert.Throws<OrderMarkingHistoryDeleteException>(
                () => new OrderService(scopedStore).DeleteOrder(fixture.OrderId));

            Assert.Equal(OrderMarkingHistoryDeleteException.OrderErrorCode, exception.ErrorCode);
            Assert.NotNull(scopedStore.GetOrder(fixture.OrderId));
            Assert.Single(scopedStore.GetOrderLines(fixture.OrderId));
            Assert.Single(scopedStore.GetMarkingOrdersByIds([requestId]));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task UpdateOrder_RemovingLineWithMarkingHistory_LogicallyCancelsAndKeepsHistory()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, scopedStore =>
        {
            var fixture = SeedCustomerOrderWithTwoLines(scopedStore);
            var requestId = AddMarkingRequest(
                scopedStore,
                fixture.OrderId,
                fixture.DeletedOrderLineId,
                fixture.DeletedItemId);
            var remaining = Assert.Single(
                scopedStore.GetOrderLines(fixture.OrderId),
                line => line.Id == fixture.RemainingOrderLineId);
            var service = new OrderService(scopedStore);
            var updateLines = new[]
            {
                new OrderLineView
                {
                    Id = remaining.Id,
                    OrderId = fixture.OrderId,
                    ItemId = remaining.ItemId,
                    QtyOrdered = remaining.QtyOrdered,
                    ProductionPurpose = remaining.ProductionPurpose
                }
            };

            service.UpdateOrder(
                fixture.OrderId,
                fixture.OrderRef,
                fixture.PartnerId,
                null,
                null,
                updateLines,
                OrderType.Customer);

            var storedLines = scopedStore.GetOrderLines(fixture.OrderId);
            Assert.Equal(2, storedLines.Count);
            var cancelled = Assert.Single(storedLines, line => line.Id == fixture.DeletedOrderLineId);
            Assert.NotNull(cancelled.CancelledAt);
            Assert.Equal("SERVER:order-update", cancelled.CancelledByActor);
            Assert.Equal("removed_from_order_update_with_marking_history", cancelled.CancelReason);
            Assert.Single(scopedStore.GetMarkingOrdersByIds([requestId]));
            Assert.Collection(
                scopedStore.GetOrderLineViews(fixture.OrderId),
                line => Assert.Equal(fixture.RemainingOrderLineId, line.Id));

            service.UpdateOrder(
                fixture.OrderId,
                fixture.OrderRef,
                fixture.PartnerId,
                null,
                null,
                updateLines,
                OrderType.Customer);
            Assert.Single(scopedStore.GetOrderLines(fixture.OrderId), line => line.CancelledAt.HasValue);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task PhysicalOrderLineDelete_WithMarkingHistory_FailsClosedWithoutPostgresFk()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, scopedStore =>
        {
            var fixture = SeedCustomerOrderWithTwoLines(scopedStore);
            var requestId = AddMarkingRequest(
                scopedStore,
                fixture.OrderId,
                fixture.DeletedOrderLineId,
                fixture.DeletedItemId);

            var exception = Assert.Throws<OrderMarkingHistoryDeleteException>(
                () => scopedStore.DeleteOrderLine(fixture.DeletedOrderLineId));

            Assert.Equal(OrderMarkingHistoryDeleteException.OrderLineErrorCode, exception.ErrorCode);
            Assert.IsNotType<PostgresException>(exception);
            Assert.Contains(
                scopedStore.GetOrderLines(fixture.OrderId),
                line => line.Id == fixture.DeletedOrderLineId && !line.CancelledAt.HasValue);
            Assert.Single(scopedStore.GetMarkingOrdersByIds([requestId]));
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("MARKING_CODE")]
    [InlineData("TRANSITION_AUDIT")]
    public async Task PhysicalOrderLineDelete_MatchingForeignReceiptLineId_DoesNotCreateMarkingHistoryDependency(
        string source)
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        var fixture = await SeedReceiptLineageFixtureAsync(
            connectionString,
            source == "MARKING_CODE" ? ReceiptLineageSource.MarkingCode : ReceiptLineageSource.TransitionAudit,
            receiptLineBelongsToTarget: false);
        try
        {
            var store = new PostgresDataStore(connectionString);
            store.Initialize();

            store.DeleteOrderLine(fixture.TargetOrderLineId);

            Assert.DoesNotContain(
                store.GetOrderLines(fixture.TargetOrderId),
                line => line.Id == fixture.TargetOrderLineId);
            Assert.Contains(
                store.GetOrderLines(fixture.ForeignOrderId),
                line => line.Id == fixture.ForeignOrderLineId);
        }
        finally
        {
            await DeleteReceiptLineageFixtureAsync(connectionString, fixture);
        }
    }

    [Theory]
    [InlineData("MARKING_CODE")]
    [InlineData("TRANSITION_AUDIT")]
    public async Task PhysicalOrderLineDelete_DifferentLinkedReceiptLineId_BlocksWithStableMarkingHistoryError(
        string source)
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        var fixture = await SeedReceiptLineageFixtureAsync(
            connectionString,
            source == "MARKING_CODE" ? ReceiptLineageSource.MarkingCode : ReceiptLineageSource.TransitionAudit,
            receiptLineBelongsToTarget: true);
        try
        {
            Assert.NotEqual(fixture.TargetOrderLineId, fixture.ReceiptLineId);
            var store = new PostgresDataStore(connectionString);
            store.Initialize();

            var exception = Assert.Throws<OrderMarkingHistoryDeleteException>(
                () => store.DeleteOrderLine(fixture.TargetOrderLineId));

            Assert.Equal(OrderMarkingHistoryDeleteException.OrderLineErrorCode, exception.ErrorCode);
            Assert.IsNotType<PostgresException>(exception);
            Assert.Contains(
                store.GetOrderLines(fixture.TargetOrderId),
                line => line.Id == fixture.TargetOrderLineId);
        }
        finally
        {
            await DeleteReceiptLineageFixtureAsync(connectionString, fixture);
        }
    }

    [Fact]
    public async Task ConcurrentExportHistoryCommitThenDelete_SerializesAndRejectsWithoutPartialWrites()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        var setupStore = new PostgresDataStore(connectionString);
        setupStore.Initialize();
        EnsureAtLeastOneLocation(setupStore);
        var fixture = SeedCustomerOrderWithSingleLine(setupStore);
        setupStore.UpdateOrderStatus(fixture.OrderId, OrderStatus.Draft);
        var line = Assert.Single(setupStore.GetOrderLines(fixture.OrderId));
        var item = setupStore.FindItemById(line.ItemId)!;
        var locationId = setupStore.GetLocations().First().Id;
        var suffix = Guid.NewGuid().ToString("N");
        setupStore.ReplaceOrderReceiptPlanLines(fixture.OrderId,
        [
            new OrderReceiptPlanLine
            {
                OrderId = fixture.OrderId,
                OrderLineId = fixture.OrderLineId,
                ItemId = line.ItemId,
                QtyPlanned = 1,
                ToLocationId = locationId,
                ToHu = $"HU-DELETE-{suffix[..12]}",
                SortOrder = 0
            }
        ]);

        var writerConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = $"order-delete-writer-{suffix}"
        }.ConnectionString;
        var deleteApplicationName = $"order-delete-waiter-{suffix}";
        var deleteConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = deleteApplicationName
        }.ConnectionString;
        var deleteStore = new PostgresDataStore(deleteConnectionString);
        deleteStore.Initialize();

        await using var writerConnection = new NpgsqlConnection(writerConnectionString);
        await writerConnection.OpenAsync();
        await using var writerTransaction = await writerConnection.BeginTransactionAsync();
        await LockOrderAsync(writerConnection, writerTransaction, fixture.OrderId);
        var requestId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        await InsertExportHistoryAsync(
            writerConnection,
            writerTransaction,
            fixture.OrderId,
            fixture.OrderLineId,
            line.ItemId,
            item.Name,
            requestId,
            batchId,
            suffix);

        var deleteTask = Task.Run(() => Record.Exception(
            () => new OrderService(deleteStore).DeleteOrder(fixture.OrderId)));

        await WaitForPostgresLockAsync(connectionString, deleteApplicationName, TimeSpan.FromSeconds(10));
        Assert.False(deleteTask.IsCompleted);
        await writerTransaction.CommitAsync();

        var exception = await deleteTask;
        var domainException = Assert.IsType<OrderMarkingHistoryDeleteException>(exception);
        Assert.Equal(OrderMarkingHistoryDeleteException.OrderErrorCode, domainException.ErrorCode);
        Assert.NotNull(setupStore.GetOrder(fixture.OrderId));
        Assert.Single(setupStore.GetOrderLines(fixture.OrderId));
        Assert.Single(setupStore.GetOrderReceiptPlanLines(fixture.OrderId));
        Assert.Single(setupStore.GetMarkingOrdersByIds([requestId]));
        Assert.NotNull(((IMarkingRequestOperationalStore)setupStore)
            .GetMarkingRequestExportBatch(fixture.OrderId, $"pre-{suffix}"));
    }

    private static Guid AddMarkingRequest(IDataStore store, long orderId, long orderLineId, long itemId)
    {
        var requestId = Guid.NewGuid();
        var now = new DateTime(2026, 8, 27, 11, 0, 0, DateTimeKind.Utc);
        store.AddMarkingOrder(new MarkingOrder
        {
            Id = requestId,
            OrderId = orderId,
            OrderLineId = orderLineId,
            ItemId = itemId,
            Gtin = "04607186952596",
            RequiredQuantity = 1,
            RequestedQuantity = 1,
            OriginalOrderId = orderId,
            OriginalOrderLineId = orderLineId,
            RequestNumber = $"DELETE-{requestId:N}",
            Status = MarkingOrderStatus.WaitingForCodes,
            SourceType = "POSTGRES_TEST",
            CreatedAt = now,
            UpdatedAt = now
        });
        return requestId;
    }

    private static long _nextReceiptLineageFixtureId = 8_000_000_000;

    private static async Task<ReceiptLineageFixture> SeedReceiptLineageFixtureAsync(
        string connectionString,
        ReceiptLineageSource source,
        bool receiptLineBelongsToTarget)
    {
        var baseId = Interlocked.Add(ref _nextReceiptLineageFixtureId, 100);
        var targetOrderId = baseId + 1;
        var foreignOrderId = baseId + 2;
        var targetOrderLineId = baseId + 10;
        var foreignOrderLineId = baseId + 11;
        var targetItemId = baseId + 20;
        var foreignItemId = baseId + 21;
        var receiptDocId = baseId + 30;
        var receiptLineId = receiptLineBelongsToTarget ? baseId + 31 : targetOrderLineId;
        var adjustmentId = baseId + 40;
        var requestId = Guid.NewGuid();
        var importId = Guid.NewGuid();
        var codeId = Guid.NewGuid();
        var adjustmentRequestId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("""
INSERT INTO items(id, name, base_uom)
VALUES (@target_item_id, @target_item_name, 'шт'),
       (@foreign_item_id, @foreign_item_name, 'шт');

INSERT INTO orders(id, order_ref, order_type, status, created_at)
VALUES (@target_order_id, @target_order_ref, 'CUSTOMER', 'DRAFT', @created_at),
       (@foreign_order_id, @foreign_order_ref, 'CUSTOMER', 'DRAFT', @created_at);

INSERT INTO order_lines(id, order_id, item_id, qty_ordered)
VALUES (@target_order_line_id, @target_order_id, @target_item_id, 1),
       (@foreign_order_line_id, @foreign_order_id, @foreign_item_id, 1);

INSERT INTO docs(id, doc_ref, type, status, created_at, order_id, order_ref)
VALUES (@receipt_doc_id, @receipt_doc_ref, 'PRODUCTION_RECEIPT', 'CLOSED', @created_at,
        @receipt_order_id, @receipt_order_ref);

INSERT INTO doc_lines(id, doc_id, order_line_id, item_id, qty)
VALUES (@receipt_line_id, @receipt_doc_id, @receipt_order_line_id, @receipt_item_id, 1);

INSERT INTO marking_order(
    id, order_id, order_line_id, item_id, gtin,
    required_quantity, reserve_quantity, requested_quantity,
    original_order_id, original_order_line_id,
    request_number, status, source_type, created_at, updated_at)
VALUES (
    @request_id, @foreign_order_id, @foreign_order_line_id, @foreign_item_id, '04607186952596',
    1, 0, 1, @foreign_order_id, @foreign_order_line_id,
    @request_number, 'WaitingForCodes', 'POSTGRES_TEST', @created_at, @created_at);

INSERT INTO marking_code_import(
    id, original_filename, storage_path, file_hash, source_type,
    matched_marking_order_id, status, created_at)
VALUES (
    @import_id, 'synthetic-test.tsv', '<test>', @file_hash, 'POSTGRES_TEST',
    @request_id, 'Imported', @created_at);

INSERT INTO marking_code(
    id, code, code_hash, gtin, marking_order_id, import_id,
    status, origin, receipt_doc_id, receipt_line_id, created_at, updated_at)
VALUES (
    @code_id, @code, @code_hash, '04607186952596', @request_id, @import_id,
    'Reserved', 'HistoricalUnknown',
    CASE WHEN @source = 'MARKING_CODE' THEN @receipt_doc_id ELSE NULL END,
    CASE WHEN @source = 'MARKING_CODE' THEN @receipt_line_id ELSE NULL END,
    @created_at, @created_at);

INSERT INTO production_pallet_filling_adjustments(
    id, action_type, request_id, payload_hash, source_prd_doc_id,
    reason_code, reason_text, created_at)
SELECT @adjustment_id, 'RESET_PARTIAL', @adjustment_request_id, @payload_hash, @receipt_doc_id,
       'ERRONEOUS_PARTIAL_FILL', 'test lineage', @created_at
WHERE @source = 'TRANSITION_AUDIT';

INSERT INTO production_marking_transition_audit(
    adjustment_id, marking_code_id, marking_order_id, import_id, origin,
    source_prd_doc_id, old_receipt_doc_id, old_receipt_line_id,
    old_status, new_status, reason_text, changed_at)
SELECT @adjustment_id, @code_id, @request_id, @import_id, 'HistoricalUnknown',
       @receipt_doc_id, @receipt_doc_id, @receipt_line_id,
       'Applied', 'Reserved', 'test lineage', @created_at
WHERE @source = 'TRANSITION_AUDIT';
""", connection, transaction);
        command.Parameters.AddWithValue("@target_item_id", targetItemId);
        command.Parameters.AddWithValue("@target_item_name", $"Target item {suffix}");
        command.Parameters.AddWithValue("@foreign_item_id", foreignItemId);
        command.Parameters.AddWithValue("@foreign_item_name", $"Foreign item {suffix}");
        command.Parameters.AddWithValue("@target_order_id", targetOrderId);
        command.Parameters.AddWithValue("@target_order_ref", $"TARGET-{suffix}");
        command.Parameters.AddWithValue("@foreign_order_id", foreignOrderId);
        command.Parameters.AddWithValue("@foreign_order_ref", $"FOREIGN-{suffix}");
        command.Parameters.AddWithValue("@target_order_line_id", targetOrderLineId);
        command.Parameters.AddWithValue("@foreign_order_line_id", foreignOrderLineId);
        command.Parameters.AddWithValue("@receipt_doc_id", receiptDocId);
        command.Parameters.AddWithValue("@receipt_doc_ref", $"PRD-{suffix}");
        command.Parameters.AddWithValue("@receipt_order_id", receiptLineBelongsToTarget ? targetOrderId : foreignOrderId);
        command.Parameters.AddWithValue("@receipt_order_ref", receiptLineBelongsToTarget ? $"TARGET-{suffix}" : $"FOREIGN-{suffix}");
        command.Parameters.AddWithValue("@receipt_line_id", receiptLineId);
        command.Parameters.AddWithValue("@receipt_order_line_id", receiptLineBelongsToTarget ? targetOrderLineId : foreignOrderLineId);
        command.Parameters.AddWithValue("@receipt_item_id", receiptLineBelongsToTarget ? targetItemId : foreignItemId);
        command.Parameters.AddWithValue("@request_id", requestId);
        command.Parameters.AddWithValue("@request_number", $"LINEAGE-{suffix}");
        command.Parameters.AddWithValue("@import_id", importId);
        command.Parameters.AddWithValue("@file_hash", $"file-{suffix}");
        command.Parameters.AddWithValue("@code_id", codeId);
        command.Parameters.AddWithValue("@code", $"TEST-CODE-{suffix}");
        command.Parameters.AddWithValue("@code_hash", $"code-{suffix}");
        command.Parameters.AddWithValue("@source", source == ReceiptLineageSource.MarkingCode ? "MARKING_CODE" : "TRANSITION_AUDIT");
        command.Parameters.AddWithValue("@adjustment_id", adjustmentId);
        command.Parameters.AddWithValue("@adjustment_request_id", adjustmentRequestId);
        command.Parameters.AddWithValue("@payload_hash", $"payload-{suffix}");
        command.Parameters.AddWithValue("@created_at", "2026-08-27T12:00:00.0000000Z");
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();

        return new ReceiptLineageFixture(
            targetOrderId,
            foreignOrderId,
            targetOrderLineId,
            foreignOrderLineId,
            targetItemId,
            foreignItemId,
            receiptDocId,
            receiptLineId,
            adjustmentId,
            requestId,
            importId,
            codeId);
    }

    private static async Task DeleteReceiptLineageFixtureAsync(
        string connectionString,
        ReceiptLineageFixture fixture)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
DELETE FROM production_marking_transition_audit WHERE marking_code_id = @code_id;
DELETE FROM production_pallet_filling_adjustments WHERE id = @adjustment_id;
DELETE FROM marking_code WHERE id = @code_id;
DELETE FROM marking_code_import WHERE id = @import_id;
DELETE FROM marking_order WHERE id = @request_id;
DELETE FROM doc_lines WHERE id = @receipt_line_id;
DELETE FROM docs WHERE id = @receipt_doc_id;
DELETE FROM order_lines WHERE id IN (@target_order_line_id, @foreign_order_line_id);
DELETE FROM orders WHERE id IN (@target_order_id, @foreign_order_id);
DELETE FROM items WHERE id IN (@target_item_id, @foreign_item_id);
""", connection);
        command.Parameters.AddWithValue("@code_id", fixture.CodeId);
        command.Parameters.AddWithValue("@adjustment_id", fixture.AdjustmentId);
        command.Parameters.AddWithValue("@import_id", fixture.ImportId);
        command.Parameters.AddWithValue("@request_id", fixture.RequestId);
        command.Parameters.AddWithValue("@receipt_line_id", fixture.ReceiptLineId);
        command.Parameters.AddWithValue("@receipt_doc_id", fixture.ReceiptDocId);
        command.Parameters.AddWithValue("@target_order_line_id", fixture.TargetOrderLineId);
        command.Parameters.AddWithValue("@foreign_order_line_id", fixture.ForeignOrderLineId);
        command.Parameters.AddWithValue("@target_order_id", fixture.TargetOrderId);
        command.Parameters.AddWithValue("@foreign_order_id", fixture.ForeignOrderId);
        command.Parameters.AddWithValue("@target_item_id", fixture.TargetItemId);
        command.Parameters.AddWithValue("@foreign_item_id", fixture.ForeignItemId);
        await command.ExecuteNonQueryAsync();
    }

    private enum ReceiptLineageSource
    {
        MarkingCode,
        TransitionAudit
    }

    private sealed record ReceiptLineageFixture(
        long TargetOrderId,
        long ForeignOrderId,
        long TargetOrderLineId,
        long ForeignOrderLineId,
        long TargetItemId,
        long ForeignItemId,
        long ReceiptDocId,
        long ReceiptLineId,
        long AdjustmentId,
        Guid RequestId,
        Guid ImportId,
        Guid CodeId);

    private static async Task LockOrderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long orderId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM orders WHERE id = @order_id FOR UPDATE;";
        command.Parameters.AddWithValue("@order_id", orderId);
        Assert.Equal(orderId, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    private static async Task InsertExportHistoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long orderId,
        long orderLineId,
        long itemId,
        string itemName,
        Guid requestId,
        Guid batchId,
        string suffix)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
INSERT INTO marking_order(
    id, order_id, order_line_id, item_id, gtin,
    required_quantity, reserve_quantity, requested_quantity,
    original_order_id, original_order_line_id,
    request_number, status, request_status, source_type, created_at, updated_at)
VALUES(
    @request_id, @order_id, @order_line_id, @item_id, @gtin,
    1, 0, 1, @order_id, @order_line_id,
    @request_number, 'WaitingForCodes', 'NotRequested', 'POSTGRES_TEST', @created_at_text, @created_at_text);

INSERT INTO marking_request_export_batch(
    id, order_id, expected_snapshot_hash, post_export_snapshot_hash,
    reserve_quantity, created_by, created_at)
VALUES(@batch_id, @order_id, @pre_hash, @post_hash, 0, 'TEST', @created_at);

INSERT INTO marking_request_export_batch_request(
    export_batch_id, marking_order_id, item_id, item_name_snapshot, gtin_snapshot,
    required_quantity_snapshot, reserve_quantity_snapshot, requested_quantity_snapshot)
VALUES(@batch_id, @request_id, @item_id, @item_name, @gtin, 1, 0, 1);
""";
        var now = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);
        command.Parameters.AddWithValue("@request_id", requestId);
        command.Parameters.AddWithValue("@batch_id", batchId);
        command.Parameters.AddWithValue("@order_id", orderId);
        command.Parameters.AddWithValue("@order_line_id", orderLineId);
        command.Parameters.AddWithValue("@item_id", itemId);
        command.Parameters.AddWithValue("@item_name", itemName);
        command.Parameters.AddWithValue("@gtin", "04607186952596");
        command.Parameters.AddWithValue("@request_number", $"DELETE-LOCK-{suffix}");
        command.Parameters.AddWithValue("@pre_hash", $"pre-{suffix}");
        command.Parameters.AddWithValue("@post_hash", $"post-{suffix}");
        command.Parameters.AddWithValue("@created_at_text", now.ToString("O"));
        command.Parameters.AddWithValue("@created_at", now);
        Assert.Equal(3, await command.ExecuteNonQueryAsync());
    }

    private static async Task WaitForPostgresLockAsync(
        string connectionString,
        string applicationName,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT EXISTS (
    SELECT 1
    FROM pg_stat_activity
    WHERE application_name = @application_name
      AND wait_event_type = 'Lock'
);
""";
            command.Parameters.AddWithValue("@application_name", applicationName);
            if (await command.ExecuteScalarAsync() is true)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"PostgreSQL session {applicationName} did not wait on the order lock.");
    }

    private static UpdateOrderHttpApi.UpdateOrderRequest BuildDeleteFirstLineRequest(CustomerOrderFixture fixture)
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
                    OrderLineId = fixture.RemainingOrderLineId,
                    ItemId = fixture.RemainingItemId,
                    QtyOrdered = 600
                }
            ]
        };
    }

    private static string BuildDeleteFirstLineRawJson(CustomerOrderFixture fixture)
    {
        return $$"""
        {
          "order_ref": "{{fixture.OrderRef}}",
          "type": "CUSTOMER",
          "partner_id": {{fixture.PartnerId}},
          "status": "IN_PROGRESS",
          "lines": [
            {
              "order_line_id": {{fixture.RemainingOrderLineId}},
              "item_id": {{fixture.RemainingItemId}},
              "qty_ordered": 600
            }
          ]
        }
        """;
    }

    private static CustomerOrderFixture SeedCustomerOrderWithTwoLines(
        IDataStore store,
        double deletedQty = 600,
        double remainingQty = 600,
        string? group = null)
    {
        var suffix = DateTime.UtcNow.Ticks.ToString();
        var partnerId = store.AddPartner(new Partner
        {
            Name = $"Тестовый клиент {suffix}",
            Code = $"T-CL-{suffix}"
        });

        var deletedItemId = store.AddItem(new Item
        {
            Name = $"Тестовый товар A {suffix}",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });

        var remainingItemId = store.AddItem(new Item
        {
            Name = $"Тестовый товар B {suffix}",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });

        var orderRef = $"T-DEL-{suffix[^6..]}";
        var orderId = store.AddOrder(new Order
        {
            OrderRef = orderRef,
            Type = OrderType.Customer,
            PartnerId = partnerId,
            Status = OrderStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });

        var deletedOrderLineId = store.AddOrderLine(new OrderLine
        {
            OrderId = orderId,
            ItemId = deletedItemId,
            QtyOrdered = deletedQty,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder,
            ProductionPalletGroup = group
        });

        var remainingOrderLineId = store.AddOrderLine(new OrderLine
        {
            OrderId = orderId,
            ItemId = remainingItemId,
            QtyOrdered = remainingQty,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder,
            ProductionPalletGroup = group
        });

        return new CustomerOrderFixture(
            orderId,
            orderRef,
            partnerId,
            deletedItemId,
            remainingItemId,
            deletedOrderLineId,
            remainingOrderLineId);
    }

    private static SingleLineCustomerOrderFixture SeedCustomerOrderWithSingleLine(IDataStore store)
    {
        var suffix = DateTime.UtcNow.Ticks.ToString();
        var partnerId = store.AddPartner(new Partner
        {
            Name = $"Тестовый клиент single {suffix}",
            Code = $"T-SCL-{suffix}",
            PartnerRole = "BOTH"
        });

        var itemId = store.AddItem(new Item
        {
            Name = $"Тестовый товар single {suffix}",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });

        var orderId = store.AddOrder(new Order
        {
            OrderRef = $"T-SINGLE-{suffix[^6..]}",
            Type = OrderType.Customer,
            PartnerId = partnerId,
            Status = OrderStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });

        var orderLineId = store.AddOrderLine(new OrderLine
        {
            OrderId = orderId,
            ItemId = itemId,
            QtyOrdered = 600,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });

        return new SingleLineCustomerOrderFixture(orderId, orderLineId);
    }

    private static PalletPlanFixture SeedDraftProductionReceiptPalletPlanForDeletedLine(
        IDataStore store,
        CustomerOrderFixture fixture,
        string palletStatus)
    {
        var suffix = DateTime.UtcNow.Ticks.ToString();
        var locationId = store.GetLocations().First().Id;
        var huCode = store.CreateProductionPalletHuCode("ORDER-DELETE-REGRESSION");
        var docId = store.AddDoc(new Doc
        {
            DocRef = $"PRD-T-{suffix[^6..]}",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            CreatedAt = DateTime.UtcNow,
            OrderId = fixture.OrderId,
            OrderRef = fixture.OrderRef
        });

        var docLineId = store.AddDocLine(new DocLine
        {
            DocId = docId,
            OrderLineId = fixture.DeletedOrderLineId,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder,
            ItemId = fixture.DeletedItemId,
            Qty = 600,
            ToLocationId = locationId,
            ToHu = huCode
        });

        var planned = store.PlanProductionPallets(docId, DateTime.UtcNow);
        var pallet = Assert.Single(planned);
        Assert.Equal(docLineId, pallet.DocLineId);
        Assert.Equal(fixture.DeletedOrderLineId, pallet.OrderLineId);
        Assert.Equal(fixture.DeletedOrderLineId, Assert.Single(pallet.Lines).OrderLineId);

        if (string.Equals(palletStatus, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(1, store.CancelProductionPallets([pallet.Id]));
        }
        else if (string.Equals(palletStatus, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(1, store.MarkProductionPalletsPrinted(fixture.OrderId, [pallet.Id], DateTime.UtcNow));
        }
        else
        {
            Assert.Equal(ProductionPalletStatus.Planned, palletStatus);
        }

        pallet = Assert.Single(store.GetProductionPalletsByDoc(docId));
        Assert.Equal(palletStatus, pallet.Status);
        Assert.Equal(docLineId, pallet.DocLineId);
        Assert.Equal(fixture.DeletedOrderLineId, pallet.OrderLineId);
        Assert.Equal(fixture.DeletedOrderLineId, Assert.Single(pallet.Lines).OrderLineId);
        Assert.Equal(fixture.DeletedOrderLineId, Assert.Single(store.GetDocLines(docId)).OrderLineId);

        return new PalletPlanFixture(docId, docLineId, pallet.Id);
    }

    private static void AssertDetachedPalletPlan(IDataStore store, PalletPlanFixture plan)
    {
        var pallet = Assert.Single(store.GetProductionPalletsByDoc(plan.PrdDocId));
        Assert.Equal(plan.PalletId, pallet.Id);
        Assert.Equal(plan.DocLineId, pallet.DocLineId);
        Assert.Null(pallet.OrderLineId);
        Assert.Null(Assert.Single(pallet.Lines).OrderLineId);

        var docLine = Assert.Single(store.GetDocLines(plan.PrdDocId));
        Assert.Equal(plan.DocLineId, docLine.Id);
        Assert.Null(docLine.OrderLineId);
    }

    private static void AssertActivePalletPlanRemoved(IDataStore store, PalletPlanFixture plan)
    {
        Assert.DoesNotContain(
            store.GetProductionPalletsByDoc(plan.PrdDocId),
            pallet => pallet.Id == plan.PalletId
                      && !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(store.GetDocLines(plan.PrdDocId), line => line.Id == plan.DocLineId);
    }

    private static void AssertNoActiveOrphanPallets(IReadOnlyCollection<ProductionPallet> pallets)
    {
        Assert.DoesNotContain(
            pallets,
            pallet => pallet.OrderLineId == null
                      && (string.Equals(pallet.Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(pallet.Status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)));
    }

    private static void EnsureAtLeastOneLocation(IDataStore store)
    {
        if (store.GetLocations().Count > 0)
        {
            return;
        }

        store.AddLocation(new Location
        {
            Code = "FG",
            Name = "Готовая продукция",
            AutoHuDistributionEnabled = true
        });
    }

    private static async Task RunInRollbackTransactionAsync(string connectionString, Func<IDataStore, Task> work)
    {
        var store = new PostgresDataStore(connectionString);
        store.Initialize();

        var exception = await Record.ExceptionAsync(() =>
        {
            store.ExecuteInTransaction(scopedStore =>
            {
                work(scopedStore).GetAwaiter().GetResult();
                throw new RollbackRequestedException();
            });
            return Task.CompletedTask;
        });

        Assert.True(
            exception is RollbackRequestedException,
            exception?.ToString() ?? "Expected rollback transaction marker exception.");
    }

    private static string? ResolvePostgresTestConnectionString()
    {
        foreach (var key in new[]
                 {
                     "FLOWSTOCK_POSTGRES_TEST_CONNECTION",
                     "FLOWSTOCK_POSTGRES_CONNECTION",
                     "POSTGRES_CONNECTION_STRING"
                 })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        const string fallback =
            "Host=127.0.0.1;Port=5432;Database=flowstock;Username=flowstock;Password=flowstock;Pooling=false;Timeout=2;Command Timeout=30";
        try
        {
            var store = new PostgresDataStore(fallback);
            store.Initialize();
            return fallback;
        }
        catch
        {
            return null;
        }
    }

    private static void AssertDeleteBefore(string source, string first, string second)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);

        Assert.True(firstIndex >= 0, $"Не найден фрагмент: {first}");
        Assert.True(secondIndex >= 0, $"Не найден фрагмент: {second}");
        Assert.True(firstIndex < secondIndex, $"Ожидалось, что '{first}' идет раньше '{second}'.");
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Не найден метод: {startMarker}");

        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Не найдена граница метода: {endMarker}");

        return source[start..end];
    }

    private static string GetPostgresDataStorePath()
        => GetRepoFilePath("apps", "windows", "FlowStock.Data", "PostgresDataStore.cs");

    private static string GetRepoFilePath(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(current, string.Concat(Enumerable.Repeat("..\\", i)), Path.Combine(parts)));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Не удалось найти файл в репозитории.", Path.Combine(parts));
    }

    private sealed record CustomerOrderFixture(
        long OrderId,
        string OrderRef,
        long PartnerId,
        long DeletedItemId,
        long RemainingItemId,
        long DeletedOrderLineId,
        long RemainingOrderLineId);

    private sealed record PalletPlanFixture(
        long PrdDocId,
        long DocLineId,
        long PalletId);

    private sealed record SingleLineCustomerOrderFixture(long OrderId, long OrderLineId);

    private sealed class RollbackRequestedException : Exception;

    private sealed class PostgresOrderUpdateHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private PostgresOrderUpdateHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<PostgresOrderUpdateHost> StartAsync(IDataStore store)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(OrderUpdateEndpoint).Assembly.FullName,
                EnvironmentName = Environments.Production
            });

            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(typeof(IDataStore), store);
            builder.Services.AddSingleton<PartnerRoleResolver>();

            var app = builder.Build();
            OrderUpdateEndpoint.Map(app);
            OrderProducedStockReleaseEndpoint.Map(app);
            await app.StartAsync();

            var addresses = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>();
            var address = addresses?.Addresses.Single();
            if (string.IsNullOrWhiteSpace(address))
            {
                await app.StopAsync();
                await app.DisposeAsync();
                throw new InvalidOperationException("HTTP test host did not expose a listening address.");
            }

            return new PostgresOrderUpdateHost(
                app,
                new HttpClient
                {
                    BaseAddress = new Uri(address, UriKind.Absolute)
                });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}

