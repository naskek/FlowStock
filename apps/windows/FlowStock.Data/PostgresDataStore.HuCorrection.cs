using FlowStock.Core.Abstractions;

namespace FlowStock.Data;

public sealed partial class PostgresDataStore : IProductionReceiptHuCorrectionStore
{
    public bool IsConsumedHuReceiptCorrectionProven(long sourceDocId) => WithConnection(connection =>
    {
        using var command = CreateCommand(connection, """
SELECT NOT EXISTS (
    SELECT 1 FROM marking_hu_current_reservation r
    JOIN marking_hu_current_transfer_line scope ON scope.id=r.transfer_line_id
    JOIN doc_lines dl ON dl.id=scope.current_line_id
    WHERE (r.consumed_doc_id=@doc OR dl.doc_id=@doc)
      AND (r.state<>'CONSUMED' OR r.consumed_doc_id IS DISTINCT FROM @doc
        OR r.consumed_line_id IS DISTINCT FROM scope.current_line_id
        OR NOT marking_hu_consumed_proven(r.id))
);
""");
        command.Parameters.AddWithValue("doc", sourceDocId);
        return (bool)command.ExecuteScalar()!;
    });

    public void PrepareHuReceiptCorrection(long adjustmentId, long sourcePalletId, long replacementPalletId, long corDocId)
    {
        if (_connection == null || _transaction == null)
            throw new InvalidOperationException("Adopted HU correction requires the caller transaction.");
        using var command = CreateCommand(_connection,
            "SELECT marking_hu_prepare_correction(@adjustment,@source,@replacement,@cor)");
        command.Parameters.AddWithValue("adjustment", adjustmentId);
        command.Parameters.AddWithValue("source", sourcePalletId);
        command.Parameters.AddWithValue("replacement", replacementPalletId);
        command.Parameters.AddWithValue("cor", corDocId);
        command.ExecuteNonQuery();
    }

    public void FinishHuReceiptCorrection(long adjustmentId)
    {
        if (_connection == null || _transaction == null)
            throw new InvalidOperationException("Adopted HU correction requires the caller transaction.");
        using var command = CreateCommand(_connection, "SELECT marking_hu_finish_correction(@adjustment)");
        command.Parameters.AddWithValue("adjustment", adjustmentId);
        command.ExecuteNonQuery();
    }
}
