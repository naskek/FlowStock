BEGIN READ ONLY;

WITH ledger_balance AS (
    SELECT l.item_id,
           UPPER(BTRIM(COALESCE(l.hu_code, l.hu))) AS hu_code,
           SUM(l.qty_delta) AS current_balance
    FROM ledger l
    WHERE NULLIF(BTRIM(COALESCE(l.hu_code, l.hu)), '') IS NOT NULL
    GROUP BY l.item_id, UPPER(BTRIM(COALESCE(l.hu_code, l.hu)))
),
active_customer_orders AS (
    SELECT o.id, o.order_ref
    FROM orders o
    WHERE UPPER(o.order_type) = 'CUSTOMER'
      AND UPPER(o.status) NOT IN ('SHIPPED', 'CANCELLED', 'CANCELED', 'MERGED')
)
SELECT 'STALE_ACTIVE_CUSTOMER_RESERVATION' AS section,
       o.id AS order_id,
       o.order_ref,
       p.order_line_id,
       p.item_id,
       BTRIM(p.to_hu) AS to_hu,
       p.qty_planned AS qty,
       COALESCE(lb.current_balance, 0) AS current_balance
FROM order_receipt_plan_lines p
INNER JOIN active_customer_orders o ON o.id = p.order_id
LEFT JOIN ledger_balance lb ON lb.item_id = p.item_id
                           AND lb.hu_code = UPPER(BTRIM(p.to_hu))
WHERE p.qty_planned > 0
  AND NULLIF(BTRIM(p.to_hu), '') IS NOT NULL
  AND COALESCE(lb.current_balance, 0) <= 0
ORDER BY o.id, p.order_line_id, BTRIM(p.to_hu);

WITH receipt_ledger AS (
    SELECT l.doc_id,
           l.item_id,
           UPPER(BTRIM(COALESCE(l.hu_code, l.hu))) AS hu_code,
           SUM(l.qty_delta) AS receipt_qty
    FROM ledger l
    WHERE l.qty_delta > 0
      AND NULLIF(BTRIM(COALESCE(l.hu_code, l.hu)), '') IS NOT NULL
    GROUP BY l.doc_id, l.item_id, UPPER(BTRIM(COALESCE(l.hu_code, l.hu)))
),
ledger_balance AS (
    SELECT l.item_id,
           UPPER(BTRIM(COALESCE(l.hu_code, l.hu))) AS hu_code,
           SUM(l.qty_delta) AS current_balance
    FROM ledger l
    WHERE NULLIF(BTRIM(COALESCE(l.hu_code, l.hu)), '') IS NOT NULL
    GROUP BY l.item_id, UPPER(BTRIM(COALESCE(l.hu_code, l.hu)))
)
SELECT 'FILLED_WITHOUT_RECEIPT_LEDGER' AS section,
       pp.id AS pallet_id,
       pp.prd_doc_id,
       d.doc_ref AS prd_doc_ref,
       d.status AS prd_status,
       COALESCE(pp.order_id, d.order_id) AS order_id,
       o.order_ref,
       o.order_type,
       o.status AS order_status,
       pp.order_line_id,
       pp.item_id,
       pp.hu_code,
       pp.planned_qty,
       COALESCE(rl.receipt_qty, 0) AS current_receipt_qty,
       COALESCE(lb.current_balance, 0) AS current_balance_qty,
       CASE
           WHEN UPPER(pp.status) = 'CANCELLED' THEN 'SKIP_CANCELLED'
           WHEN UPPER(pp.status) <> 'FILLED' THEN 'SKIP_NOT_FILLED'
           WHEN NULLIF(BTRIM(pp.hu_code), '') IS NULL THEN 'SKIP_NO_HU'
           WHEN pp.to_location_id IS NULL THEN 'SKIP_NO_LOCATION'
           WHEN COALESCE(rl.receipt_qty, 0) > 0 THEN 'SKIP_ALREADY_HAS_RECEIPT_LEDGER'
           ELSE 'SAFE_TO_BACKFILL'
       END AS decision
FROM production_pallets pp
INNER JOIN docs d ON d.id = pp.prd_doc_id
LEFT JOIN orders o ON o.id = COALESCE(pp.order_id, d.order_id)
LEFT JOIN receipt_ledger rl ON rl.doc_id = pp.prd_doc_id
                           AND rl.item_id = pp.item_id
                           AND rl.hu_code = UPPER(BTRIM(pp.hu_code))
