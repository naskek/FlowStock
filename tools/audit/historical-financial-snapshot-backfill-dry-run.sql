BEGIN READ ONLY;

-- Issue #66: read-only inventory for legacy SHIPPED CUSTOMER lines whose
-- financial snapshots are incomplete. Proposed values intentionally use
-- CURRENT commercial terms only as a statistical approximation.
-- This script never mutates order_lines.

WITH candidates AS (
    SELECT
        o.id AS order_id,
        o.order_ref,
        o.created_at AS order_created_at,
        o.due_date,
        o.partner_id,
        p.code AS partner_code,
        p.name AS partner_name,
        ol.id AS order_line_id,
        ol.item_id,
        i.gtin,
        i.name AS item_name,
        ol.qty_ordered,
        ol.unit_price_gross AS existing_unit_price_gross,
        ol.vat_rate AS existing_vat_rate,
        pip.unit_price_gross AS current_partner_unit_price_gross,
        i.default_sale_price_gross AS current_item_default_unit_price_gross,
        COALESCE(pip.unit_price_gross, i.default_sale_price_gross) AS proposed_unit_price_gross,
        i.default_sale_vat_rate_id AS current_vat_rate_id,
        vr.rate AS current_vat_rate,
        vr.is_active AS current_vat_rate_is_active,
        CASE
            WHEN pip.id IS NOT NULL THEN 'CURRENT_ACTIVE_PARTNER_PRICE'
            WHEN i.default_sale_price_gross IS NOT NULL THEN 'CURRENT_ITEM_DEFAULT_PRICE'
            ELSE 'CURRENT_PRICE_MISSING'
        END AS proposed_price_source,
        CASE
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
                 AND COALESCE(pip.unit_price_gross, i.default_sale_price_gross) IS NULL
                THEN 'BLOCKED_CURRENT_PRICE_MISSING'
            WHEN ol.vat_rate IS NULL
                 AND i.default_sale_vat_rate_id IS NULL
                THEN 'BLOCKED_CURRENT_VAT_REQUIRED'
            WHEN ol.vat_rate IS NULL
                 AND vr.id IS NULL
                THEN 'BLOCKED_CURRENT_VAT_REFERENCE_MISSING'
            WHEN ol.vat_rate IS NULL
                 AND NOT vr.is_active
                THEN 'BLOCKED_CURRENT_VAT_INACTIVE'
            ELSE 'CANDIDATE_CURRENT_APPROXIMATION'
        END AS decision
    FROM orders o
    INNER JOIN order_lines ol ON ol.order_id = o.id
    LEFT JOIN partners p ON p.id = o.partner_id
    LEFT JOIN items i ON i.id = ol.item_id
    LEFT JOIN partner_item_sale_prices pip
           ON pip.partner_id = o.partner_id
          AND pip.item_id = ol.item_id
          AND pip.is_active = TRUE
    LEFT JOIN vat_rates vr ON vr.id = i.default_sale_vat_rate_id
    WHERE UPPER(o.order_type) = 'CUSTOMER'
      AND UPPER(o.status) = 'SHIPPED'
      AND ol.cancelled_at IS NULL
      AND (ol.unit_price_gross IS NULL OR ol.vat_rate IS NULL)
)
SELECT
    'SUMMARY_BY_DECISION' AS section,
    decision,
    COUNT(*) AS line_count,
    COALESCE(SUM(qty_ordered), 0) AS quantity,
    COUNT(*) FILTER (WHERE existing_unit_price_gross IS NULL) AS missing_price_line_count,
    COUNT(*) FILTER (WHERE existing_vat_rate IS NULL) AS missing_vat_line_count,
    MIN(order_created_at) AS earliest_order_created_at,
    MAX(order_created_at) AS latest_order_created_at
FROM candidates
GROUP BY decision
ORDER BY decision;

