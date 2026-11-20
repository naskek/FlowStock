using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class MarkingExcelService
{
    public MarkingExcelService(IDataStore data) { }
    public IReadOnlyList<MarkingOrderQueueRow> GetOrderQueue(bool includeCompleted) => Array.Empty<MarkingOrderQueueRow>();
    public MarkingExcelExportResult Export(IReadOnlyCollection<long> orderIds, DateTime generatedAt) =>
        Disabled();
    public MarkingExcelExportResult Export(IReadOnlyCollection<Guid> markingOrderIds,
        IReadOnlyCollection<long> orderIds, DateTime generatedAt) => Disabled();
    private static MarkingExcelExportResult Disabled() =>
        MarkingExcelExportResult.Empty("Откройте заказ и выберите «Сформировать Excel ЧЗ».");
}

public sealed record MarkingExcelExportResult(
    bool IsSuccess,
    string? Error,
    byte[]? FileBytes,
    IReadOnlyList<MarkingExportRow> Rows,
    IReadOnlyList<long> MarkedOrderIds,
    IReadOnlyList<Guid> MarkedMarkingOrderIds)
{
    public static MarkingExcelExportResult Empty(string error)
    {
        return new MarkingExcelExportResult(false, error, null, Array.Empty<MarkingExportRow>(), Array.Empty<long>(), Array.Empty<Guid>());
    }
}
