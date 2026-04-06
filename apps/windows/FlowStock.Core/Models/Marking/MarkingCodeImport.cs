namespace FlowStock.Core.Models.Marking;

public sealed class MarkingCodeImport
{
    public Guid Id { get; init; }
    public string OriginalFilename { get; init; } = string.Empty;
    public string StoragePath { get; init; } = string.Empty;
    public string FileHash { get; init; } = string.Empty;
    public string SourceType { get; init; } = string.Empty;
    public string? DetectedRequestNumber { get; init; }
    public string? DetectedGtin { get; init; }
    public int? DetectedQuantity { get; init; }
    public Guid? MatchedMarkingOrderId { get; init; }
    public decimal? MatchConfidence { get; init; }
    public string Status { get; init; } = MarkingCodeImportStatus.New;
    public int ImportedRows { get; init; }
    public int ValidCodeRows { get; init; }
    public int DuplicateCodeRows { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ProcessedAt { get; init; }
}
