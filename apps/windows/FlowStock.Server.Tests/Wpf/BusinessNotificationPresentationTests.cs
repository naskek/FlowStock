using FlowStock.App;
using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server.Tests.Wpf;

public sealed class BusinessNotificationPresentationTests
{
    [Theory]
    [InlineData("CUSTOMER_ORDER_READY", "готов к отгрузке")]
    [InlineData("CUSTOMER_ORDER_SHIPPED", "полностью отгружен")]
    [InlineData("CUSTOMER_ORDER_PARTIALLY_SHIPPED", "частично отгружен")]
    public void EventSummary_UsesHistoricalSnapshotAndPreciseBusinessFact(string type, string expected)
    {
        var n = new BusinessNotification
        {
            EventType = type, EntityType = "ORDER", EntityRef = "OLD-REF", Title = "сырой заголовок", Message = "Все паллеты произведены",
            CreatedAt = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc),
            Payload = new() { OrderId = 42, OrderRef = "SO-042", OrderType = "CUSTOMER", PartnerName = "Имя на момент события" }
        };
        var row = new BusinessNotificationRow(n);
        Assert.Equal($"Заказ SO-042 для «Имя на момент события» {expected}", row.Summary);
        Assert.DoesNotContain("паллеты произведены", row.Summary);
        Assert.Equal(42, row.OrderId);
        Assert.Contains(n.CreatedAt.ToLocalTime().ToString("HH:mm"), row.TimeDisplay);
        Assert.Equal(n.CreatedAt, BusinessNotificationRow.AsUtc(DateTime.SpecifyKind(n.CreatedAt, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void LegacyAndFuturePayloads_DoNotInventClientOrUnsafeNavigation()
    {
        var legacy = new BusinessNotification { EventType = "EXPLICIT_FINALIZE_FAILED", EntityType = "OUTBOUND", EntityId = 123, Message = "Sensitive raw error" };
        var row = new BusinessNotificationRow(legacy);
        Assert.Equal("Не удалось завершить отгрузку", row.Summary);
        Assert.Null(row.OrderId); Assert.Null(row.DocId);
        Assert.DoesNotContain("Sensitive", row.Summary);
        Assert.Contains("Sensitive raw error", row.Diagnostics);
        Assert.Equal("Клиент на момент события не сохранён", row.PartnerDisplay);
        var future = new BusinessNotificationRow(new BusinessNotification { Payload = new() { Version = 99, OrderId = 1, PartnerName = "Непроверенное" } });
        Assert.Null(future.OrderId);
        Assert.DoesNotContain("Непроверенное", future.Summary);
    }

    [Fact]
    public void InternalCompletionIsNotCustomerShipment_AndPartialQuantitiesRemainPerItem()
    {
        Assert.Equal("Внутренний заказ INT-1 выполнен", BusinessNotificationContent.Summary(new()
        { EventType = "INTERNAL_ORDER_COMPLETED", Payload = new() { OrderRef = "INT-1", OrderType = "INTERNAL", PartnerName = "не клиент" } }));
        var row = new BusinessNotificationRow(new() { EventType = "CUSTOMER_ORDER_PARTIALLY_SHIPPED", Payload = new()
        { ShipmentLines = new[] { new BusinessNotificationQuantity("Товар", 100, 25, 75) } } });
        Assert.Contains("осталось 75", row.Details);
        Assert.DoesNotContain("паллет", row.Details);
    }
}
