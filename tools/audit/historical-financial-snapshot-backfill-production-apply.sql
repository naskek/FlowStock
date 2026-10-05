\set ON_ERROR_STOP on

-- Issue #66 production mutation script.
-- Run only after:
--   1. a fresh verified production PostgreSQL backup exists;
--   2. historical-financial-snapshot-backfill-production-preflight.sql was run;
--   3. every EXPECTED_* value below is copied from that exact preflight output.
--
-- Required psql variables:
--   CONFIRM_PRODUCTION_APPLY=APPLY_HISTORICAL_SNAPSHOT_BACKFILL
--   CONFIRM_FRESH_BACKUP=YES
--   EXPECTED_CANDIDATE_COUNT=<preflight candidate_count>
--   EXPECTED_CANDIDATE_QUANTITY=<preflight candidate_quantity>
--   EXPECTED_RECONSTRUCTED_GROSS=<preflight reconstructed_gross>
--   EXPECTED_CANDIDATE_FINGERPRINT=<preflight candidate_fingerprint>
--   EXPECTED_BLOCKED_COUNT=<preflight blocked_count>
--   EXPECTED_BLOCKED_QUANTITY=<preflight blocked_quantity>
--
-- Any mismatch aborts before UPDATE. Post-update invariant failures also roll back.

\if :{?CONFIRM_PRODUCTION_APPLY}
\else
\echo 'ERROR: pass -v CONFIRM_PRODUCTION_APPLY=APPLY_HISTORICAL_SNAPSHOT_BACKFILL'
SELECT 1 / 0 AS fail_closed;
\endif

SELECT :'CONFIRM_PRODUCTION_APPLY' = 'APPLY_HISTORICAL_SNAPSHOT_BACKFILL' AS production_confirmed \gset
\if :production_confirmed
\else
\echo 'ERROR: CONFIRM_PRODUCTION_APPLY must be exactly APPLY_HISTORICAL_SNAPSHOT_BACKFILL'
SELECT 1 / 0 AS fail_closed;
\endif

\if :{?CONFIRM_FRESH_BACKUP}
\else
\echo 'ERROR: pass -v CONFIRM_FRESH_BACKUP=YES'
SELECT 1 / 0 AS fail_closed;
\endif

SELECT :'CONFIRM_FRESH_BACKUP' = 'YES' AS backup_confirmed \gset
\if :backup_confirmed
\else
\echo 'ERROR: CONFIRM_FRESH_BACKUP must be exactly YES'
SELECT 1 / 0 AS fail_closed;
\endif

\if :{?EXPECTED_CANDIDATE_COUNT}
\else
\echo 'ERROR: pass -v EXPECTED_CANDIDATE_COUNT=<preflight value>'
SELECT 1 / 0 AS fail_closed;
\endif
\if :{?EXPECTED_CANDIDATE_QUANTITY}
\else
\echo 'ERROR: pass -v EXPECTED_CANDIDATE_QUANTITY=<preflight value>'
SELECT 1 / 0 AS fail_closed;
\endif
\if :{?EXPECTED_RECONSTRUCTED_GROSS}
\else
\echo 'ERROR: pass -v EXPECTED_RECONSTRUCTED_GROSS=<preflight value>'
SELECT 1 / 0 AS fail_closed;
\endif
\if :{?EXPECTED_CANDIDATE_FINGERPRINT}
\else
\echo 'ERROR: pass -v EXPECTED_CANDIDATE_FINGERPRINT=<preflight value>'
SELECT 1 / 0 AS fail_closed;
\endif
\if :{?EXPECTED_BLOCKED_COUNT}
\else
\echo 'ERROR: pass -v EXPECTED_BLOCKED_COUNT=<preflight value>'
SELECT 1 / 0 AS fail_closed;
\endif
\if :{?EXPECTED_BLOCKED_QUANTITY}
\else
\echo 'ERROR: pass -v EXPECTED_BLOCKED_QUANTITY=<preflight value>'
SELECT 1 / 0 AS fail_closed;
\endif

BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '120s';

-- Freeze all source rows/terms used by reconstruction while allowing readers.
-- The transaction is intentionally short and should be run in a maintenance window.
LOCK TABLE orders IN SHARE MODE;
LOCK TABLE order_lines IN SHARE ROW EXCLUSIVE MODE;
LOCK TABLE partners IN SHARE MODE;
LOCK TABLE items IN SHARE MODE;
LOCK TABLE partner_item_sale_prices IN SHARE MODE;
LOCK TABLE vat_rates IN SHARE MODE;

CREATE TEMP TABLE historical_snapshot_backfill_plan ON COMMIT DROP AS
SELECT
    o.id AS order_id,
    o.order_ref,
    o.created_at AS order_created_at,
    o.partner_id,
    ol.id AS order_line_id,
    ol.item_id,
    ol.qty_ordered,
    ol.unit_price_gross AS existing_unit_price_gross,
    ol.vat_rate AS existing_vat_rate,
    later_price.order_id AS subsequent_snapshot_order_id,
    later_price.order_ref AS subsequent_snapshot_order_ref,
    later_price.order_created_at AS subsequent_snapshot_order_created_at,
    later_price.order_line_id AS subsequent_snapshot_order_line_id,
    later_price.unit_price_gross AS subsequent_snapshot_unit_price_gross,
    pip.unit_price_gross AS current_partner_unit_price_gross,
    i.default_sale_price_gross AS current_item_default_unit_price_gross,
    COALESCE(
        later_price.unit_price_gross,
        pip.unit_price_gross,
        i.default_sale_price_gross
    ) AS proposed_unit_price_gross,
    CASE
        WHEN ol.vat_rate IS NOT NULL THEN ol.vat_rate
        WHEN vr.is_active THEN vr.rate
        ELSE NULL
    END AS proposed_vat_rate,
    CASE
        WHEN later_price.unit_price_gross IS NOT NULL THEN 'FIRST_SUBSEQUENT_SNAPSHOT_PRICE'
        WHEN pip.id IS NOT NULL THEN 'CURRENT_ACTIVE_PARTNER_PRICE'
        WHEN i.default_sale_price_gross IS NOT NULL THEN 'CURRENT_ITEM_DEFAULT_PRICE'
        ELSE 'PRICE_SOURCE_MISSING'
    END AS proposed_price_source,
    CASE
        WHEN ol.vat_rate IS NOT NULL THEN 'EXISTING_SNAPSHOT'
        WHEN i.default_sale_vat_rate_id IS NULL THEN 'CURRENT_ITEM_VAT_REQUIRED'
        WHEN vr.id IS NULL THEN 'CURRENT_VAT_REFERENCE_MISSING'
        WHEN NOT vr.is_active THEN 'CURRENT_VAT_INACTIVE'
        ELSE 'CURRENT_ITEM_VAT_RATE'
    END AS proposed_vat_source,
    CASE
        WHEN o.partner_id IS NULL THEN 'BLOCKED_PARTNER_MISSING'
        WHEN p.id IS NULL THEN 'BLOCKED_PARTNER_REFERENCE_MISSING'
        WHEN i.id IS NULL THEN 'BLOCKED_ITEM_REFERENCE_MISSING'
        WHEN ol.qty_ordered <= 0 THEN 'BLOCKED_NON_POSITIVE_QTY'
        WHEN ol.unit_price_gross IS NULL
             AND COALESCE(
                 later_price.unit_price_gross,
                 pip.unit_price_gross,
                 i.default_sale_price_gross
             ) IS NULL
            THEN 'BLOCKED_PRICE_SOURCE_MISSING'
        WHEN ol.vat_rate IS NULL
             AND i.default_sale_vat_rate_id IS NULL
            THEN 'BLOCKED_CURRENT_VAT_REQUIRED'
        WHEN ol.vat_rate IS NULL
             AND vr.id IS NULL
            THEN 'BLOCKED_CURRENT_VAT_REFERENCE_MISSING'
        WHEN ol.vat_rate IS NULL
             AND NOT vr.is_active
            THEN 'BLOCKED_CURRENT_VAT_INACTIVE'
        ELSE 'CANDIDATE_STATISTICAL_APPROXIMATION'
    END AS decision
