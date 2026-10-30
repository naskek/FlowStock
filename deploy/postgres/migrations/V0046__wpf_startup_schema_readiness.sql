-- #177: legacy WPF startup used to add these columns implicitly on every launch.
-- Move that bootstrap repair into the explicit migrator contract. On already
-- upgraded production databases these IF NOT EXISTS statements are no-ops.
-- Do not backfill or change business rows as part of WPF startup.
ALTER TABLE item_types
    ADD COLUMN IF NOT EXISTS min_stock_uses_order_binding BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS enable_order_reservation BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS enable_marking BOOLEAN NOT NULL DEFAULT FALSE;

ALTER TABLE orders
    ADD COLUMN IF NOT EXISTS marking_status TEXT NOT NULL DEFAULT 'NOT_REQUIRED',
    ADD COLUMN IF NOT EXISTS marking_excel_generated_at TEXT NULL,
    ADD COLUMN IF NOT EXISTS marking_printed_at TEXT NULL,
    ADD COLUMN IF NOT EXISTS marking_responsibility TEXT NOT NULL DEFAULT 'FLOWSTOCK';

ALTER TABLE order_lines
    ADD COLUMN IF NOT EXISTS production_purpose TEXT NOT NULL DEFAULT 'INTERNAL_STOCK',
    ADD COLUMN IF NOT EXISTS production_pallet_group TEXT NULL,
    ADD COLUMN IF NOT EXISTS cancelled_at TEXT NULL,
    ADD COLUMN IF NOT EXISTS cancelled_by_actor TEXT NULL,
    ADD COLUMN IF NOT EXISTS cancelled_by_device_id TEXT NULL,
    ADD COLUMN IF NOT EXISTS cancel_reason TEXT NULL,
    ADD COLUMN IF NOT EXISTS revision BIGINT NOT NULL DEFAULT 0;

ALTER TABLE doc_lines
    ADD COLUMN IF NOT EXISTS production_purpose TEXT NOT NULL DEFAULT 'INTERNAL_STOCK';

ALTER TABLE production_pallets
    ADD COLUMN IF NOT EXISTS pallet_no INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS pallet_count INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS printed_at TEXT NULL,
    ADD COLUMN IF NOT EXISTS cancel_reason TEXT NULL,
    ADD COLUMN IF NOT EXISTS cancelled_at TEXT NULL;

ALTER TABLE production_pallet_lines
    ADD COLUMN IF NOT EXISTS filled_at TEXT NULL;

ALTER TABLE marking_order
    ADD COLUMN IF NOT EXISTS order_line_id BIGINT NULL,
    ADD COLUMN IF NOT EXISTS request_status TEXT NOT NULL DEFAULT 'NotRequested',
    ADD COLUMN IF NOT EXISTS last_excel_requested_at TEXT NULL,
    ADD COLUMN IF NOT EXISTS last_excel_request_hash TEXT NULL,
    ADD COLUMN IF NOT EXISTS source_type TEXT NULL,
    ADD COLUMN IF NOT EXISTS source_order_id BIGINT NULL;

ALTER TABLE marking_code
    ADD COLUMN IF NOT EXISTS origin TEXT NOT NULL DEFAULT 'HistoricalUnknown',
    ADD COLUMN IF NOT EXISTS receipt_doc_id BIGINT NULL,
    ADD COLUMN IF NOT EXISTS receipt_line_id BIGINT NULL;

-- Legacy WPF EnsureNullable previously applied this change at every startup.
ALTER TABLE marking_order ALTER COLUMN order_id DROP NOT NULL;
