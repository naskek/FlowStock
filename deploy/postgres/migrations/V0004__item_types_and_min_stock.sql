BEGIN;

CREATE TABLE IF NOT EXISTS item_types (
    id BIGSERIAL PRIMARY KEY,
    name TEXT NOT NULL,
    code TEXT,
    sort_order INTEGER NOT NULL DEFAULT 0,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_visible_in_product_catalog BOOLEAN NOT NULL DEFAULT FALSE,
    enable_min_stock_control BOOLEAN NOT NULL DEFAULT FALSE
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_item_types_name ON item_types(name);
CREATE UNIQUE INDEX IF NOT EXISTS ux_item_types_code ON item_types(code) WHERE code IS NOT NULL;

ALTER TABLE IF EXISTS items ADD COLUMN IF NOT EXISTS item_type_id BIGINT;
ALTER TABLE IF EXISTS items ADD COLUMN IF NOT EXISTS min_stock_qty REAL;

DO $$
DECLARE
    default_item_type_id BIGINT;
BEGIN
    INSERT INTO item_types(name, code, sort_order, is_active, is_visible_in_product_catalog, enable_min_stock_control)
    VALUES ('Без типа', 'GENERAL', 0, TRUE, FALSE, FALSE)
    ON CONFLICT (name) DO NOTHING;

    SELECT id
    INTO default_item_type_id
    FROM item_types
    WHERE name = 'Без типа'
    ORDER BY id
    LIMIT 1;

    IF default_item_type_id IS NOT NULL THEN
        UPDATE items
        SET item_type_id = default_item_type_id
        WHERE item_type_id IS NULL;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_items_item_type'
    ) THEN
        ALTER TABLE IF EXISTS items
            ADD CONSTRAINT fk_items_item_type
            FOREIGN KEY (item_type_id) REFERENCES item_types(id);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_items_item_type_id ON items(item_type_id);
CREATE INDEX IF NOT EXISTS ix_item_types_active_sort ON item_types(is_active, sort_order, name);

UPDATE item_types
SET code = NULL
WHERE code IS NOT NULL
  AND TRIM(code) = '';

COMMIT;