FROM orders o
INNER JOIN order_lines ol ON ol.order_id = o.id
LEFT JOIN partners p ON p.id = o.partner_id
LEFT JOIN items i ON i.id = ol.item_id
LEFT JOIN LATERAL (
    SELECT
        o2.id AS order_id,
        o2.order_ref,
        o2.created_at AS order_created_at,
        ol2.id AS order_line_id,
        ol2.unit_price_gross
    FROM orders o2
    INNER JOIN order_lines ol2 ON ol2.order_id = o2.id
    WHERE UPPER(o2.order_type) = 'CUSTOMER'
      AND UPPER(o2.status) = 'SHIPPED'
      AND o2.partner_id = o.partner_id
      AND ol2.item_id = ol.item_id
      AND ol2.cancelled_at IS NULL
      AND ol2.unit_price_gross IS NOT NULL
      AND (o2.created_at, o2.id, ol2.id) > (o.created_at, o.id, ol.id)
    ORDER BY o2.created_at, o2.id, ol2.id
    LIMIT 1
) later_price ON TRUE
LEFT JOIN partner_item_sale_prices pip
       ON pip.partner_id = o.partner_id
      AND pip.item_id = ol.item_id
      AND pip.is_active = TRUE
LEFT JOIN vat_rates vr ON vr.id = i.default_sale_vat_rate_id
WHERE UPPER(o.order_type) = 'CUSTOMER'
  AND UPPER(o.status) = 'SHIPPED'
  AND ol.cancelled_at IS NULL
  AND (ol.unit_price_gross IS NULL OR ol.vat_rate IS NULL);

SELECT
    COUNT(*) FILTER (
        WHERE decision = 'CANDIDATE_STATISTICAL_APPROXIMATION'
    )::bigint AS actual_candidate_count,
    COALESCE(SUM(qty_ordered) FILTER (
        WHERE decision = 'CANDIDATE_STATISTICAL_APPROXIMATION'
    ), 0)::bigint AS actual_candidate_quantity,
    ROUND(COALESCE(SUM(
        qty_ordered::numeric * proposed_unit_price_gross::numeric
    ) FILTER (
        WHERE decision = 'CANDIDATE_STATISTICAL_APPROXIMATION'
    ), 0::numeric), 2) AS actual_reconstructed_gross,
    COALESCE(md5(string_agg(
        order_line_id::text || '|' ||
        COALESCE(proposed_unit_price_gross::text, '') || '|' ||
        COALESCE(proposed_vat_rate::text, ''),
        ',' ORDER BY order_line_id
    ) FILTER (
        WHERE decision = 'CANDIDATE_STATISTICAL_APPROXIMATION'
    )), '') AS actual_candidate_fingerprint,
    COUNT(*) FILTER (
        WHERE decision <> 'CANDIDATE_STATISTICAL_APPROXIMATION'
    )::bigint AS actual_blocked_count,
    COALESCE(SUM(qty_ordered) FILTER (
        WHERE decision <> 'CANDIDATE_STATISTICAL_APPROXIMATION'
    ), 0)::bigint AS actual_blocked_quantity
FROM historical_snapshot_backfill_plan
\gset

SELECT
    :actual_candidate_count::bigint > 0
    AND :actual_candidate_count::bigint = :'EXPECTED_CANDIDATE_COUNT'::bigint
    AND :actual_candidate_quantity::bigint = :'EXPECTED_CANDIDATE_QUANTITY'::bigint
    AND :actual_reconstructed_gross::numeric = :'EXPECTED_RECONSTRUCTED_GROSS'::numeric
    AND :'actual_candidate_fingerprint' = :'EXPECTED_CANDIDATE_FINGERPRINT'
    AND :actual_blocked_count::bigint = :'EXPECTED_BLOCKED_COUNT'::bigint
    AND :actual_blocked_quantity::bigint = :'EXPECTED_BLOCKED_QUANTITY'::bigint
    AS production_guard_matches
