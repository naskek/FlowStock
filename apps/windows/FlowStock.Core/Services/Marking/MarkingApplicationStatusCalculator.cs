using FlowStock.Core.Models;

namespace FlowStock.Core.Services.Marking;

/// <summary>
/// Canonical three-state presentation contract. Applicability and coverage are
/// deliberately independent from remaining production need.
/// </summary>
public static class MarkingApplicationStatusCalculator
{
    public static MarkingStatus Calculate(bool hasActiveMarkingQuantity, bool hasCompleteAggregateCoverage)
    {
        if (!hasActiveMarkingQuantity)
        {
            return MarkingStatus.NotRequired;
        }

        return hasCompleteAggregateCoverage
            ? MarkingStatus.Applied
            : MarkingStatus.NotApplied;
    }
}
