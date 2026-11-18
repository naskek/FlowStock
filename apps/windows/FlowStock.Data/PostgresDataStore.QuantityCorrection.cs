using FlowStock.Core.Abstractions;

namespace FlowStock.Data;

public sealed partial class PostgresDataStore : IProductionReceiptQuantityCorrectionStore
{
    public IReadOnlyList<ReceiptQuantityCorrectionSlice> ReadReceiptQuantityCorrection(long docId, bool lockData)
    {
        if (lockData && (_connection == null || _transaction == null))
            throw new InvalidOperationException("Quantity correction requires the caller transaction.");

        return WithConnection(connection =>
        {
            if (lockData)
            {
                // Same conservative line-writer boundary and ordering as PRD Close.
                // NOWAIT prevents upgrades after TSD/COR line writes from deadlocking.
                using var locks = CreateCommand(connection, """
DO $$ BEGIN
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'QUANTITY_COR_READ_COMMITTED_REQUIRED' USING ERRCODE = '25001';
    END IF;
END $$;
LOCK TABLE doc_lines IN SHARE ROW EXCLUSIVE MODE NOWAIT;
SELECT id FROM doc_lines WHERE doc_id=@doc ORDER BY id FOR SHARE;
SELECT id FROM order_lines WHERE id IN (SELECT order_line_id FROM doc_lines WHERE doc_id=@doc) ORDER BY id FOR SHARE;
SELECT id FROM items WHERE id IN (SELECT item_id FROM doc_lines WHERE doc_id=@doc) ORDER BY id FOR SHARE;
SELECT id FROM item_types WHERE id IN (SELECT item_type_id FROM items WHERE id IN
    (SELECT item_id FROM doc_lines WHERE doc_id=@doc)) ORDER BY id FOR SHARE;
SELECT mo.id FROM marking_order mo WHERE mo.id IN (
    SELECT p.marking_order_id FROM marking_synthetic_quantity_pool p
    JOIN marking_synthetic_quantity_event e ON e.pool_id=p.id
    WHERE e.receipt_doc_id=@doc OR e.receipt_line_id IN (SELECT id FROM doc_lines WHERE doc_id=@doc)
    UNION SELECT c.marking_order_id FROM marking_code c WHERE c.receipt_doc_id=@doc
       OR c.receipt_line_id IN (SELECT id FROM doc_lines WHERE doc_id=@doc))
ORDER BY mo.id FOR UPDATE;
SELECT p.id FROM marking_synthetic_quantity_pool p WHERE EXISTS (
    SELECT 1 FROM marking_synthetic_quantity_event e WHERE e.pool_id=p.id AND
    (e.receipt_doc_id=@doc OR e.receipt_line_id IN (SELECT id FROM doc_lines WHERE doc_id=@doc)))
ORDER BY p.id FOR UPDATE;
""");
                locks.Parameters.AddWithValue("doc", docId);
                locks.ExecuteNonQuery();
            }

            // Include either side of a broken receipt binding. A prior RELEASE,
            // even net zero, is history and cannot silently authorize another COR.
            using var command = CreateCommand(connection, """
SELECT e.id, e.pool_id, e.receipt_line_id, e.quantity,
 COALESCE(e.action='ASSIGN' AND e.receipt_doc_id=@doc AND dl.doc_id=@doc
 AND d.type='PRODUCTION_RECEIPT' AND d.status='CLOSED'
 AND o.order_type='INTERNAL' AND ol.order_id=d.order_id AND ol.item_id=dl.item_id
 AND dl.qty > 0 AND dl.qty::numeric=TRUNC(dl.qty::numeric)
 AND dl.qty::text NOT IN ('NaN','Infinity','-Infinity')
 AND NOT EXISTS (SELECT 1 FROM doc_lines n WHERE n.replaces_line_id=dl.id)
 AND it.enable_marking AND NULLIF(BTRIM(i.gtin),'') IS NOT NULL
 AND p.item_id=dl.item_id AND p.item_id=mo.item_id
 AND p.order_line_id IS NOT DISTINCT FROM mo.order_line_id
 AND (p.order_line_id IS NULL OR p.order_line_id=dl.order_line_id)
 AND p.normalized_gtin=BTRIM(i.gtin) AND p.normalized_gtin=BTRIM(mo.gtin)
 AND p.source_type=BTRIM(mo.source_type)
 AND p.source_scope=JSONB_BUILD_OBJECT('source_type',BTRIM(mo.source_type),
     'source_order_id',mo.source_order_id,'order_id',mo.order_id)
 AND ((p.source_type='PRODUCTION_ORDER' AND mo.source_order_id=d.order_id
       AND (mo.order_id IS NULL OR mo.order_id=d.order_id))
      OR (p.source_type='PRODUCTION_NEED' AND mo.source_order_id IS NULL
          AND mo.order_id IS NULL AND p.order_line_id IS NULL))
 AND NOT EXISTS (SELECT 1 FROM marking_code c WHERE c.marking_order_id=p.marking_order_id)
 AND NOT EXISTS (SELECT 1 FROM marking_code c WHERE c.receipt_line_id IN
     (SELECT id FROM doc_lines WHERE doc_id=@doc) AND c.receipt_doc_id IS DISTINCT FROM @doc)
 AND NOT EXISTS (SELECT 1 FROM km_code c WHERE c.receipt_doc_id=@doc
     OR c.receipt_line_id IN (SELECT id FROM doc_lines WHERE doc_id=@doc))
 AND e.operation_id=MD5('quantity-prd:v1:' || d.id || ':' || dl.id || ':' || p.id)::uuid
 AND (SELECT COUNT(*) FROM marking_synthetic_quantity_event h WHERE h.pool_id=p.id
       AND (h.receipt_doc_id=@doc OR h.receipt_line_id IN (SELECT id FROM doc_lines WHERE doc_id=@doc))
       AND h.receipt_line_id=e.receipt_line_id)=1
 AND b.issued_quantity > 0 AND b.available_quantity >= 0 AND b.applied_quantity >= e.quantity
 AND b.voided_quantity >= 0 AND b.issued_quantity::numeric=b.available_quantity::numeric+b.applied_quantity::numeric+b.voided_quantity::numeric
 AND b.applied_quantity=(SELECT COALESCE(SUM(r.applied_quantity),0) FROM marking_synthetic_quantity_receipt_balance r WHERE r.pool_id=p.id)
 AND NOT EXISTS (SELECT 1 FROM marking_synthetic_quantity_receipt_balance r WHERE r.pool_id=p.id AND r.applied_quantity<=0)
 AND NOT EXISTS (SELECT 1 FROM production_marking_quantity_release_audit a WHERE a.assignment_event_id=e.id), FALSE) AS valid
FROM marking_synthetic_quantity_event e
JOIN marking_synthetic_quantity_pool p ON p.id=e.pool_id
JOIN marking_synthetic_quantity_balance b ON b.pool_id=p.id
JOIN marking_order mo ON mo.id=p.marking_order_id
LEFT JOIN doc_lines dl ON dl.id=e.receipt_line_id
LEFT JOIN docs d ON d.id=dl.doc_id
LEFT JOIN orders o ON o.id=d.order_id
LEFT JOIN order_lines ol ON ol.id=dl.order_line_id
LEFT JOIN items i ON i.id=dl.item_id
LEFT JOIN item_types it ON it.id=i.item_type_id
WHERE e.receipt_doc_id=@doc OR dl.doc_id=@doc
ORDER BY e.pool_id,e.receipt_line_id,e.id;
""");
            command.Parameters.AddWithValue("doc", docId);
            using var reader = command.ExecuteReader();
            var slices = new List<ReceiptQuantityCorrectionSlice>();
            while (reader.Read())
                slices.Add(new(reader.GetInt64(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetBoolean(4)));
            return (IReadOnlyList<ReceiptQuantityCorrectionSlice>)slices;
        });
    }

    public void ReleaseReceiptQuantityCorrection(long adjustmentId, long sourceDocId, long corDocId)
    {
        var slices = ReadReceiptQuantityCorrection(sourceDocId, lockData: true);
        if (slices.Any(slice => !slice.IsValid))
            throw new InvalidOperationException("QUANTITY_COR_INVALID_SOURCE");

        foreach (var slice in slices)
        {
            using var command = CreateCommand(_connection!, """
SELECT * FROM marking_synthetic_quantity_apply(
    (SELECT MD5('quantity-cor:v1:' || request_id || ':' || @assignment)::uuid
       FROM production_pallet_filling_adjustments WHERE id=@adjustment),
    @pool,'RELEASE',@qty,NULL,NULL,@doc,@line,'HU-CORRECTION');
INSERT INTO production_marking_quantity_release_audit(assignment_event_id,release_event_id,adjustment_id,cor_doc_id)
SELECT @assignment,e.id,@adjustment,@cor FROM marking_synthetic_quantity_event e
JOIN production_pallet_filling_adjustments j ON j.id=@adjustment
WHERE e.operation_id=MD5('quantity-cor:v1:' || j.request_id || ':' || @assignment)::uuid;
""");
            command.Parameters.AddWithValue("assignment", slice.AssignmentEventId);
            command.Parameters.AddWithValue("pool", slice.PoolId);
            command.Parameters.AddWithValue("qty", slice.Quantity);
            command.Parameters.AddWithValue("doc", sourceDocId);
            command.Parameters.AddWithValue("line", slice.ReceiptLineId);
            command.Parameters.AddWithValue("adjustment", adjustmentId);
            command.Parameters.AddWithValue("cor", corDocId);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("QUANTITY_COR_AUDIT_MISSING");
        }
    }
}
