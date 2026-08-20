DO $$
DECLARE
    duplicate_barcode TEXT;
    duplicate_gtin TEXT;
BEGIN
    SELECT LOWER(BTRIM(barcode))
    INTO duplicate_barcode
    FROM items
    WHERE NULLIF(BTRIM(barcode), '') IS NOT NULL
    GROUP BY LOWER(BTRIM(barcode))
    HAVING COUNT(*) > 1
    ORDER BY LOWER(BTRIM(barcode))
    LIMIT 1;

    IF duplicate_barcode IS NOT NULL THEN
        RAISE EXCEPTION 'V0034 preflight: case-insensitive duplicate item barcode: %', duplicate_barcode;
    END IF;

    SELECT LOWER(BTRIM(gtin))
    INTO duplicate_gtin
    FROM items
    WHERE NULLIF(BTRIM(gtin), '') IS NOT NULL
    GROUP BY LOWER(BTRIM(gtin))
    HAVING COUNT(*) > 1
    ORDER BY LOWER(BTRIM(gtin))
    LIMIT 1;

    IF duplicate_gtin IS NOT NULL THEN
        RAISE EXCEPTION 'V0034 preflight: case-insensitive duplicate item GTIN: %', duplicate_gtin;
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_items_barcode_normalized
    ON items (LOWER(BTRIM(barcode)))
    WHERE NULLIF(BTRIM(barcode), '') IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_items_gtin_normalized
    ON items (LOWER(BTRIM(gtin)))
    WHERE NULLIF(BTRIM(gtin), '') IS NOT NULL;
