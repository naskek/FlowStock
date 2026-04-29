using FlowStock.Core.Models;
using FlowStock.Server;

namespace FlowStock.Server.Tests.Catalog;

public sealed class HuStockReadModelMapperTests
{
    [Fact]
    public void Map_ReturnsOriginAndReservedTogether_WhenContextContainsBoth()
    {
        var context = new HuOrderContextRow
        {
            HuCode = "hu-1",
            ItemId = 1001,
            OriginInternalOrderId = 11,
            OriginInternalOrderRef = "INT-11",
            ReservedCustomerOrderId = 22,
            ReservedCustomerOrderRef = "CUST-22",
            ReservedCustomerId = 33,
            ReservedCustomerName = "ООО Клиент"
        };
        var map = HuStockReadModelMapper.BuildContextMap([context]);

        var row = HuStockReadModelMapper.Map(1001, 5, "HU-1", 12, map);

        Assert.Equal("HU-1", row.Hu);
        Assert.Equal(11, row.OriginInternalOrderId);
        Assert.Equal("INT-11", row.OriginInternalOrderRef);
        Assert.Equal(22, row.ReservedCustomerOrderId);
        Assert.Equal("CUST-22", row.ReservedCustomerOrderRef);
        Assert.Equal(33, row.ReservedCustomerId);
        Assert.Equal("ООО Клиент", row.ReservedCustomerName);
    }

    [Fact]
    public void Map_ReturnsNullReserve_WhenNoReservationContext()
    {
        var context = new HuOrderContextRow
        {
            HuCode = "HU-1",
            ItemId = 1001,
            OriginInternalOrderId = 11,
            OriginInternalOrderRef = "INT-11"
        };
        var map = HuStockReadModelMapper.BuildContextMap([context]);

        var row = HuStockReadModelMapper.Map(1001, 5, "HU-1", 12, map);

        Assert.Equal(11, row.OriginInternalOrderId);
        Assert.Equal("INT-11", row.OriginInternalOrderRef);
        Assert.Null(row.ReservedCustomerOrderId);
        Assert.Null(row.ReservedCustomerOrderRef);
        Assert.Null(row.ReservedCustomerId);
        Assert.Null(row.ReservedCustomerName);
    }

    [Fact]
    public void Map_ReturnsReservedCustomerContext_WhenOriginIsMissing()
    {
        var context = new HuOrderContextRow
        {
            HuCode = "HU-1",
            ItemId = 1001,
            ReservedCustomerOrderId = 22,
            ReservedCustomerOrderRef = "CUST-22",
            ReservedCustomerId = 33,
            ReservedCustomerName = "ООО Клиент"
        };
        var map = HuStockReadModelMapper.BuildContextMap([context]);

        var row = HuStockReadModelMapper.Map(1001, 5, "HU-1", 12, map);

        Assert.Null(row.OriginInternalOrderId);
        Assert.Null(row.OriginInternalOrderRef);
        Assert.Equal(22, row.ReservedCustomerOrderId);
        Assert.Equal("CUST-22", row.ReservedCustomerOrderRef);
        Assert.Equal(33, row.ReservedCustomerId);
        Assert.Equal("ООО Клиент", row.ReservedCustomerName);
    }
}
