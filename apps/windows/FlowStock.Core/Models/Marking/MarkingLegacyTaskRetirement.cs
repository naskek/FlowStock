namespace FlowStock.Core.Models.Marking;

public sealed record MarkingLegacyTaskRetirementResult(
    string Mode,
    bool Eligible,
    bool WasApplied,
    IReadOnlyList<string> BlockerCodes,
    long OrderLineId,
    Guid MarkingOrderId,
    decimal TargetQuantity,
    int CandidateReservedQuantity,
    int RemainingTaskCount,
    int RemainingAppliedQuantity,
    int RemainingReservedQuantity,
    int RemainingVoidedQuantity,
    string PreflightHashBefore,
    string? PreflightHashAfter,
    string EligibilityHash,
    string? ResultingClassification,
    bool WasAlreadyApplied);
