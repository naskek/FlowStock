using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server.Tests.Orders;

public sealed class OperationOrderCandidatesApiQueryTests
{
    [Fact]
    public void BuildPath_ProductionReceipt_IncludesDocTypeLimitAndSearch()
    {
        var path = OperationOrderCandidatesApiQuery.BuildPath(DocType.ProductionReceipt, "ABC 1", 25);

        Assert.Equal("/api/orders/candidates?doc_type=PRODUCTION_RECEIPT&limit=25&q=ABC%201", path);
    }

    [Fact]
    public void BuildPath_Outbound_OmitsSearchWhenEmpty()
    {
        var path = OperationOrderCandidatesApiQuery.BuildPath(DocType.Outbound, null, 50);

        Assert.Equal("/api/orders/candidates?doc_type=OUTBOUND&limit=50", path);
    }

    [Fact]
    public void BuildPath_ClampsLimitToFifty()
    {
        var path = OperationOrderCandidatesApiQuery.BuildPath(DocType.Outbound, null, 500);

        Assert.Contains("limit=50", path, StringComparison.Ordinal);
        Assert.DoesNotContain("limit=500", path, StringComparison.Ordinal);
    }
}
