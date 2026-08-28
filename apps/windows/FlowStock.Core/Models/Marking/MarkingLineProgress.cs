namespace FlowStock.Core.Models.Marking;

public sealed record MarkingLineProgress(
    long OrderLineId,
    bool Applicable,
    string? ConfigurationError,
    string State,
    double RealRequiredQuantity,
    double ValidRealCoveredQuantity,
    double ActiveScopedQuantity,
    IReadOnlyList<MarkingLineRequestProgress> Requests);

public sealed record MarkingLineRequestProgress(
    Guid MarkingOrderId,
    string RequestNumber,
    int OperationalRequiredQuantity,
    int ImportedQuantity,
    string Classification);
