using FlowStock.Core.Models;
using FlowStock.Core.Services.Marking;

namespace FlowStock.Server.Tests.Marking;

public sealed class MarkingApplicationStatusCalculatorTests
{
    [Fact]
    public void Calculate_OrderRequirements_AggregatesOnlyRealRequiredQuantity()
    {
        var legacyOnly = MarkingApplicationStatusCalculator.Calculate(
        [
            new MarkingLineRequirement(100, 100, 0, null),
            new MarkingLineRequirement(50, 50, 0, null)
        ]);
        var mixedUncovered = MarkingApplicationStatusCalculator.Calculate(
        [
            new MarkingLineRequirement(100, 100, 0, null),
            new MarkingLineRequirement(50, 0, 49, null)
        ]);
        var mixedCovered = MarkingApplicationStatusCalculator.Calculate(
        [
            new MarkingLineRequirement(100, 100, 0, null),
            new MarkingLineRequirement(50, 0, 50, null)
        ]);

        Assert.Equal(MarkingStatus.NotRequired, legacyOnly);
        Assert.Equal(MarkingStatus.NotApplied, mixedUncovered);
        Assert.Equal(MarkingStatus.Applied, mixedCovered);
    }

    [Fact]
    public void Calculate_ConfigurationError_RemainsFailClosedForRealRequiredLine()
    {
        var status = MarkingApplicationStatusCalculator.Calculate(
        [new MarkingLineRequirement(10, 0, 10, "GTIN_REQUIRED")]);

        Assert.Equal(MarkingStatus.NotApplied, status);
    }

    [Fact]
    public void Calculate_MultipleRealRequiredLines_OneUncoveredCannotBeHiddenByLegacyOrCoveredLines()
    {
        var status = MarkingApplicationStatusCalculator.Calculate(
        [
            new MarkingLineRequirement(100, 100, 0, null),
            new MarkingLineRequirement(40, 0, 40, null),
            new MarkingLineRequirement(25, 0, 24, null)
        ]);

        Assert.Equal(MarkingStatus.NotApplied, status);
    }

    [Fact]
    public void Calculate_NonMarkingAndCancelledLinesAreOmittedAndLegacyConfigurationDoesNotClaimApplied()
    {
        // Non-marking and cancelled lines do not enter the requirement sequence. A fully
        // frozen legacy line remains applicable in the separate API flag, but requires no
        // real marking and therefore aggregates to NOT_REQUIRED.
        var status = MarkingApplicationStatusCalculator.Calculate(
        [new MarkingLineRequirement(80, 80, 0, null)]);

        Assert.Equal(MarkingStatus.NotRequired, status);
    }

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
