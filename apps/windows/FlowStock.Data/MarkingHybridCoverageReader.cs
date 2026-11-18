using System.Data;
using Npgsql;

namespace FlowStock.Data;

/// <summary>
/// Diagnostic-only, server-owned view of legacy per-code and new synthetic
/// quantity sources. Not an eligibility decision or a replacement for the
/// existing marking_code read/write paths.
/// </summary>
public static class MarkingHybridCoverageReader
{
    public static async Task<MarkingHybridCoverageSnapshot> ReadTaskAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid markingOrderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        // Three SELECT statements must see one consistent snapshot. In
        // READ COMMITTED, a concurrent ISSUE/ASSIGN between statements can
        // make the diagnostic appear internally inconsistent or double count.
        if (transaction.IsolationLevel is not
            (IsolationLevel.RepeatableRead or IsolationLevel.Serializable))
        {
            throw new InvalidOperationException(
                "Hybrid coverage diagnostics require a REPEATABLE READ or SERIALIZABLE transaction.");
        }

        var rows = new List<MarkingHybridCoverageRow>();

        // No implicit join or deduplication of the two sources. In particular,
        // HistoricalUnknown is NOT considered a real DM or technical TEMP
        // without evidence of source type and the exact code prefix.
        await using (var command = CreateCommand(connection, transaction, """
SELECT c.import_id::text AS source_id,
       i.source_type,
       c.origin,
       c.status,
       c.receipt_doc_id,
       c.receipt_line_id,
       NULLIF(BTRIM(c.gtin), '') AS normalized_gtin,
       mo.order_line_id,
       mo.item_id,
       mo.order_id,
       COUNT(*)::bigint AS quantity,
       CASE WHEN c.code LIKE 'TEMP-CHZ-%'
                 AND i.source_type = 'temporary-chz-export'
                 AND c.origin IN ('LegacySynthetic', 'HistoricalUnknown')
                 THEN 'ProvenTemporary'
            WHEN c.origin IN ('RealImport', 'LegacyRealImport')
                 AND i.source_type IN ('csv', 'tsv')
                 AND c.code NOT LIKE 'TEMP-CHZ-%'
                 THEN 'RealCode'
            ELSE 'Unverified'
       END AS evidence
FROM marking_code c
INNER JOIN marking_code_import i ON i.id = c.import_id
INNER JOIN marking_order mo ON mo.id = c.marking_order_id
WHERE c.marking_order_id = @task
GROUP BY c.import_id, i.source_type, c.origin, c.status,
         c.receipt_doc_id, c.receipt_line_id,
         NULLIF(BTRIM(c.gtin), ''), mo.order_line_id,
         mo.item_id, mo.order_id,
         CASE WHEN c.code LIKE 'TEMP-CHZ-%'
                   AND i.source_type = 'temporary-chz-export'
                   AND c.origin IN ('LegacySynthetic', 'HistoricalUnknown')
                   THEN 'ProvenTemporary'
              WHEN c.origin IN ('RealImport', 'LegacyRealImport')
                   AND i.source_type IN ('csv', 'tsv')
                   AND c.code NOT LIKE 'TEMP-CHZ-%'
                   THEN 'RealCode'
              ELSE 'Unverified'
         END
ORDER BY c.import_id, c.origin, c.status, c.receipt_line_id;
""", markingOrderId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new MarkingHybridCoverageRow(
                    SourceFamily: "PerCode",
                    SourceId: reader.GetString(0),
                    SourceType: reader.GetString(1),
                    Origin: reader.GetString(2),
                    Status: reader.GetString(3),
                    ReceiptDocId: NullableInt64(reader, 4),
                    ReceiptLineId: NullableInt64(reader, 5),
                    NormalizedGtin: NullableString(reader, 6),
                    OrderLineId: NullableInt64(reader, 7),
                    ItemId: NullableInt64(reader, 8),
                    OrderId: NullableInt64(reader, 9),
                    Quantity: reader.GetInt64(10),
                    Evidence: reader.GetString(11)));
            }
        }

        // One pool contributes its free and voided balances once. These
        // balances are derived from immutable quantity events in V0048.
        await using (var command = CreateCommand(connection, transaction, """
SELECT b.pool_id::text, b.source_type, b.origin,
       b.order_line_id, b.item_id, mo.order_id,
       b.normalized_gtin, b.available_quantity,
       b.voided_quantity
FROM marking_synthetic_quantity_balance b
INNER JOIN marking_order mo ON mo.id = b.marking_order_id
WHERE b.marking_order_id = @task
ORDER BY b.pool_id;
""", markingOrderId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var sourceId = reader.GetString(0);
                var sourceType = reader.GetString(1);
                var origin = reader.GetString(2);
                var line = NullableInt64(reader, 3);
                var item = NullableInt64(reader, 4);
                var order = NullableInt64(reader, 5);
                var gtin = reader.GetString(6);
                var available = reader.GetInt64(7);
                var voided = reader.GetInt64(8);
                if (available > 0)
                {
                    rows.Add(new MarkingHybridCoverageRow(
                        "QuantityPool", sourceId, sourceType, origin, "Reserved",
                        null, null, gtin, line, item, order, available, "NewQuantity"));
                }
                if (voided > 0)
                {
                    rows.Add(new MarkingHybridCoverageRow(
                        "QuantityPool", sourceId, sourceType, origin, "Voided",
                        null, null, gtin, line, item, order, voided, "NewQuantity"));
                }
            }
        }

        // Applied quantity is attributed to each concrete receipt doc/line,
        // never silently merged with free coverage or another receipt line.
        await using (var command = CreateCommand(connection, transaction, """
SELECT b.pool_id::text, b.source_type, b.origin,
       b.order_line_id, b.item_id, mo.order_id, b.normalized_gtin,
       r.receipt_doc_id, r.receipt_line_id, r.applied_quantity
FROM marking_synthetic_quantity_receipt_balance r
INNER JOIN marking_synthetic_quantity_balance b ON b.pool_id = r.pool_id
INNER JOIN marking_order mo ON mo.id = b.marking_order_id
WHERE b.marking_order_id = @task
ORDER BY b.pool_id, r.receipt_doc_id, r.receipt_line_id;
""", markingOrderId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetInt64(9) <= 0)
                {
                    // The writer prohibits negative per-line balances.
                    // Do not present corrupt/negative quantities as coverage.
                    throw new InvalidOperationException(
                        "Quantity receipt balance must be strictly positive.");
                }

                rows.Add(new MarkingHybridCoverageRow(
                    "QuantityPool", reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), "Applied", reader.GetInt64(7),
                    reader.GetInt64(8), reader.GetString(6),
                    NullableInt64(reader, 3), NullableInt64(reader, 4),
                    NullableInt64(reader, 5), reader.GetInt64(9),
                    "NewQuantity"));
            }
        }

        var perCode = rows.Where(row => row.SourceFamily == "PerCode").ToArray();
        var quantityPool = rows.Where(row => row.SourceFamily == "QuantityPool").ToArray();
        var legacyActive = perCode
            .Where(row => row.Status is "Reserved" or "Applied")
            .Sum(row => row.Quantity);
        var quantityActive = quantityPool
            .Where(row => row.Status is "Reserved" or "Applied")
            .Sum(row => row.Quantity);
        var unverified = perCode.Any(row => row.Evidence == "Unverified");
        var mixed = perCode.Length != 0 && quantityPool.Length != 0;

        // Even distinct source IDs do not prove the logical quantity was not
        // generated twice. The reader reports observed counts, but declines
        // to assert usable combined coverage for mixed or ambiguous sources.
        return new MarkingHybridCoverageSnapshot(
            markingOrderId,
            rows,
            PerCodeRows: perCode.Sum(row => row.Quantity),
            QuantityIssued: quantityPool.Sum(row => row.Quantity),
            PerCodeObservedActive: legacyActive,
            QuantityObservedActive: quantityActive,
            ObservedPotentialActive: checked(legacyActive + quantityActive),
            RequiresReconciliation: mixed || unverified,
            UnambiguousObservedActive: mixed || unverified
                ? null
                : checked(legacyActive + quantityActive));
    }

    private static NpgsqlCommand CreateCommand(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid taskId)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("@task", taskId);
        return command;
    }

    private static long? NullableInt64(NpgsqlDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetInt64(index);

    private static string? NullableString(NpgsqlDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);
}

public sealed record MarkingHybridCoverageRow(
    string SourceFamily,
    string SourceId,
    string SourceType,
    string Origin,
    string Status,
    long? ReceiptDocId,
    long? ReceiptLineId,
    string? NormalizedGtin,
    long? OrderLineId,
    long? ItemId,
    long? OrderId,
    long Quantity,
    string Evidence);

public sealed record MarkingHybridCoverageSnapshot(
    Guid MarkingOrderId,
    IReadOnlyList<MarkingHybridCoverageRow> Rows,
    long PerCodeRows,
    long QuantityIssued,
    long PerCodeObservedActive,
    long QuantityObservedActive,
    long ObservedPotentialActive,
    bool RequiresReconciliation,
    long? UnambiguousObservedActive);
