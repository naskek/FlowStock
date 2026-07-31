using System.Runtime.ExceptionServices;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;
using Npgsql;

namespace FlowStock.Server.Tests.Tsd;

public sealed class TsdOutboundEligibilityPostgresTests
{
    [PostgresFact]
    public void CustomerToInternal_GenericUpdatePersistsServerOwnedReset()
    {
        var connectionString = RequirePostgresTestConnectionString();
        RunInRollbackTransaction(connectionString, store =>
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var partnerId = store.AddPartner(new Partner
            {
                Code = $"TYPE-{suffix}",
                Name = "Type transition customer",
                CreatedAt = DateTime.UtcNow
            });
            var orderId = store.AddOrder(new Order
            {
                OrderRef = $"TYPE-{suffix}",
                Type = OrderType.Customer,
                PartnerId = partnerId,
                Status = OrderStatus.InProgress,
                CreatedAt = DateTime.UtcNow
            });
            store.UpdateOrderPartialOutboundPermission(orderId, true);
            var service = new OrderService(store);

            service.UpdateOrder(
                orderId,
                $"TYPE-{suffix}",
                partnerId: null,
                dueDate: null,
                comment: null,
                lines: Array.Empty<OrderLineView>(),
                type: OrderType.Internal);

            Assert.Equal(OrderType.Internal, store.GetOrder(orderId)!.Type);
            Assert.False(store.GetOrder(orderId)!.AllowPartialOutbound);

            // Legacy/inconsistent value must not become effective again on the reverse transition.
            store.UpdateOrderPartialOutboundPermission(orderId, true);

            service.UpdateOrder(
                orderId,
                $"TYPE-{suffix}",
                partnerId,
                dueDate: null,
                comment: null,
                lines: Array.Empty<OrderLineView>(),
                type: OrderType.Customer);

            Assert.Equal(OrderType.Customer, store.GetOrder(orderId)!.Type);
            Assert.False(store.GetOrder(orderId)!.AllowPartialOutbound);
        });
    }

    [PostgresFact]
    public async Task GenericUpdateAndPermissionCommand_DoNotLoseConcurrentPermissionValue()
    {
        var connectionString = RequirePostgresTestConnectionString();

        var store = new PostgresDataStore(connectionString);
        store.Initialize();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var orderId = store.AddOrder(new Order
        {
            OrderRef = $"PERMISSION-RACE-{suffix}",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });

        try
        {
            await using (var lockConnection = new NpgsqlConnection(connectionString))
            {
                await lockConnection.OpenAsync();
                await using var lockTransaction = await lockConnection.BeginTransactionAsync();
                await using var lockCommand = new NpgsqlCommand(
                    "UPDATE orders SET comment = 'generic-first' WHERE id = @id",
                    lockConnection,
                    lockTransaction);
                lockCommand.Parameters.AddWithValue("id", orderId);
                await lockCommand.ExecuteNonQueryAsync();

                var permissionWrite = Task.Run(() => store.UpdateOrderPartialOutboundPermission(orderId, true));
                await Task.Delay(100);
                Assert.False(permissionWrite.IsCompleted);
                await lockTransaction.CommitAsync();
                await permissionWrite;
            }
            Assert.True(store.GetOrder(orderId)!.AllowPartialOutbound);

            var staleGenericOrder = store.GetOrder(orderId)!;
            await using (var lockConnection = new NpgsqlConnection(connectionString))
            {
                await lockConnection.OpenAsync();
                await using var lockTransaction = await lockConnection.BeginTransactionAsync();
                await using var lockCommand = new NpgsqlCommand(
                    "UPDATE orders SET allow_partial_outbound = FALSE WHERE id = @id",
                    lockConnection,
                    lockTransaction);
                lockCommand.Parameters.AddWithValue("id", orderId);
                await lockCommand.ExecuteNonQueryAsync();

                var genericWrite = Task.Run(() => store.UpdateOrder(new Order
                {
                    Id = staleGenericOrder.Id,
                    OrderRef = staleGenericOrder.OrderRef,
                    Type = staleGenericOrder.Type,
                    PartnerId = staleGenericOrder.PartnerId,
                    DueDate = staleGenericOrder.DueDate,
                    Status = staleGenericOrder.Status,
                    Comment = "generic-after-permission",
                    UseReservedStock = staleGenericOrder.UseReservedStock,
                    AllowPartialOutbound = true
                }));
                await Task.Delay(100);
                Assert.False(genericWrite.IsCompleted);
                await lockTransaction.CommitAsync();
                await genericWrite;
            }
            Assert.False(store.GetOrder(orderId)!.AllowPartialOutbound);

            using var allFalseConnection = new NpgsqlConnection(connectionString);
            allFalseConnection.Open();
            using (var compatible = new NpgsqlCommand(
                       "UPDATE orders SET status = 'SHIPPED' WHERE id = @id",
                       allFalseConnection))
            {
                compatible.Parameters.AddWithValue("id", orderId);
                Assert.Equal(1, compatible.ExecuteNonQuery());
            }
            using (var reactivate = new NpgsqlCommand(
                       "UPDATE orders SET status = 'IN_PROGRESS', allow_partial_outbound = TRUE WHERE id = @id",
                       allFalseConnection))
            {
                reactivate.Parameters.AddWithValue("id", orderId);
                reactivate.ExecuteNonQuery();
            }
            using var incompatible = new NpgsqlCommand(
                "UPDATE orders SET status = 'SHIPPED' WHERE id = @id",
                allFalseConnection);
            incompatible.Parameters.AddWithValue("id", orderId);
            var checkFailure = Assert.Throws<PostgresException>(() => incompatible.ExecuteNonQuery());
            Assert.Equal(PostgresErrorCodes.CheckViolation, checkFailure.SqlState);
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(connectionString);
            await cleanup.OpenAsync();
            await using var command = new NpgsqlCommand("DELETE FROM orders WHERE id = @id", cleanup);
            command.Parameters.AddWithValue("id", orderId);
            await command.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public void OptimizedSql_MatchesOutboundEligibilityParityMatrix()
    {
        var connectionString = RequirePostgresTestConnectionString();

        RunInRollbackTransaction(connectionString, store =>
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var locationId = store.AddLocation(new Location { Code = $"TSD-{suffix}", Name = "TSD parity" });
            var partnerId = store.AddPartner(new Partner { Code = $"C-{suffix}", Name = "TSD parity customer", CreatedAt = DateTime.UtcNow });
            var itemA = store.AddItem(new Item { Name = $"TSD parity item A {suffix}", Barcode = $"A{suffix}" });
            var itemB = store.AddItem(new Item { Name = $"TSD parity item B {suffix}", Barcode = $"B{suffix}" });
            var stockDocId = store.AddDoc(new Doc
            {
                DocRef = $"IN-TSD-PARITY-{suffix}",
                Type = DocType.Inbound,
                Status = DocStatus.Closed,
                CreatedAt = DateTime.UtcNow,
                ClosedAt = DateTime.UtcNow
            });

            var sequence = 0;
            (long OrderId, long ReadyLineId, string Hu) AddPartialOrder(
                string key,
                bool permission,
                bool withReadyHu = true,
                OrderStatus status = OrderStatus.InProgress)
            {
                var orderId = store.AddOrder(new Order
                {
                    OrderRef = $"TSD-{key}-{suffix}-{++sequence}",
                    Type = OrderType.Customer,
                    PartnerId = partnerId,
                    Status = status,
                    CreatedAt = DateTime.UtcNow
                });
                var readyLineId = store.AddOrderLine(new OrderLine
                {
                    OrderId = orderId,
                    ItemId = itemA,
                    QtyOrdered = 5,
                    ProductionPurpose = ProductionLinePurpose.CustomerOrder
                });
                store.AddOrderLine(new OrderLine
                {
                    OrderId = orderId,
                    ItemId = itemB,
                    QtyOrdered = 5,
                    ProductionPurpose = ProductionLinePurpose.CustomerOrder
                });
                var hu = $"HU-{key}-{suffix}".ToUpperInvariant();
                if (withReadyHu)
                {
                    store.ReplaceOrderReceiptPlanLines(orderId,
                    [
                        new OrderReceiptPlanLine
                        {
                            OrderId = orderId,
                            OrderLineId = readyLineId,
                            ItemId = itemA,
                            QtyPlanned = 5,
                            ToLocationId = locationId,
                            ToHu = hu
                        }
                    ]);
                    store.AddLedgerEntry(new LedgerEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        DocId = stockDocId,
                        ItemId = itemA,
                        LocationId = locationId,
                        QtyDelta = 5,
                        HuCode = hu
                    });
                }
                if (permission)
                {
                    store.UpdateOrderPartialOutboundPermission(orderId, true);
                }
                return (orderId, readyLineId, hu);
            }

            var permissionOff = AddPartialOrder("OFF", permission: false);
            var permissionOn = AddPartialOrder("ON", permission: true);
            var noReadyHu = AddPartialOrder("NO-READY", permission: true, withReadyHu: false);
            var activeControl = AddPartialOrder("CONTROL", permission: true);
            var foreignOwner = AddPartialOrder("FOREIGN-OWNER", permission: false, withReadyHu: false);
            var foreignDraft = AddPartialOrder("FOREIGN", permission: true);
            var nullableForeignDraft = AddPartialOrder("FOREIGN-NULL", permission: true);
            var continuation = AddPartialOrder("CONTINUATION", permission: false);

            var taskId = store.AddOrderControlTask(new OrderControlTask
            {
                TaskRef = $"CTRL-{suffix}",
                Status = OrderControlTaskStatus.New,
                CreatedAt = DateTime.UtcNow,
                ExpectedHuCount = 1,
                SnapshotHash = suffix
            });
            store.AddOrderControlTaskOrder(new OrderControlTaskOrder
            {
                TaskId = taskId,
                OrderId = activeControl.OrderId,
                OrderRef = $"TSD-CONTROL-{suffix}",
                IsActive = true
            });

            void AddForeignDraft(long? ownerOrderId, string hu, string key)
            {
                var docId = store.AddDoc(new Doc
                {
                    DocRef = $"OUT-{key}-{suffix}",
                    Type = DocType.Outbound,
                    Status = DocStatus.Draft,
                    OrderId = ownerOrderId,
                    CreatedAt = DateTime.UtcNow
                });
                store.AddDocLine(new DocLine
                {
                    DocId = docId,
                    ItemId = itemA,
                    Qty = 5,
                    FromLocationId = locationId,
                    FromHu = hu
                });
            }

            AddForeignDraft(foreignOwner.OrderId, foreignDraft.Hu, "FOREIGN");
            AddForeignDraft(null, nullableForeignDraft.Hu, "FOREIGN-NULL");
            var continuationDocId = store.AddDoc(new Doc
            {
                DocRef = $"OUT-CONT-{suffix}",
                Type = DocType.Outbound,
                Status = DocStatus.Draft,
                OrderId = continuation.OrderId,
                OrderRef = $"TSD-CONTINUATION-{suffix}",
                Comment = "TSD OUTBOUND PICKING (TSD-PARITY)",
                CreatedAt = DateTime.UtcNow
            });
            store.AddDocLine(new DocLine
            {
                DocId = continuationDocId,
                OrderLineId = continuation.ReadyLineId,
                ItemId = itemA,
                Qty = 5,
                FromLocationId = locationId,
                FromHu = continuation.Hu
            });

            var mixedOrderId = store.AddOrder(new Order
            {
                OrderRef = $"TSD-MIXED-{suffix}",
                Type = OrderType.Customer,
                PartnerId = partnerId,
                Status = OrderStatus.InProgress,
                CreatedAt = DateTime.UtcNow
            });
            var mixedLineA = store.AddOrderLine(new OrderLine
            {
                OrderId = mixedOrderId,
                ItemId = itemA,
                QtyOrdered = 3,
                ProductionPurpose = ProductionLinePurpose.CustomerOrder,
                ProductionPalletGroup = $"MIX-{suffix}"
            });
            var mixedLineB = store.AddOrderLine(new OrderLine
            {
                OrderId = mixedOrderId,
                ItemId = itemB,
                QtyOrdered = 2,
                ProductionPurpose = ProductionLinePurpose.CustomerOrder,
                ProductionPalletGroup = $"MIX-{suffix}"
            });
            store.UpdateOrderPartialOutboundPermission(mixedOrderId, true);
            var mixedHu = $"HU-MIXED-{suffix}".ToUpperInvariant();
            var prdId = store.AddDoc(new Doc
            {
                DocRef = $"PRD-MIXED-{suffix}",
                Type = DocType.ProductionReceipt,
                Status = DocStatus.Draft,
                OrderId = mixedOrderId,
                OrderRef = $"TSD-MIXED-{suffix}",
                CreatedAt = DateTime.UtcNow
            });
            store.AddDocLine(new DocLine
            {
                DocId = prdId,
                OrderLineId = mixedLineA,
                ItemId = itemA,
                Qty = 3,
                ToLocationId = locationId,
                ToHu = mixedHu,
                PackSingleHu = true
            });
            store.AddDocLine(new DocLine
            {
                DocId = prdId,
                OrderLineId = mixedLineB,
                ItemId = itemB,
                Qty = 2,
                ToLocationId = locationId,
                ToHu = mixedHu,
                PackSingleHu = true
            });
            var mixedPallet = Assert.Single(store.PlanProductionPallets(prdId, DateTime.UtcNow));
            store.MarkProductionPalletFilled(mixedPallet.Id, DateTime.UtcNow, "TSD-PARITY");
            store.AddLedgerEntry(new LedgerEntry
            {
                Timestamp = DateTime.UtcNow,
                DocId = prdId,
                ItemId = itemA,
                LocationId = locationId,
                QtyDelta = 3,
                HuCode = mixedHu
            });

            var terminal = AddPartialOrder(
                "TERMINAL",
                permission: false,
                status: OrderStatus.Shipped);

            var optimized = Assert.IsAssignableFrom<IOptimizedTsdOutboundPickingStore>(store);
            var beforeCompleteMixedLedger = optimized.GetTsdOutboundOrderRows();
            Assert.DoesNotContain(beforeCompleteMixedLedger, row => row.OrderId == mixedOrderId);

            store.AddLedgerEntry(new LedgerEntry
            {
                Timestamp = DateTime.UtcNow,
                DocId = prdId,
                ItemId = itemB,
                LocationId = locationId,
                QtyDelta = 2,
                HuCode = mixedHu
            });

            var rows = optimized.GetTsdOutboundOrderRows();
            var actualIds = rows.Select(row => row.OrderId).ToHashSet();
            var expectedVisibleIds = new HashSet<long>
            {
                permissionOn.OrderId,
                continuation.OrderId,
                mixedOrderId
            };

            Assert.True(expectedVisibleIds.SetEquals(actualIds.Intersect(
                new[]
                {
                    permissionOff.OrderId,
                    permissionOn.OrderId,
                    noReadyHu.OrderId,
                    activeControl.OrderId,
                    foreignDraft.OrderId,
                    nullableForeignDraft.OrderId,
                    continuation.OrderId,
                    mixedOrderId,
                    terminal.OrderId
                })));

            var permitted = Assert.Single(rows, row => row.OrderId == permissionOn.OrderId);
            Assert.True(permitted.AllowPartialOutbound);
            Assert.Equal(1, permitted.ExpectedHuCount);

            var continuationRow = Assert.Single(rows, row => row.OrderId == continuation.OrderId);
            Assert.False(continuationRow.AllowPartialOutbound);
            Assert.Equal(1, continuationRow.ExpectedHuCount);
            Assert.Equal(1, continuationRow.PickedHuCount);

            var mixed = Assert.Single(rows, row => row.OrderId == mixedOrderId);
            Assert.True(mixed.AllowPartialOutbound);
            Assert.Equal(1, mixed.ExpectedHuCount);

            Assert.DoesNotContain(rows, row => row.OrderId == permissionOff.OrderId);
            Assert.DoesNotContain(rows, row => row.OrderId == noReadyHu.OrderId);
            Assert.DoesNotContain(rows, row => row.OrderId == activeControl.OrderId);
            Assert.DoesNotContain(rows, row => row.OrderId == foreignDraft.OrderId);
            Assert.DoesNotContain(rows, row => row.OrderId == nullableForeignDraft.OrderId);
            Assert.DoesNotContain(rows, row => row.OrderId == terminal.OrderId);
            Assert.False(store.GetOrder(terminal.OrderId)!.AllowPartialOutbound);
        });
    }

    private static string RequirePostgresTestConnectionString() =>
        ResolvePostgresTestConnectionString()
        ?? throw new InvalidOperationException(
            "PostgreSQL test connection disappeared after test discovery.");

    private static void RunInRollbackTransaction(string connectionString, Action<IDataStore> work)
    {
        var store = new PostgresDataStore(connectionString);
        store.Initialize();
        var exception = Record.Exception(() => store.ExecuteInTransaction(scoped =>
        {
            work(scoped);
            throw new RollbackRequestedException();
        }));
        if (exception is RollbackRequestedException)
        {
            return;
        }
        if (exception != null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
        Assert.Fail("Rollback transaction did not request rollback.");
    }

    internal static string? ResolvePostgresTestConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("FLOWSTOCK_POSTGRES_TEST_CONNECTION");
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed class RollbackRequestedException : Exception;
}

internal sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (TsdOutboundEligibilityPostgresTests.ResolvePostgresTestConnectionString() == null)
        {
            Skip = "PostgreSQL test connection is unavailable. Set FLOWSTOCK_POSTGRES_TEST_CONNECTION or start an isolated local test DB.";
        }
    }
}
