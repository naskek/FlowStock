-- Issue #186. Диагностика количественной проекции технических кодов.
-- Выполнять ТОЛЬКО на изолированной, проверенной копии PostgreSQL production.
-- psql -X -v ON_ERROR_STOP=1 -f deploy/scripts/marking-synthetic-parity.sql
--
-- Не изменяет данные, схему и настройки сессии вне транзакции.
-- Не выводит UUID, содержимое КМ/DM, GTIN, секреты или данные заказов.
-- Это структурная проверка количеств, НЕ доказательство runtime parity:
-- FOR UPDATE SKIP LOCKED, priority selection, per-code UUID, correction
-- lineage, concurrency и lifecycle в ней не воспроизводятся.
\set ON_ERROR_STOP on
\pset pager off
\echo '=== marking synthetic diagnostic: snapshot ==='
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '120s';
SET LOCAL lock_timeout = '1s';

SELECT current_setting('transaction_read_only') AS read_only,
       current_setting('server_version') AS pg_version,
       (SELECT state FROM marking_cutover_state WHERE id = TRUE) AS cutover_state;

\echo '=== origin / status / source classification (counts only) ==='
SELECT c.origin,
       c.status,
       COALESCE(i.source_type, '(missing)') AS import_source,
       COUNT(*)::bigint AS codes,
       COUNT(*) FILTER (
           WHERE c.code LIKE 'TEMP-CHZ-%'
             AND i.source_type = 'temporary-chz-export'
       )::bigint AS proven_temp_chz,
       COUNT(*) FILTER (WHERE c.receipt_line_id IS NOT NULL)::bigint AS receipt_bound,
       COUNT(*) FILTER (WHERE c.receipt_line_id IS NULL)::bigint AS not_receipt_bound
FROM marking_code c
LEFT JOIN marking_code_import i ON i.id = c.import_id
GROUP BY c.origin, c.status, i.source_type
ORDER BY c.origin, c.status, import_source;

\echo '=== hypothetical group counts; NOT an archival/retention plan ==='
-- BASE: воспроизводит прежний production snapshot 1421 групп.
-- GTIN: сохраняет нормализованный c.gtin для eligibility-фильтров.
-- LIFECYCLE: добавляет timestamps, значимые для rollback/check.
-- Ни один набор ключей не сохраняет выбор конкретных code IDs.
WITH base_groups AS (
    SELECT COUNT(*)::bigint AS qty
    FROM marking_code c
    GROUP BY c.marking_order_id, c.import_id, c.origin, c.status,
             c.receipt_doc_id, c.receipt_line_id
),
gtin_groups AS (
    SELECT COUNT(*)::bigint AS qty
    FROM marking_code c
    GROUP BY c.marking_order_id, c.import_id, c.origin, c.status,
             c.receipt_doc_id, c.receipt_line_id,
             NULLIF(BTRIM(c.gtin), '')
),
lifecycle_groups AS (
    SELECT COUNT(*)::bigint AS qty
    FROM marking_code c
    GROUP BY c.marking_order_id, c.import_id, c.origin, c.status,
             c.receipt_doc_id, c.receipt_line_id,
             NULLIF(BTRIM(c.gtin), ''),
             c.printed_at, c.applied_at, c.reported_at, c.introduced_at
)
SELECT 'BASE' AS projection,
       COUNT(*)::bigint AS group_rows,
       COALESCE(SUM(qty), 0) AS code_qty,
       COALESCE(MAX(qty), 0) AS largest_group
FROM base_groups
UNION ALL
SELECT 'GTIN', COUNT(*)::bigint, COALESCE(SUM(qty), 0), COALESCE(MAX(qty), 0)
FROM gtin_groups
UNION ALL
SELECT 'LIFECYCLE', COUNT(*)::bigint, COALESCE(SUM(qty), 0), COALESCE(MAX(qty), 0)
FROM lifecycle_groups;

\echo '=== binding / lifecycle flags; investigation only ==='
SELECT COUNT(*)::bigint AS total_codes,
       COUNT(*) FILTER (WHERE c.receipt_line_id IS NOT NULL)::bigint AS bound_codes,
       COUNT(*) FILTER (WHERE c.receipt_line_id IS NULL)::bigint AS free_or_other_codes,
       COUNT(*) FILTER (
           WHERE (c.receipt_doc_id IS NULL) <> (c.receipt_line_id IS NULL)
       )::bigint AS partial_receipt_binding,
       COUNT(*) FILTER (
           WHERE c.status = 'Applied' AND c.receipt_line_id IS NULL
       )::bigint AS applied_without_line,
       COUNT(*) FILTER (
           WHERE c.status IN ('Reserved', 'Printed')
             AND c.receipt_line_id IS NOT NULL
       )::bigint AS reserved_with_line,
       COUNT(*) FILTER (
           WHERE c.reported_at IS NOT NULL OR c.introduced_at IS NOT NULL
       )::bigint AS reported_or_introduced
