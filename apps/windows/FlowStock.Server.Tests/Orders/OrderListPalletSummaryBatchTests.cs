using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;
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
        harness.SeedOrderLine(new OrderLine { Id = 101, OrderId = 10, ItemId = 1, QtyOrdered = 15 });
        harness.SeedOrderLine(new OrderLine { Id = 201, OrderId = 20, ItemId = 2, QtyOrdered = 7 });
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

        return harness;
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
