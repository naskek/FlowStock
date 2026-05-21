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
reserved_candidates AS (
    SELECT p.item_id,
           UPPER(BTRIM(p.to_hu)) AS hu_code,
           p.order_id AS reserved_by_order_id,
           o.order_ref AS reserved_by_order_ref,
           ROW_NUMBER() OVER (
               PARTITION BY p.item_id, UPPER(BTRIM(p.to_hu))
               ORDER BY o.created_at, p.order_id, p.id
           ) AS rn
    FROM order_receipt_plan_lines p
    INNER JOIN orders o ON o.id = p.order_id
    INNER JOIN requested_items ri ON ri.item_id = p.item_id
    WHERE p.qty_planned > 0
      AND p.to_hu IS NOT NULL
      AND BTRIM(p.to_hu) <> ''
      AND o.order_type = @customer_order_type
      AND o.status <> @shipped_status
      AND o.status <> @cancelled_status
      AND (@customer_order_id IS NULL OR p.order_id <> @customer_order_id)
),
reserved_map AS (
    SELECT item_id,
           hu_code,
           reserved_by_order_id,
           reserved_by_order_ref
    FROM reserved_candidates
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
           TRUE AS ship_ready,
           ''::text AS note
    FROM ledger_by_hu_item lb
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
filled_pallet_items AS (
    SELECT UPPER(BTRIM(pp.hu_code)) AS hu_code,
           COALESCE(pll.item_id, pp.item_id) AS item_id,
           CASE
               WHEN COALESCE(pll.filled_qty, 0) > @qty_tolerance THEN pll.filled_qty
               ELSE COALESCE(pll.planned_qty, pp.planned_qty)
           END AS qty,
           pp.prd_doc_id,
           d.doc_ref AS prd_doc_ref,
           d.status AS prd_status,
           COALESCE(pp.order_id, d.order_id) AS source_order_id,
           COALESCE(o.order_ref, d.order_ref) AS source_order_ref,
           pp.to_location_id
    FROM production_pallets pp
    INNER JOIN docs d ON d.id = pp.prd_doc_id
    INNER JOIN orders o ON o.id = COALESCE(pp.order_id, d.order_id)
    LEFT JOIN production_pallet_lines pll ON pll.production_pallet_id = pp.id
    INNER JOIN requested_items ri ON ri.item_id = COALESCE(pll.item_id, pp.item_id)
    WHERE pp.status = @filled_status
      AND o.order_type = @internal_order_type
      AND o.status IN (@draft_order_status, @in_progress_order_status)
      AND d.status = @draft_doc_status
      AND d.status <> @closed_status
      AND NULLIF(BTRIM(pp.hu_code), '') IS NOT NULL
),
internal_candidates AS (
    SELECT 'INTERNAL_FILLED'::text AS source,
           fpi.hu_code,
           fpi.item_id,
           SUM(fpi.qty) AS qty,
           fpi.source_order_id,
           fpi.source_order_ref,
           fpi.prd_doc_id AS source_prd_doc_id,
           fpi.prd_doc_ref AS source_prd_ref,
           FALSE AS ship_ready,
           'FILLED, PRD не закрыт'::text AS note
    FROM filled_pallet_items fpi
    LEFT JOIN ledger_by_hu_item lb ON lb.item_id = fpi.item_id
                                 AND lb.hu_code = fpi.hu_code
    WHERE COALESCE(lb.qty, 0) <= @qty_tolerance
      AND NOT EXISTS (
        SELECT 1
        FROM excluded_hu eh
        WHERE eh.hu_code = fpi.hu_code
    )
      AND NOT EXISTS (
        SELECT 1
        FROM reserved_map rm
        WHERE rm.item_id = fpi.item_id
          AND rm.hu_code = fpi.hu_code
    )
    GROUP BY fpi.hu_code,
             fpi.item_id,
             fpi.source_order_id,
             fpi.source_order_ref,
             fpi.prd_doc_id,
             fpi.prd_doc_ref
    HAVING SUM(fpi.qty) > @qty_tolerance
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
           ship_ready,
           note
    FROM ledger_candidates

    UNION ALL

    SELECT source,
           hu_code,
           item_id,
           qty,
           source_order_id,
           source_order_ref,
           source_prd_doc_id,
           source_prd_ref,
           ship_ready,
           note
    FROM internal_candidates
)
SELECT c.source,
       c.hu_code,
       c.item_id,
       c.qty,
       c.source_order_id,
       c.source_order_ref,
       c.source_prd_doc_id,
       c.source_prd_ref,
       c.ship_ready,
       rm.reserved_by_order_id,
       rm.reserved_by_order_ref,
       c.note
FROM combined c
LEFT JOIN reserved_map rm ON rm.item_id = c.item_id
                         AND rm.hu_code = c.hu_code
ORDER BY CASE c.source WHEN 'LEDGER_STOCK' THEN 0 ELSE 1 END,
         c.hu_code,
         c.source_order_ref,
         c.source_prd_ref,
         c.item_id;
";
}
