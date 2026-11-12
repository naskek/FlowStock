using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class BusinessNotificationCloseTests
{
    [Fact]
    public void PartialOutboundClose_CapturesClientQuantitiesAndDoesNotDuplicateOnReplay()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedPartner(new Partner { Id = 1, Name = "Исторический клиент" });
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Склад" });
        harness.SeedItem(new Item { Id = 1, Name = "Товар", BaseUom = "шт" });
        harness.SeedOrder(new Order { Id = 42, OrderRef = "SO-42", Type = OrderType.Customer, PartnerId = 1, Status = OrderStatus.InProgress });
        harness.SeedOrderLine(new OrderLine { Id = 43, OrderId = 42, ItemId = 1, QtyOrdered = 100 });
        harness.SeedDoc(new Doc { Id = 44, DocRef = "OUT-44", Type = DocType.Outbound, Status = DocStatus.Draft, OrderId = 42, PartnerId = 1 });
        harness.SeedLine(new DocLine { Id = 45, DocId = 44, ItemId = 1, OrderLineId = 43, Qty = 40, FromLocationId = 1 });
        harness.SeedBalance(1, 1, 100);
        var notifications = new List<BusinessNotification>();
        Mock.Get(harness.Store).Setup(s => s.AddBusinessNotification(It.IsAny<BusinessNotification>()))
            .Callback<BusinessNotification>(notifications.Add).Returns(true);
        var service = new DocumentService(harness.Store);
        var result = service.TryCloseDoc(44, allowNegative: false);
        Assert.True(result.Success, string.Join(";", result.Errors));
        var partial = Assert.Single(notifications.Where(n => n.EventType == "CUSTOMER_ORDER_PARTIALLY_SHIPPED"));
        Assert.Equal("Исторический клиент", partial.Payload!.PartnerName);
        Assert.Equal("OUT-44", partial.Payload.DocRef);
        Assert.Equal(60, Assert.Single(partial.Payload.ShipmentLines!).Remaining);
        Assert.DoesNotContain(notifications, n => n.EventType == "CUSTOMER_ORDER_SHIPPED");
        Assert.True(service.TryCloseDoc(44, allowNegative: false).Success);
        Assert.Equal(2, notifications.Count);
    }
}
