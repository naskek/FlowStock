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
)
SELECT 'FILLED_PRODUCTION_PALLET_WITHOUT_LEDGER' AS section,
       pp.id AS production_pallet_id,
       pp.prd_doc_id,
       d.doc_ref AS prd_doc_ref,
       pp.order_id,
       pp.order_line_id,
       pp.item_id,
       pp.hu_code,
       pp.planned_qty,
       COALESCE(rl.receipt_qty, 0) AS receipt_ledger_qty
FROM production_pallets pp
INNER JOIN docs d ON d.id = pp.prd_doc_id
LEFT JOIN receipt_ledger rl ON rl.doc_id = pp.prd_doc_id
                           AND rl.item_id = pp.item_id
                           AND rl.hu_code = UPPER(BTRIM(pp.hu_code))
WHERE UPPER(pp.status) = 'FILLED'
  AND COALESCE(rl.receipt_qty, 0) <= 0
ORDER BY pp.prd_doc_id, pp.id;

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
