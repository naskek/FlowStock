using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;
using Npgsql;

namespace FlowStock.Server.Tests.ProductionPallets;

/// <summary>
/// Live PostgreSQL concurrency proofs for the explicit pallet plan confirm:
/// (1) two independent connections confirming the same preview delta produce exactly one
/// plan — the loser gets PLAN_PREVIEW_STALE, PRD/HU exist once, ledger is untouched;
/// (2) an order update transaction holds the orders row lock, a parallel confirm waits
/// for commit and never plans against the stale quantity.
///
/// Env-gated like the marking cutover suite: requires FLOWSTOCK_RUN_MUTATING_POSTGRES_TESTS=1
/// and a dedicated FLOWSTOCK_PRODUCTION_PALLET_TEST_CONNECTION pointing at a disposable
/// database named 'flowstock_production_pallet_test' (or with that prefix). No fallback to
/// shared connection variables.
/// </summary>
public sealed class ProductionPalletConfirmConcurrencyPostgresTests
{
    private const string DisposableDatabaseName = "flowstock_production_pallet_test";

    [Fact]
    public void TwoParallelConfirms_OneWins_SecondGetsStale_SinglePlanAndNoLedger()
    {
        RunOnDisposableDatabase((connectionString, seed) =>
        {
            var previewStore = new PostgresDataStore(connectionString);
            var preview = new ProductionPalletService(previewStore).BuildPlanPreview(seed.OrderId);
            Assert.True(preview.ProductionRequired);
            var request = BuildFullDeltaRequest(preview);

            using var barrier = new Barrier(2);
            var results = new Exception?[2];
            var tasks = Enumerable.Range(0, 2)
                .Select(index => Task.Run(() =>
                {
                    var service = new ProductionPalletService(new PostgresDataStore(connectionString));
                    barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                    try
                    {
                        service.ConfirmExplicitPlan(seed.OrderId, request);
                    }
                    catch (Exception ex)
                    {
                        results[index] = ex;
                    }
                }))
                .ToArray();
            Assert.True(Task.WaitAll(tasks, TimeSpan.FromSeconds(60)), "Confirm tasks did not finish in time.");

            var failures = results.Where(result => result != null).ToArray();
            Assert.Single(failures);
            var stale = Assert.IsType<ProductionPalletPlanException>(failures[0]);
            Assert.Equal(ProductionPalletPlanErrorCodes.PlanPreviewStale, stale.ErrorCode);

            var verifyStore = new PostgresDataStore(connectionString);
            var prdDocs = verifyStore.GetDocsByOrder(seed.OrderId)
                .Where(doc => doc.Type == DocType.ProductionReceipt)
                .ToArray();
            var prd = Assert.Single(prdDocs);
            var pallets = verifyStore.GetProductionPalletsByDoc(prd.Id)
                .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.Equal(request.Pallets.Count, pallets.Length);
            Assert.Equal(pallets.Length, pallets.Select(pallet => pallet.HuCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(0, verifyStore.CountLedgerEntriesByDocId(prd.Id));
        });
    }

    [Fact]
    public void OrderUpdateHoldsRowLock_ParallelConfirmWaits_AndDoesNotPlanOnStaleQty()
    {
        RunOnDisposableDatabase((connectionString, seed) =>
        {
            var previewStore = new PostgresDataStore(connectionString);
            var preview = new ProductionPalletService(previewStore).BuildPlanPreview(seed.OrderId);
            var request = BuildFullDeltaRequest(preview);

            // Simulate OrderService.UpdateOrder: UPDATE orders first (row lock), then order_lines.
            using var updateConnection = new NpgsqlConnection(connectionString);
            updateConnection.Open();
            using var updateTransaction = updateConnection.BeginTransaction();
            Execute(updateConnection, $"UPDATE orders SET comment = 'lock-test' WHERE id = {seed.OrderId};");
            Execute(updateConnection,
                $"UPDATE order_lines SET qty_ordered = qty_ordered + 500, revision = revision + 1 WHERE id = {seed.FirstOrderLineId};");

            Exception? confirmOutcome = null;
            var confirmTask = Task.Run(() =>
            {
                try
                {
                    new ProductionPalletService(new PostgresDataStore(connectionString))
                        .ConfirmExplicitPlan(seed.OrderId, request);
                }
                catch (Exception ex)
                {
                    confirmOutcome = ex;
                }
            });

            // While the update transaction holds the orders row lock, confirm must wait.
            Assert.False(confirmTask.Wait(TimeSpan.FromSeconds(2)), "Confirm did not wait for the orders row lock.");

            updateTransaction.Commit();
            Assert.True(confirmTask.Wait(TimeSpan.FromSeconds(60)), "Confirm did not finish after the lock was released.");

            // After the committed qty change the fingerprint no longer matches — no plan on stale qty.
            var stale = Assert.IsType<ProductionPalletPlanException>(confirmOutcome);
            Assert.Equal(ProductionPalletPlanErrorCodes.PlanPreviewStale, stale.ErrorCode);

            var verifyStore = new PostgresDataStore(connectionString);
            Assert.DoesNotContain(
                verifyStore.GetDocsByOrder(seed.OrderId),
                doc => doc.Type == DocType.ProductionReceipt);
        });
    }

    [Fact]
    public void CustomerHuBindingParallelToConfirm_NeverProducesDoubleCoverage()
    {
        if (!MutatingPostgresTestsEnabled())
        {
            return;
        }

        var connectionString = ResolveConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        EnsureDisposableDatabase(connection);
        // Зеркалим старт сервера: EnsureSchemaReady добавляет runtime-колонки поверх миграций.
        new PostgresDataStore(connectionString).Initialize();
        CleanupTestRows(connection);

        try
        {
            // Реальная гонка: настоящий ApplyFinal и настоящий ConfirmExplicitPlan стартуют
            // одновременно на независимых соединениях. Несколько итераций повышают
            // вероятность коллизии; инвариант обязан выполняться при любом переплетении.
            for (var iteration = 0; iteration < 3; iteration++)
            {
                var seedStore = new PostgresDataStore(connectionString);
                EnsureLocation(seedStore);
                var itemTypeId = seedStore.AddItemType(new ItemType
                {
                    Name = $"PPLT-CONC Тип {DateTime.UtcNow.Ticks % 1000000}-{iteration}",
                    Code = $"PPLT-CONC-{iteration}",
                    IsActive = true,
                    EnableOrderReservation = true
                });
                var itemId = seedStore.AddItem(new Item
                {
                    Name = $"PPLT-CONC Хрен биндинг {iteration}",
                    BaseUom = "шт",
                    MaxQtyPerHu = 600,
                    ItemTypeId = itemTypeId
                });
                var partnerId = seedStore.AddPartner(new Partner
                {
                    Name = $"PPLT-CONC Клиент {iteration}",
                    Code = $"PPLT-CONC-P{DateTime.UtcNow.Ticks % 1000000}-{iteration}"
                });
                var orderId = seedStore.AddOrder(new Order
                {
                    OrderRef = $"PPLT-CONC-B{DateTime.UtcNow.Ticks % 1000000}-{iteration}",
                    Type = OrderType.Customer,
                    PartnerId = partnerId,
                    Status = OrderStatus.InProgress,
                    UseReservedStock = true,
                    CreatedAt = DateTime.UtcNow
                });
                var orderLineId = seedStore.AddOrderLine(new OrderLine
                {
                    OrderId = orderId,
                    ItemId = itemId,
                    QtyOrdered = 600,
                    ProductionPurpose = ProductionLinePurpose.CustomerOrder
                });

                // Свободный складской HU c ledger stock — кандидат LEDGER_STOCK для биндинга.
                var warehouseHu = $"PPLT-CONC-WH{DateTime.UtcNow.Ticks % 1000000}-{iteration}";
                var locationId = seedStore.GetLocations().First().Id;
                var inboundDocId = seedStore.AddDoc(new Doc
                {
                    DocRef = $"PPLT-CONC-INB{DateTime.UtcNow.Ticks % 1000000}-{iteration}",
                    Type = DocType.Inbound,
                    Status = DocStatus.Closed,
                    CreatedAt = DateTime.UtcNow
                });
                seedStore.AddLedgerEntry(new LedgerEntry
                {
                    Timestamp = DateTime.UtcNow,
                    DocId = inboundDocId,
                    ItemId = itemId,
                    LocationId = locationId,
                    QtyDelta = 600,
                    HuCode = warehouseHu
                });

                var preview = new ProductionPalletService(new PostgresDataStore(connectionString)).BuildPlanPreview(orderId);
                Assert.True(preview.ProductionRequired);
                var confirmRequest = BuildFullDeltaRequest(preview);

                using var barrier = new Barrier(2);
                Exception? confirmError = null;
                Exception? bindingError = null;
                var confirmTask = Task.Run(() =>
                {
                    var service = new ProductionPalletService(new PostgresDataStore(connectionString));
                    barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                    try
                    {
                        service.ConfirmExplicitPlan(orderId, confirmRequest);
                    }
                    catch (Exception ex)
                    {
                        confirmError = ex;
                    }
                });
                var bindingTask = Task.Run(() =>
                {
                    var service = new OrderHuBindingApplyFinalService(new PostgresDataStore(connectionString));
                    barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                    try
                    {
                        service.ApplyFinal(orderId, new OrderHuBindingApplyFinalRequest
                        {
                            Mode = OrderHuBindingApplyFinalRequest.ReplaceFinalSelectionMode,
                            Lines =
                            [
                                new OrderHuBindingApplyFinalLineRequest
                                {
                                    OrderLineId = orderLineId,
                                    ExpectedBoundHuCodes = [],
                                    FinalHuCodes = [warehouseHu]
                                }
                            ]
                        });
                    }
                    catch (Exception ex)
                    {
                        bindingError = ex;
                    }
                });
                Assert.True(Task.WaitAll([confirmTask, bindingTask], TimeSpan.FromSeconds(60)), "Race tasks did not finish in time.");

                // Биндинг свободного HU обязан пройти при любом порядке; confirm — успех или stale.
                Assert.True(bindingError == null, $"iteration {iteration}: binding failed: {bindingError}");
                if (confirmError != null)
                {
                    var stale = Assert.IsType<ProductionPalletPlanException>(confirmError);
                    Assert.Equal(ProductionPalletPlanErrorCodes.PlanPreviewStale, stale.ErrorCode);
                }

                var verifyStore = new PostgresDataStore(connectionString);
                var boundQty = verifyStore.GetOrderReceiptPlanLines(orderId)
                    .Where(line => line.OrderLineId == orderLineId)
                    .Sum(line => line.QtyPlanned);
                var activePlannedQty = verifyStore.GetDocsByOrder(orderId)
                    .Where(doc => doc.Type == DocType.ProductionReceipt)
                    .SelectMany(doc => verifyStore.GetProductionPalletsByDoc(doc.Id))
                    .Where(pallet => string.Equals(pallet.Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(pallet.Status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase))
                    .Sum(pallet => pallet.Lines.Count > 0
                        ? pallet.Lines.Where(line => line.OrderLineId == orderLineId).Sum(line => line.PlannedQty)
                        : pallet.OrderLineId == orderLineId ? pallet.PlannedQty : 0);

                Assert.Equal(600, boundQty, 3);
                Assert.True(
                    boundQty + activePlannedQty <= 600 + 0.000001d,
                    $"iteration {iteration}: double coverage: bound={boundQty}, active planned={activePlannedQty}");
                if (confirmError != null)
                {
                    Assert.Equal(0, activePlannedQty, 3);
                }
            }
        }
        finally
        {
            CleanupTestRows(connection);
        }
    }

    [Fact]
    public void RedistributionParallelToConfirm_NeverPlansAgainstStaleSourceQty()
    {
        if (!MutatingPostgresTestsEnabled())
        {
            return;
        }

        var connectionString = ResolveConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        EnsureDisposableDatabase(connection);
        // Зеркалим старт сервера: EnsureSchemaReady добавляет runtime-колонки поверх миграций.
        new PostgresDataStore(connectionString).Initialize();
        CleanupTestRows(connection);

        try
        {
            for (var iteration = 0; iteration < 3; iteration++)
            {
                var seedStore = new PostgresDataStore(connectionString);
                EnsureLocation(seedStore);
                var itemId = seedStore.AddItem(new Item
                {
                    Name = $"PPLT-CONC Хрен перенос {iteration}",
                    BaseUom = "шт",
                    MaxQtyPerHu = 600
                });
                var partnerId = seedStore.AddPartner(new Partner
                {
                    Name = $"PPLT-CONC Клиент-Т {iteration}",
                    Code = $"PPLT-CONC-PT{DateTime.UtcNow.Ticks % 1000000}-{iteration}"
                });
                var sourceOrderId = seedStore.AddOrder(new Order
                {
                    OrderRef = $"PPLT-CONC-S{DateTime.UtcNow.Ticks % 1000000}-{iteration}",
                    Type = OrderType.Internal,
                    Status = OrderStatus.InProgress,
                    CreatedAt = DateTime.UtcNow
                });
                var sourceLineId = seedStore.AddOrderLine(new OrderLine
                {
                    OrderId = sourceOrderId,
                    ItemId = itemId,
                    QtyOrdered = 600
                });
                var targetOrderId = seedStore.AddOrder(new Order
                {
                    OrderRef = $"PPLT-CONC-T{DateTime.UtcNow.Ticks % 1000000}-{iteration}",
                    Type = OrderType.Customer,
                    PartnerId = partnerId,
                    Status = OrderStatus.InProgress,
                    UseReservedStock = false,
                    CreatedAt = DateTime.UtcNow
                });

                var preview = new ProductionPalletService(new PostgresDataStore(connectionString)).BuildPlanPreview(sourceOrderId);
                var confirmRequest = BuildFullDeltaRequest(preview);

                using var barrier = new Barrier(2);
                Exception? confirmError = null;
                Exception? redistributionError = null;
                var confirmTask = Task.Run(() =>
                {
                    var service = new ProductionPalletService(new PostgresDataStore(connectionString));
                    barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                    try
                    {
                        service.ConfirmExplicitPlan(sourceOrderId, confirmRequest);
                    }
                    catch (Exception ex)
                    {
                        confirmError = ex;
                    }
                });
                var redistributionTask = Task.Run(() =>
                {
                    var service = new OrderRedistributionService(new PostgresDataStore(connectionString));
                    barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                    try
                    {
                        service.Redistribute(sourceOrderId, targetOrderId, itemId, 600);
                    }
                    catch (Exception ex)
                    {
                        redistributionError = ex;
                    }
                });
                Assert.True(Task.WaitAll([confirmTask, redistributionTask], TimeSpan.FromSeconds(60)), "Race tasks did not finish in time.");

                // Confirm: успех или структурный stale/not-plannable; redistribution: успех или guard-блок.
                if (confirmError != null)
                {
                    var planError = Assert.IsType<ProductionPalletPlanException>(confirmError);
                    Assert.True(
                        planError.ErrorCode is ProductionPalletPlanErrorCodes.PlanPreviewStale
                            or ProductionPalletPlanErrorCodes.OrderNotPlannable,
                        $"iteration {iteration}: unexpected confirm error: {planError.ErrorCode}");
                }

                if (redistributionError != null)
                {
                    Assert.IsType<InvalidOperationException>(redistributionError);
                }

                // Инвариант: активный план на строке источника не превышает её актуальное количество.
                var verifyStore = new PostgresDataStore(connectionString);
                var sourceQtyAfter = verifyStore.GetOrderLines(sourceOrderId)
                    .Where(line => line.Id == sourceLineId)
                    .Sum(line => line.QtyOrdered);
                var activePlannedQty = verifyStore.GetDocsByOrder(sourceOrderId)
                    .Where(doc => doc.Type == DocType.ProductionReceipt)
                    .SelectMany(doc => verifyStore.GetProductionPalletsByDoc(doc.Id))
                    .Where(pallet => string.Equals(pallet.Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(pallet.Status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase))
                    .Sum(pallet => pallet.Lines.Count > 0
                        ? pallet.Lines.Where(line => line.OrderLineId == sourceLineId).Sum(line => line.PlannedQty)
                        : pallet.OrderLineId == sourceLineId ? pallet.PlannedQty : 0);

                Assert.True(
                    activePlannedQty <= sourceQtyAfter + 0.000001d,
                    $"iteration {iteration}: plan against stale qty: source qty={sourceQtyAfter}, active planned={activePlannedQty}");
            }
        }
        finally
        {
            CleanupTestRows(connection);
        }
    }

    [Fact]
    public void OrderHuReservationApplyParallelToConfirm_NeverProducesDoubleCoverage()
    {
        RunWarehouseHuCoverageRaceAgainstConfirm("RA", (store, ctx) =>
            new OrderHuReservationApplyService(store).Apply(ctx.OrderId, new OrderHuReservationApplyRequest
            {
                Lines =
                [
                    new OrderHuReservationApplyLineRequest
                    {
                        OrderLineId = ctx.OrderLineId,
                        SelectedHuCodes = [ctx.WarehouseHu]
                    }
                ]
            }));
    }

    [Fact]
    public void OrderHuBindingManageApplyParallelToConfirm_NeverProducesDoubleCoverage()
    {
        RunWarehouseHuCoverageRaceAgainstConfirm("BM", (store, ctx) =>
            new OrderHuBindingManageApplyService(store).ApplyFinal(new OrderHuBindingManageApplyRequest
            {
                Mode = OrderHuBindingManageApplyRequest.ReplaceFinalSelectionMode,
                ExpectedHuStates =
                [
                    new ManageExpectedHuState
                    {
                        HuCode = ctx.WarehouseHu,
                        ItemId = ctx.ItemId,
                        ExpectedQty = 600,
                        ExpectedOrderId = null,
                        ExpectedOrderLineId = null
                    }
                ],
                Lines =
                [
                    new OrderHuBindingManageApplyLineRequest
                    {
                        OrderId = ctx.OrderId,
                        OrderLineId = ctx.OrderLineId,
                        ExpectedBoundHuCodes = [],
                        FinalHuCodes = [ctx.WarehouseHu]
                    }
                ]
            }));
    }

    [Fact]
    public void OrderProducedHuReservationParallelToConfirm_NeverProducesDoubleCoverage()
    {
        if (!MutatingPostgresTestsEnabled())
        {
            return;
        }

        var connectionString = ResolveConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        EnsureDisposableDatabase(connection);
        new PostgresDataStore(connectionString).Initialize();
        CleanupTestRows(connection);

        try
        {
            for (var iteration = 0; iteration < 3; iteration++)
            {
                var seedStore = new PostgresDataStore(connectionString);
                EnsureLocation(seedStore);
                var locationId = seedStore.GetLocations().First().Id;
                var itemTypeId = seedStore.AddItemType(new ItemType
                {
                    Name = $"PPLT-CONC Тип-P {DateTime.UtcNow.Ticks % 1000000}-{iteration}",
                    Code = $"PPLT-CONC-TP{iteration}",
                    IsActive = true,
                    EnableOrderReservation = true
                });
                var itemId = seedStore.AddItem(new Item
                {
                    Name = $"PPLT-CONC Хрен готовый {iteration}",
                    BaseUom = "шт",
                    MaxQtyPerHu = 600,
                    ItemTypeId = itemTypeId
                });

                // INTERNAL источник с FILLED-паллетой (готовый HU на складе, происхождение — источник).
                var sourceOrderId = seedStore.AddOrder(new Order
                {
                    OrderRef = $"PPLT-CONC-PS{DateTime.UtcNow.Ticks % 1000000}-{iteration}",
                    Type = OrderType.Internal,
                    Status = OrderStatus.InProgress,
                    CreatedAt = DateTime.UtcNow
                });
                var sourceLineId = seedStore.AddOrderLine(new OrderLine
                {
                    OrderId = sourceOrderId,
                    ItemId = itemId,
                    QtyOrdered = 600
                });
                var producedHu = $"PPLT-CONC-PH{DateTime.UtcNow.Ticks % 1000000}-{iteration}";
                var prdDocId = seedStore.AddDoc(new Doc
                {
                    DocRef = $"PPLT-CONC-PPRD{DateTime.UtcNow.Ticks % 1000000}-{iteration}",
                    Type = DocType.ProductionReceipt,
                    Status = DocStatus.Draft,
                    OrderId = sourceOrderId,
                    CreatedAt = DateTime.UtcNow
                });
                seedStore.AddDocLine(new DocLine
                {
                    DocId = prdDocId,
                    OrderLineId = sourceLineId,
                    ItemId = itemId,
                    Qty = 600,
                    ToLocationId = locationId,
                    ToHu = producedHu,
                    PackSingleHu = true
                });
                var filledPallet = seedStore.PlanProductionPallets(prdDocId, DateTime.UtcNow).Single();
                seedStore.MarkProductionPalletFilled(filledPallet.Id, DateTime.UtcNow, "TEST");
                seedStore.AddLedgerEntry(new LedgerEntry
                {
                    Timestamp = DateTime.UtcNow,
                    DocId = prdDocId,
                    ItemId = itemId,
                    LocationId = locationId,
                    QtyDelta = 600,
                    HuCode = producedHu
                });

                // CUSTOMER-получатель с производственной потребностью 600 и включённым резервом.
                var partnerId = seedStore.AddPartner(new Partner
                {
                    Name = $"PPLT-CONC Клиент-P {iteration}",
                    Code = $"PPLT-CONC-PP{DateTime.UtcNow.Ticks % 1000000}-{iteration}"
                });
                var targetOrderId = seedStore.AddOrder(new Order
                {
                    OrderRef = $"PPLT-CONC-PT{DateTime.UtcNow.Ticks % 1000000}-{iteration}",
                    Type = OrderType.Customer,
                    PartnerId = partnerId,
                    Status = OrderStatus.InProgress,
                    UseReservedStock = true,
                    CreatedAt = DateTime.UtcNow
                });
                var targetLineId = seedStore.AddOrderLine(new OrderLine
                {
                    OrderId = targetOrderId,
                    ItemId = itemId,
                    QtyOrdered = 600,
                    ProductionPurpose = ProductionLinePurpose.CustomerOrder
                });

                var preview = new ProductionPalletService(new PostgresDataStore(connectionString)).BuildPlanPreview(targetOrderId);
                Assert.True(preview.ProductionRequired);
                var confirmRequest = BuildFullDeltaRequest(preview);

                using var barrier = new Barrier(2);
                Exception? confirmError = null;
                Exception? reserveError = null;
                var confirmTask = Task.Run(() =>
                {
                    var service = new ProductionPalletService(new PostgresDataStore(connectionString));
                    barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                    try
                    {
                        service.ConfirmExplicitPlan(targetOrderId, confirmRequest);
                    }
                    catch (Exception ex)
                    {
                        confirmError = ex;
                    }
                });
                var reserveTask = Task.Run(() =>
                {
                    var service = new OrderProducedHuReservationService(new PostgresDataStore(connectionString));
                    barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                    try
                    {
                        service.Reserve(new OrderProducedHuReservationRequest
                        {
                            SourceInternalOrderId = sourceOrderId,
                            TargetCustomerOrderId = targetOrderId,
                            ItemId = itemId,
                            TargetOrderLineId = targetLineId,
                            HuCodes = [producedHu]
                        });
                    }
                    catch (Exception ex)
                    {
                        reserveError = ex;
                    }
                });
                Assert.True(Task.WaitAll([confirmTask, reserveTask], TimeSpan.FromSeconds(60)), "Race tasks did not finish in time.");

                // Резерв готового HU обязан пройти при любом порядке; confirm — успех или stale.
                Assert.True(reserveError == null, $"iteration {iteration}: reserve failed: {reserveError}");
                if (confirmError != null)
                {
                    var stale = Assert.IsType<ProductionPalletPlanException>(confirmError);
                    Assert.Equal(ProductionPalletPlanErrorCodes.PlanPreviewStale, stale.ErrorCode);
                }

                var verifyStore = new PostgresDataStore(connectionString);
                var boundQty = verifyStore.GetOrderReceiptPlanLines(targetOrderId)
                    .Where(line => line.OrderLineId == targetLineId)
                    .Sum(line => line.QtyPlanned);
                var activePlannedQty = ActivePlannedQty(verifyStore, targetOrderId, targetLineId);
                Assert.Equal(600, boundQty, 3);
                Assert.True(
                    boundQty + activePlannedQty <= 600 + 0.000001d,
                    $"iteration {iteration}: double coverage: bound={boundQty}, active planned={activePlannedQty}");
                if (confirmError != null)
                {
                    Assert.Equal(0, activePlannedQty, 3);
                }
            }
        }
        finally
        {
            CleanupTestRows(connection);
        }
    }

    private sealed record WarehouseRaceContext(long OrderId, long OrderLineId, long ItemId, string WarehouseHu);

    /// <summary>
    /// Общий каркас гонки: реальный operator-facing coverage writer против настоящего
    /// plan-explicit confirm на независимых соединениях. Инвариант: warehouse-bound coverage +
    /// active production coverage не превышает актуальную потребность; confirm либо stale, либо
    /// работает по состоянию после writer.
    /// </summary>
    private void RunWarehouseHuCoverageRaceAgainstConfirm(
        string labelPrefix,
        Action<PostgresDataStore, WarehouseRaceContext> writer)
    {
        if (!MutatingPostgresTestsEnabled())
        {
            return;
        }

        var connectionString = ResolveConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        EnsureDisposableDatabase(connection);
        new PostgresDataStore(connectionString).Initialize();
        CleanupTestRows(connection);

        try
        {
            for (var iteration = 0; iteration < 3; iteration++)
            {
                var context = SeedCustomerOrderWithFreeWarehouseHu(connectionString, labelPrefix, iteration);
                var preview = new ProductionPalletService(new PostgresDataStore(connectionString)).BuildPlanPreview(context.OrderId);
                Assert.True(preview.ProductionRequired);
                var confirmRequest = BuildFullDeltaRequest(preview);

                using var barrier = new Barrier(2);
                Exception? confirmError = null;
                Exception? writerError = null;
                var confirmTask = Task.Run(() =>
                {
                    var service = new ProductionPalletService(new PostgresDataStore(connectionString));
                    barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                    try
                    {
                        service.ConfirmExplicitPlan(context.OrderId, confirmRequest);
                    }
                    catch (Exception ex)
                    {
                        confirmError = ex;
                    }
                });
                var writerTask = Task.Run(() =>
                {
                    var store = new PostgresDataStore(connectionString);
                    barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                    try
                    {
                        writer(store, context);
                    }
                    catch (Exception ex)
                    {
                        writerError = ex;
                    }
                });
                Assert.True(Task.WaitAll([confirmTask, writerTask], TimeSpan.FromSeconds(60)), "Race tasks did not finish in time.");

                Assert.True(writerError == null, $"iteration {iteration}: writer failed: {writerError}");
                if (confirmError != null)
                {
                    var stale = Assert.IsType<ProductionPalletPlanException>(confirmError);
                    Assert.Equal(ProductionPalletPlanErrorCodes.PlanPreviewStale, stale.ErrorCode);
                }

                var verifyStore = new PostgresDataStore(connectionString);
                var boundQty = verifyStore.GetOrderReceiptPlanLines(context.OrderId)
                    .Where(line => line.OrderLineId == context.OrderLineId)
                    .Sum(line => line.QtyPlanned);
                var activePlannedQty = ActivePlannedQty(verifyStore, context.OrderId, context.OrderLineId);
                Assert.Equal(600, boundQty, 3);
                Assert.True(
                    boundQty + activePlannedQty <= 600 + 0.000001d,
                    $"iteration {iteration}: double coverage: bound={boundQty}, active planned={activePlannedQty}");
                if (confirmError != null)
                {
                    Assert.Equal(0, activePlannedQty, 3);
                }
            }
        }
        finally
        {
            CleanupTestRows(connection);
        }
    }

    private static WarehouseRaceContext SeedCustomerOrderWithFreeWarehouseHu(
        string connectionString,
        string labelPrefix,
        int iteration)
    {
        var seedStore = new PostgresDataStore(connectionString);
        EnsureLocation(seedStore);
        var itemTypeId = seedStore.AddItemType(new ItemType
        {
            Name = $"PPLT-CONC Тип {labelPrefix} {DateTime.UtcNow.Ticks % 1000000}-{iteration}",
            Code = $"PPLT-CONC-{labelPrefix}{iteration}",
            IsActive = true,
            EnableOrderReservation = true
        });
        var itemId = seedStore.AddItem(new Item
        {
            Name = $"PPLT-CONC Хрен {labelPrefix} {iteration}",
            BaseUom = "шт",
            MaxQtyPerHu = 600,
            ItemTypeId = itemTypeId
        });
        var partnerId = seedStore.AddPartner(new Partner
        {
            Name = $"PPLT-CONC Клиент {labelPrefix} {iteration}",
            Code = $"PPLT-CONC-P{labelPrefix}{DateTime.UtcNow.Ticks % 1000000}-{iteration}"
        });
        var orderId = seedStore.AddOrder(new Order
        {
            OrderRef = $"PPLT-CONC-{labelPrefix}{DateTime.UtcNow.Ticks % 1000000}-{iteration}",
            Type = OrderType.Customer,
            PartnerId = partnerId,
            Status = OrderStatus.InProgress,
            UseReservedStock = true,
            CreatedAt = DateTime.UtcNow
        });
        var orderLineId = seedStore.AddOrderLine(new OrderLine
        {
            OrderId = orderId,
            ItemId = itemId,
            QtyOrdered = 600,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });

        var warehouseHu = $"PPLT-CONC-WH{labelPrefix}{DateTime.UtcNow.Ticks % 1000000}-{iteration}";
        var locationId = seedStore.GetLocations().First().Id;
        var inboundDocId = seedStore.AddDoc(new Doc
        {
            DocRef = $"PPLT-CONC-INB{labelPrefix}{DateTime.UtcNow.Ticks % 1000000}-{iteration}",
            Type = DocType.Inbound,
            Status = DocStatus.Closed,
            CreatedAt = DateTime.UtcNow
        });
        seedStore.AddLedgerEntry(new LedgerEntry
        {
            Timestamp = DateTime.UtcNow,
            DocId = inboundDocId,
            ItemId = itemId,
            LocationId = locationId,
            QtyDelta = 600,
            HuCode = warehouseHu
        });

        return new WarehouseRaceContext(orderId, orderLineId, itemId, warehouseHu);
    }

    private static double ActivePlannedQty(PostgresDataStore store, long orderId, long orderLineId)
    {
        return store.GetDocsByOrder(orderId)
            .Where(doc => doc.Type == DocType.ProductionReceipt)
            .SelectMany(doc => store.GetProductionPalletsByDoc(doc.Id))
            .Where(pallet => string.Equals(pallet.Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(pallet.Status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase))
            .Sum(pallet => pallet.Lines.Count > 0
                ? pallet.Lines.Where(line => line.OrderLineId == orderLineId).Sum(line => line.PlannedQty)
                : pallet.OrderLineId == orderLineId ? pallet.PlannedQty : 0);
    }

    private static void EnsureLocation(PostgresDataStore store)
    {
        if (store.GetLocations().Count == 0)
        {
            store.AddLocation(new Location { Code = "PPLT-MAIN", Name = "PPLT тестовый склад" });
        }
    }

    private static ProductionPalletExplicitPlanRequest BuildFullDeltaRequest(ProductionPalletPlanPreview preview)
    {
        return new ProductionPalletExplicitPlanRequest(
            preview.PreviewFingerprint,
            preview.SuggestedPallets
                .Select(pallet => new ProductionPalletExplicitPlanPallet(
                    pallet.Components
                        .Select(component => new ProductionPalletExplicitPlanComponent(component.OrderLineId, component.Qty))
                        .ToArray()))
                .ToArray());
    }

    private sealed record Seed(long OrderId, long FirstOrderLineId, long SecondOrderLineId, long ItemAId, long ItemBId);

    private void RunOnDisposableDatabase(Action<string, Seed> work)
    {
        if (!MutatingPostgresTestsEnabled())
        {
            return;
        }

        var connectionString = ResolveConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        EnsureDisposableDatabase(connection);
        // Зеркалим старт сервера: EnsureSchemaReady добавляет runtime-колонки поверх миграций.
        new PostgresDataStore(connectionString).Initialize();
        CleanupTestRows(connection);

        try
        {
            var seedStore = new PostgresDataStore(connectionString);
            if (seedStore.GetLocations().Count == 0)
            {
                seedStore.AddLocation(new Location { Code = "PPLT-MAIN", Name = "PPLT тестовый склад" });
            }

            var itemAId = seedStore.AddItem(new Item { Name = "PPLT-CONC Хрен столовый", BaseUom = "шт", MaxQtyPerHu = 2250 });
            var itemBId = seedStore.AddItem(new Item { Name = "PPLT-CONC Хрен со свёклой", BaseUom = "шт", MaxQtyPerHu = 2250 });
            var orderId = seedStore.AddOrder(new Order
            {
                OrderRef = $"PPLT-CONC-{DateTime.UtcNow.Ticks % 1000000}",
                Type = OrderType.Internal,
                Status = OrderStatus.InProgress,
                CreatedAt = DateTime.UtcNow
            });
            var firstLineId = seedStore.AddOrderLine(new OrderLine { OrderId = orderId, ItemId = itemAId, QtyOrdered = 3375 });
            var secondLineId = seedStore.AddOrderLine(new OrderLine { OrderId = orderId, ItemId = itemBId, QtyOrdered = 1125 });

            work(connectionString, new Seed(orderId, firstLineId, secondLineId, itemAId, itemBId));
        }
        finally
        {
            CleanupTestRows(connection);
        }
    }

    private static bool MutatingPostgresTestsEnabled()
        => string.Equals(
            Environment.GetEnvironmentVariable("FLOWSTOCK_RUN_MUTATING_POSTGRES_TESTS"),
            "1",
            StringComparison.Ordinal);

    // Dedicated connection variable, no fallback to shared/production connection strings.
    private static string? ResolveConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("FLOWSTOCK_PRODUCTION_PALLET_TEST_CONNECTION");
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static void EnsureDisposableDatabase(NpgsqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_database();";
        var databaseName = command.ExecuteScalar() as string ?? string.Empty;

        var isDisposable =
            string.Equals(databaseName, DisposableDatabaseName, StringComparison.Ordinal)
            || databaseName.StartsWith(DisposableDatabaseName + "_", StringComparison.Ordinal);

        if (!isDisposable)
        {
            throw new InvalidOperationException(
                $"Refusing to run a mutating pallet concurrency test against database '{databaseName}'. " +
                $"Point FLOWSTOCK_PRODUCTION_PALLET_TEST_CONNECTION at a disposable database named " +
                $"'{DisposableDatabaseName}' or prefixed with '{DisposableDatabaseName}_'.");
        }
    }

    private static void CleanupTestRows(NpgsqlConnection connection)
    {
        Execute(connection, @"
DELETE FROM ledger WHERE doc_id IN (SELECT id FROM docs WHERE doc_ref LIKE 'PPLT-CONC-INB%');
DELETE FROM order_receipt_plan_lines WHERE order_id IN (SELECT id FROM orders WHERE order_ref LIKE 'PPLT-CONC-%');
DELETE FROM ledger WHERE doc_id IN (SELECT id FROM docs WHERE order_id IN (SELECT id FROM orders WHERE order_ref LIKE 'PPLT-CONC-%'));
DELETE FROM production_pallet_lines WHERE production_pallet_id IN (
    SELECT pp.id FROM production_pallets pp
    JOIN docs d ON d.id = pp.prd_doc_id
    WHERE d.order_id IN (SELECT id FROM orders WHERE order_ref LIKE 'PPLT-CONC-%'));
DELETE FROM hus WHERE hu_code IN (
    SELECT pp.hu_code FROM production_pallets pp
    JOIN docs d ON d.id = pp.prd_doc_id
    WHERE d.order_id IN (SELECT id FROM orders WHERE order_ref LIKE 'PPLT-CONC-%'));
DELETE FROM production_pallets WHERE prd_doc_id IN (
    SELECT id FROM docs WHERE order_id IN (SELECT id FROM orders WHERE order_ref LIKE 'PPLT-CONC-%'));
DELETE FROM doc_lines WHERE doc_id IN (
    SELECT id FROM docs WHERE order_id IN (SELECT id FROM orders WHERE order_ref LIKE 'PPLT-CONC-%'));
DELETE FROM docs WHERE order_id IN (SELECT id FROM orders WHERE order_ref LIKE 'PPLT-CONC-%');
DELETE FROM order_lines WHERE order_id IN (SELECT id FROM orders WHERE order_ref LIKE 'PPLT-CONC-%');
DELETE FROM orders WHERE order_ref LIKE 'PPLT-CONC-%';
DELETE FROM docs WHERE doc_ref LIKE 'PPLT-CONC-INB%';
DELETE FROM items WHERE name LIKE 'PPLT-CONC %';
DELETE FROM item_types WHERE name LIKE 'PPLT-CONC %';
DELETE FROM partners WHERE code LIKE 'PPLT-CONC-P%';");
    }

    private static void Execute(NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
