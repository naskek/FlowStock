namespace FlowStock.Core.Models.Marking;

public sealed record MarkingPalletEligibility(
    long ProductionPalletId,
    bool IsEligible,
    string? BlockerCode,
    string? Message);
