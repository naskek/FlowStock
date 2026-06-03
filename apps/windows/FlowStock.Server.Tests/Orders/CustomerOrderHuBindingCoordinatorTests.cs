using FlowStock.App;
using FlowStock.Core.Models;

namespace FlowStock.Server.Tests.Orders;

public sealed class CustomerOrderHuBindingCoordinatorTests
{
    [Fact]
    public void NotifyLineChanged_WithMissingLineData_DoesNotThrow()
    {
        using var context = new WpfServiceTestContext();
        using var coordinator = new CustomerOrderHuBindingCoordinator(
            context.ReadApi,
            _ => Array.Empty<OrderReceiptPlanLine>());

        coordinator.ResetForNewOrder();

        var exception = Record.Exception(() =>
        {
            coordinator.NotifyLineChanged(null);
            coordinator.NotifyLineChanged(new OrderLineView
            {
                ItemName = string.Empty,
                ItemId = 0,
                QtyOrdered = 1800,
                QtyRemaining = 1800
            });
        });

        Assert.Null(exception);
    }

    [Fact]
    public void AutoSelectedCandidates_DoNotPopulateSelectedHuCodes()
    {
        var state = new CustomerOrderLineHuState("line-203");
        state.AttachLine(
            new OrderLineView
            {
                Id = 203,
                ItemId = 6,
                ItemName = "Горчица, Печагин, 1 кг",
                QtyOrdered = 1800,
                QtyRemaining = 1800
            },
            orderId: 78);

        state.ApplyCandidates(new WpfHuReservationCandidatesLineResult
        {
            ClientLineKey = "line-203",
            OrderLineId = 203,
            ItemId = 6,
            QtyOrdered = 1800,
            AvailableQty = 1200,
            AutoSelectedQty = 1200,
            Candidates =
            [
                new WpfHuReservationCandidateRow
                {
                    HuCode = "HU-0000493",
                    Source = "LEDGER_STOCK",
                    Qty = 1200,
                    ShipReady = true,
                    AutoSelected = true
                }
            ]
        });

        Assert.Single(state.Candidates);
        Assert.Equal("HU-0000493", Assert.Single(state.GetPickerCandidates()).HuCode);
        Assert.Empty(state.SelectedHuCodes);
        Assert.Empty(state.HuDisplayRows);
        Assert.Equal(0, state.BoundQty, 3);
        Assert.False(state.ShouldSendOnApply);
        Assert.Equal("missing", state.HuCoverageTone);
        Assert.Equal(
            "Горчица, Печагин, 1 кг: привязано 0 из 1800, не хватает 1800",
            state.HuCoverageToolTip);
        Assert.DoesNotContain("203", state.HuCoverageToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoSelectedFullCoverageCandidate_DoesNotMarkLineCovered()
    {
        var state = new CustomerOrderLineHuState("line-204");
        state.AttachLine(
            new OrderLineView
            {
                Id = 204,
                ItemId = 6,
                ItemName = "Горчица, Печагин, 1 кг",
                QtyOrdered = 1800,
                QtyRemaining = 1800
            },
            orderId: 78);

        state.ApplyCandidates(new WpfHuReservationCandidatesLineResult
        {
            ClientLineKey = "line-204",
            OrderLineId = 204,
            ItemId = 6,
            QtyOrdered = 1800,
            AvailableQty = 1800,
            AutoSelectedQty = 1800,
            Candidates =
            [
                new WpfHuReservationCandidateRow
                {
                    HuCode = "HU-0000494",
                    Source = "LEDGER_STOCK",
                    Qty = 1800,
                    ShipReady = true,
                    AutoSelected = true
                }
            ]
        });

        Assert.Empty(state.SelectedHuCodes);
        Assert.Equal(0, state.BoundQty, 3);
        Assert.False(state.ShouldSendOnApply);
        Assert.Equal("missing", state.HuCoverageTone);
        Assert.Equal(
            "Горчица, Печагин, 1 кг: привязано 0 из 1800, не хватает 1800",
            state.HuCoverageToolTip);
    }

    [Fact]
    public void AutoSelectedOversizedHu_RemainsOnlyCandidate()
    {
        var state = new CustomerOrderLineHuState("line-205");
        state.AttachLine(
            new OrderLineView
            {
                Id = 205,
                ItemId = 6,
                ItemName = "Горчица, Печагин, 1 кг",
                QtyOrdered = 500,
                QtyRemaining = 500
            },
            orderId: 78);

        state.ApplyCandidates(new WpfHuReservationCandidatesLineResult
        {
            ClientLineKey = "line-205",
            OrderLineId = 205,
            ItemId = 6,
            QtyOrdered = 500,
            AvailableQty = 600,
            AutoSelectedQty = 0,
            Candidates =
            [
                new WpfHuReservationCandidateRow
                {
                    HuCode = "HU-0000600",
                    Source = "LEDGER_STOCK",
                    Qty = 600,
                    ShipReady = true,
                    AutoSelected = true
                }
            ]
        });

        Assert.Empty(state.SelectedHuCodes);
        Assert.Equal(0, state.BoundQty, 3);
        Assert.False(state.IsSelectionOverRemaining);
    }

    [Fact]
    public void FullyPalletPlannedLine_DisablesHuPicker()
    {
        var state = new CustomerOrderLineHuState("line-222");
        state.AttachLine(
            new OrderLineView
            {
                Id = 222,
                ItemId = 29,
                ItemName = "Товар A",
                QtyOrdered = 120,
                PlannedPalletQty = 120,
                ProductionHuCodes = "HU-0000574"
            },
            orderId: 83);

        Assert.False(state.IsHuPickerEnabled);
        Assert.Equal("Покрыто планом", state.HuPickerLabel);
        Assert.Contains("HU-0000574", state.HuPickerToolTip ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(0, state.ManualBindableRemaining, 3);
    }

    [Fact]
    public void ProductionPalletGroupEditability_UnlocksLineAfterItsActivePalletIsDeleted()
    {
        var line = new OrderLineView
        {
            Id = 101,
            OrderId = 10,
            ItemId = 6,
            ItemName = "Товар A"
        };

        ProductionPalletGroupEditability.Apply([line], new HashSet<long> { 101 }, orderEditable: true);
        Assert.False(line.IsProductionPalletGroupEditable);

        ProductionPalletGroupEditability.Apply([line], new HashSet<long>(), orderEditable: true);
        Assert.True(line.IsProductionPalletGroupEditable);
    }

    [Fact]
    public void ProductionPalletGroupEditability_BlocksOnlyLineWithRemainingActivePallet()
    {
        var deletedLine = new OrderLineView { Id = 101, OrderId = 10, ItemId = 6, ItemName = "Товар A" };
        var remainingLine = new OrderLineView { Id = 102, OrderId = 10, ItemId = 7, ItemName = "Товар B" };

        ProductionPalletGroupEditability.Apply(
            [deletedLine, remainingLine],
            new HashSet<long> { 102 },
            orderEditable: true);

        Assert.True(deletedLine.IsProductionPalletGroupEditable);
        Assert.False(remainingLine.IsProductionPalletGroupEditable);
    }

    [Fact]
    public void PartiallyPalletPlannedLine_KeepsHuPickerEnabledForRemainder()
    {
        var state = new CustomerOrderLineHuState("line-223");
        state.AttachLine(
            new OrderLineView
            {
                Id = 223,
                ItemId = 13,
                ItemName = "Товар B",
                QtyOrdered = 200,
                PlannedPalletQty = 120
            },
            orderId: 83);

        Assert.True(state.IsHuPickerEnabled);
        Assert.Equal("Выбрать HU (80)", state.HuPickerLabel);
        Assert.Equal(80, state.ManualBindableRemaining, 3);
        Assert.Contains("остаток", state.HuPickerToolTip ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BoundCustomerLine_KeepsHuPickerEnabledWithoutCandidates()
    {
        var state = new CustomerOrderLineHuState("line-302");
        state.AttachLine(
            new OrderLineView
            {
                Id = 302,
                ItemId = 6,
                ItemName = "Товар",
                QtyOrdered = 600
            },
            orderId: 78);
        state.MergeExistingReservation("HU-0000400", 600);

        Assert.True(state.IsHuPickerEnabled);
        Assert.Equal("HU (1)", state.HuPickerLabel);
        Assert.True(state.ShouldSendOnApply);
        Assert.Equal(["HU-0000400"], state.SelectedHuCodes);
        Assert.Equal("склад", Assert.Single(state.HuDisplayRows).Label);
    }

    [Fact]
    public void ManualPickerSelection_PopulatesSelectedAndDisplayRows()
    {
        var state = new CustomerOrderLineHuState("line-303");
        state.AttachLine(
            new OrderLineView
            {
                Id = 303,
                ItemId = 6,
                ItemName = "Товар",
                QtyOrdered = 1200
            },
            orderId: 78);
        state.ApplyCandidates(new WpfHuReservationCandidatesLineResult
        {
            ClientLineKey = "line-303",
            OrderLineId = 303,
            ItemId = 6,
            QtyOrdered = 1200,
            AvailableQty = 1200,
            AutoSelectedQty = 1200,
            Candidates =
            [
                new WpfHuReservationCandidateRow
                {
                    HuCode = "HU-0000401",
                    Source = "LEDGER_STOCK",
                    Qty = 600,
                    ShipReady = true,
                    AutoSelected = true
                }
            ]
        });

        state.ApplyManualSelection(["HU-0000401"]);

        Assert.True(state.ShouldSendOnApply);
        Assert.Equal(["HU-0000401"], state.SelectedHuCodes);
        Assert.Equal(600, state.BoundQty, 3);
        var row = Assert.Single(state.HuDisplayRows);
        Assert.Equal("HU-0000401", row.HuCode);
        Assert.Equal("склад", row.Label);
        Assert.True(row.IsBold);
    }

    [Fact]
    public void SetOrderContext_WithFreshServerPlan_DropsStaleSelectedHu()
    {
        using var context = new WpfServiceTestContext();
        IReadOnlyList<OrderReceiptPlanLine> planLines =
        [
            CreatePlanLine(1, "HU-000009"),
            CreatePlanLine(2, "HU-000010"),
            CreatePlanLine(3, "HU-000011"),
            CreatePlanLine(4, "HU-000012")
        ];
        using var coordinator = new CustomerOrderHuBindingCoordinator(
            context.ReadApi,
            _ => planLines);

        coordinator.BeginLoad();
        coordinator.SetOrderContext(55, OrderType.Customer, [CreateLine(qtyOrdered: 2400)]);
        coordinator.EndLoad();

        var initialState = Assert.Single(coordinator.Lines).State;
        Assert.Equal(
            new[] { "HU-000009", "HU-000010", "HU-000011", "HU-000012" },
            initialState.SelectedHuCodes.OrderBy(code => code, StringComparer.OrdinalIgnoreCase).ToArray());

        planLines =
        [
            CreatePlanLine(1, "HU-000009"),
            CreatePlanLine(2, "HU-000010"),
            CreatePlanLine(3, "HU-000011")
        ];
        coordinator.BeginLoad();
        coordinator.SetOrderContext(55, OrderType.Customer, [CreateLine(qtyOrdered: 1800)]);
        coordinator.EndLoad();

        var reloadedState = Assert.Single(coordinator.Lines).State;
        var selected = reloadedState.SelectedHuCodes
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(new[] { "HU-000009", "HU-000010", "HU-000011" }, selected);
        Assert.DoesNotContain("HU-000012", reloadedState.HuDisplayRows.Select(row => row.HuCode));
        Assert.Equal(3, reloadedState.HuDisplayRows.Count(row => row.Label == "склад"));
    }

    [Fact]
    public void HuDisplayRows_ShowWarehouseFirstBoldAndProductionSecondRegular()
    {
        var state = new CustomerOrderLineHuState("line-400");
        state.AttachLine(
            new OrderLineView
            {
                Id = 400,
                ItemId = 6,
                ItemName = "Товар",
                QtyOrdered = 1200,
                ProductionHuDisplayEntries =
                [
                    new OrderLineHuDisplayEntry("HU-0000576", "план", 600, IsWarehouseBound: false, SortOrder: 2)
                ]
            },
            orderId: 78);
        state.MergeExistingReservation("HU-0000446", 600);

        var rows = state.HuDisplayRows;

        Assert.Equal(2, rows.Count);
        Assert.Equal("HU-0000446", rows[0].HuCode);
        Assert.Equal("склад", rows[0].Label);
        Assert.True(rows[0].IsBold);
        Assert.Equal("HU-0000576", rows[1].HuCode);
        Assert.Equal("план", rows[1].Label);
        Assert.False(rows[1].IsBold);
    }

    [Fact]
    public void HuDisplayRows_SkipWarehouseDuplicateWhenProductionRowHasSameHu()
    {
        var state = new CustomerOrderLineHuState("line-401");
        state.AttachLine(
            new OrderLineView
            {
                Id = 401,
                ItemId = 6,
                ItemName = "Товар",
                QtyOrdered = 1200,
                ProductionHuDisplayEntries =
                [
                    new OrderLineHuDisplayEntry("HU-0000577", "наполнено", 600, IsWarehouseBound: false, SortOrder: 2)
                ]
            },
            orderId: 78);
        state.ApplyCandidates(new WpfHuReservationCandidatesLineResult
        {
            ClientLineKey = "line-401",
            OrderLineId = 401,
            ItemId = 6,
            QtyOrdered = 1200,
            AvailableQty = 600,
            AutoSelectedQty = 600,
            Candidates =
            [
                new WpfHuReservationCandidateRow
                {
                    HuCode = "HU-0000577",
                    Source = "LEDGER_STOCK",
                    Qty = 600,
                    ShipReady = true,
                    AutoSelected = true
                }
            ]
        });
        state.ApplyManualSelection(["HU-0000577"]);

        var row = Assert.Single(state.HuDisplayRows);
        Assert.Equal("HU-0000577", row.HuCode);
        Assert.Equal("наполнено", row.Label);
        Assert.False(row.IsBold);
    }

    [Fact]
    public void InternalOrderLineCompletionHighlight_UsesProducedQtyOnly()
    {
        var complete = new OrderLineView
        {
            ItemName = "Горчица, Печагин, 1 кг",
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            QtyOrdered = 3648,
            QtyProduced = 3648
        };
        var plannedOnly = new OrderLineView
        {
            ItemName = "Горчица, Печагин, 1 кг",
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            QtyOrdered = 3648,
            QtyProduced = 0,
            PlannedPalletQty = 3648,
            PlannedPalletCount = 4
        };
        var customer = new OrderLineView
        {
            ItemName = "Горчица, Печагин, 1 кг",
            ProductionPurpose = ProductionLinePurpose.CustomerOrder,
            QtyOrdered = 3648,
            QtyProduced = 3648
        };

        Assert.Equal("covered", complete.HuCoverageTone);
        Assert.Contains("выпущено 3648 из 3648", complete.HuCoverageToolTip, StringComparison.Ordinal);
        Assert.Equal("neutral", plannedOnly.HuCoverageTone);
        Assert.Equal("neutral", customer.HuCoverageTone);
    }

    private static OrderLineView CreateLine(double qtyOrdered)
    {
        return new OrderLineView
        {
            Id = 144,
            OrderId = 55,
            ItemId = 6,
            ItemName = "Горчица",
            QtyOrdered = qtyOrdered,
            QtyRemaining = qtyOrdered
        };
    }

    private static OrderReceiptPlanLine CreatePlanLine(long id, string huCode)
    {
        return new OrderReceiptPlanLine
        {
            Id = id,
            OrderId = 55,
            OrderLineId = 144,
            ItemId = 6,
            ItemName = "Горчица",
            QtyPlanned = 600,
            ToLocationId = 1,
            ToHu = huCode,
            SortOrder = (int)id
        };
    }

    private sealed class WpfServiceTestContext : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "flowstock-wpf-tests", Guid.NewGuid().ToString("N"));

        public WpfServiceTestContext()
        {
            Directory.CreateDirectory(_dir);
            ReadApi = new WpfReadApiService(
                new SettingsService(Path.Combine(_dir, "settings.json")),
                new FileLogger(Path.Combine(_dir, "app.log")));
        }

        public WpfReadApiService ReadApi { get; }

        public void Dispose()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
    }
}
