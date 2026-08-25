using FlowStock.Core.Models;

namespace FlowStock.Core.Services.Marking;

/// <summary>
/// Canonical three-state presentation contract. Applicability and coverage are
/// deliberately independent from remaining production need.
/// </summary>
public static class MarkingApplicationStatusCalculator
{
    public static MarkingStatus Calculate(IReadOnlyCollection<MarkingLineRequirement> lines)
    {
        var realRequired = 0d;
        foreach (var line in lines)
        {
            var applicable = Math.Max(0, line.MarkingApplicableQuantity);
            var legacyExempt = Math.Min(applicable, Math.Max(0, line.LegacyExemptQuantity));
            var required = Math.Max(0, applicable - legacyExempt);
            realRequired += required;

            if (required <= 0.000001)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(line.ConfigurationError)
                || Math.Min(required, Math.Max(0, line.ValidRealCoveredQuantity)) + 0.000001 < required)
            {
                return MarkingStatus.NotApplied;
            }
        }

        return realRequired <= 0.000001
            ? MarkingStatus.NotRequired
            : MarkingStatus.Applied;
    }

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

public sealed record MarkingLineRequirement(
    double MarkingApplicableQuantity,
    double LegacyExemptQuantity,
    double ValidRealCoveredQuantity,
    string? ConfigurationError);
