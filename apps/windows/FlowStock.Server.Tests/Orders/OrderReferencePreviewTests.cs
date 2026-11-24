using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderReferencePreviewTests
{
    [Fact]
    public void LargeOrderSet_ReadsOnlyRawRefsAndPendingRequests()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        var lookup = store.As<IOrderRefLookupStore>();
        lookup.Setup(x => x.GetAllOrderRefsForNextNumber())
            .Returns(Enumerable.Range(1, 10000).Select(x => x.ToString()).Concat(["ABC", " -10 ", " 010001 "]).ToArray());
        store.Setup(x => x.GetOrderRequests(false)).Returns(new[]
        {
            new OrderRequest { PayloadJson = "{\"order_ref\":\" 10005 \"}" },
            new OrderRequest { PayloadJson = "{\"order_ref\":10006}" },
            new OrderRequest { PayloadJson = "broken json" },
            new OrderRequest { RequestType = OrderRequestType.SetOrderStatus, PayloadJson = "{\"order_ref\":\"99999\"}" }
        });

        Assert.Equal("10007", OrderReferencePreview.GetNext(store.Object));
        lookup.Verify(x => x.GetAllOrderRefsForNextNumber(), Times.Once);
        store.Verify(x => x.GetOrderRequests(false), Times.Once);
        store.Verify(x => x.GetOrders(), Times.Never);
        lookup.VerifyNoOtherCalls();
    }

    [Fact]
    public void LegacyStore_PreservesAllStatusesAndIgnoresInvalidNumericRefs()
    {
        var store = new Mock<IDataStore>();
        store.Setup(x => x.GetOrders()).Returns(new[]
        {
            new Order { OrderRef = "005", Status = OrderStatus.Cancelled },
            new Order { OrderRef = "007", Status = OrderStatus.Merged },
            new Order { OrderRef = "9223372036854775808" }, new Order { OrderRef = "+12" }
        });
        store.Setup(x => x.GetOrderRequests(false)).Returns(Array.Empty<OrderRequest>());
        Assert.Equal("008", OrderReferencePreview.GetNext(store.Object));
    }

    [Fact]
    public void ExhaustedNumber_DoesNotWrapIntoConflictingSuggestion()
    {
        var store = new Mock<IDataStore>();
        store.As<IOrderRefLookupStore>().Setup(x => x.GetAllOrderRefsForNextNumber()).Returns([long.MaxValue.ToString()]);
        store.Setup(x => x.GetOrderRequests(false)).Returns(Array.Empty<OrderRequest>());
        Assert.Throws<OverflowException>(() => OrderReferencePreview.GetNext(store.Object));
    }
}
