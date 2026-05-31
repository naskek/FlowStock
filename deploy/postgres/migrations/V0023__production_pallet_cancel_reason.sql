ALTER TABLE production_pallets
    ADD COLUMN IF NOT EXISTS cancel_reason TEXT NULL,
    ADD COLUMN IF NOT EXISTS cancelled_at TEXT NULL;
