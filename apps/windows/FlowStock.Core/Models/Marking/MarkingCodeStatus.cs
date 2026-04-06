namespace FlowStock.Core.Models.Marking;

public static class MarkingCodeStatus
{
    public const string Imported = "Imported";
    public const string Reserved = "Reserved";
    public const string AssignedToPrintBatch = "AssignedToPrintBatch";
    public const string Printed = "Printed";
    public const string Applied = "Applied";
    public const string Reported = "Reported";
    public const string Circulated = "Circulated";
    public const string Voided = "Voided";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        Imported,
        Reserved,
        AssignedToPrintBatch,
        Printed,
        Applied,
        Reported,
        Circulated,
        Voided
    };
}
