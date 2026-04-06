namespace FlowStock.Core.Models.Marking;

public sealed class MarkingImportResult
{
    public string FileName { get; init; } = string.Empty;
    public string FileHash { get; init; } = string.Empty;
    public Guid? ImportId { get; init; }
    public MarkingParsedFile? ParsedFile { get; init; }
    public MarkingImportDecision Decision { get; init; } = new();
    public int PersistedCodeCount { get; init; }
    public int SkippedExistingCodeCount { get; init; }
}
