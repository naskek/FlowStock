using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using Xunit;

namespace FlowStock.Server.Tests.Diagnostics;

public sealed class NegativeStockLedgerTsTests
{
    [Fact]
    public void GetNegativeStockBalances_ReturnsNegativeRow_WithoutThrowing()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = 100, Name = "Товар 100", IsActive = true, BaseUom = "шт" });
        harness.SeedLocation(new Location { Id = 10, Code = "A-01", Name = "A-01" });
        harness.SeedBalance(100, 10, -1200, "HU-NEG-001");

        var rows = new NegativeStockCorrectionService(harness.Store).GetNegativeBalances();
        var row = Assert.Single(rows);
        Assert.Equal(-1200, row.Qty);
        Assert.Equal("HU-NEG-001", row.HuCode);
    }

    [Fact]
    public void GetNegativeStockBalances_SqlUsesLedgerTs_NotTimestampColumn()
    {
        var source = File.ReadAllText(GetPostgresDataStorePath());
        var methodStart = source.IndexOf("GetNegativeStockBalances()", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "GetNegativeStockBalances method must exist.");

        var nextMethod = source.IndexOf("\n    public ", methodStart + 1, StringComparison.Ordinal);
        var methodBody = nextMethod > methodStart
            ? source[methodStart..nextMethod]
            : source[methodStart..];

        Assert.Contains("led.ts AS last_movement_at", methodBody, StringComparison.Ordinal);
        Assert.Contains("led.ts DESC", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("led.timestamp", methodBody, StringComparison.Ordinal);
    }

    [Fact]
    public void LedgerTimestampParser_ParsesIsoOrReturnsNullWithoutThrowing()
    {
        Assert.Null(LedgerTimestampParser.TryParse(null));
        Assert.Null(LedgerTimestampParser.TryParse(""));
        Assert.Null(LedgerTimestampParser.TryParse("not-a-date"));

        var parsed = LedgerTimestampParser.TryParse("2024-05-19T12:34:56Z");
        Assert.NotNull(parsed);
        Assert.Equal(2024, parsed!.Value.Year);
        Assert.Equal(5, parsed.Value.Month);
        Assert.Equal(19, parsed.Value.Day);
    }

    private static string GetPostgresDataStorePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "apps",
                "windows",
                "FlowStock.Data",
                "PostgresDataStore.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("PostgresDataStore.cs not found from test output directory.");
    }
}
