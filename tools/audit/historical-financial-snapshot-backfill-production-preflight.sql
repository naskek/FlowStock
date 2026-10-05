-- Issue #66 production preflight.
-- READ ONLY. Run this immediately before production apply.
-- Copy the guard values exactly into the production apply command.

BEGIN READ ONLY;
SET LOCAL statement_timeout = '120s';

WITH candidates AS (
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
      AND (ol.unit_price_gross IS NULL OR ol.vat_rate IS NULL)
), guards AS (
    SELECT
        COUNT(*) FILTER (
            WHERE decision = 'CANDIDATE_STATISTICAL_APPROXIMATION'
        )::bigint AS candidate_count,
        COALESCE(SUM(qty_ordered) FILTER (
            WHERE decision = 'CANDIDATE_STATISTICAL_APPROXIMATION'
        ), 0)::bigint AS candidate_quantity,
        ROUND(COALESCE(SUM(
            qty_ordered::numeric * proposed_unit_price_gross::numeric
        ) FILTER (
            WHERE decision = 'CANDIDATE_STATISTICAL_APPROXIMATION'
        ), 0::numeric), 2) AS reconstructed_gross,
        COALESCE(md5(string_agg(
            order_line_id::text || '|' ||
            COALESCE(proposed_unit_price_gross::text, '') || '|' ||
            COALESCE(proposed_vat_rate::text, ''),
            ',' ORDER BY order_line_id
        ) FILTER (
            WHERE decision = 'CANDIDATE_STATISTICAL_APPROXIMATION'
        )), '') AS candidate_fingerprint,
        COUNT(*) FILTER (
            WHERE decision <> 'CANDIDATE_STATISTICAL_APPROXIMATION'
        )::bigint AS blocked_count,
        COALESCE(SUM(qty_ordered) FILTER (
            WHERE decision <> 'CANDIDATE_STATISTICAL_APPROXIMATION'
        ), 0)::bigint AS blocked_quantity,
        COUNT(*)::bigint AS total_incomplete_count,
        COALESCE(SUM(qty_ordered), 0)::bigint AS total_incomplete_quantity
    FROM candidates
)
SELECT
    'PRODUCTION_GUARDS' AS section,
    candidate_count,
    candidate_quantity,
    reconstructed_gross,
    candidate_fingerprint,
    blocked_count,
    blocked_quantity,
    total_incomplete_count,
    total_incomplete_quantity
FROM guards;

ROLLBACK;
