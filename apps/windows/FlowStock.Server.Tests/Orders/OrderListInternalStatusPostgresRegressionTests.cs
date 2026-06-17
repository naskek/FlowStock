using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services;
using FlowStock.Data;
using FlowStock.Server;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderListInternalStatusPostgresRegressionTests
{
    [Theory]
    [InlineData(ProductionPalletStatus.Planned)]
    [InlineData(ProductionPalletStatus.Printed)]
    public async Task InternalDraft_WithActiveProductionPallets_IsInProgressInOrderList(string palletStatus)
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, scopedStore =>
        {
            EnsureAtLeastOneLocation(scopedStore);
            var fixture = SeedInternalDraft(scopedStore, orderRefSuffix: $"PAL-{palletStatus}-{DateTime.UtcNow.Ticks}", createdAt: DateTime.UtcNow.AddMinutes(-5));
            SeedDraftPrdPallet(scopedStore, fixture, palletStatus);

            var order = ReadSingleOrderPageRow(scopedStore, fixture.OrderRef);
            var json = MapOrderJson(order);

            Assert.Equal(OrderStatus.InProgress, order.Status);
            Assert.Equal("IN_PROGRESS", json.GetProperty("order_status").GetString());
            Assert.Equal("В работе", json.GetProperty("order_status_display").GetString());
            Assert.Equal("В работе", json.GetProperty("status").GetString());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task InternalDraft_WithoutProductionActivity_RemainsDraftInOrderList()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, scopedStore =>
        {
            var fixture = SeedInternalDraft(scopedStore, orderRefSuffix: $"EMPTY-{DateTime.UtcNow.Ticks}", createdAt: DateTime.UtcNow);

            var order = ReadSingleOrderPageRow(scopedStore, fixture.OrderRef);
            var json = MapOrderJson(order);

            Assert.Equal(OrderStatus.Draft, order.Status);
            Assert.Equal("DRAFT", json.GetProperty("order_status").GetString());
            Assert.Equal("Черновик", json.GetProperty("order_status_display").GetString());
            Assert.Equal("Черновик", json.GetProperty("status").GetString());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task InternalDraft_WithFullClosedProductionReceipt_IsShippedInOrderList()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, scopedStore =>
        {
            EnsureAtLeastOneLocation(scopedStore);
            var fixture = SeedInternalDraft(scopedStore, orderRefSuffix: $"FULL-{DateTime.UtcNow.Ticks}", createdAt: DateTime.UtcNow);
            SeedClosedProductionReceipt(scopedStore, fixture, qty: fixture.QtyOrdered);

            var order = ReadSingleOrderPageRow(scopedStore, fixture.OrderRef);
            var json = MapOrderJson(order);

            Assert.Equal(OrderStatus.Shipped, order.Status);
            Assert.Equal("SHIPPED", json.GetProperty("order_status").GetString());
            Assert.Equal("Выполнен", json.GetProperty("order_status_display").GetString());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task OrderList_DefaultOrdering_NoLimitAndPagedKeepRealInternalProductionOrderInCanonicalPosition()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, scopedStore =>
        {
            EnsureAtLeastOneLocation(scopedStore);
            var numericPrefix = DateTime.UtcNow.Ticks.ToString()[^8..];
            var olderInternal = SeedInternalDraft(scopedStore, numericPrefix + "112", new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc));
            SeedDraftPrdPallet(scopedStore, olderInternal, ProductionPalletStatus.Planned);
            SeedCustomerOrder(scopedStore, numericPrefix + "117", new DateTime(2026, 5, 2, 8, 0, 0, DateTimeKind.Utc));

            var pagedRefs = new OrderService(scopedStore)
                .GetOrdersPage(includeInternal: true, numericPrefix, limit: 10, offset: 0)
                .Select(order => order.OrderRef)
                .ToArray();
            var noLimitRefs = OrderPageSortSql.SortOrders(
                    new OrderService(scopedStore).GetOrders()
                        .Where(order => order.OrderRef.Contains(numericPrefix, StringComparison.OrdinalIgnoreCase))
                        .Where(order => order.Status is not (OrderStatus.Cancelled or OrderStatus.Merged)),
                    includeCancelledMerged: false)
                .Select(order => order.OrderRef)
                .ToArray();

            Assert.Equal(noLimitRefs, pagedRefs);
            Assert.Equal(new[] { numericPrefix + "117", numericPrefix + "112" }, pagedRefs);
            Assert.Equal(OrderStatus.InProgress, ReadSingleOrderPageRow(scopedStore, olderInternal.OrderRef).Status);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task OrderList_DefaultOrdering_NoLimitAndPagedSortRealOrdersByNumericOrderRefBeforeCreatedAtAndId()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, scopedStore =>
        {
            var numericPrefix = DateTime.UtcNow.Ticks.ToString()[^8..];

            SeedCustomerOrder(scopedStore, numericPrefix + "117", new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc));
            SeedCustomerOrder(scopedStore, numericPrefix + "116", new DateTime(2026, 5, 2, 8, 0, 0, DateTimeKind.Utc));
            SeedCustomerOrder(scopedStore, numericPrefix + "119", new DateTime(2026, 5, 3, 8, 0, 0, DateTimeKind.Utc));
            SeedCustomerOrder(scopedStore, numericPrefix + "118", new DateTime(2026, 5, 4, 8, 0, 0, DateTimeKind.Utc));

            var pagedRefs = new OrderService(scopedStore)
                .GetOrdersPage(includeInternal: true, numericPrefix, limit: 10, offset: 0)
                .Select(order => order.OrderRef)
                .ToArray();
            var noLimitRefs = OrderPageSortSql.SortOrders(
                    new OrderService(scopedStore).GetOrders()
                        .Where(order => order.OrderRef.Contains(numericPrefix, StringComparison.OrdinalIgnoreCase))
                        .Where(order => order.Status is not (OrderStatus.Cancelled or OrderStatus.Merged)),
                    includeCancelledMerged: false)
                .Select(order => order.OrderRef)
                .ToArray();

            var expectedRefs = new[]
            {
                numericPrefix + "119",
                numericPrefix + "118",
                numericPrefix + "117",
                numericPrefix + "116"
            };
            Assert.Equal(expectedRefs, pagedRefs);
            Assert.Equal(expectedRefs, noLimitRefs);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task OrderListMarkingReadModel_ScopedMarkingCodeAggregates_PreserveCoverageSemantics()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, scopedStore =>
        {
            var prefix = $"MK-{DateTime.UtcNow.Ticks}";
            var itemTypeId = scopedStore.AddItemType(new ItemType
            {
                Name = $"{prefix}-type",
                Code = $"{prefix}-type",
                IsActive = true,
                EnableMarking = true
            });
            var itemId = scopedStore.AddItem(new Item
            {
                Name = $"{prefix}-item",
                BaseUom = "шт",
                ItemTypeId = itemTypeId,
                Gtin = "04607186951520"
            });
            var importId = scopedStore.AddMarkingCodeImport(new MarkingCodeImport
            {
                Id = Guid.NewGuid(),
                OriginalFilename = $"{prefix}.txt",
                StoragePath = $"{prefix}.txt",
                FileHash = Guid.NewGuid().ToString("N"),
                SourceType = "test",
                Status = MarkingCodeImportStatus.Bound,
                CreatedAt = DateTime.UtcNow,
                ProcessedAt = DateTime.UtcNow
            });

            var covered = SeedMarkableOrder(scopedStore, $"{prefix}-covered", itemId, qty: 2);
            var coveredTask = AddMarkingOrder(scopedStore, covered.OrderId, itemId, requestedQty: 2);
            AddMarkingCode(scopedStore, importId, coveredTask, "COVERED-R", MarkingCodeStatus.Reserved, gtin: null);
            AddMarkingCode(scopedStore, importId, coveredTask, "COVERED-P", MarkingCodeStatus.Printed, gtin: "04607186951520");

            var missing = SeedMarkableOrder(scopedStore, $"{prefix}-missing", itemId, qty: 2);
            var missingTask = AddMarkingOrder(scopedStore, missing.OrderId, itemId, requestedQty: 2);
            AddMarkingCode(scopedStore, importId, missingTask, "MISSING-V", MarkingCodeStatus.Voided, gtin: "04607186951520");
            var other = SeedMarkableOrder(scopedStore, $"{prefix}-other", itemId, qty: 10);
            var otherTask = AddMarkingOrder(scopedStore, other.OrderId, itemId, requestedQty: 10);
            AddMarkingCode(scopedStore, importId, otherTask, "OTHER-R", MarkingCodeStatus.Reserved, gtin: "04607186951520");
            AddMarkingCode(scopedStore, importId, otherTask, "OTHER-P", MarkingCodeStatus.Printed, gtin: "04607186951520");

            var bound = SeedMarkableOrder(scopedStore, $"{prefix}-bound", itemId, qty: 1);
            var boundTask = AddMarkingOrder(scopedStore, bound.OrderId, itemId, requestedQty: 1);
            var boundCodeId = AddMarkingCode(scopedStore, importId, boundTask, "BOUND-R", MarkingCodeStatus.Reserved, gtin: "04607186951520");
            var docId = scopedStore.AddDoc(new Doc
            {
                DocRef = $"{prefix}-prd",
                Type = DocType.ProductionReceipt,
                Status = DocStatus.Closed,
                OrderId = bound.OrderId,
                CreatedAt = DateTime.UtcNow,
                ClosedAt = DateTime.UtcNow
            });
            var docLineId = scopedStore.AddDocLine(new DocLine
            {
                DocId = docId,
                OrderLineId = bound.OrderLineId,
                ItemId = itemId,
                Qty = 1,
                ProductionPurpose = ProductionLinePurpose.InternalStock
            });
            Assert.Equal(1, scopedStore.AssignProductionMarkingCodesToReceipt([boundCodeId], docId, docLineId, DateTime.UtcNow));

            var cancelled = SeedMarkableOrder(scopedStore, $"{prefix}-cancelled", itemId, qty: 1);
            var cancelledTask = AddMarkingOrder(scopedStore, cancelled.OrderId, itemId, requestedQty: 1, status: MarkingOrderStatus.Cancelled);
            AddMarkingCode(scopedStore, importId, cancelledTask, "CANCELLED-R", MarkingCodeStatus.Reserved, gtin: "04607186951520");

            var sourceLinked = SeedMarkableOrder(scopedStore, $"{prefix}-source", itemId, qty: 1);
            var sourceTask = AddMarkingOrder(
                scopedStore,
                sourceLinked.OrderId,
                itemId,
                requestedQty: 1,
                sourceLinked: true);
            AddMarkingCode(scopedStore, importId, sourceTask, "SOURCE-R", MarkingCodeStatus.Reserved, gtin: "04607186951520");

            var printed = SeedMarkableOrder(scopedStore, $"{prefix}-printed", itemId, qty: 1);
            scopedStore.MarkOrdersPrinted([printed.OrderId], DateTime.UtcNow);

            AssertMarking(ReadSingleOrderPageRow(scopedStore, covered.OrderRef), required: true, covered: true);
            AssertMarking(ReadSingleOrderPageRow(scopedStore, missing.OrderRef), required: true, covered: false);
            AssertMarking(ReadSingleOrderPageRow(scopedStore, bound.OrderRef), required: true, covered: true);
            AssertMarking(ReadSingleOrderPageRow(scopedStore, cancelled.OrderRef), required: true, covered: false);
            AssertMarking(ReadSingleOrderPageRow(scopedStore, sourceLinked.OrderRef), required: true, covered: true);

            var printedRow = ReadSingleOrderPageRow(scopedStore, printed.OrderRef);
            Assert.Equal(MarkingStatus.Printed, printedRow.MarkingStatus);
            Assert.NotNull(printedRow.MarkingExcelGeneratedAt);
            Assert.NotNull(printedRow.MarkingPrintedAt);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task OrderListMarkingReadModel_SetBasedCoverage_PreservesNeedEdgeCases()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, scopedStore =>
        {
            EnsureAtLeastOneLocation(scopedStore);
            var prefix = $"MK2-{DateTime.UtcNow.Ticks}";
            var itemTypeId = scopedStore.AddItemType(new ItemType
            {
                Name = $"{prefix}-type",
                Code = $"{prefix}-type",
                IsActive = true,
                EnableMarking = true
            });
            var sharedGtin = "04607186951521";
            var itemA = AddMarkableItem(scopedStore, itemTypeId, $"{prefix}-item-a", sharedGtin);
            var itemB = AddMarkableItem(scopedStore, itemTypeId, $"{prefix}-item-b", sharedGtin);
            var itemC = AddMarkableItem(scopedStore, itemTypeId, $"{prefix}-item-c", "04607186951522");
            var itemD = AddMarkableItem(scopedStore, itemTypeId, $"{prefix}-item-d", "04607186951523");
            var importId = scopedStore.AddMarkingCodeImport(new MarkingCodeImport
            {
                Id = Guid.NewGuid(),
                OriginalFilename = $"{prefix}.txt",
                StoragePath = $"{prefix}.txt",
                FileHash = Guid.NewGuid().ToString("N"),
                SourceType = "test",
                Status = MarkingCodeImportStatus.Bound,
                CreatedAt = DateTime.UtcNow,
                ProcessedAt = DateTime.UtcNow
            });

            var gtinFallback = SeedMarkableOrder(scopedStore, $"{prefix}-gtin-fallback", itemA, qty: 1);
            var gtinFallbackTask = AddMarkingOrder(
                scopedStore,
                gtinFallback.OrderId,
                itemB,
                requestedQty: 1,
                gtin: sharedGtin);
            AddMarkingCode(scopedStore, importId, gtinFallbackTask, "GTIN-FALLBACK", MarkingCodeStatus.Reserved, gtin: null);

            var itemMatchMultipleBuckets = SeedMarkableOrder(scopedStore, $"{prefix}-item-buckets", itemA, qty: 2);
            var itemMatchMultipleBucketsTask = AddMarkingOrder(
                scopedStore,
                itemMatchMultipleBuckets.OrderId,
                itemA,
                requestedQty: 2);
            AddMarkingCode(scopedStore, importId, itemMatchMultipleBucketsTask, "ITEM-BUCKET-1", MarkingCodeStatus.Reserved, gtin: sharedGtin);
            AddMarkingCode(scopedStore, importId, itemMatchMultipleBucketsTask, "ITEM-BUCKET-2", MarkingCodeStatus.Printed, gtin: "04607186959999");

            var singleFreeCodeWithMultipleNeeds = SeedMarkableOrderWithLines(
                scopedStore,
                $"{prefix}-one-free-code",
                OrderType.Internal,
                (itemA, 2),
                (itemB, 1));
            var singleFreeTask = AddMarkingOrder(scopedStore, singleFreeCodeWithMultipleNeeds.OrderId, itemA, requestedQty: 1);
            AddMarkingCode(scopedStore, importId, singleFreeTask, "ONE-FREE-CODE", MarkingCodeStatus.Reserved, gtin: sharedGtin);

            var singleBoundCodeWithMultipleNeeds = SeedMarkableOrderWithLines(
                scopedStore,
                $"{prefix}-one-bound-code",
                OrderType.Internal,
                (itemA, 2),
                (itemB, 1));
            var singleBoundTask = AddMarkingOrder(scopedStore, singleBoundCodeWithMultipleNeeds.OrderId, itemA, requestedQty: 1);
            AddBoundMarkingCode(scopedStore, importId, singleBoundCodeWithMultipleNeeds, itemA, singleBoundTask, "ONE-BOUND-CODE", MarkingCodeStatus.Reserved, sharedGtin);

            var emptyGtinDoesNotFallback = SeedMarkableOrder(scopedStore, $"{prefix}-empty-gtin", itemA, qty: 1);
            var emptyGtinTask = AddMarkingOrder(
                scopedStore,
                emptyGtinDoesNotFallback.OrderId,
                itemB,
                requestedQty: 1,
                gtin: " ");
            AddMarkingCode(scopedStore, importId, emptyGtinTask, "EMPTY-GTIN", MarkingCodeStatus.Reserved, gtin: " ");

            var sharedNeed = SeedMarkableOrderWithLines(
                scopedStore,
                $"{prefix}-shared-need",
                OrderType.Internal,
                (itemA, 1),
                (itemB, 1));
            var sharedNeedTask = AddMarkingOrder(scopedStore, sharedNeed.OrderId, itemA, requestedQty: 1);
            AddMarkingCode(scopedStore, importId, sharedNeedTask, "SHARED-NEED", MarkingCodeStatus.Reserved, gtin: sharedGtin);

            var multiNeed = SeedMarkableOrderWithLines(
                scopedStore,
                $"{prefix}-multi-need",
                OrderType.Internal,
                (itemA, 1),
                (itemC, 1));
            var multiNeedTaskA = AddMarkingOrder(scopedStore, multiNeed.OrderId, itemA, requestedQty: 1);
            AddMarkingCode(scopedStore, importId, multiNeedTaskA, "MULTI-A", MarkingCodeStatus.Reserved, gtin: sharedGtin);

            var freeAndBound = SeedMarkableOrder(scopedStore, $"{prefix}-free-bound", itemA, qty: 2);
            var freeAndBoundTask = AddMarkingOrder(scopedStore, freeAndBound.OrderId, itemA, requestedQty: 2);
            AddMarkingCode(scopedStore, importId, freeAndBoundTask, "FREE-BOUND-FREE", MarkingCodeStatus.Reserved, gtin: sharedGtin);
            AddBoundMarkingCode(scopedStore, importId, freeAndBound, freeAndBoundTask, "FREE-BOUND-BOUND", MarkingCodeStatus.Reserved, sharedGtin);

            var boundOnlyNeedsTwo = SeedMarkableOrder(scopedStore, $"{prefix}-bound-only-two", itemA, qty: 2);
            var boundOnlyTask = AddMarkingOrder(scopedStore, boundOnlyNeedsTwo.OrderId, itemA, requestedQty: 2);
            AddBoundMarkingCode(scopedStore, importId, boundOnlyNeedsTwo, boundOnlyTask, "BOUND-ONLY", MarkingCodeStatus.Reserved, sharedGtin);

            var voidedBound = SeedMarkableOrder(scopedStore, $"{prefix}-voided-bound", itemA, qty: 1);
            var voidedBoundTask = AddMarkingOrder(scopedStore, voidedBound.OrderId, itemA, requestedQty: 1);
            AddBoundMarkingCode(scopedStore, importId, voidedBound, voidedBoundTask, "VOIDED-BOUND", MarkingCodeStatus.Voided, sharedGtin);

            var failedTaskOrder = SeedMarkableOrder(scopedStore, $"{prefix}-failed", itemA, qty: 1);
            var failedTask = AddMarkingOrder(scopedStore, failedTaskOrder.OrderId, itemA, requestedQty: 1, status: MarkingOrderStatus.Failed);
            AddMarkingCode(scopedStore, importId, failedTask, "FAILED-R", MarkingCodeStatus.Reserved, gtin: sharedGtin);

            var reservedCustomer = SeedMarkableOrderWithLines(
                scopedStore,
                $"{prefix}-reserved-customer",
                OrderType.Customer,
                (itemD, 1));
            SeedReservedLedgerHu(scopedStore, reservedCustomer, itemD, qty: 1, huCode: $"{prefix}-HU");

            AssertMarking(ReadSingleOrderPageRow(scopedStore, gtinFallback.OrderRef), required: true, covered: true);
            AssertMarking(ReadSingleOrderPageRow(scopedStore, itemMatchMultipleBuckets.OrderRef), required: true, covered: true);
            AssertMarking(ReadSingleOrderPageRow(scopedStore, singleFreeCodeWithMultipleNeeds.OrderRef), required: true, covered: false);
            AssertMarking(ReadSingleOrderPageRow(scopedStore, singleBoundCodeWithMultipleNeeds.OrderRef), required: true, covered: false);
            AssertMarking(ReadSingleOrderPageRow(scopedStore, emptyGtinDoesNotFallback.OrderRef), required: true, covered: false);
            AssertMarking(ReadSingleOrderPageRow(scopedStore, sharedNeed.OrderRef), required: true, covered: true);
            AssertMarking(ReadSingleOrderPageRow(scopedStore, multiNeed.OrderRef), required: true, covered: false);
            AssertMarking(ReadSingleOrderPageRow(scopedStore, freeAndBound.OrderRef), required: true, covered: true);
            AssertMarking(ReadSingleOrderPageRow(scopedStore, boundOnlyNeedsTwo.OrderRef), required: true, covered: false);
            AssertMarking(ReadSingleOrderPageRow(scopedStore, voidedBound.OrderRef), required: true, covered: false);
            AssertMarking(ReadSingleOrderPageRow(scopedStore, failedTaskOrder.OrderRef), required: true, covered: false);
            AssertMarking(ReadSingleOrderPageRow(scopedStore, reservedCustomer.OrderRef), required: false, covered: true);

            var multiNeedTaskC = AddMarkingOrder(scopedStore, multiNeed.OrderId, itemC, requestedQty: 1);
            AddMarkingCode(scopedStore, importId, multiNeedTaskC, "MULTI-C", MarkingCodeStatus.Printed, gtin: "04607186951522");
            AssertMarking(ReadSingleOrderPageRow(scopedStore, multiNeed.OrderRef), required: true, covered: true);
            return Task.CompletedTask;
        });
    }

    private static JsonElement MapOrderJson(Order order)
    {
        return JsonSerializer.SerializeToElement(OrderApiMapper.MapOrder(
            order,
            order.HasShipmentRemaining,
            order.HasProductionPalletPlan,
            order.NeedsProductionPalletPlan,
            new ProductionPalletSummary
            {
                PlannedPalletCount = order.PlannedPalletCount,
                FilledPalletCount = order.FilledPalletCount,
                PlannedQty = order.PlannedQty,
                FilledQty = order.FilledQty
            }));
    }

    private static Order ReadSingleOrderPageRow(IDataStore store, string orderRef)
    {
        return Assert.Single(new OrderService(store).GetOrdersPage(includeInternal: true, orderRef, limit: 10, offset: 0));
    }

    private static InternalOrderFixture SeedInternalDraft(IDataStore store, string orderRefSuffix, DateTime createdAt)
    {
        var itemId = store.AddItem(new Item
        {
            Name = $"Тестовый внутренний товар {orderRefSuffix}",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        var orderRef = orderRefSuffix.All(char.IsDigit) ? orderRefSuffix : $"T-INT-{orderRefSuffix}"[..Math.Min(60, $"T-INT-{orderRefSuffix}".Length)];
        var orderId = store.AddOrder(new Order
        {
            OrderRef = orderRef,
            Type = OrderType.Internal,
            Status = OrderStatus.Draft,
            CreatedAt = createdAt
        });
        var orderLineId = store.AddOrderLine(new OrderLine
        {
            OrderId = orderId,
            ItemId = itemId,
            QtyOrdered = 600,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });

        return new InternalOrderFixture(orderId, orderLineId, itemId, orderRef, 600);
    }

    private static InternalOrderFixture SeedMarkableOrder(IDataStore store, string orderRef, long itemId, double qty)
    {
        var orderId = store.AddOrder(new Order
        {
            OrderRef = orderRef,
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });
        var orderLineId = store.AddOrderLine(new OrderLine
        {
            OrderId = orderId,
            ItemId = itemId,
            QtyOrdered = qty,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });

        return new InternalOrderFixture(orderId, orderLineId, itemId, orderRef, qty);
    }

    private static MarkableOrderFixture SeedMarkableOrderWithLines(
        IDataStore store,
        string orderRef,
        OrderType orderType,
        params (long ItemId, double Qty)[] lines)
    {
        long? partnerId = null;
        if (orderType == OrderType.Customer)
        {
            partnerId = store.AddPartner(new Partner
            {
                Name = $"Тестовый клиент {orderRef}",
                Code = $"T-{orderRef}"[..Math.Min(60, $"T-{orderRef}".Length)]
            });
        }

        var orderId = store.AddOrder(new Order
        {
            OrderRef = orderRef,
            Type = orderType,
            PartnerId = partnerId,
            Status = OrderStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });
        var seededLines = new List<MarkableOrderLineFixture>();
        foreach (var line in lines)
        {
            var orderLineId = store.AddOrderLine(new OrderLine
            {
                OrderId = orderId,
                ItemId = line.ItemId,
                QtyOrdered = line.Qty,
                ProductionPurpose = orderType == OrderType.Customer
                    ? ProductionLinePurpose.CustomerOrder
                    : ProductionLinePurpose.InternalStock
            });
            seededLines.Add(new MarkableOrderLineFixture(orderLineId, line.ItemId, line.Qty));
        }

        return new MarkableOrderFixture(orderId, orderRef, seededLines);
    }

    private static long AddMarkableItem(IDataStore store, long itemTypeId, string name, string gtin)
    {
        return store.AddItem(new Item
        {
            Name = name,
            BaseUom = "шт",
            ItemTypeId = itemTypeId,
            Gtin = gtin
        });
    }

    private static Guid AddMarkingOrder(
        IDataStore store,
        long orderId,
        long? itemId,
        int requestedQty,
        string status = MarkingOrderStatus.Printed,
        bool sourceLinked = false,
        string? gtin = null)
    {
        var id = Guid.NewGuid();
        store.AddMarkingOrder(new MarkingOrder
        {
            Id = id,
            OrderId = sourceLinked ? null : orderId,
            SourceOrderId = sourceLinked ? orderId : null,
            SourceType = sourceLinked ? MarkingNeedCreationService.ProductionOrderSourceType : null,
            ItemId = itemId,
            Gtin = gtin,
            RequestedQuantity = requestedQty,
            RequestNumber = id.ToString("N"),
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        return id;
    }

    private static Guid AddMarkingCode(
        IDataStore store,
        Guid importId,
        Guid markingOrderId,
        string suffix,
        string status,
        string? gtin,
        long? receiptDocId = null,
        long? receiptLineId = null)
    {
        var id = Guid.NewGuid();
        store.AddMarkingCodes([
            new MarkingCode
            {
                Id = id,
                Code = $"TEST-{suffix}-{id:N}",
                CodeHash = id.ToString("N"),
                Gtin = gtin,
                ImportId = importId,
                MarkingOrderId = markingOrderId,
                Status = status,
                ReceiptDocId = receiptDocId,
                ReceiptLineId = receiptLineId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        ]);
        return id;
    }

    private static Guid AddBoundMarkingCode(
        IDataStore store,
        Guid importId,
        InternalOrderFixture fixture,
        Guid markingOrderId,
        string suffix,
        string status,
        string? gtin)
    {
        var docId = store.AddDoc(new Doc
        {
            DocRef = $"{fixture.OrderRef}-{suffix}-prd",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = fixture.OrderId,
            CreatedAt = DateTime.UtcNow,
            ClosedAt = DateTime.UtcNow
        });
        var docLineId = store.AddDocLine(new DocLine
        {
            DocId = docId,
            OrderLineId = fixture.OrderLineId,
            ItemId = fixture.ItemId,
            Qty = 1,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });

        if (status == MarkingCodeStatus.Voided)
        {
            return AddMarkingCode(store, importId, markingOrderId, suffix, status, gtin, docId, docLineId);
        }

        var codeId = AddMarkingCode(store, importId, markingOrderId, suffix, status, gtin);
        Assert.Equal(1, store.AssignProductionMarkingCodesToReceipt([codeId], docId, docLineId, DateTime.UtcNow));
        return codeId;
    }

    private static Guid AddBoundMarkingCode(
        IDataStore store,
        Guid importId,
        MarkableOrderFixture fixture,
        long itemId,
        Guid markingOrderId,
        string suffix,
        string status,
        string? gtin)
    {
        var line = Assert.Single(fixture.Lines, seededLine => seededLine.ItemId == itemId);
        var docId = store.AddDoc(new Doc
        {
            DocRef = $"{fixture.OrderRef}-{suffix}-prd",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = fixture.OrderId,
            CreatedAt = DateTime.UtcNow,
            ClosedAt = DateTime.UtcNow
        });
        var docLineId = store.AddDocLine(new DocLine
        {
            DocId = docId,
            OrderLineId = line.OrderLineId,
            ItemId = itemId,
            Qty = 1,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });

        if (status == MarkingCodeStatus.Voided)
        {
            return AddMarkingCode(store, importId, markingOrderId, suffix, status, gtin, docId, docLineId);
        }

        var codeId = AddMarkingCode(store, importId, markingOrderId, suffix, status, gtin);
        Assert.Equal(1, store.AssignProductionMarkingCodesToReceipt([codeId], docId, docLineId, DateTime.UtcNow));
        return codeId;
    }

    private static void SeedReservedLedgerHu(
        IDataStore store,
        MarkableOrderFixture fixture,
        long itemId,
        double qty,
        string huCode)
    {
        var line = Assert.Single(fixture.Lines, seededLine => seededLine.ItemId == itemId);
        var location = store.GetLocations().First();
        var docId = store.AddDoc(new Doc
        {
            DocRef = $"{fixture.OrderRef}-stock",
            Type = DocType.Inbound,
            Status = DocStatus.Closed,
            CreatedAt = DateTime.UtcNow,
            ClosedAt = DateTime.UtcNow
        });
        store.AddLedgerEntry(new LedgerEntry
        {
            Timestamp = DateTime.UtcNow,
            DocId = docId,
            ItemId = itemId,
            LocationId = location.Id,
            QtyDelta = qty,
            HuCode = huCode
        });
        store.ReplaceOrderReceiptPlanLines(fixture.OrderId, [
            new OrderReceiptPlanLine
            {
                OrderId = fixture.OrderId,
                OrderLineId = line.OrderLineId,
                ItemId = itemId,
                QtyPlanned = qty,
                ToLocationId = location.Id,
                ToHu = huCode,
                SortOrder = 0
            }
        ]);
    }

    private static void AssertMarking(Order order, bool required, bool covered)
    {
        Assert.True(order.MarkingApplies);
        Assert.Equal(required, order.MarkingRequired);
        Assert.Equal(covered, order.MarkingCodeCovered);
    }

    private static void SeedCustomerOrder(IDataStore store, string orderRef, DateTime createdAt)
    {
        var partnerId = store.AddPartner(new Partner
        {
            Name = $"Тестовый клиент {orderRef}",
            Code = $"T-{orderRef}"
        });
        var itemId = store.AddItem(new Item
        {
            Name = $"Тестовый клиентский товар {orderRef}",
            BaseUom = "шт"
        });
        var orderId = store.AddOrder(new Order
        {
            OrderRef = orderRef,
            Type = OrderType.Customer,
            PartnerId = partnerId,
            Status = OrderStatus.InProgress,
            CreatedAt = createdAt
        });
        store.AddOrderLine(new OrderLine
        {
            OrderId = orderId,
            ItemId = itemId,
            QtyOrdered = 600,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
    }

    private static void SeedDraftPrdPallet(IDataStore store, InternalOrderFixture fixture, string palletStatus)
    {
        var locationId = store.GetLocations().First().Id;
        var huCode = store.CreateProductionPalletHuCode("ORDER-LIST-REGRESSION");
        var docId = store.AddDoc(new Doc
        {
            DocRef = $"PRD-LIST-{DateTime.UtcNow.Ticks.ToString()[^8..]}",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            CreatedAt = DateTime.UtcNow,
            OrderId = fixture.OrderId,
            OrderRef = fixture.OrderRef
        });
        store.AddDocLine(new DocLine
        {
            DocId = docId,
            OrderLineId = fixture.OrderLineId,
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            ItemId = fixture.ItemId,
            Qty = fixture.QtyOrdered,
            ToLocationId = locationId,
            ToHu = huCode
        });

        var pallets = store.PlanProductionPallets(docId, DateTime.UtcNow);
        var pallet = Assert.Single(pallets);
        if (string.Equals(palletStatus, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(1, store.MarkProductionPalletsPrinted(fixture.OrderId, [pallet.Id], DateTime.UtcNow));
        }
        else
        {
            Assert.Equal(ProductionPalletStatus.Planned, palletStatus);
        }
    }

    private static void SeedClosedProductionReceipt(IDataStore store, InternalOrderFixture fixture, double qty)
    {
        var locationId = store.GetLocations().First().Id;
        var docId = store.AddDoc(new Doc
        {
            DocRef = $"PRD-CLOSED-{DateTime.UtcNow.Ticks.ToString()[^8..]}",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            CreatedAt = DateTime.UtcNow,
            ClosedAt = DateTime.UtcNow,
            OrderId = fixture.OrderId,
            OrderRef = fixture.OrderRef
        });
        store.AddDocLine(new DocLine
        {
            DocId = docId,
            OrderLineId = fixture.OrderLineId,
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            ItemId = fixture.ItemId,
            Qty = qty,
            ToLocationId = locationId,
            ToHu = $"HU-CLOSED-{DateTime.UtcNow.Ticks.ToString()[^8..]}"
        });
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

    private sealed record InternalOrderFixture(long OrderId, long OrderLineId, long ItemId, string OrderRef, double QtyOrdered);
    private sealed record MarkableOrderFixture(long OrderId, string OrderRef, IReadOnlyList<MarkableOrderLineFixture> Lines);
    private sealed record MarkableOrderLineFixture(long OrderLineId, long ItemId, double QtyOrdered);

    private sealed class RollbackRequestedException : Exception;
}
