using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Abstractions;

public interface IMarkingLegacyTaskRetirementStore
{
    MarkingLegacyTaskRetirementResult DryRun(
        long orderLineId,
        Guid markingOrderId,
        string expectedPreflightHash,
        DateTime generatedAt);

    MarkingLegacyTaskRetirementResult Apply(
        long orderLineId,
        Guid markingOrderId,
        string expectedPreflightHash,
        string expectedEligibilityHash,
        string idempotencyKey,
        string actor,
        DateTime appliedAt);
}
