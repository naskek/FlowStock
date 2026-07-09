using FlowStock.App;
using FlowStock.Core.Models;

namespace FlowStock.Server.Tests.Orders;

public sealed class IncomingRequestsRowsBuilderTests
{
    [Fact]
    public void SummaryTotalExcludesReadyHuBindingPending()
    {
        // Legacy ready-HU UI-flow удалён: серверное поле парсится ради совместимости, но
        // больше не участвует в badge/tooltip Центра событий.
        var summary = new IncomingRequestsSummary(1, 2, 5);

        Assert.Equal(3, summary.ActionRequiredCount);
        Assert.Equal(3, summary.TotalPending);
    }

    [Fact]
    public void BuildsItemAndOrderRows()
    {
        var rows = IncomingRequestsRowsBuilder.Build(
            [BuildItemRequest()],
            [BuildOrderRequest()],
            IncomingRequestTypeFilter.All,
            new DateTime(2026, 1, 2, 3, 4, 5));

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.Kind == IncomingRequestRowKind.Item);
        Assert.Contains(rows, row => row.Kind == IncomingRequestRowKind.Order);
    }

    [Fact]
    public void ItemFilterShowsOnlyItemRows()
    {
        var rows = IncomingRequestsRowsBuilder.Build(
            [BuildItemRequest()],
            [BuildOrderRequest()],
            IncomingRequestTypeFilter.Item,
            new DateTime(2026, 1, 2, 3, 4, 5));

        var row = Assert.Single(rows);
        Assert.Equal(IncomingRequestRowKind.Item, row.Kind);
    }

    private static ItemRequest BuildItemRequest() =>
        new()
        {
            Id = 10,
            Barcode = "4600000000000",
            Comment = "Need item",
            Status = "NEW",
            CreatedAt = new DateTime(2026, 1, 1, 12, 0, 0)
        };

    private static OrderRequest BuildOrderRequest() =>
        new()
        {
            Id = 20,
            RequestType = OrderRequestType.CreateOrder,
            PayloadJson = """{"order_ref":"101","partner_id":7,"lines":[{"item_id":1,"qty":2}]}""",
            Status = OrderRequestStatus.Pending,
            CreatedAt = new DateTime(2026, 1, 1, 13, 0, 0)
        };
}
