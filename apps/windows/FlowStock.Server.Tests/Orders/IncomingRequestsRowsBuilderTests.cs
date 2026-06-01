using FlowStock.App;
using FlowStock.Core.Models;

namespace FlowStock.Server.Tests.Orders;

public sealed class IncomingRequestsRowsBuilderTests
{
    [Fact]
    public void SummaryTotalIncludesReadyHuBindingPending()
    {
        var summary = new IncomingRequestsSummary(1, 2, 1);

        Assert.Equal(4, summary.TotalPending);
    }

    [Fact]
    public void NullReadyHuModelDoesNotBlockExistingRows()
    {
        var rows = IncomingRequestsRowsBuilder.Build(
            [BuildItemRequest()],
            [BuildOrderRequest()],
            null,
            IncomingRequestTypeFilter.All,
            new DateTime(2026, 1, 2, 3, 4, 5));

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.Kind == IncomingRequestRowKind.Item);
        Assert.Contains(rows, row => row.Kind == IncomingRequestRowKind.Order);
    }

    [Fact]
    public void EmptyReadyHuModelDoesNotAddComputedRow()
    {
        var rows = IncomingRequestsRowsBuilder.Build(
            [],
            [],
            new WpfReadyHuBindingReadModel { HuCount = 0, OrderCount = 0, LineCount = 0 },
            IncomingRequestTypeFilter.All,
            new DateTime(2026, 1, 2, 3, 4, 5));

        Assert.Empty(rows);
    }

    [Fact]
    public void NonEmptyReadyHuModelAddsOneComputedRow()
    {
        var model = BuildReadyHuModel();

        var rows = IncomingRequestsRowsBuilder.Build(
            [],
            [],
            model,
            IncomingRequestTypeFilter.All,
            new DateTime(2026, 1, 2, 3, 4, 5));

        var row = Assert.Single(rows);
        Assert.Equal(IncomingRequestRowKind.ReadyHu, row.Kind);
        Assert.Equal(IncomingRequestRow.ReadyHuBindingRequestType, row.RequestTypeCode);
        Assert.Equal("Свободных HU: 2 · подходящих заказов: 1 · строк: 1", row.Summary);
        Assert.False(row.CanApprove);
        Assert.False(row.CanReject);
        Assert.True(row.CanOpenDetails);
    }

    [Fact]
    public void ReadyHuFilterShowsOnlyComputedRow()
    {
        var rows = IncomingRequestsRowsBuilder.Build(
            [BuildItemRequest()],
            [BuildOrderRequest()],
            BuildReadyHuModel(),
            IncomingRequestTypeFilter.ReadyHu,
            new DateTime(2026, 1, 2, 3, 4, 5));

        var row = Assert.Single(rows);
        Assert.Equal(IncomingRequestRowKind.ReadyHu, row.Kind);
    }

    [Fact]
    public void PersistentFiltersDoNotChangeReadyHuRowPresenceWhenFilterAllowsIt()
    {
        var rows = IncomingRequestsRowsBuilder.Build(
            [],
            [],
            BuildReadyHuModel(),
            IncomingRequestTypeFilter.All,
            new DateTime(2026, 1, 2, 3, 4, 5));

        Assert.Single(rows);
        Assert.Equal(IncomingRequestRowKind.ReadyHu, rows[0].Kind);
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

    private static WpfReadyHuBindingReadModel BuildReadyHuModel() =>
        new()
        {
            RequestType = IncomingRequestRow.ReadyHuBindingRequestType,
            HuCount = 2,
            OrderCount = 1,
            LineCount = 1,
            HuRows =
            [
                new WpfReadyHuBindingHuRow
                {
                    HuCode = "HU-001",
                    ItemId = 1,
                    ItemName = "Item A",
                    Qty = 5,
                    LocationDisplay = "MAIN",
                    CompatibleOrders =
                    [
                        new WpfReadyHuBindingCompatibleOrderRow
                        {
                            OrderId = 100,
                            OrderRef = "100",
                            Lines =
                            [
                                new WpfReadyHuBindingCompatibleLineRow
                                {
                                    OrderLineId = 1001,
                                    ItemId = 1,
                                    ItemName = "Item A",
                                    QtyOrdered = 10,
                                    ShipmentRemainingQty = 10,
                                    MaxAdditionalBindQty = 10
                                }
                            ]
                        }
                    ]
                }
            ]
        };
}
