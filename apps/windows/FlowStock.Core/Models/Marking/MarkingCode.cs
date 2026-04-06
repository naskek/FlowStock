namespace FlowStock.Core.Models.Marking;

public sealed class MarkingCode
{
    public Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string CodeHash { get; init; } = string.Empty;
    public string? Gtin { get; init; }
    public Guid MarkingOrderId { get; init; }
    public Guid ImportId { get; init; }
    public string Status { get; init; } = MarkingCodeStatus.Imported;
    public int? SourceRowNumber { get; init; }
    public DateTime? PrintedAt { get; init; }
    public DateTime? AppliedAt { get; init; }
    public DateTime? ReportedAt { get; init; }
    public DateTime? IntroducedAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
