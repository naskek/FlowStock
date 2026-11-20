-- #214: read-only diagnostic for planned INTERNAL -> CUSTOMER HU adoption.
-- Run ONLY against an isolated PostgreSQL copy with the PR migration tree:
--   psql --no-password --set=ON_ERROR_STOP=1 -v target_order_id=403 \
--     -f deploy/scripts/diagnostics/marking-adoption-coverage.sql
-- A row's source_candidate_free_* values are SOURCE-ORDER/ITEM totals shared
-- by all its transferred HU. NEVER sum them per HU or use them as proof.
-- This query does not assign/reserve/transfer any code or synthetic quantity.
--
-- A PRINTED pallet label has no bearing on marking evidence. Existing
-- marking_order/marking_code/quantity_pool do not bind *free* marking
-- units to a specific PLANNED/PRINTED HU. The provable allocated amount is
-- therefore always zero until a separate guarded reservation contract exists.
WITH transfer_lines AS (
    SELECT t.id AS transfer_id,
           t.source_order_id,
           t.source_order_ref,
           t.target_order_id,
           t.target_order_ref,
           t.source_prd_doc_id,
           t.target_prd_doc_id,
           t.production_pallet_id,
           t.hu_code,
           t.pallet_status_at_transfer,
           t.compensation_kind,
           l.source_order_line_id,
           l.target_order_line_id,
           l.source_doc_line_id,
           l.item_id,
           l.transferred_qty,
           NULLIF(BTRIM(i.gtin), '') AS normalized_gtin,
           COALESCE(it.enable_marking, FALSE)
               AND NULLIF(BTRIM(i.gtin), '') IS NOT NULL AS markable,
           pp.id IS NOT NULL
               AND pp.status IN ('PLANNED', 'PRINTED')
               AND pp.hu_code = t.hu_code
               AND pp.order_id = t.target_order_id
               AND pp.prd_doc_id = t.target_prd_doc_id AS pallet_identity_valid,
           dl.id IS NOT NULL
               AND dl.item_id = l.item_id
               AND dl.order_line_id = l.target_order_line_id
               AND dl.doc_id = t.target_prd_doc_id
               AND ABS(dl.qty - l.transferred_qty) <= 0.000001 AS line_identity_valid,
           ol.id IS NOT NULL
               AND ol.order_id = t.target_order_id
               AND ol.item_id = l.item_id AS customer_line_valid
    FROM order_coverage_transfers t
    JOIN order_coverage_transfer_lines l ON l.transfer_id = t.id
    JOIN items i ON i.id = l.item_id
    LEFT JOIN item_types it ON it.id = i.item_type_id
    LEFT JOIN production_pallets pp ON pp.id = t.production_pallet_id
    LEFT JOIN doc_lines dl ON dl.id = l.source_doc_line_id
    LEFT JOIN order_lines ol ON ol.id = l.target_order_line_id
    WHERE t.target_order_id = :'target_order_id'::bigint
      AND t.transfer_type = 'PLANNED_PALLET_ADOPTION'
),
source_scopes AS (
    SELECT DISTINCT source_order_id, item_id, normalized_gtin
    FROM transfer_lines
    WHERE markable AND normalized_gtin IS NOT NULL
),
source_tasks AS (
    SELECT DISTINCT scopes.source_order_id,
           scopes.item_id,
           scopes.normalized_gtin,
           mo.id AS marking_order_id
    FROM source_scopes scopes
    JOIN marking_order mo
      ON mo.item_id = scopes.item_id
     AND NULLIF(BTRIM(mo.gtin), '') = scopes.normalized_gtin
     AND mo.source_type = 'PRODUCTION_ORDER'
     AND (mo.source_order_id = scopes.source_order_id
          OR (mo.source_order_id IS NULL AND mo.order_id = scopes.source_order_id))
     AND (mo.order_id IS NULL OR mo.order_id = scopes.source_order_id)
     AND mo.status NOT IN ('Cancelled', 'Failed')
     AND (mo.order_line_id IS NULL OR EXISTS (
          SELECT 1
          FROM transfer_lines t
          WHERE t.source_order_id = scopes.source_order_id
            AND t.item_id = scopes.item_id
            AND t.source_order_line_id = mo.order_line_id
     ))
),
per_code AS (
    SELECT s.source_order_id, s.item_id, s.normalized_gtin,
           s.marking_order_id,
           COUNT(c.id)::bigint AS history_rows,
           COUNT(c.id) FILTER (
               WHERE c.status IN ('Reserved', 'Printed')
                 AND c.receipt_doc_id IS NULL AND c.receipt_line_id IS NULL
                 AND (
                   (c.code LIKE 'TEMP-CHZ-%'
                    AND imp.source_type = 'temporary-chz-export'
                    AND c.origin IN ('LegacySynthetic', 'HistoricalUnknown'))
                   OR
                   (c.code NOT LIKE 'TEMP-CHZ-%'
                    AND imp.source_type IN ('csv', 'tsv')
                    AND c.origin IN ('RealImport', 'LegacyRealImport'))
                 )
           )::bigint AS verified_free_code_candidates,
           COUNT(c.id) FILTER (
               WHERE c.status IN ('Reserved', 'Printed')
                 AND c.receipt_doc_id IS NULL AND c.receipt_line_id IS NULL
                 AND NOT (
                   (c.code LIKE 'TEMP-CHZ-%'
                    AND imp.source_type = 'temporary-chz-export'
                    AND c.origin IN ('LegacySynthetic', 'HistoricalUnknown'))
                   OR
                   (c.code NOT LIKE 'TEMP-CHZ-%'
                    AND imp.source_type IN ('csv', 'tsv')
                    AND c.origin IN ('RealImport', 'LegacyRealImport'))
                 )
           )::bigint AS unverified_free_codes,
           COUNT(c.id) FILTER (
               WHERE c.receipt_doc_id IS NOT NULL OR c.receipt_line_id IS NOT NULL
           )::bigint AS already_receipt_bound
    FROM source_tasks s
    LEFT JOIN marking_code c ON c.marking_order_id = s.marking_order_id
    LEFT JOIN marking_code_import imp ON imp.id = c.import_id
    GROUP BY s.source_order_id, s.item_id, s.normalized_gtin, s.marking_order_id
),
quantity AS (
    SELECT s.source_order_id, s.item_id, s.normalized_gtin,
           s.marking_order_id,
           COUNT(p.id)::bigint AS pool_count,
           COALESCE(SUM(
               CASE WHEN p.source_scope = JSONB_BUILD_OBJECT(
                    'source_type', BTRIM(mo.source_type),
                    'source_order_id', mo.source_order_id,
                    'order_id', mo.order_id)
                AND p.source_type = BTRIM(mo.source_type)
                AND p.item_id = mo.item_id
                AND p.order_line_id IS NOT DISTINCT FROM mo.order_line_id
                AND p.normalized_gtin = BTRIM(mo.gtin)
                AND b.available_quantity >= 0 AND b.applied_quantity >= 0
                THEN b.available_quantity ELSE 0 END
           ), 0)::bigint AS free_quantity_candidates,
           COUNT(p.id) FILTER (
               WHERE p.source_scope IS DISTINCT FROM JSONB_BUILD_OBJECT(
                    'source_type', BTRIM(mo.source_type),
                    'source_order_id', mo.source_order_id,
                    'order_id', mo.order_id)
                  OR p.source_type IS DISTINCT FROM BTRIM(mo.source_type)
                  OR p.item_id IS DISTINCT FROM mo.item_id
                  OR p.order_line_id IS DISTINCT FROM mo.order_line_id
                  OR p.normalized_gtin IS DISTINCT FROM BTRIM(mo.gtin)
                  OR b.available_quantity < 0
                  OR b.applied_quantity < 0
           )::bigint AS invalid_pools
    FROM source_tasks s
    JOIN marking_order mo ON mo.id = s.marking_order_id
    LEFT JOIN marking_synthetic_quantity_pool p ON p.marking_order_id = s.marking_order_id
    LEFT JOIN marking_synthetic_quantity_balance b ON b.pool_id = p.id
    GROUP BY s.source_order_id, s.item_id, s.normalized_gtin, s.marking_order_id
),
source_evidence AS (
    SELECT s.source_order_id, s.item_id, s.normalized_gtin,
           COUNT(*)::bigint AS active_source_tasks,
           COALESCE(SUM(c.verified_free_code_candidates), 0)::bigint AS free_per_code_candidates,
           COALESCE(SUM(q.free_quantity_candidates), 0)::bigint AS free_quantity_candidates,
           COALESCE(SUM(c.unverified_free_codes), 0)::bigint AS unverified_free_codes,
           COALESCE(SUM(c.already_receipt_bound), 0)::bigint AS bound_code_rows,
           COALESCE(SUM(q.invalid_pools), 0)::bigint AS invalid_pool_rows,
           COALESCE(SUM(CASE WHEN c.history_rows > 0 AND q.pool_count > 0
                             THEN 1 ELSE 0 END), 0)::bigint AS mixed_source_tasks
    FROM source_tasks s
    JOIN per_code c ON c.marking_order_id = s.marking_order_id
                   AND c.source_order_id = s.source_order_id
                   AND c.item_id = s.item_id
    JOIN quantity q ON q.marking_order_id = s.marking_order_id
                   AND q.source_order_id = s.source_order_id
                   AND q.item_id = s.item_id
    GROUP BY s.source_order_id, s.item_id, s.normalized_gtin
)
SELECT t.transfer_id,
       t.source_order_id,
       t.source_order_ref,
       t.target_order_id,
       t.target_order_ref,
       t.production_pallet_id,
       t.hu_code,
       t.item_id,
       t.normalized_gtin,
       t.source_order_line_id,
       t.target_order_line_id,
       t.transferred_qty,
       t.pallet_status_at_transfer,
       t.compensation_kind,
       COALESCE(e.active_source_tasks, 0) AS active_source_tasks,
       COALESCE(e.free_per_code_candidates, 0) AS source_candidate_free_codes,
       COALESCE(e.free_quantity_candidates, 0) AS source_candidate_free_quantity,
       COALESCE(e.unverified_free_codes, 0) AS source_unverified_free_codes,
       COALESCE(e.bound_code_rows, 0) AS source_receipt_bound_codes,
       COALESCE(e.invalid_pool_rows, 0) AS source_invalid_pools,
       COALESCE(e.mixed_source_tasks, 0) AS source_mixed_source_tasks,
       0::bigint AS proven_hu_allocated_marking_qty,
       CASE
           WHEN NOT t.markable THEN 'NOT_MARKABLE'
           WHEN t.compensation_kind IS NOT NULL THEN 'COMPENSATED'
           WHEN NOT (t.pallet_identity_valid AND t.line_identity_valid
                     AND t.customer_line_valid) THEN 'STALE_TRANSFER_IDENTITY'
           WHEN COALESCE(e.mixed_source_tasks, 0) > 0
                OR COALESCE(e.invalid_pool_rows, 0) > 0
                OR COALESCE(e.unverified_free_codes, 0) > 0 THEN 'MIXED_OR_AMBIGUOUS_SOURCE'
           WHEN COALESCE(e.active_source_tasks, 0) = 0 THEN 'NO_SOURCE_TASK'
           WHEN COALESCE(e.free_per_code_candidates, 0)
                + COALESCE(e.free_quantity_candidates, 0) > 0
                THEN 'SOURCE_CODES_UNALLOCATED_TO_HU'
           ELSE 'SOURCE_TASK_WITHOUT_FREE_COVERAGE'
       END AS diagnostic
FROM transfer_lines t
LEFT JOIN source_evidence e
  ON e.source_order_id = t.source_order_id
 AND e.item_id = t.item_id
 AND e.normalized_gtin = t.normalized_gtin
ORDER BY t.source_order_id, t.transfer_id, t.item_id, t.source_order_line_id;