WITH candidates AS (
    SELECT
        o.id AS order_id,
        o.order_ref,
        o.created_at AS order_created_at,
        o.due_date,
        o.partner_id,
        p.code AS partner_code,
        p.name AS partner_name,
        ol.id AS order_line_id,
        ol.item_id,
        i.gtin,
        i.name AS item_name,
        ol.qty_ordered,
        ol.unit_price_gross AS existing_unit_price_gross,
        ol.vat_rate AS existing_vat_rate,
        pip.unit_price_gross AS current_partner_unit_price_gross,
        i.default_sale_price_gross AS current_item_default_unit_price_gross,
        COALESCE(pip.unit_price_gross, i.default_sale_price_gross) AS proposed_unit_price_gross,
        i.default_sale_vat_rate_id AS current_vat_rate_id,
        vr.rate AS current_vat_rate,
        vr.is_active AS current_vat_rate_is_active,
        CASE
            WHEN pip.id IS NOT NULL THEN 'CURRENT_ACTIVE_PARTNER_PRICE'
            WHEN i.default_sale_price_gross IS NOT NULL THEN 'CURRENT_ITEM_DEFAULT_PRICE'
            ELSE 'CURRENT_PRICE_MISSING'
        END AS proposed_price_source,
        CASE
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
                 AND COALESCE(pip.unit_price_gross, i.default_sale_price_gross) IS NULL
                THEN 'BLOCKED_CURRENT_PRICE_MISSING'
            WHEN ol.vat_rate IS NULL
                 AND i.default_sale_vat_rate_id IS NULL
                THEN 'BLOCKED_CURRENT_VAT_REQUIRED'
            WHEN ol.vat_rate IS NULL
                 AND vr.id IS NULL
                THEN 'BLOCKED_CURRENT_VAT_REFERENCE_MISSING'
            WHEN ol.vat_rate IS NULL
                 AND NOT vr.is_active
                THEN 'BLOCKED_CURRENT_VAT_INACTIVE'
            ELSE 'CANDIDATE_CURRENT_APPROXIMATION'
        END AS decision
    FROM orders o
    INNER JOIN order_lines ol ON ol.order_id = o.id
    LEFT JOIN partners p ON p.id = o.partner_id
    LEFT JOIN items i ON i.id = ol.item_id
    LEFT JOIN partner_item_sale_prices pip
           ON pip.partner_id = o.partner_id
          AND pip.item_id = ol.item_id
          AND pip.is_active = TRUE
    LEFT JOIN vat_rates vr ON vr.id = i.default_sale_vat_rate_id
    WHERE UPPER(o.order_type) = 'CUSTOMER'
      AND UPPER(o.status) = 'SHIPPED'
      AND ol.cancelled_at IS NULL
      AND (ol.unit_price_gross IS NULL OR ol.vat_rate IS NULL)
)
SELECT
    'DETAIL' AS section,
    decision,
    order_id,
    order_ref,
    order_created_at,
    due_date,
    partner_id,
    partner_code,
    partner_name,
    order_line_id,
    item_id,
    gtin,
    item_name,
    qty_ordered,
    existing_unit_price_gross,
    existing_vat_rate,
    proposed_unit_price_gross,
    CASE
        WHEN existing_unit_price_gross IS NOT NULL THEN 'EXISTING_SNAPSHOT'
        ELSE proposed_price_source
    END AS effective_price_source,
    CASE
        WHEN existing_vat_rate IS NOT NULL THEN existing_vat_rate
        WHEN current_vat_rate_is_active THEN current_vat_rate
        ELSE NULL
    END AS proposed_vat_rate,
    CASE
        WHEN existing_vat_rate IS NOT NULL THEN 'EXISTING_SNAPSHOT'
        ELSE proposed_vat_source
    END AS effective_vat_source,
    current_partner_unit_price_gross,
    current_item_default_unit_price_gross,
    current_vat_rate_id,
    current_vat_rate,
    current_vat_rate_is_active
FROM candidates
ORDER BY
    CASE decision WHEN 'CANDIDATE_CURRENT_APPROXIMATION' THEN 0 ELSE 1 END,
    order_created_at,
    order_id,
    order_line_id;

SELECT
    'EXCLUDED_CANCELLED_LINES' AS section,
    COUNT(*) AS line_count,
    COALESCE(SUM(ol.qty_ordered), 0) AS quantity
FROM orders o
INNER JOIN order_lines ol ON ol.order_id = o.id
WHERE UPPER(o.order_type) = 'CUSTOMER'
  AND UPPER(o.status) = 'SHIPPED'
  AND ol.cancelled_at IS NOT NULL
  AND (ol.unit_price_gross IS NULL OR ol.vat_rate IS NULL);

ROLLBACK;
