using FlowStock.Core.Models;
using FlowStock.Core.Services.Marking;

namespace FlowStock.Server.Tests.Marking;

public sealed class MarkingApplicationStatusCalculatorTests
{
    [Theory]
    [InlineData(false, false, MarkingStatus.NotRequired)]
    [InlineData(false, true, MarkingStatus.NotRequired)]
    [InlineData(true, false, MarkingStatus.NotApplied)]
    [InlineData(true, true, MarkingStatus.Applied)]
    public void StatusDependsOnApplicabilityAndAggregateCoverage_NotProductionNeed(
        bool applies,
        bool fullyCovered,
        MarkingStatus expected)
    {
        Assert.Equal(expected, MarkingApplicationStatusCalculator.Calculate(applies, fullyCovered));
    }

    [Fact]
    public void Customer1200_BindUnbindRebind_UsesSameReadyHuFactAndReturnsApplied()
    {
        const double activeMarkingQuantity = 1200;
        const double readyHuFactMarkedQuantity = 1200;
        const double remainingToProduce = 0;
        const bool hasProductionPlan = false;
        const bool hasExcelRequest = false;
        var factId = Guid.NewGuid();

        var boundStatus = MarkingApplicationStatusCalculator.Calculate(
            hasActiveMarkingQuantity: true,
            hasCompleteAggregateCoverage: readyHuFactMarkedQuantity >= activeMarkingQuantity);
        var unboundStatus = MarkingApplicationStatusCalculator.Calculate(
            hasActiveMarkingQuantity: true,
            hasCompleteAggregateCoverage: false);
        var reboundStatus = MarkingApplicationStatusCalculator.Calculate(
            hasActiveMarkingQuantity: true,
            hasCompleteAggregateCoverage: readyHuFactMarkedQuantity >= activeMarkingQuantity);

        Assert.Equal(MarkingStatus.Applied, boundStatus);
        Assert.Equal(MarkingStatus.NotApplied, unboundStatus);
        Assert.Equal(MarkingStatus.Applied, reboundStatus);
        Assert.Equal(0, remainingToProduce);
        Assert.False(hasProductionPlan);
        Assert.False(hasExcelRequest);
        Assert.NotEqual(Guid.Empty, factId); // binding state never rewrites the durable fact identity
    }
}
