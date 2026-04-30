namespace FlowStock.Core.Models;

public sealed class MarkingExportRow
{
    public string ItemName { get; init; } = string.Empty;
    public string Gtin { get; init; } = string.Empty;
    public double Qty { get; init; }
}
