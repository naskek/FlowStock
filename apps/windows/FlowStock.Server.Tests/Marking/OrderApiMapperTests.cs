using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Server;

namespace FlowStock.Server.Tests.Marking;

public sealed class OrderApiMapperTests
{
    [Theory]
    [InlineData(OrderStatus.InProgress, MarkingStatus.Printed, false, "PRINTED", "ЧЗ готов к нанесению")]
    [InlineData(OrderStatus.InProgress, MarkingStatus.NotRequired, true, "REQUIRED", "Требуется файл ЧЗ")]
    [InlineData(OrderStatus.InProgress, MarkingStatus.NotRequired, false, "NOT_REQUIRED", "Маркировка не требуется")]
    [InlineData(OrderStatus.Cancelled, MarkingStatus.NotRequired, true, "NOT_REQUIRED", "Маркировка не требуется")]
    [InlineData(OrderStatus.Cancelled, MarkingStatus.Printed, true, "PRINTED", "ЧЗ готов к нанесению")]
    public void MapOrder_ReturnsEffectiveMarkingStatusForOrderApi(
        OrderStatus orderStatus,
        MarkingStatus storedStatus,
        bool markingRequired,
        string expectedStatus,
        string expectedDisplay)
    {
        var order = new Order
        {
            Id = 1,
            OrderRef = "CO-1",
            Type = OrderType.Customer,
            Status = orderStatus,
            CreatedAt = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc),
            MarkingStatus = storedStatus,
            MarkingRequired = markingRequired
        };

        var json = JsonSerializer.SerializeToElement(OrderApiMapper.MapOrder(order));

        Assert.Equal(expectedStatus, json.GetProperty("marking_status").GetString());
        Assert.Equal(expectedStatus, json.GetProperty("marking_effective_status").GetString());
        Assert.Equal(markingRequired, json.GetProperty("marking_required").GetBoolean());
        Assert.Equal(expectedDisplay, json.GetProperty("marking_status_display").GetString());
    }

    [Fact]
    public void MapOrder_ReturnsPrintedForLegacyExcelGeneratedRawStatus()
    {
        var order = new Order
        {
            Id = 1,
            OrderRef = "CO-1",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc),
            MarkingStatus = MarkingStatusMapper.FromString("EXCEL_GENERATED"),
            MarkingRequired = false
        };

        var json = JsonSerializer.SerializeToElement(OrderApiMapper.MapOrder(order));

        Assert.Equal("PRINTED", json.GetProperty("marking_status").GetString());
        Assert.Equal("PRINTED", json.GetProperty("marking_effective_status").GetString());
        Assert.Equal("ЧЗ готов к нанесению", json.GetProperty("marking_status_display").GetString());
    }
}
