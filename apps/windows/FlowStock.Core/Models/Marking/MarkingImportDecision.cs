namespace FlowStock.Core.Models.Marking;

public sealed class MarkingImportDecision
{
    public MarkingImportDecisionType DecisionType { get; init; }
    public Guid? TargetMarkingOrderId { get; init; }
    public decimal? MatchConfidence { get; init; }
    public string Reason { get; init; } = string.Empty;
}
