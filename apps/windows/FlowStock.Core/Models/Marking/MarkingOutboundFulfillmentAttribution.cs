namespace FlowStock.Core.Models.Marking;

public static class MarkingOutboundBasis
{
    public const string LegacyExempt = "LEGACY_EXEMPT";
    public const string RealReady = "REAL_READY";
}

public sealed record MarkingOutboundFulfillmentDecision(
    long OutboundDocId,
    long OutboundDocLineId,
    long OrderId,
    long OrderLineId,
    long ItemId,
    long? HuId,
    string NormalizedHu,
    decimal Quantity,
    string Basis,
    long? FrozenLineScopeId,
    Guid? RealReadyHuFactId,
    string DecisionHash,
    string Actor,
    DateTime DecidedAt);
