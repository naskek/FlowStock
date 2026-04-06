namespace FlowStock.Core.Models.Marking;

public static class MarkingOrderStatus
{
    public const string Draft = "Draft";
    public const string WaitingForCodes = "WaitingForCodes";
    public const string CodesBound = "CodesBound";
    public const string ReadyForPrint = "ReadyForPrint";
    public const string Printing = "Printing";
    public const string Printed = "Printed";
    public const string PartiallyApplied = "PartiallyApplied";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        Draft,
        WaitingForCodes,
        CodesBound,
        ReadyForPrint,
        Printing,
        Printed,
        PartiallyApplied,
        Completed,
        Failed,
        Cancelled
    };
}
