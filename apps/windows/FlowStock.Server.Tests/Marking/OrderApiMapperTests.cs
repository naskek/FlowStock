using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Server;

namespace FlowStock.Server.Tests.Marking;

public sealed class OrderApiMapperTests
{
    [Theory]
    [InlineData(MarkingStatus.NotRequired, true, "REQUIRED", "Требуется файл ЧЗ")]
    [InlineData(MarkingStatus.Printed, false, "PRINTED", "Маркировка проведена")]
    [InlineData(MarkingStatus.ExcelGenerated, false, "EXCEL_GENERATED", "Файл ЧЗ сформирован")]
    [InlineData(MarkingStatus.NotRequired, false, "NOT_REQUIRED", "Маркировка не требуется")]
    public void MapOrder_ReturnsEffectiveMarkingStatusForOrderApi(
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
            Status = OrderStatus.InProgress,
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
}
