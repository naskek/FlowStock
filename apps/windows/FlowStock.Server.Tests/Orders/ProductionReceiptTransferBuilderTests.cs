using FlowStock.App;
using FlowStock.Core.Models;

namespace FlowStock.Server.Tests.Orders;

public sealed class ProductionReceiptTransferBuilderTests
{
    [Fact]
    public void BuildInitialContexts_CollapsesHuPlannedInternalLines_AndKeepsHuEmpty()
    {
        var contexts = ProductionReceiptTransferBuilder.BuildInitialContexts(
            new[]
            {
                new OrderReceiptLine
                {
                    OrderLineId = 501,
                    OrderId = 50,
                    ItemId = 1001,
                    QtyOrdered = 3600,
                    QtyRemaining = 1800,
                    ProductionPurpose = ProductionLinePurpose.InternalStock,
                    ToLocationId = 1,
                    ToHu = "HU-001",
                    SortOrder = 1
                },
                new OrderReceiptLine
                {
                    OrderLineId = 501,
                    OrderId = 50,
                    ItemId = 1001,
                    QtyOrdered = 3600,
                    QtyRemaining = 1800,
                    ProductionPurpose = ProductionLinePurpose.InternalStock,
                    ToLocationId = 1,
                    ToHu = "HU-002",
                    SortOrder = 2
                }
            },
            defaultToLocationId: 10);

        var context = Assert.Single(contexts);
        Assert.Equal(1001, context.ItemId);
        Assert.Equal(501, context.OrderLineId);
        Assert.Equal(3600, context.QtyBase);
        Assert.Equal(10, context.ToLocationId);
        Assert.Null(context.ToHu);
    }

    [Fact]
    public void BuildInitialContexts_KeepsSingleCustomerLineWithoutHu()
    {
        var contexts = ProductionReceiptTransferBuilder.BuildInitialContexts(
            new[]
            {
                new OrderReceiptLine
                {
                    OrderLineId = 701,
                    OrderId = 70,
                    ItemId = 2001,
                    QtyOrdered = 7200,
                    QtyRemaining = 7200,
                    ProductionPurpose = ProductionLinePurpose.CustomerOrder,
                    ToHu = "HU-SHOULD-NOT-BE-COPIED"
                }
            },
            defaultToLocationId: 15);

        var context = Assert.Single(contexts);
        Assert.Equal(2001, context.ItemId);
        Assert.Equal(701, context.OrderLineId);
        Assert.Equal(7200, context.QtyBase);
        Assert.Equal(15, context.ToLocationId);
        Assert.Null(context.ToHu);
    }
}
