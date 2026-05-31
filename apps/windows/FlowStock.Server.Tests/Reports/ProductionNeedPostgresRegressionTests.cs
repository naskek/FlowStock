using System.Runtime.ExceptionServices;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;

namespace FlowStock.Server.Tests.Reports;

public sealed class ProductionNeedPostgresRegressionTests
{
    [Theory]
    [InlineData(18, "Аджика 1 кг", 1512, 1134)]
    [InlineData(34, "Горчица 200 гр", 7296, 5472)]
    [InlineData(19, "Хрен столовый 200 гр", 5472, 5472)]
    public async Task ProductionNeed_WithCustomerOwnedFilledHu_UsesRealFreeStockForMinStockCreate(
        long sourceItemId,
        string itemName,
        double stockQty,
        double minStockQty)
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, scopedStore =>
        {
            var itemId = SeedCustomerOwnedFilledHuStock(scopedStore, sourceItemId, itemName, stockQty, minStockQty);

            var row = Assert.Single(new ProductionNeedService(scopedStore)
                .GetRows(includeZeroNeed: true)
                .Where(candidate => candidate.ItemId == itemId));

            Assert.Equal(0, row.FreeStockQty, 3);
            Assert.Equal(minStockQty, row.MinStockQty, 3);
            Assert.Equal(0, row.ToCloseOrdersQty, 3);
            Assert.Equal(minStockQty, row.ToMinStockQty, 3);
            Assert.Equal(minStockQty, row.QtyToCreate, 3);
            Assert.True(row.CanCreateOrder);

            return Task.CompletedTask;
        });
    }

    private static long SeedCustomerOwnedFilledHuStock(
        IDataStore store,
        long sourceItemId,
        string itemName,
        double stockQty,
        double minStockQty)
    {
        var suffix = $"{sourceItemId}-{DateTime.UtcNow.Ticks}";
        var locationId = EnsureAtLeastOneLocation(store);
        var itemTypeId = store.AddItemType(new ItemType
        {
            Name = $"PN owner-aware {suffix}",
            EnableMinStockControl = true,
            EnableOrderReservation = true
        });
        var itemId = store.AddItem(new Item
        {
            Name = $"{itemName} {suffix}",
            BaseUom = "шт",
            ItemTypeId = itemTypeId,
            MinStockQty = minStockQty,
            MaxQtyPerHu = stockQty
        });
        var partnerId = store.AddPartner(new Partner
        {
            Code = $"PN-CUST-{suffix}",
            Name = $"Production need customer {suffix}"
        });
        var orderRef = $"PN-CUST-{suffix}";
        var orderId = store.AddOrder(new Order
        {
            OrderRef = orderRef,
            Type = OrderType.Customer,
            PartnerId = partnerId,
            Status = OrderStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });
        var orderLineId = store.AddOrderLine(new OrderLine
        {
            OrderId = orderId,
            ItemId = itemId,
            QtyOrdered = stockQty,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        var docId = store.AddDoc(new Doc
        {
            DocRef = $"PN-PRD-{suffix}",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            CreatedAt = DateTime.UtcNow,
            OrderId = orderId,
            OrderRef = orderRef
        });
        var huCode = store.CreateProductionPalletHuCode("PN-OWNER-AWARE-REGRESSION");
        store.AddDocLine(new DocLine
        {
            DocId = docId,
            OrderLineId = orderLineId,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder,
            ItemId = itemId,
            Qty = stockQty,
            ToLocationId = locationId,
            ToHu = huCode
        });

        var pallet = Assert.Single(store.PlanProductionPallets(docId, DateTime.UtcNow));
        store.MarkProductionPalletFilled(pallet.Id, DateTime.UtcNow, "PN-TEST");
        pallet = Assert.Single(store.GetProductionPalletsByDoc(docId));
        Assert.Equal(ProductionPalletStatus.Filled, pallet.Status);

        store.AddLedgerEntry(new LedgerEntry
        {
            Timestamp = DateTime.UtcNow,
            DocId = docId,
            ItemId = itemId,
            LocationId = locationId,
            QtyDelta = stockQty,
            HuCode = pallet.HuCode
        });

        return itemId;
    }

    private static long EnsureAtLeastOneLocation(IDataStore store)
    {
        var existing = store.GetLocations().FirstOrDefault();
        if (existing != null)
        {
            return existing.Id;
        }

        return store.AddLocation(new Location
        {
            Code = "PN-FG",
            Name = "Production need finished goods",
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

    private sealed class RollbackRequestedException : Exception
    {
    }
}
