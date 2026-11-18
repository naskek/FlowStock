using FlowStock.Core.Abstractions;
using FlowStock.Core.Models.Marking;

namespace FlowStock.Data;

/// <summary>
/// Explicitly dormant until the whole Excel/PRD/COR path is operator-qualified.
/// This never runs from the public/legacy Excel handlers by default.
/// </summary>
public sealed partial class PostgresDataStore : IMarkingQuantityIssueStore
{
    private const string ExcelQuantitySourceKey = "excel-quantity:v1";

    public MarkingTaskQuantityEvidence ReadMarkingTaskQuantityEvidence(Guid taskId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, """
SELECT
    (SELECT COUNT(*) FROM marking_code c WHERE c.marking_order_id = mo.id) AS history_count,
    (SELECT COUNT(*) FROM marking_code c
     WHERE c.marking_order_id = mo.id AND c.status <> 'Voided') AS active_codes,
    (SELECT COUNT(*) FROM marking_synthetic_quantity_pool p
     WHERE p.marking_order_id = mo.id) AS pool_count,
    COALESCE((
        SELECT SUM(b.issued_quantity - b.voided_quantity)::bigint
        FROM marking_synthetic_quantity_balance b
        WHERE b.marking_order_id = mo.id
    ), 0)::bigint AS active_quantity,
    EXISTS (
        SELECT 1
        FROM marking_synthetic_quantity_pool p
        JOIN marking_synthetic_quantity_balance b ON b.pool_id = p.id
        WHERE p.marking_order_id = mo.id AND (
            p.source_key <> @source_key
            OR p.origin <> 'SyntheticQuantity'
            OR p.item_id IS DISTINCT FROM mo.item_id
            OR p.order_line_id IS DISTINCT FROM mo.order_line_id
            OR p.normalized_gtin IS DISTINCT FROM BTRIM(mo.gtin)
            OR p.source_type IS DISTINCT FROM BTRIM(mo.source_type)
            OR p.source_scope IS DISTINCT FROM JSONB_BUILD_OBJECT(
                'source_type', BTRIM(mo.source_type),
                'source_order_id', mo.source_order_id,
                'order_id', mo.order_id)
            OR mo.source_type IS DISTINCT FROM 'PRODUCTION_ORDER'
            OR mo.source_order_id IS NULL
            OR mo.order_id IS DISTINCT FROM mo.source_order_id
            OR b.available_quantity < 0
            OR b.applied_quantity < 0
            OR b.voided_quantity <> 0
            OR b.issued_quantity <> b.available_quantity + b.applied_quantity + b.voided_quantity
        )
    ) AS invalid_pool
FROM marking_order mo
WHERE mo.id = @task_id;
""");
            command.Parameters.AddWithValue("task_id", taskId);
            command.Parameters.AddWithValue("source_key", ExcelQuantitySourceKey);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw new MarkingQuantityIssueException("MARKING_TASK_NOT_FOUND: задача ЧЗ не найдена.");

