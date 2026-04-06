namespace FlowStock.Core.Models.Marking;

public sealed class MarkingParsedFile
{
    public MarkingFileSourceType SourceType { get; init; }
    public string FileHash { get; init; } = string.Empty;
    public int TotalRows { get; init; }
    public int ValidRows { get; init; }
    public int InvalidRows { get; init; }
    public int DuplicateRowsInFile { get; init; }
    public string? DetectedRequestNumber { get; init; }
    public string? DetectedGtin { get; init; }
    public int DetectedQuantity { get; init; }
    public IReadOnlyList<string> AcceptedCodes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
