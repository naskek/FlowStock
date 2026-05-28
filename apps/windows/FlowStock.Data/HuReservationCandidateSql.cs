namespace FlowStock.Data;

public static class HuReservationCandidateSql
{
    public const string SelectSources = @"
WITH requested_items AS (
    SELECT UNNEST(@item_ids::bigint[]) AS item_id
),
excluded_hu AS (
    SELECT UPPER(BTRIM(UNNEST(@exclude_hu_codes::text[]))) AS hu_code
    WHERE COALESCE(array_length(@exclude_hu_codes::text[], 1), 0) > 0
),
ledger_by_hu_item AS (
    SELECT led.item_id,
           UPPER(BTRIM(COALESCE(led.hu_code, led.hu))) AS hu_code,
           SUM(led.qty_delta) AS qty
    FROM ledger led
    INNER JOIN requested_items ri ON ri.item_id = led.item_id
    WHERE NULLIF(BTRIM(COALESCE(led.hu_code, led.hu)), '') IS NOT NULL
    GROUP BY led.item_id, UPPER(BTRIM(COALESCE(led.hu_code, led.hu)))
    HAVING SUM(led.qty_delta) > @qty_tolerance
),
first_receipts AS (
    SELECT item_id,
           hu_code,
           first_receipt_at,
           first_receipt_doc_id
    FROM (
        SELECT led.item_id,
               UPPER(BTRIM(COALESCE(led.hu_code, led.hu))) AS hu_code,
               COALESCE(NULLIF(BTRIM(led.ts), ''), NULLIF(BTRIM(d.created_at), ''), NULLIF(BTRIM(d.closed_at), '')) AS first_receipt_at,
               led.doc_id AS first_receipt_doc_id,
               ROW_NUMBER() OVER (
                   PARTITION BY led.item_id, UPPER(BTRIM(COALESCE(led.hu_code, led.hu)))
                   ORDER BY COALESCE(NULLIF(BTRIM(led.ts), ''), NULLIF(BTRIM(d.created_at), ''), NULLIF(BTRIM(d.closed_at), '')) NULLS LAST,
                            led.doc_id NULLS LAST,
                            led.id
               ) AS rn
        FROM ledger led
        LEFT JOIN docs d ON d.id = led.doc_id
        INNER JOIN requested_items ri ON ri.item_id = led.item_id
        WHERE led.qty_delta > 0
          AND NULLIF(BTRIM(COALESCE(led.hu_code, led.hu)), '') IS NOT NULL
    ) ranked
    WHERE rn = 1
),
reserved_candidates AS (
    SELECT p.item_id,
           UPPER(BTRIM(p.to_hu)) AS hu_code,
           p.order_id AS reserved_by_order_id,
           o.order_ref AS reserved_by_order_ref,
           1 AS source_priority
    FROM order_receipt_plan_lines p
    INNER JOIN orders o ON o.id = p.order_id
    INNER JOIN requested_items ri ON ri.item_id = p.item_id
    WHERE p.qty_planned > 0
      AND p.to_hu IS NOT NULL
      AND BTRIM(p.to_hu) <> ''
      AND o.order_type = @customer_order_type
      AND o.status <> @shipped_status
      AND o.status <> @cancelled_status
      AND o.status <> @merged_status
      AND (@customer_order_id::bigint IS NULL OR p.order_id <> @customer_order_id::bigint)

    UNION ALL

    SELECT p.item_id,
           UPPER(BTRIM(p.hu_code)) AS hu_code,
           p.order_id AS reserved_by_order_id,
           o.order_ref AS reserved_by_order_ref,
           0 AS source_priority
    FROM production_pallets p
    INNER JOIN orders o ON o.id = p.order_id
    INNER JOIN requested_items ri ON ri.item_id = p.item_id
    WHERE p.order_id IS NOT NULL
      AND p.status = @filled_status
      AND p.hu_code IS NOT NULL
      AND BTRIM(p.hu_code) <> ''
      AND o.order_type = @customer_order_type
      AND o.status <> @shipped_status
      AND o.status <> @cancelled_status
      AND o.status <> @merged_status
      AND (@customer_order_id::bigint IS NULL OR p.order_id <> @customer_order_id::bigint)
),
reserved_ranked AS (
    SELECT item_id,
           hu_code,
           reserved_by_order_id,
           reserved_by_order_ref,
           ROW_NUMBER() OVER (
               PARTITION BY item_id, hu_code
               ORDER BY source_priority, reserved_by_order_id, reserved_by_order_ref
           ) AS rn
    FROM reserved_candidates
),
reserved_map AS (
    SELECT item_id,
           hu_code,
           reserved_by_order_id,
           reserved_by_order_ref
    FROM reserved_ranked
    WHERE rn = 1
),
ledger_candidates AS (
    SELECT 'LEDGER_STOCK'::text AS source,
           lb.hu_code,
           lb.item_id,
           lb.qty,
           NULL::bigint AS source_order_id,
           NULL::text AS source_order_ref,
           NULL::bigint AS source_prd_doc_id,
           NULL::text AS source_prd_ref,
           fr.first_receipt_at,
           fr.first_receipt_doc_id,
           TRUE AS ship_ready,
           ''::text AS note
    FROM ledger_by_hu_item lb
    LEFT JOIN first_receipts fr ON fr.item_id = lb.item_id
                               AND fr.hu_code = lb.hu_code
    WHERE NOT EXISTS (
        SELECT 1
        FROM excluded_hu eh
        WHERE eh.hu_code = lb.hu_code
    )
      AND NOT EXISTS (
        SELECT 1
        FROM reserved_map rm
        WHERE rm.item_id = lb.item_id
          AND rm.hu_code = lb.hu_code
    )
),
combined AS (
    SELECT source,
           hu_code,
           item_id,
           qty,
           source_order_id,
           source_order_ref,
           source_prd_doc_id,
           source_prd_ref,
           first_receipt_at,
           first_receipt_doc_id,
           ship_ready,
           note
    FROM ledger_candidates
)
SELECT c.source,
       c.hu_code,
       c.item_id,
       c.qty,
       c.source_order_id,
       c.source_order_ref,
       c.source_prd_doc_id,
       c.source_prd_ref,
       c.first_receipt_at,
       c.first_receipt_doc_id,
       c.ship_ready,
       rm.reserved_by_order_id,
       rm.reserved_by_order_ref,
       c.note
FROM combined c
LEFT JOIN reserved_map rm ON rm.item_id = c.item_id
                         AND rm.hu_code = c.hu_code
ORDER BY CASE c.source WHEN 'LEDGER_STOCK' THEN 0 ELSE 1 END,
         c.first_receipt_at NULLS LAST,
         c.first_receipt_doc_id NULLS LAST,
         c.hu_code,
         c.source_order_ref,
         c.source_prd_ref,
         c.item_id;
";
}
