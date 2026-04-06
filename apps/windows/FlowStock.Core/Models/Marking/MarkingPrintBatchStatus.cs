namespace FlowStock.Core.Models.Marking;

public static class MarkingPrintBatchStatus
{
    public const string New = "New";
    public const string Ready = "Ready";
    public const string Printing = "Printing";
    public const string WaitingConfirmation = "WaitingConfirmation";
    public const string Completed = "Completed";
    public const string Reprinted = "Reprinted";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        New,
        Ready,
        Printing,
        WaitingConfirmation,
        Completed,
        Reprinted,
        Failed,
        Cancelled
    };
}
