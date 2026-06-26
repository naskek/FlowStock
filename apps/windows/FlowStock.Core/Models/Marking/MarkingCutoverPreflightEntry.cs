namespace FlowStock.Core.Models.Marking;

public sealed record MarkingCutoverPreflightEntry(
    long? OrderId,
    long? OrderLineId,
    string IssueCode,
    string Level,
    double? TargetQty,
    int? RealCodeQty,
    int? LegacySyntheticQty,
    string Details,
    string SuggestedRemediation);
