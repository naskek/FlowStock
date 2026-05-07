BEGIN;

ALTER TABLE IF EXISTS marking_order
    ADD COLUMN IF NOT EXISTS source_type TEXT NULL;

ALTER TABLE IF EXISTS marking_order
    ADD COLUMN IF NOT EXISTS source_order_id BIGINT NULL;

CREATE INDEX IF NOT EXISTS ix_marking_order_source
    ON marking_order(source_type, source_order_id, item_id);

COMMIT;