            var historyCount = reader.GetInt64(0);
            var activeCodes = reader.GetInt64(1);
            var poolCount = reader.GetInt64(2);
            var activeQuantity = reader.GetInt64(3);
            var invalid = reader.GetBoolean(4);
            if (poolCount > 1 || invalid || (poolCount > 0 && historyCount > 0) || activeQuantity < 0)
                throw new MarkingQuantityIssueException(
                    "MARKING_QUANTITY_AMBIGUOUS: несогласованная история технической маркировки. Экспорт остановлен.");
            return new MarkingTaskQuantityEvidence(
                poolCount > 0 ? activeQuantity : activeCodes,
                HasHistoricalCodes: historyCount > 0,
                HasQuantityPool: poolCount > 0);
        });
    }

    public void RequireResolvedCustomerAdoptedMarking(long customerOrderId)
    {
        // Adoption has only production-plan/HU provenance. PRINTED is a label,
        // and a free task-level legacy code cannot be attributed to an adopted
        // HU. Until #214 supplies authoritative code-to-HU evidence, the opt-in
        // writer must not guess the customer's new quantity.
        var hasUnresolvedAdoption = WithConnection(connection =>
        {
            using var command = CreateCommand(connection, """
SELECT EXISTS (
    SELECT 1
    FROM order_coverage_transfers t
    INNER JOIN order_coverage_transfer_lines l ON l.transfer_id = t.id
    WHERE t.target_order_id = @order_id
      AND t.compensation_kind IS NULL
      AND l.transferred_qty > 0
);
""");
            command.Parameters.AddWithValue("order_id", customerOrderId);
            return Convert.ToBoolean(command.ExecuteScalar());
        });
        if (hasUnresolvedAdoption)
            throw new MarkingQuantityIssueException(
                "ADOPTED_HU_MARKING_UNVERIFIED: перенесённые INTERNAL HU требуют подтверждения " +
                "маркировочного покрытия по источнику (#214); печать палетного ярлыка недостаточна.");
    }

    public long EnsureMarkingTaskQuantityIssued(Guid taskId, DateTime generatedAt)
    {
        if (_connection == null || _transaction == null)
            throw new InvalidOperationException("Excel quantity ISSUE requires the locked order transaction.");

        using (var isolation = CreateCommand(_connection,
                   "SELECT current_setting('transaction_isolation');"))
        {
            if (!string.Equals(Convert.ToString(isolation.ExecuteScalar()),
                    "read committed", StringComparison.Ordinal))
                throw new MarkingQuantityIssueException(
                    "EXCEL_QUANTITY_ISOLATION: требуется транзакция READ COMMITTED.");
        }

        int requested;
        using (var taskLock = CreateCommand(_connection, """
SELECT mo.requested_quantity
FROM marking_order mo
INNER JOIN orders o ON o.id = mo.source_order_id
INNER JOIN items i ON i.id = mo.item_id
INNER JOIN item_types it ON it.id = i.item_type_id
WHERE mo.id = @task_id
  AND mo.source_type = 'PRODUCTION_ORDER'
  AND mo.source_order_id = mo.order_id
  AND mo.order_line_id IS NULL
  AND mo.status NOT IN ('Cancelled', 'Failed')
  AND o.status NOT IN ('SHIPPED', 'CANCELLED', 'MERGED')
  AND COALESCE(it.enable_marking, FALSE)
  AND NULLIF(BTRIM(mo.gtin), '') IS NOT NULL
  AND BTRIM(mo.gtin) = BTRIM(i.gtin)
FOR UPDATE OF mo;
"""))
        {
            taskLock.Parameters.AddWithValue("task_id", taskId);
            var value = taskLock.ExecuteScalar();
            if (value == null || value is DBNull)
                throw new MarkingQuantityIssueException(
                    "EXCEL_QUANTITY_INELIGIBLE: источник, GTIN или состояние задачи изменились.");
            requested = Convert.ToInt32(value);
        }

        if (requested <= 0)
            throw new MarkingQuantityIssueException("EXCEL_QUANTITY_INVALID: количество должно быть положительным.");

        Guid? existingPoolId = null;
        using (var poolLock = CreateCommand(_connection, """
SELECT id
FROM marking_synthetic_quantity_pool
WHERE marking_order_id = @task_id
ORDER BY id
FOR UPDATE;
"""))
        {
            poolLock.Parameters.AddWithValue("task_id", taskId);
            using var reader = poolLock.ExecuteReader();
            if (reader.Read())
                existingPoolId = reader.GetGuid(0);
            if (reader.Read())
                throw new MarkingQuantityIssueException(
                    "MARKING_QUANTITY_AMBIGUOUS: для задачи существует несколько пулов.");
        }

        var evidence = ReadMarkingTaskQuantityEvidence(taskId);
        if (evidence.HasHistoricalCodes)
            throw new MarkingQuantityIssueException(
                "EXCEL_QUANTITY_LEGACY_CONFLICT: исторические поштучные коды нельзя перевести в quantity автоматически.");

        if (existingPoolId.HasValue != evidence.HasQuantityPool)
            throw new MarkingQuantityIssueException(
                "EXCEL_QUANTITY_CONCURRENT_CHANGE: изменился источник маркировки.");

        var delta = checked((long)requested - evidence.ActiveQuantity);
        if (delta > 0)
        {
            // Request retries serialize on marking_order; the requested target
            // is read again under lock. An HTTP retry after committed ISSUE
            // sees delta = 0. The journal itself stays append-only.
            using var issue = CreateCommand(_connection, """
SELECT issued_quantity
FROM marking_synthetic_quantity_apply(
    @operation_id, @pool_id, 'ISSUE', @quantity,
    @task_id, @source_key, NULL::bigint, NULL::bigint, 'EXCEL-CHZ'
);
""");
            issue.Parameters.AddWithValue("operation_id", Guid.NewGuid());
            issue.Parameters.AddWithValue("pool_id", existingPoolId ?? Guid.NewGuid());
            issue.Parameters.AddWithValue("quantity", delta);
            issue.Parameters.AddWithValue("task_id", taskId);
            issue.Parameters.AddWithValue("source_key", ExcelQuantitySourceKey);
            var issued = issue.ExecuteScalar();
            if (issued == null || issued is DBNull || Convert.ToInt64(issued) < requested)
                throw new MarkingQuantityIssueException(
                    "EXCEL_QUANTITY_ISSUE_MISMATCH: выпуск количества не подтверждён.");
        }

        // Unlike the legacy MarkMarkingOrdersPrinted, this path intentionally
        // permits the verified quantity pool and forbids legacy code history.
        using var mark = CreateCommand(_connection, """
UPDATE marking_order
SET status = 'Printed',
    codes_bound_at = COALESCE(codes_bound_at, @generated_at),
    updated_at = @generated_at
WHERE id = @task_id;
""");
        mark.Parameters.AddWithValue("task_id", taskId);
        mark.Parameters.AddWithValue("generated_at", ToDbDate(generatedAt));
        if (mark.ExecuteNonQuery() != 1)
            throw new MarkingQuantityIssueException("EXCEL_QUANTITY_STATUS_FAILED: задача не обновлена.");

        return Math.Max(0, delta);
    }
}
