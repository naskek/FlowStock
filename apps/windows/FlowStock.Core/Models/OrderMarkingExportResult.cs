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

public sealed record OrderMarkingExportPreviewResult(
    bool IsSuccess,
    string Message,
    long OrderId,
    string OrderRef,
    int LineCount,
    double TotalQty,
    IReadOnlyList<OrderMarkingExportPreviewLine> Lines)
{
    public static OrderMarkingExportPreviewResult Failure(string message)
    {
        return new OrderMarkingExportPreviewResult(
            false,
            message,
            0,
            string.Empty,
            0,
            0,
            Array.Empty<OrderMarkingExportPreviewLine>());
    }
}

public sealed record OrderMarkingExportPreviewLine(
    long OrderLineId,
    long ItemId,
    string ItemName,
    string Gtin,
    double Qty,
    int HuCount,
    IReadOnlyList<string> HuCodes);