LEFT JOIN ledger_balance lb ON lb.item_id = pp.item_id
                           AND lb.hu_code = UPPER(BTRIM(pp.hu_code))
WHERE UPPER(pp.status) <> 'CANCELLED'
  AND (
      UPPER(pp.status) = 'FILLED'
      OR NULLIF(BTRIM(pp.hu_code), '') IS NULL
      OR pp.to_location_id IS NULL
      OR COALESCE(rl.receipt_qty, 0) > 0
  )
  AND (
      UPPER(pp.status) <> 'FILLED'
      OR COALESCE(rl.receipt_qty, 0) <= 0
      OR NULLIF(BTRIM(pp.hu_code), '') IS NULL
      OR pp.to_location_id IS NULL
  )
ORDER BY pp.prd_doc_id, pp.id;

WITH internal_draft_prd AS (
    SELECT d.id AS doc_id,
           d.doc_ref,
           d.order_id,
           o.order_ref
    FROM docs d
    INNER JOIN orders o ON o.id = d.order_id
    WHERE UPPER(d.type) = 'PRD'
      AND UPPER(d.status) = 'DRAFT'
      AND UPPER(o.order_type) = 'INTERNAL'
),
ordered AS (
    SELECT ol.order_id,
           SUM(GREATEST(0, ol.qty_ordered)) AS ordered_qty
    FROM order_lines ol
    INNER JOIN internal_draft_prd prd ON prd.order_id = ol.order_id
    GROUP BY ol.order_id
),
gross_receipt AS (
    SELECT d.order_id,
           SUM(GREATEST(0, l.qty_delta)) AS gross_receipt_qty
    FROM docs d
    INNER JOIN internal_draft_prd prd ON prd.order_id = d.order_id
    INNER JOIN ledger l ON l.doc_id = d.id
    WHERE UPPER(d.type) = 'PRD'
      AND l.qty_delta > 0
    GROUP BY d.order_id
),
blocking_pallets AS (
    SELECT prd.doc_id,
           COUNT(*) FILTER (WHERE UPPER(pp.status) IN ('PLANNED', 'PRINTED')) AS open_unfilled_pallet_count
    FROM internal_draft_prd prd
    LEFT JOIN production_pallets pp ON pp.prd_doc_id = prd.doc_id
                                  AND UPPER(pp.status) <> 'CANCELLED'
    GROUP BY prd.doc_id
)
SELECT 'STALE_INTERNAL_DRAFT_PRD_CLOSE_CANDIDATES' AS section,
       prd.doc_id,
       prd.doc_ref,
       prd.order_id,
       prd.order_ref,
       COALESCE(gross.gross_receipt_qty, 0) AS gross_receipt_qty_by_order,
       COALESCE(ordered.ordered_qty, 0) AS ordered_qty_by_order,
       COALESCE(blocking.open_unfilled_pallet_count, 0) AS open_unfilled_pallet_count,
       CASE
           WHEN COALESCE(blocking.open_unfilled_pallet_count, 0) > 0 THEN 'BLOCKED_OPEN_PALLETS'
           WHEN COALESCE(gross.gross_receipt_qty, 0) + 0.000001 >= COALESCE(ordered.ordered_qty, 0)
                AND COALESCE(ordered.ordered_qty, 0) > 0 THEN 'SAFE_TO_CLOSE'
           ELSE 'BLOCKED_GROSS_RECEIPT_SHORTAGE'
       END AS decision
FROM internal_draft_prd prd
LEFT JOIN ordered ON ordered.order_id = prd.order_id
LEFT JOIN gross_receipt gross ON gross.order_id = prd.order_id
LEFT JOIN blocking_pallets blocking ON blocking.doc_id = prd.doc_id
WHERE COALESCE(blocking.open_unfilled_pallet_count, 0) = 0
  AND COALESCE(gross.gross_receipt_qty, 0) + 0.000001 >= COALESCE(ordered.ordered_qty, 0)
  AND COALESCE(ordered.ordered_qty, 0) > 0
ORDER BY prd.doc_id;

SELECT 'DRAFT_PRD_WITH_LEDGER' AS section,
       d.id AS prd_doc_id,
       d.doc_ref AS prd_doc_ref,
       d.order_id,
       COUNT(l.id) AS ledger_row_count,
       COALESCE(SUM(l.qty_delta), 0) AS ledger_qty_delta
