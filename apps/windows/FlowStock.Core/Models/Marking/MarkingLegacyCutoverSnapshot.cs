namespace FlowStock.Core.Models.Marking;

public sealed record MarkingLegacyCutoverLineSnapshot(
    long OrderId,
    long OrderLineId,
    string OrderType,
    string MarkingResponsibility,
    long LineRevision,
    long ItemId,
    string? Gtin,
    decimal FrozenQuantity,
    decimal ShippedQuantityAtCutover,
    decimal FrozenUnshippedLegacyQuantity,
    decimal ProductionNeedSnapshot);

public sealed record MarkingLegacyCutoverSubjectSnapshot(
    Guid MarkingSubjectId,
    long OrderId,
    long OrderLineId,
    long ItemId,
    string Gtin,
    long SubjectRevision,
    decimal SubjectQuantity,
    Guid RootSubjectId,
    Guid? PredecessorSubjectId);
