DO $$
DECLARE
    blank_uom_id BIGINT;
    duplicate_name TEXT;
    blank_item_id BIGINT;
    orphan_name TEXT;
BEGIN
    SELECT id INTO blank_uom_id
    FROM uoms
    WHERE BTRIM(name) = ''
    ORDER BY id
    LIMIT 1;

    IF blank_uom_id IS NOT NULL THEN
        RAISE EXCEPTION 'V0035 preflight: blank UOM name at id %', blank_uom_id;
    END IF;

    SELECT LOWER(BTRIM(name)) INTO duplicate_name
    FROM uoms
    GROUP BY LOWER(BTRIM(name))
    HAVING COUNT(*) > 1
    ORDER BY LOWER(BTRIM(name))
    LIMIT 1;

    IF duplicate_name IS NOT NULL THEN
        RAISE EXCEPTION 'V0035 preflight: normalized duplicate UOM name: %', duplicate_name;
    END IF;

    SELECT id INTO blank_item_id
    FROM items
    WHERE BTRIM(base_uom) = ''
    ORDER BY id
    LIMIT 1;

    IF blank_item_id IS NOT NULL THEN
        RAISE EXCEPTION 'V0035 preflight: blank items.base_uom at item id %', blank_item_id;
    END IF;

    SELECT LOWER(BTRIM(i.base_uom)) INTO orphan_name
    FROM items i
    WHERE LOWER(BTRIM(i.base_uom)) <> 'шт'
      AND NOT EXISTS (
          SELECT 1
          FROM uoms u
          WHERE LOWER(BTRIM(u.name)) = LOWER(BTRIM(i.base_uom)))
    ORDER BY LOWER(BTRIM(i.base_uom))
    LIMIT 1;

    IF orphan_name IS NOT NULL THEN
        RAISE EXCEPTION 'V0035 preflight: orphan items.base_uom: %', orphan_name;
    END IF;
END $$;

ALTER TABLE uoms
    ADD CONSTRAINT ck_uoms_name_not_blank CHECK (BTRIM(name) <> '');

CREATE UNIQUE INDEX ux_uoms_name_normalized
    ON uoms (LOWER(BTRIM(name)));

CREATE INDEX ix_items_base_uom_normalized
    ON items (LOWER(BTRIM(base_uom)));
