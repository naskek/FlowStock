using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
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

    private sealed class RollbackRequestedException : Exception;
}
