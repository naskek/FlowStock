using FlowStock.App;

namespace FlowStock.Server.Tests.Orders;

public sealed class CustomerOrderHuPickerRulesTests
{
    [Fact]
    public void ApplyRowEnablement_DisablesUnselectedWhenLineCovered()
    {
        var rows = new[]
        {
            CreateRow("HU-1", 600, selected: true),
            CreateRow("HU-2", 600, selected: false),
            CreateRow("HU-3", 100, selected: false)
        };

        CustomerOrderHuPickerRules.ApplyRowEnablement(rows, lineRemainingQty: 600, selectedOnOtherLines: new HashSet<string>());

        Assert.True(rows[0].IsEnabled);
        Assert.False(rows[1].IsEnabled);
        Assert.Equal("Покрыто выбранными HU", rows[1].DisableReason);
        Assert.False(rows[2].IsEnabled);
    }

    [Fact]
    public void ApplyRowEnablement_ReenablesAfterUncheck()
    {
        var rows = new[]
        {
            CreateRow("HU-1", 600, selected: false),
            CreateRow("HU-2", 600, selected: false)
        };

        CustomerOrderHuPickerRules.ApplyRowEnablement(rows, lineRemainingQty: 600, selectedOnOtherLines: new HashSet<string>());

        Assert.True(rows[0].IsEnabled);
        Assert.True(rows[1].IsEnabled);
    }

    [Fact]
    public void ApplyRowEnablement_DisablesCandidateExceedingRemainingCapacity()
    {
        var rows = new[]
        {
            CreateRow("HU-1", 400, selected: false),
            CreateRow("HU-2", 400, selected: false)
        };

        CustomerOrderHuPickerRules.ApplyRowEnablement(rows, lineRemainingQty: 600, selectedOnOtherLines: new HashSet<string>());

        Assert.True(rows[0].IsEnabled);
        Assert.True(rows[1].IsEnabled);

        rows[0].IsSelected = true;
        CustomerOrderHuPickerRules.ApplyRowEnablement(rows, lineRemainingQty: 600, selectedOnOtherLines: new HashSet<string>());

        Assert.True(rows[0].IsEnabled);
        Assert.False(rows[1].IsEnabled);
        Assert.Equal("Превышает количество строки", rows[1].DisableReason);
    }

    [Fact]
    public void TrySelectRow_BlocksSelectionAboveLineRemaining()
    {
        var rows = new[]
        {
            CreateRow("HU-1", 400, selected: true),
            CreateRow("HU-2", 400, selected: false)
        };

        Assert.False(CustomerOrderHuPickerRules.TrySelectRow(rows[1], rows, lineRemainingQty: 600, desiredSelected: true));
    }

    [Fact]
    public void BuildExcludeHuCodesForOtherLines_ExcludesOnlyOtherLines()
    {
        var lineA = CreateState("line-a", ["HU-1", "HU-2"]);
        var lineB = CreateState("line-b", ["HU-3"]);

        var exclude = CustomerOrderHuPickerRules.BuildExcludeHuCodesForOtherLines([lineA, lineB], "line-a");

        Assert.Single(exclude);
        Assert.Equal("HU-3", exclude[0]);
    }

    private static HuReservationPickerRow CreateRow(string huCode, double qty, bool selected)
    {
        var row = new HuReservationPickerRow(
            new WpfHuReservationCandidateRow
            {
                HuCode = huCode,
                Source = "LEDGER_STOCK",
                Qty = qty,
                ShipReady = true
            },
            selected);
        return row;
    }

    private static CustomerOrderLineHuState CreateState(string key, IReadOnlyList<string> selected)
    {
        var state = new CustomerOrderLineHuState(key);
        state.AttachLine(
            new FlowStock.Core.Models.OrderLineView
            {
                Id = 1,
                ItemId = 6,
                QtyOrdered = 600,
                QtyRemaining = 600
            },
            orderId: 78);
        state.ApplyManualSelection(selected);
        return state;
    }
}
