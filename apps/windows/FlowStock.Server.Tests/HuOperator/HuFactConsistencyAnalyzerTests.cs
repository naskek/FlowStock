using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server.Tests.HuOperator;

public sealed class HuFactConsistencyAnalyzerTests
{
    [Theory]
    [InlineData(ProductionPalletStatus.Planned, 100d)]
    [InlineData(ProductionPalletStatus.Printed, 100d)]
    [InlineData(ProductionPalletStatus.Planned, 1d)]
    [InlineData(ProductionPalletStatus.Printed, 1d)]
    public void NonFilledProductionHuWithAnyComponentProgress_ReportsStatusFillMismatch(
        string palletStatus,
        double filledQty)
    {
        var analysis = HuFactConsistencyAnalyzer.Analyze(new HuOperatorFacts
        {
            HuCode = "HU-PLANNED-COMPLETE",
            ProductionPallets =
            [
                new HuOperatorProductionPalletFact
                {
                    PalletId = 9,
                    Status = palletStatus,
                    Components =
                    [
                        new HuOperatorComponentFact { ItemId = 1, PlannedQty = 100, FilledQty = filledQty }
                    ]
                }
            ]
        });

        Assert.Contains(
            analysis.Issues,
            issue => issue.Code == HuFactConsistencyIssueCode.ProductionStatusFillMismatch);
    }

    [Fact]
    public void MixedProductionHuWithOnlySomeCompletedComponents_ReportsPartialFill()
    {
        var analysis = HuFactConsistencyAnalyzer.Analyze(new HuOperatorFacts
        {
            HuCode = "HU-MIXED-PARTIAL",
            ProductionPallets =
            [
                new HuOperatorProductionPalletFact
                {
                    PalletId = 10,
                    Status = ProductionPalletStatus.Printed,
                    Components =
                    [
                        new HuOperatorComponentFact { ItemId = 1, PlannedQty = 100, FilledQty = 100 },
                        new HuOperatorComponentFact { ItemId = 2, PlannedQty = 50, FilledQty = 0 }
                    ]
                }
            ]
        });

        Assert.Contains(
            analysis.Issues,
            issue => issue.Code == HuFactConsistencyIssueCode.PartialComponentFill);
    }
}
