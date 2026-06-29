using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class HuCurrentLocationResolverTests
{
    private static readonly IReadOnlyDictionary<long, string> Locations = new Dictionary<long, string>
    {
        [1] = "01",
        [2] = "02",
        [3] = "03"
    };

    [Fact]
    public void Resolve_SingleLocationWithStockForItem_ReturnsLocationCode()
    {
        var resolver = HuCurrentLocationResolver.Create(
            new[]
            {
                new HuStockRow { HuCode = "HU-1", ItemId = 100, LocationId = 1, Qty = 600 }
            },
            Locations);

        Assert.Equal("01", resolver.Resolve("HU-1", 100));
    }

    [Fact]
    public void Resolve_HuNormalizedCaseInsensitive_ReturnsLocationCode()
    {
        var resolver = HuCurrentLocationResolver.Create(
            new[]
            {
                new HuStockRow { HuCode = "hu-1", ItemId = 100, LocationId = 2, Qty = 10 }
            },
            Locations);

        Assert.Equal("02", resolver.Resolve("  HU-1 ", 100));
    }

    [Fact]
    public void Resolve_NoPositiveStockForHu_ReturnsNull()
    {
        var resolver = HuCurrentLocationResolver.Create(
            new[]
            {
                new HuStockRow { HuCode = "HU-1", ItemId = 100, LocationId = 1, Qty = 0 }
            },
            Locations);

        Assert.Null(resolver.Resolve("HU-1", 100));
    }

    [Fact]
    public void Resolve_SingleLocationButRequestedItemHasNoStock_ReturnsNull()
    {
        var resolver = HuCurrentLocationResolver.Create(
            new[]
            {
                new HuStockRow { HuCode = "HU-1", ItemId = 100, LocationId = 1, Qty = 600 }
            },
            Locations);

        // Другой itemId того же HU в том же месте остатка не имеет.
        Assert.Null(resolver.Resolve("HU-1", 200));
    }

    [Fact]
    public void Resolve_MultipleItemsSameLocation_NoConflict_ReturnsLocationCode()
    {
        var resolver = HuCurrentLocationResolver.Create(
            new[]
            {
                new HuStockRow { HuCode = "HU-1", ItemId = 100, LocationId = 1, Qty = 600 },
                new HuStockRow { HuCode = "HU-1", ItemId = 200, LocationId = 1, Qty = 400 }
            },
            Locations);

        Assert.Equal("01", resolver.Resolve("HU-1", 100));
        Assert.Equal("01", resolver.Resolve("HU-1", 200));
    }

    [Fact]
    public void Resolve_SameHuTwoItemsTwoLocations_ThrowsInvariantViolation()
    {
        var resolver = HuCurrentLocationResolver.Create(
            new[]
            {
                new HuStockRow { HuCode = "HU-1", ItemId = 100, LocationId = 1, Qty = 600 },
                new HuStockRow { HuCode = "HU-1", ItemId = 200, LocationId = 2, Qty = 400 }
            },
            Locations);

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve("HU-1", 100));
        Assert.Contains("HU-1", ex.Message);
    }

    [Fact]
    public void Resolve_MultipleLocationsMessage_ContainsHuAndBothLocationsSorted()
    {
        var resolver = HuCurrentLocationResolver.Create(
            new[]
            {
                // Намеренно не по порядку location_id, чтобы проверить детерминированную сортировку.
                new HuStockRow { HuCode = "HU-1", ItemId = 100, LocationId = 2, Qty = 600 },
                new HuStockRow { HuCode = "HU-1", ItemId = 100, LocationId = 1, Qty = 400 }
            },
            Locations);

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve("HU-1", 100));
        Assert.Contains("HU-1", ex.Message);
        Assert.Contains("01", ex.Message);
        Assert.Contains("02", ex.Message);
        // Детерминированный порядок: "01" перед "02".
        Assert.True(ex.Message.IndexOf("01", StringComparison.Ordinal)
                    < ex.Message.IndexOf("02", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_BuiltOnce_ReusedForMultiplePositions_OrderIndependent()
    {
        var resolver = HuCurrentLocationResolver.Create(
            new[]
            {
                new HuStockRow { HuCode = "HU-1", ItemId = 100, LocationId = 1, Qty = 600 },
                new HuStockRow { HuCode = "HU-2", ItemId = 200, LocationId = 2, Qty = 400 }
            },
            Locations);

        Assert.Equal("02", resolver.Resolve("HU-2", 200));
        Assert.Equal("01", resolver.Resolve("HU-1", 100));
        Assert.Equal("02", resolver.Resolve("HU-2", 200));
        Assert.Null(resolver.Resolve("HU-1", 200));
    }
}
