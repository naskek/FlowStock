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
    public void PartialHuCoverageTooltip_UsesProductNameAndQuantities()
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

        Assert.Equal("missing", state.HuCoverageTone);
        Assert.Equal(
            "Горчица, Печагин, 1 кг: привязано 1200 из 1800, не хватает 600",
            state.HuCoverageToolTip);
        Assert.DoesNotContain("203", state.HuCoverageToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void FullHuCoverage_IsMarkedCovered()
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

        Assert.Equal("covered", state.HuCoverageTone);
        Assert.Equal(
            "Горчица, Печагин, 1 кг: привязано 1800 из 1800",
            state.HuCoverageToolTip);
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
