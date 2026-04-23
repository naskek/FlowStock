ALTER TABLE items
    ADD COLUMN IF NOT EXISTS is_active boolean NOT NULL DEFAULT TRUE;

UPDATE items
SET is_active = TRUE
WHERE is_active IS NULL;
