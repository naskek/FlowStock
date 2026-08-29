using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Abstractions;

/// <summary>
/// Canonical current-use projection over immutable marking requests/scopes and
/// durable membership of request-only Excel exports.
/// </summary>
public interface IMarkingRequestOperationalStore
{
    IReadOnlyList<MarkingRequestOperationalSnapshot> GetMarkingRequestOperationalSnapshots(long orderId);
    MarkingRequestExportBatchSnapshot? GetMarkingRequestExportBatch(long orderId, string expectedSnapshotHash);
    IReadOnlyList<MarkingRequestExportBatchSnapshot> GetMarkingRequestExportBatches(long orderId);
    void CreateMarkingRequestExportBatch(CreateMarkingRequestExportBatchCommand command);
}
