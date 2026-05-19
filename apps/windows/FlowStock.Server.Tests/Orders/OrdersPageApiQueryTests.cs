using FlowStock.Core.Services;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrdersPageApiQueryTests
{
    [Fact]
    public void BuildPath_IncludesLimitOffsetAndIncludeCancelledMerged()
    {
        var path = OrdersPageApiQuery.BuildPath(
            includeInternal: true,
            search: "ABC 1",
            limit: 15,
            offset: 30,
            includeCancelledMerged: false);

        Assert.Equal(
            "/api/orders?include_internal=1&q=ABC%201&limit=15&offset=30&include_cancelled_merged=0",
            path);
    }

    [Fact]
    public void BuildPath_UsesIncludeCancelledMergedFlagWhenEnabled()
    {
        var path = OrdersPageApiQuery.BuildPath(
            includeInternal: false,
            search: null,
            limit: 15,
            offset: 0,
            includeCancelledMerged: true);

        Assert.Equal("/api/orders?limit=15&offset=0&include_cancelled_merged=1", path);
    }
}
