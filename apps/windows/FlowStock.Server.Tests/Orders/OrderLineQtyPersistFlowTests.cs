using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Xunit;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderLineQtyPersistFlowTests
{
    [Fact]
    public void BuildLinesForPersist_UsesNewQtyInPayload_NotOldQty()
    {
        var line = CreateLine(id: 101, qty: 1200, hu: "HU-1, HU-2");
        var payload = OrderLineQtyPersistFlow.BuildLinesForPersist([line], 101, 2400);

        var updated = Assert.Single(payload);
        Assert.Equal(2400, updated.QtyOrdered);
        Assert.Equal(1200, line.QtyOrdered);
    }

    [Fact]
    public void BuildLinesForPersist_SecondEdit_UsesLatestQty()
    {
        var line = CreateLine(id: 101, qty: 1200, hu: "HU-1");
        var firstPayload = OrderLineQtyPersistFlow.BuildLinesForPersist([line], 101, 2400);
        line.QtyOrdered = 2400;

        var secondPayload = OrderLineQtyPersistFlow.BuildLinesForPersist(firstPayload, 101, 4800);
        var updated = Assert.Single(secondPayload);
        Assert.Equal(4800, updated.QtyOrdered);
    }

    [Fact]
    public void FormatQtyEditLogLine_ContainsPayloadAndHuFields()
    {
        var line = OrderLineQtyPersistFlow.FormatQtyEditLogLine(new OrderLineQtyEditLogEntry
        {
            OrderId = 87,
            OrderLineId = 501,
            OldQty = 1200,
            NewQty = 2400,
            PayloadQty = 2400,
            PutStatus = 200,
            ReloadStarted = true,
            ReloadFinished = true,
            HuCodesBefore = "HU-1, HU-2",
            HuCodesAfter = "HU-1, HU-2, HU-3, HU-4"
        });

        Assert.Contains("[OrderLineQtyEdit]", line);
        Assert.Contains("payload_qty=2400", line);
        Assert.Contains("hu_codes_after=\"HU-1, HU-2, HU-3, HU-4\"", line);
    }

    private static OrderLineView CreateLine(long id, double qty, string hu)
    {
        return new OrderLineView
        {
            Id = id,
            OrderId = 87,
            ItemId = 10,
            ItemName = "Item",
            QtyOrdered = qty,
            ProductionHuCodes = hu
        };
    }
}