FROM marking_code c;

\echo '=== receipt doc/line provenance anomalies ==='
SELECT COUNT(*) FILTER (
           WHERE c.receipt_line_id IS NOT NULL AND dl.id IS NULL
       )::bigint AS missing_receipt_doc_line,
       COUNT(*) FILTER (
           WHERE c.receipt_line_id IS NOT NULL
             AND c.receipt_doc_id IS DISTINCT FROM dl.doc_id
       )::bigint AS receipt_doc_line_mismatch
FROM marking_code c
LEFT JOIN doc_lines dl ON dl.id = c.receipt_line_id
WHERE c.receipt_line_id IS NOT NULL;

\echo '=== arithmetic parity: task count and free-count read semantics ==='
-- Две формы агрегации из ОДНОГО источника.
-- zero mismatches доказывает только арифметику групп, НЕ эквивалентность
-- GetAvailableProductionMarkingCodeIdsForReceipt / транзакционного close.
WITH direct AS (
    SELECT marking_order_id,
           COUNT(*) FILTER (WHERE status <> 'Voided')::bigint AS nonvoid_qty,
           COUNT(*) FILTER (
               WHERE status IN ('Reserved', 'Printed')
                 AND receipt_doc_id IS NULL
                 AND receipt_line_id IS NULL
           )::bigint AS free_qty
    FROM marking_code
    GROUP BY marking_order_id
),
projection AS (
    SELECT marking_order_id, status, receipt_doc_id, receipt_line_id,
           COUNT(*)::bigint AS qty
    FROM marking_code
    GROUP BY marking_order_id, status, receipt_doc_id, receipt_line_id
),
derived AS (
    SELECT marking_order_id,
           COALESCE(SUM(qty) FILTER (WHERE status <> 'Voided'), 0) AS nonvoid_qty,
           COALESCE(SUM(qty) FILTER (
               WHERE status IN ('Reserved', 'Printed')
                 AND receipt_doc_id IS NULL
                 AND receipt_line_id IS NULL
           ), 0) AS free_qty
    FROM projection
    GROUP BY marking_order_id
)
SELECT COUNT(*)::bigint AS task_keys_compared,
       COUNT(*) FILTER (
           WHERE d.marking_order_id IS NULL
              OR a.marking_order_id IS NULL
              OR d.nonvoid_qty IS DISTINCT FROM a.nonvoid_qty
              OR d.free_qty IS DISTINCT FROM a.free_qty
       )::bigint AS mismatched_task_keys
FROM direct d
FULL JOIN derived a USING (marking_order_id);

\echo '=== arithmetic parity: receipt-line nonvoid count ==='
WITH direct AS (
    SELECT receipt_line_id,
           COUNT(*) FILTER (WHERE status <> 'Voided')::bigint AS qty
    FROM marking_code
    WHERE receipt_line_id IS NOT NULL
    GROUP BY receipt_line_id
),
projection AS (
    SELECT receipt_line_id, status, COUNT(*)::bigint AS qty
    FROM marking_code
    WHERE receipt_line_id IS NOT NULL
    GROUP BY receipt_line_id, status
),
derived AS (
    SELECT receipt_line_id,
           COALESCE(SUM(qty) FILTER (WHERE status <> 'Voided'), 0) AS qty
    FROM projection
    GROUP BY receipt_line_id
)
SELECT COUNT(*)::bigint AS receipt_line_keys_compared,
       COUNT(*) FILTER (
           WHERE d.receipt_line_id IS NULL
              OR a.receipt_line_id IS NULL
              OR d.qty IS DISTINCT FROM a.qty
       )::bigint AS mismatched_receipt_line_keys
FROM direct d
FULL JOIN derived a USING (receipt_line_id);

\echo '=== FK references / existing audited transitions ==='
SELECT conrelid::regclass AS referencing_table,
       conname AS fk_name
FROM pg_constraint
WHERE contype = 'f'
  AND confrelid = 'public.marking_code'::regclass
ORDER BY conrelid::regclass::text, conname;

SELECT 'production_marking_transition_audit' AS source,
       COUNT(*)::bigint AS rows
FROM production_marking_transition_audit
UNION ALL
SELECT 'marking_print_batch_code', COUNT(*)::bigint
FROM marking_print_batch_code;

COMMIT;
\echo '=== marking synthetic diagnostic: complete ==='
