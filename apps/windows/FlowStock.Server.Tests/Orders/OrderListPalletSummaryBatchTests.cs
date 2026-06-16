using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderListPalletSummaryBatchTests
{
    [Fact]
    public void HarnessBatchSummary_ReturnsOrderOwnedSummariesForMultipleOrderIds()
    {
        var harness = CreateHarnessWithPallets();
        var summaryStore = Assert.IsAssignableFrom<IOrderOwnedPalletSummaryBatchStore>(harness.Store);

        var empty = summaryStore.GetOrderOwnedProductionPalletSummaries(Array.Empty<long>());
        var summaries = summaryStore.GetOrderOwnedProductionPalletSummaries([10, 20, 999, 10]);

        Assert.Empty(empty);
        Assert.Equal(3, summaries.Count);
        Assert.Equal(2, summaries[10].PlannedPalletCount);
        Assert.Equal(15, summaries[10].PlannedQty, 3);
        Assert.Equal(1, summaries[10].FilledPalletCount);
        Assert.Equal(5, summaries[10].FilledQty, 3);
        Assert.Equal(1, summaries[10].RemainingPalletCount);
        Assert.Equal(10, summaries[10].RemainingQty, 3);
        Assert.Equal(1, summaries[20].PlannedPalletCount);
        Assert.Equal(7, summaries[20].PlannedQty, 3);
        Assert.Equal(0, summaries[999].PlannedPalletCount);
    }

    [Fact]
    public void HarnessBatchSummary_UsesBatchPalletAndOrderLineReadsOnly()
    {
        var harness = CreateHarnessWithPallets();
        var summaryStore = Assert.IsAssignableFrom<IOrderOwnedPalletSummaryBatchStore>(harness.Store);

        _ = summaryStore.GetOrderOwnedProductionPalletSummaries([10, 20]);

        harness.VerifyOrderOwnedPalletSummaryBatchPathUsed(Times.Once());
        harness.VerifyProductionPalletDetailPathNotUsed();
    }

    [Fact]
    public void LoadedListPalletSummaryFields_MatchBatchSummary_ForRepresentativePalletPlans()
    {
        var harness = CreateHarnessWithPallets();
        var summaryStore = Assert.IsAssignableFrom<IOrderOwnedPalletSummaryBatchStore>(harness.Store);

        var batchSummaries = summaryStore.GetOrderOwnedProductionPalletSummaries([10, 20, 30]);

        AssertSummaryEquals(batchSummaries[10], BuildLoadedSummaryFromListFields(10, batchSummaries[10]));
        AssertSummaryEquals(batchSummaries[20], BuildLoadedSummaryFromListFields(20, batchSummaries[20]));
        AssertSummaryEquals(batchSummaries[30], BuildLoadedSummaryFromListFields(30, batchSummaries[30]));
        Assert.Equal(2, batchSummaries[10].PlannedPalletCount);
        Assert.Equal(1, batchSummaries[10].FilledPalletCount);
        Assert.Equal(1, batchSummaries[10].RemainingPalletCount);
        Assert.Equal(1, batchSummaries[20].PlannedPalletCount);
        Assert.Equal(1, batchSummaries[20].RemainingPalletCount);
        Assert.Equal(1, batchSummaries[30].PlannedPalletCount);
        Assert.Equal(10, batchSummaries[30].PlannedQty, 3);
        Assert.Equal(1, batchSummaries[30].RemainingPalletCount);
    }

    [Fact]
    public void PostgresDataStore_ExposesOrderOwnedPalletSummaryBatchStore()
    {
        Assert.Contains(
            "IOrderOwnedPalletSummaryBatchStore",
            File.ReadAllText(GetRepoPath("apps", "windows", "FlowStock.Data", "PostgresDataStore.cs")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void PostgresBatchSummary_ReusesExistingBatchReadsAndOrderOwnedFilter()
    {
        var source = File.ReadAllText(GetRepoPath("apps", "windows", "FlowStock.Data", "PostgresDataStore.cs"));
        var start = source.IndexOf("public IReadOnlyDictionary<long, ProductionPalletSummary> GetOrderOwnedProductionPalletSummaries", StringComparison.Ordinal);
        var end = source.IndexOf("public IReadOnlyList<ProductionFillingCompletion>", start, StringComparison.Ordinal);
        var method = source[start..end];

        Assert.Contains("GetProductionPalletsByOrderIds(ids)", method, StringComparison.Ordinal);
        Assert.Contains("GetOrderLinesByOrderIds(ids)", method, StringComparison.Ordinal);
        Assert.Contains("ProductionPalletService.BuildOrderOwnedPalletViews", method, StringComparison.Ordinal);
        Assert.Contains("ProductionPalletService.BuildSummary", method, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProductionPalletsByDoc", method, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDocsByOrder", method, StringComparison.Ordinal);
    }

    private static CloseDocumentHarness CreateHarnessWithPallets()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = 1, Name = "Item A", BaseUom = "шт" });
        harness.SeedItem(new Item { Id = 2, Name = "Item B", BaseUom = "шт" });
        harness.SeedItem(new Item { Id = 3, Name = "Item C", BaseUom = "шт" });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "010",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrder(new Order
        {
            Id = 20,
            OrderRef = "020",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 6, 1, 10, 5, 0, DateTimeKind.Utc)
        });
        harness.SeedOrder(new Order
        {
            Id = 30,
            OrderRef = "030",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 6, 1, 10, 10, 0, DateTimeKind.Utc)
        });
        harness.SeedOrder(new Order
        {
            Id = 40,
            OrderRef = "040",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 6, 1, 10, 15, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine { Id = 101, OrderId = 10, ItemId = 1, QtyOrdered = 15 });
        harness.SeedOrderLine(new OrderLine { Id = 201, OrderId = 20, ItemId = 2, QtyOrdered = 7 });
        harness.SeedOrderLine(new OrderLine { Id = 301, OrderId = 30, ItemId = 1, QtyOrdered = 4 });
        harness.SeedOrderLine(new OrderLine { Id = 302, OrderId = 30, ItemId = 2, QtyOrdered = 6 });
        harness.SeedOrderLine(new OrderLine { Id = 401, OrderId = 40, ItemId = 3, QtyOrdered = 11 });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 1001,
            PrdDocId = 501,
            DocLineId = 9001,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 1,
            ItemName = "Item A",
            HuCode = "HU-1001",
            PlannedQty = 10,
            Status = ProductionPalletStatus.Planned,
            CreatedAt = new DateTime(2026, 6, 1, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 1002,
            PrdDocId = 501,
            DocLineId = 9002,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 1,
            ItemName = "Item A",
            HuCode = "HU-1002",
            PlannedQty = 5,
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 6, 1, 11, 5, 0, DateTimeKind.Utc),
            CreatedAt = new DateTime(2026, 6, 1, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 1003,
            PrdDocId = 501,
            DocLineId = 9003,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 1,
            ItemName = "Item A",
            HuCode = "HU-1003",
            PlannedQty = 99,
            Status = ProductionPalletStatus.Cancelled,
            CreatedAt = new DateTime(2026, 6, 1, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 2001,
            PrdDocId = 601,
            DocLineId = 9101,
            OrderId = 20,
            OrderLineId = 201,
            ItemId = 2,
            ItemName = "Item B",
            HuCode = "HU-2001",
            PlannedQty = 7,
            Status = ProductionPalletStatus.Printed,
            CreatedAt = new DateTime(2026, 6, 1, 11, 10, 0, DateTimeKind.Utc)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 3001,
            PrdDocId = 701,
            DocLineId = 9201,
            OrderId = 30,
            OrderLineId = null,
            ItemId = 1,
            ItemName = "Mixed",
            HuCode = "HU-3001",
            PlannedQty = 10,
            Status = ProductionPalletStatus.Printed,
            CreatedAt = new DateTime(2026, 6, 1, 11, 15, 0, DateTimeKind.Utc),
            Lines =
            [
                new ProductionPalletComponentLine
                {
                    Id = 30011,
                    ProductionPalletId = 3001,
                    DocLineId = 9201,
                    OrderLineId = 301,
                    ItemId = 1,
                    ItemName = "Item A",
                    PlannedQty = 4,
                    CreatedAt = new DateTime(2026, 6, 1, 11, 15, 0, DateTimeKind.Utc)
                },
                new ProductionPalletComponentLine
                {
                    Id = 30012,
                    ProductionPalletId = 3001,
                    DocLineId = 9202,
                    OrderLineId = 302,
                    ItemId = 2,
                    ItemName = "Item B",
                    PlannedQty = 6,
                    CreatedAt = new DateTime(2026, 6, 1, 11, 15, 0, DateTimeKind.Utc)
                }
            ]
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 4001,
            PrdDocId = 701,
            DocLineId = 9301,
            OrderId = 40,
            OrderLineId = 401,
            ItemId = 3,
            ItemName = "Item C",
            HuCode = "HU-4001",
            PlannedQty = 11,
            Status = ProductionPalletStatus.Planned,
            CreatedAt = new DateTime(2026, 6, 1, 11, 20, 0, DateTimeKind.Utc)
        });

        return harness;
    }

    private static ProductionPalletSummary BuildLoadedSummaryFromListFields(long orderId, ProductionPalletSummary summary)
    {
        var programType = typeof(OrderApiMapper).Assembly.GetType("Program", throwOnError: true)!;
        var method = programType
            .GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.Name.Contains("BuildLoadedPalletSummary", StringComparison.Ordinal));
        Assert.NotNull(method);

        return Assert.IsType<ProductionPalletSummary>(method.Invoke(null,
        [
            new Order
            {
                Id = orderId,
                OrderRef = orderId.ToString("D3"),
                Type = OrderType.Internal,
                Status = OrderStatus.InProgress,
                CreatedAt = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc),
                ListMetricsLoaded = true,
                PlannedPalletCount = summary.PlannedPalletCount,
                FilledPalletCount = summary.FilledPalletCount,
                PlannedQty = summary.PlannedQty,
                FilledQty = summary.FilledQty
            }
        ])!);
    }

    private static void AssertSummaryEquals(ProductionPalletSummary expected, ProductionPalletSummary actual)
    {
        Assert.Equal(expected.PlannedPalletCount, actual.PlannedPalletCount);
        Assert.Equal(expected.PlannedQty, actual.PlannedQty, 3);
        Assert.Equal(expected.FilledPalletCount, actual.FilledPalletCount);
        Assert.Equal(expected.FilledQty, actual.FilledQty, 3);
        Assert.Equal(expected.RemainingPalletCount, actual.RemainingPalletCount);
        Assert.Equal(expected.RemainingQty, actual.RemainingQty, 3);
    }

    private static string GetRepoPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
