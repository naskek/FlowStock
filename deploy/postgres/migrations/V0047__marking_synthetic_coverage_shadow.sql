-- Issue #188. Read-only shadow quantity projection for *proven* legacy TEMP-CHZ codes.
-- This is intentionally a normal VIEW: no backfill, writes, materialization or
-- runtime switch. Real DataMatrix and non-proven HistoricalUnknown stay outside.
-- Never use this view as an authorization/eligibility rule or as the sole
-- source of truth for PRD close, corrections, retries or concurrency.
CREATE VIEW marking_synthetic_coverage_shadow AS
SELECT
    c.marking_order_id,
    c.import_id,
    c.origin,
    c.status,
    c.receipt_doc_id,
    c.receipt_line_id,
    NULLIF(BTRIM(c.gtin), '') AS normalized_gtin,
    c.printed_at,
    c.applied_at,
    c.reported_at,
    c.introduced_at,
    COUNT(*)::BIGINT AS quantity
FROM marking_code c
INNER JOIN marking_code_import i ON i.id = c.import_id
WHERE c.code LIKE 'TEMP-CHZ-%'
  AND i.source_type = 'temporary-chz-export'
  AND c.origin IN ('LegacySynthetic', 'HistoricalUnknown')
GROUP BY
    c.marking_order_id,
    c.import_id,
    c.origin,
    c.status,
    c.receipt_doc_id,
    c.receipt_line_id,
    NULLIF(BTRIM(c.gtin), ''),
    c.printed_at,
    c.applied_at,
    c.reported_at,
    c.introduced_at;
