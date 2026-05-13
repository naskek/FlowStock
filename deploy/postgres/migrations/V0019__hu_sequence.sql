CREATE SEQUENCE IF NOT EXISTS hu_code_seq;

DO $$
DECLARE
    max_hu_number BIGINT;
BEGIN
    SELECT COALESCE(MAX(value), 0)
    INTO max_hu_number
    FROM (
        SELECT NULLIF(SUBSTRING(hu_code FROM '^HU-0*([0-9]+)$'), '')::BIGINT AS value
        FROM hus
        WHERE hu_code IS NOT NULL
        UNION ALL
        SELECT NULLIF(SUBSTRING(hu_code FROM '^HU-0*([0-9]+)$'), '')::BIGINT AS value
        FROM production_pallets
        WHERE hu_code IS NOT NULL
        UNION ALL
        SELECT NULLIF(SUBSTRING(hu_code FROM '^HU-0*([0-9]+)$'), '')::BIGINT AS value
        FROM ledger
        WHERE hu_code IS NOT NULL
        UNION ALL
        SELECT NULLIF(SUBSTRING(from_hu FROM '^HU-0*([0-9]+)$'), '')::BIGINT AS value
        FROM doc_lines
        WHERE from_hu IS NOT NULL
        UNION ALL
        SELECT NULLIF(SUBSTRING(to_hu FROM '^HU-0*([0-9]+)$'), '')::BIGINT AS value
        FROM doc_lines
        WHERE to_hu IS NOT NULL
        UNION ALL
        SELECT NULLIF(SUBSTRING(from_hu FROM '^HU-0*([0-9]+)$'), '')::BIGINT AS value
        FROM api_docs
        WHERE from_hu IS NOT NULL
        UNION ALL
        SELECT NULLIF(SUBSTRING(to_hu FROM '^HU-0*([0-9]+)$'), '')::BIGINT AS value
        FROM api_docs
        WHERE to_hu IS NOT NULL
    ) existing_hu_numbers
    WHERE value IS NOT NULL;

    PERFORM setval('hu_code_seq', GREATEST(max_hu_number, 0) + 1, false);
END $$;