\gset

SELECT
    'PRE_UPDATE_GUARDS' AS section,
    :actual_candidate_count::bigint AS candidate_count,
    :actual_candidate_quantity::bigint AS candidate_quantity,
    :actual_reconstructed_gross::numeric AS reconstructed_gross,
    :'actual_candidate_fingerprint' AS candidate_fingerprint,
    :actual_blocked_count::bigint AS blocked_count,
    :actual_blocked_quantity::bigint AS blocked_quantity;

\if :production_guard_matches
\else
\echo 'ERROR: production candidate set differs from reviewed preflight; rolling back without UPDATE'
ROLLBACK;
SELECT 1 / 0 AS fail_closed;
\endif

CREATE TEMP TABLE historical_snapshot_backfill_updated ON COMMIT DROP AS
WITH updated AS (
    UPDATE order_lines ol
    SET
        unit_price_gross = CASE
            WHEN ol.unit_price_gross IS NULL THEN plan.proposed_unit_price_gross
            ELSE ol.unit_price_gross
        END,
        vat_rate = CASE
            WHEN ol.vat_rate IS NULL THEN plan.proposed_vat_rate
            ELSE ol.vat_rate
        END
    FROM historical_snapshot_backfill_plan plan
    WHERE plan.decision = 'CANDIDATE_STATISTICAL_APPROXIMATION'
      AND plan.order_line_id = ol.id
      AND (ol.unit_price_gross IS NULL OR ol.vat_rate IS NULL)
    RETURNING ol.id AS order_line_id
)
SELECT order_line_id FROM updated;

SELECT COUNT(*)::bigint AS actual_updated_count
FROM historical_snapshot_backfill_updated
\gset

SELECT
    COUNT(*)::bigint AS actual_candidate_incomplete_count
FROM order_lines ol
INNER JOIN historical_snapshot_backfill_plan plan
        ON plan.order_line_id = ol.id
WHERE plan.decision = 'CANDIDATE_STATISTICAL_APPROXIMATION'
  AND (ol.unit_price_gross IS NULL OR ol.vat_rate IS NULL)
\gset

SELECT
    COUNT(*)::bigint AS actual_remaining_incomplete_count,
    COALESCE(SUM(ol.qty_ordered), 0)::bigint AS actual_remaining_incomplete_quantity
FROM orders o
INNER JOIN order_lines ol ON ol.order_id = o.id
WHERE UPPER(o.order_type) = 'CUSTOMER'
  AND UPPER(o.status) = 'SHIPPED'
  AND ol.cancelled_at IS NULL
  AND (ol.unit_price_gross IS NULL OR ol.vat_rate IS NULL)
\gset

SELECT
    :actual_updated_count::bigint = :'EXPECTED_CANDIDATE_COUNT'::bigint
    AND :actual_candidate_incomplete_count::bigint = 0
    AND :actual_remaining_incomplete_count::bigint = :'EXPECTED_BLOCKED_COUNT'::bigint
    AND :actual_remaining_incomplete_quantity::bigint = :'EXPECTED_BLOCKED_QUANTITY'::bigint
    AS post_update_guard_matches
\gset

SELECT
    'POST_UPDATE_GUARDS' AS section,
    :actual_updated_count::bigint AS updated_count,
    :actual_candidate_incomplete_count::bigint AS candidate_incomplete_count,
    :actual_remaining_incomplete_count::bigint AS remaining_incomplete_count,
    :actual_remaining_incomplete_quantity::bigint AS remaining_incomplete_quantity;

\if :post_update_guard_matches
\else
\echo 'ERROR: post-update invariants failed; rolling back transaction'
ROLLBACK;
SELECT 1 / 0 AS fail_closed;
\endif

COMMIT;
