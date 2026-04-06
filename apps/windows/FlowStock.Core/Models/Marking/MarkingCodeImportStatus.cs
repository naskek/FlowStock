namespace FlowStock.Core.Models.Marking;

public static class MarkingCodeImportStatus
{
    public const string New = "New";
    public const string Processing = "Processing";
    public const string Bound = "Bound";
    public const string ManualReview = "ManualReview";
    public const string Failed = "Failed";
    public const string Archived = "Archived";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        New,
        Processing,
        Bound,
        ManualReview,
        Failed,
        Archived
    };
}
