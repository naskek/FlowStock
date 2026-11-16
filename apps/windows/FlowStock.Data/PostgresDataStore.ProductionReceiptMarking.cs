using FlowStock.Core.Abstractions;

namespace FlowStock.Data;

public sealed partial class PostgresDataStore : IProductionReceiptMarkingStore
{
    public void LockProductionReceiptMarkingLines()
    {
        if (_connection == null || _transaction == null)
            throw new InvalidOperationException("PRD marking requires the Close transaction.");

        using var command = CreateCommand(_connection,
            "LOCK TABLE doc_lines IN SHARE ROW EXCLUSIVE MODE NOWAIT;");
        command.ExecuteNonQuery();
    }

    public void AssignProductionReceiptMarking(long docId, DateTime appliedAt)
    {
        if (_connection == null || _transaction == null)
            throw new InvalidOperationException("PRD marking requires the Close transaction.");

        using var command = CreateCommand(_connection,
            "SELECT marking_assign_prd_close(@doc, 'PRD-CLOSE', @at);");
        command.Parameters.AddWithValue("doc", docId);
        command.Parameters.AddWithValue("at", ToDbDate(appliedAt));
        command.ExecuteScalar();
    }
}
