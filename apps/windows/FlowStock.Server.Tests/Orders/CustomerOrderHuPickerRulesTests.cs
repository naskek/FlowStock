using FlowStock.App;
using FlowStock.Core.Models;

namespace FlowStock.Server.Tests.Orders;

public sealed class CustomerOrderHuPickerRulesTests
{
    [Fact]
    public void IsHuPickerEnabled_FullyPalletPlannedCustomerLine_IsFalse()
    {
        var line = new OrderLineView
        {
            Id = 222,
            ItemId = 29,
            QtyOrdered = 120,
            PlannedPalletQty = 120,
            ProductionHuCodes = "HU-0000574"
        };

        Assert.False(CustomerOrderHuPickerRules.IsHuPickerEnabled(
            hasOrderId: true,
            line,
            boundHuQty: 0,
            awaitingSave: false));
        Assert.Equal("Покрыто планом", CustomerOrderHuPickerRules.BuildHuPickerLabel(
            hasOrderId: true,
            line,
            boundHuQty: 0,
            selectedHuCount: 0,
            awaitingSave: false,
            candidatesFailed: false));
        Assert.Equal(
            "Строка покрыта паллетным планом: HU-0000574",
            CustomerOrderHuPickerRules.BuildHuPickerToolTip(
                line,
                boundHuQty: 0,
                awaitingSave: false,
                candidatesFailed: false,
                isPickerEnabled: false));
    }

    [Fact]
    public void IsHuPickerEnabled_PartiallyPalletPlannedLine_IsTrueWithoutPreloadedCandidates()
    {
        var line = new OrderLineView
        {
            Id = 301,
            ItemId = 6,
            QtyOrdered = 200,
            PlannedPalletQty = 120
        };

        Assert.Equal(80, CustomerOrderHuPickerRules.ComputeManualBindableRemaining(line, boundHuQty: 0), 3);
        Assert.True(CustomerOrderHuPickerRules.IsHuPickerEnabled(
            hasOrderId: true,
            line,
            boundHuQty: 0,
            awaitingSave: false));
        Assert.Equal("Выбрать HU (80)", CustomerOrderHuPickerRules.BuildHuPickerLabel(
            hasOrderId: true,
            line,
            boundHuQty: 0,
            selectedHuCount: 0,
            awaitingSave: false,
            candidatesFailed: false));
        Assert.Equal(
            "Часть строки покрыта паллетным планом, выбрать HU можно только на остаток.",
            CustomerOrderHuPickerRules.BuildHuPickerToolTip(
                line,
                boundHuQty: 0,
                awaitingSave: false,
                candidatesFailed: false,
                isPickerEnabled: true));
    }

    [Fact]
    public void IsHuPickerEnabled_FullyBoundCustomerLineWithoutPalletPlan_IsTrueForEditing()
    {
        var line = new OrderLineView
        {
            Id = 302,
            ItemId = 6,
            QtyOrdered = 600
        };

        Assert.True(CustomerOrderHuPickerRules.IsHuPickerEnabled(
            hasOrderId: true,
            line,
            boundHuQty: 600,
            awaitingSave: false));
    }

    [Fact]
    public void IsHuPickerEnabled_FullyPalletPlannedLineWithBoundHu_IsTrueForEditing()
    {
        var line = new OrderLineView
        {
            Id = 307,
            ItemId = 6,
            QtyOrdered = 1000,
            PlannedPalletQty = 400
        };

        Assert.True(CustomerOrderHuPickerRules.IsHuPickerEnabled(
            hasOrderId: true,
            line,
            boundHuQty: 600,
            awaitingSave: false));
        Assert.Equal("HU (1)", CustomerOrderHuPickerRules.BuildHuPickerLabel(
            hasOrderId: true,
            line,
            boundHuQty: 600,
            selectedHuCount: 1,
            awaitingSave: false,
            candidatesFailed: false));
    }

    [Fact]
    public void IsHuPickerEnabled_NoPalletPlanWithoutPreloadedCandidates_IsTrue()
    {
        var line = new OrderLineView
        {
            Id = 305,
            ItemId = 6,
            QtyOrdered = 200,
            PlannedPalletQty = 0
        };

        Assert.True(CustomerOrderHuPickerRules.IsHuPickerEnabled(
            hasOrderId: true,
            line,
            boundHuQty: 0,
            awaitingSave: false));
    }

    [Fact]
    public void IsHuPickerEnabled_CandidatesLoadFailed_DoesNotDisableButton()
    {
        var line = new OrderLineView
        {
            Id = 306,
            ItemId = 6,
            QtyOrdered = 200
        };

        Assert.True(CustomerOrderHuPickerRules.IsHuPickerEnabled(
            hasOrderId: true,
            line,
            boundHuQty: 0,
            awaitingSave: false));
    }

    [Fact]
    public void ComputeManualBindingCapacity_IgnoresCancelledPalletQtyFromLineMetrics()
    {
        var line = new OrderLineView
        {
            Id = 303,
            ItemId = 6,
            QtyOrdered = 200,
            PlannedPalletQty = 0
        };

        Assert.Equal(200, CustomerOrderHuPickerRules.ComputeManualBindingCapacity(line), 3);
        Assert.True(CustomerOrderHuPickerRules.IsHuPickerEnabled(
            hasOrderId: true,
            line,
            boundHuQty: 0,
            awaitingSave: false));
    }

    [Fact]
    public void ComputeManualBindingCapacity_SubtractsShippedAndActivePalletQty()
    {
        var line = new OrderLineView
        {
            Id = 304,
            ItemId = 6,
            QtyOrdered = 300,
            QtyShipped = 50,
            PlannedPalletQty = 120
        };

        Assert.Equal(130, CustomerOrderHuPickerRules.ComputeManualBindingCapacity(line), 3);
    }

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

    [Fact]
    public void ProposalLine_UncheckedHuIsNotIncludedInApplySelection()
    {
        var state = new CustomerOrderLineHuState("line-203");
        state.AttachLine(
            new OrderLineView
            {
                Id = 203,
                ItemId = 6,
                ItemName = "Товар",
                QtyOrdered = 1200
            },
            orderId: 78);
        state.ApplyCandidates(new WpfHuReservationCandidatesLineResult
        {
            ClientLineKey = "line-203",
            OrderLineId = 203,
            ItemId = 6,
            QtyOrdered = 1200,
            AvailableQty = 1200,
            AutoSelectedQty = 1200,
            Candidates =
            [
                new WpfHuReservationCandidateRow { HuCode = "HU-1", Source = "LEDGER_STOCK", Qty = 600, AutoSelected = true },
                new WpfHuReservationCandidateRow { HuCode = "HU-2", Source = "LEDGER_STOCK", Qty = 600, AutoSelected = true }
            ]
        });
        var presentation = new CustomerOrderLinePresentation(state);
        var proposal = new CustomerHuReservationProposalLine(presentation);

        proposal.Candidates.Single(candidate => candidate.HuCode == "HU-2").IsSelected = false;
        proposal.Refresh();

        Assert.Equal(600, proposal.SelectedQty, 3);
        Assert.Equal(600, proposal.UncoveredQty, 3);
        var selected = proposal.Candidates.Where(candidate => candidate.IsSelected).Select(candidate => candidate.HuCode).ToArray();
        Assert.Single(selected);
        Assert.Equal("HU-1", selected[0]);
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