FROM docs d
INNER JOIN ledger l ON l.doc_id = d.id
WHERE UPPER(d.type) = 'PRD'
  AND UPPER(d.status) = 'DRAFT'
GROUP BY d.id, d.doc_ref, d.order_id
ORDER BY d.id;

SELECT 'DUPLICATE_ACTIVE_PRODUCTION_HU' AS section,
       UPPER(BTRIM(pp.hu_code)) AS hu_code,
       COUNT(*) AS pallet_count,
       ARRAY_AGG(pp.id ORDER BY pp.id) AS production_pallet_ids,
       ARRAY_AGG(pp.prd_doc_id ORDER BY pp.id) AS prd_doc_ids,
       ARRAY_AGG(pp.order_id ORDER BY pp.id) AS order_ids
FROM production_pallets pp
WHERE NULLIF(BTRIM(pp.hu_code), '') IS NOT NULL
  AND UPPER(pp.status) <> 'CANCELLED'
GROUP BY UPPER(BTRIM(pp.hu_code))
HAVING COUNT(*) > 1
ORDER BY hu_code;

WITH active_customer_orders AS (
    SELECT o.id, o.order_ref
    FROM orders o
    WHERE UPPER(o.order_type) = 'CUSTOMER'
      AND UPPER(o.status) NOT IN ('SHIPPED', 'CANCELLED', 'CANCELED', 'MERGED')
)
SELECT 'HU_RESERVED_TO_MULTIPLE_ACTIVE_CUSTOMERS' AS section,
       p.item_id,
       UPPER(BTRIM(p.to_hu)) AS to_hu,
       COUNT(DISTINCT p.order_id) AS active_order_count,
       ARRAY_AGG(DISTINCT p.order_id ORDER BY p.order_id) AS order_ids
FROM order_receipt_plan_lines p
INNER JOIN active_customer_orders o ON o.id = p.order_id
WHERE p.qty_planned > 0
  AND NULLIF(BTRIM(p.to_hu), '') IS NOT NULL
GROUP BY p.item_id, UPPER(BTRIM(p.to_hu))
HAVING COUNT(DISTINCT p.order_id) > 1
ORDER BY p.item_id, to_hu;

WITH receipt_ledger AS (
    SELECT l.doc_id,
           UPPER(BTRIM(COALESCE(l.hu_code, l.hu))) AS hu_code,
           COUNT(*) AS ledger_row_count,
           SUM(l.qty_delta) AS ledger_qty
    FROM ledger l
    WHERE NULLIF(BTRIM(COALESCE(l.hu_code, l.hu)), '') IS NOT NULL
    GROUP BY l.doc_id, UPPER(BTRIM(COALESCE(l.hu_code, l.hu)))
)
SELECT 'PALLETIZED_PRD_SUMMARY' AS section,
       d.id AS prd_doc_id,
       d.doc_ref AS prd_doc_ref,
       d.status AS prd_status,
       COUNT(pp.id) AS pallet_count,
       COUNT(*) FILTER (WHERE UPPER(pp.status) = 'FILLED') AS filled_pallet_count,
       COUNT(*) FILTER (WHERE UPPER(pp.status) IN ('PLANNED', 'PRINTED')) AS open_pallet_count,
       COUNT(*) FILTER (WHERE UPPER(pp.status) = 'CANCELLED') AS cancelled_pallet_count,
       COUNT(*) FILTER (WHERE COALESCE(rl.ledger_row_count, 0) > 0) AS pallets_with_ledger,
       COUNT(*) FILTER (WHERE UPPER(pp.status) = 'FILLED' AND COALESCE(rl.ledger_row_count, 0) = 0) AS filled_without_ledger_count,
       COALESCE(SUM(rl.ledger_qty), 0) AS ledger_qty
FROM docs d
INNER JOIN production_pallets pp ON pp.prd_doc_id = d.id
LEFT JOIN receipt_ledger rl ON rl.doc_id = d.id
                       AND rl.hu_code = UPPER(BTRIM(pp.hu_code))
WHERE UPPER(d.type) = 'PRD'
GROUP BY d.id, d.doc_ref, d.status
ORDER BY d.id;

ROLLBACK;
