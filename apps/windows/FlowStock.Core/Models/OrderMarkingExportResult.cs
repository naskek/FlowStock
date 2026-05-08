namespace FlowStock.Core.Models;

public sealed record OrderMarkingExportResult(
    bool IsSuccess,
    string Message,
    byte[]? FileBytes,
    string FileName,
    int LineCount,
    int ExportLineCount,
    double RequiredQty,
    double CoveredQty,
    double CreatedCodeQty,
    double ReusedCodeQty,
    IReadOnlyList<OrderMarkingExportLineSummary> Lines)
{
    public static OrderMarkingExportResult Failure(string message)
    {
        return new OrderMarkingExportResult(
            false,
            message,
            null,
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            0,
            Array.Empty<OrderMarkingExportLineSummary>());
    }
}

public sealed record OrderMarkingExportLineSummary(
    long OrderLineId,
    long ItemId,
    string ItemName,
    string Gtin,
    double RequiredQty,
    double CoveredQty,
    double ExistingCodeQty,
    double ExportQty);
