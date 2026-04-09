CREATE TABLE IF NOT EXISTS items (
    id BIGSERIAL PRIMARY KEY,
    name TEXT NOT NULL,
    barcode TEXT UNIQUE,
    gtin TEXT,
    uom TEXT,
    base_uom TEXT NOT NULL DEFAULT 'шт',
    default_packaging_id BIGINT,
    brand TEXT,
    volume TEXT,
    shelf_life_months INTEGER,
    max_qty_per_hu REAL,
    tara_id BIGINT,
    is_marked INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS uoms (
    id BIGSERIAL PRIMARY KEY,
    name TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_uoms_name ON uoms(name);

CREATE TABLE IF NOT EXISTS taras (
    id BIGSERIAL PRIMARY KEY,
    name TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_taras_name ON taras(name);

CREATE TABLE IF NOT EXISTS item_packaging (
    id BIGSERIAL PRIMARY KEY,
    item_id BIGINT NOT NULL,
    code TEXT NOT NULL,
    name TEXT NOT NULL,
    factor_to_base REAL NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1,
    sort_order INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (item_id) REFERENCES items(id)
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_item_packaging_item_code ON item_packaging(item_id, code);
CREATE INDEX IF NOT EXISTS ix_item_packaging_item ON item_packaging(item_id);

CREATE TABLE IF NOT EXISTS locations (
    id BIGSERIAL PRIMARY KEY,
    code TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS partners (
    id BIGSERIAL PRIMARY KEY,
    name TEXT NOT NULL,
    code TEXT,
    created_at TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_partners_code ON partners(code);

CREATE TABLE IF NOT EXISTS item_requests (
    id BIGSERIAL PRIMARY KEY,
    barcode TEXT NOT NULL,
    comment TEXT NOT NULL,
    device_id TEXT,
    login TEXT,
    status TEXT NOT NULL DEFAULT 'NEW',
    created_at TEXT NOT NULL,
    resolved_at TEXT
);
CREATE INDEX IF NOT EXISTS ix_item_requests_status ON item_requests(status);

CREATE TABLE IF NOT EXISTS order_requests (
    id BIGSERIAL PRIMARY KEY,
    request_type TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'PENDING',
    created_at TEXT NOT NULL,
    created_by_login TEXT,
    created_by_device_id TEXT,
    resolved_at TEXT,
    resolved_by TEXT,
    resolution_note TEXT,
    applied_order_id BIGINT
);
CREATE INDEX IF NOT EXISTS ix_order_requests_status ON order_requests(status);

CREATE TABLE IF NOT EXISTS orders (
    id BIGSERIAL PRIMARY KEY,
    order_ref TEXT NOT NULL,
    order_type TEXT NOT NULL DEFAULT 'CUSTOMER',
    partner_id BIGINT,
    due_date TEXT,
    status TEXT NOT NULL DEFAULT 'ACCEPTED',
    comment TEXT,
    created_at TEXT NOT NULL,
    FOREIGN KEY (partner_id) REFERENCES partners(id)
);
CREATE INDEX IF NOT EXISTS ix_orders_ref ON orders(order_ref);
CREATE INDEX IF NOT EXISTS ix_orders_partner ON orders(partner_id);

CREATE TABLE IF NOT EXISTS order_lines (
    id BIGSERIAL PRIMARY KEY,
    order_id BIGINT NOT NULL,
    item_id BIGINT NOT NULL,
    qty_ordered REAL NOT NULL,
    FOREIGN KEY (order_id) REFERENCES orders(id),
    FOREIGN KEY (item_id) REFERENCES items(id)
);
CREATE INDEX IF NOT EXISTS ix_order_lines_order ON order_lines(order_id);

CREATE TABLE IF NOT EXISTS docs (
    id BIGSERIAL PRIMARY KEY,
    doc_ref TEXT NOT NULL,
    type TEXT NOT NULL,
    status TEXT NOT NULL,
    created_at TEXT NOT NULL,
    closed_at TEXT,
    partner_id BIGINT,
    order_id BIGINT,
    order_ref TEXT,
    shipping_ref TEXT,
    reason_code TEXT,
    comment TEXT,
    production_batch_no TEXT,
    FOREIGN KEY (partner_id) REFERENCES partners(id),
    FOREIGN KEY (order_id) REFERENCES orders(id)
);
DROP INDEX IF EXISTS ix_docs_ref_type;
CREATE UNIQUE INDEX IF NOT EXISTS ix_docs_ref ON docs(doc_ref);

CREATE TABLE IF NOT EXISTS doc_lines (
    id BIGSERIAL PRIMARY KEY,
    doc_id BIGINT NOT NULL,
    replaces_line_id BIGINT,
    order_line_id BIGINT,
    item_id BIGINT NOT NULL,
    qty REAL NOT NULL,
    qty_input REAL,
    uom_code TEXT,
    from_location_id BIGINT,
    to_location_id BIGINT,
    from_hu TEXT,
    to_hu TEXT
);
CREATE INDEX IF NOT EXISTS ix_doc_lines_doc ON doc_lines(doc_id);

CREATE TABLE IF NOT EXISTS ledger (
    id BIGSERIAL PRIMARY KEY,
    ts TEXT NOT NULL,
    doc_id BIGINT NOT NULL,
    item_id BIGINT NOT NULL,
    location_id BIGINT NOT NULL,
    qty_delta REAL NOT NULL,
    hu_code TEXT,
    hu TEXT
);
CREATE INDEX IF NOT EXISTS ix_ledger_item_location ON ledger(item_id, location_id);

CREATE TABLE IF NOT EXISTS imported_events (
    event_id TEXT PRIMARY KEY,
    imported_at TEXT NOT NULL,
    source_file TEXT NOT NULL,
    device_id TEXT
);

CREATE TABLE IF NOT EXISTS import_errors (
    id BIGSERIAL PRIMARY KEY,
    event_id TEXT,
    reason TEXT NOT NULL,
    raw_json TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS api_docs (
    doc_uid TEXT PRIMARY KEY,
    doc_id BIGINT NOT NULL,
    status TEXT NOT NULL,
    created_at TEXT NOT NULL,
    doc_type TEXT,
    doc_ref TEXT,
    partner_id BIGINT,
    from_location_id BIGINT,
    to_location_id BIGINT,
    from_hu TEXT,
    to_hu TEXT,
    device_id TEXT
);
CREATE INDEX IF NOT EXISTS ix_api_docs_doc ON api_docs(doc_id);

CREATE TABLE IF NOT EXISTS api_events (
    event_id TEXT PRIMARY KEY,
    event_type TEXT NOT NULL,
    doc_uid TEXT,
    created_at TEXT NOT NULL,
    received_at TEXT,
    device_id TEXT,
    raw_json TEXT
);
CREATE INDEX IF NOT EXISTS ix_api_events_doc ON api_events(doc_uid);

CREATE TABLE IF NOT EXISTS stock_reservation_lines (
    id BIGSERIAL PRIMARY KEY,
    doc_uid TEXT NOT NULL,
    item_id BIGINT NOT NULL,
    location_id BIGINT NOT NULL,
    qty REAL NOT NULL,
    created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_stock_reservation_doc ON stock_reservation_lines(doc_uid);
CREATE INDEX IF NOT EXISTS ix_stock_reservation_item_loc ON stock_reservation_lines(item_id, location_id);

CREATE TABLE IF NOT EXISTS hus (
    id BIGSERIAL PRIMARY KEY,
    hu_code TEXT NOT NULL UNIQUE,
    status TEXT NOT NULL DEFAULT 'ACTIVE',
    created_at TEXT NOT NULL,
    created_by TEXT,
    closed_at TEXT,
    note TEXT
);
CREATE INDEX IF NOT EXISTS idx_hus_status ON hus(status);
CREATE INDEX IF NOT EXISTS idx_hus_created_at ON hus(created_at);

CREATE TABLE IF NOT EXISTS tsd_devices (
    id BIGSERIAL PRIMARY KEY,
    device_id TEXT NOT NULL UNIQUE,
    login TEXT NOT NULL UNIQUE,
    password_salt TEXT NOT NULL,
    password_hash TEXT NOT NULL,
    password_iterations INTEGER NOT NULL,
    platform TEXT NOT NULL DEFAULT 'TSD',
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TEXT NOT NULL,
    last_seen TEXT
);
CREATE INDEX IF NOT EXISTS ix_tsd_devices_login ON tsd_devices(login);
CREATE INDEX IF NOT EXISTS ix_tsd_devices_device_id ON tsd_devices(device_id);

CREATE TABLE IF NOT EXISTS km_code_batch (
    id BIGSERIAL PRIMARY KEY,
    order_id BIGINT,
    file_name TEXT,
    file_hash TEXT NOT NULL UNIQUE,
    imported_at TEXT NOT NULL,
    imported_by TEXT,
    total_codes INTEGER NOT NULL DEFAULT 0,
    error_count INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (order_id) REFERENCES orders(id)
);
CREATE INDEX IF NOT EXISTS ix_km_code_batch_order ON km_code_batch(order_id);

CREATE TABLE IF NOT EXISTS km_code (
    id BIGSERIAL PRIMARY KEY,
    batch_id BIGINT NOT NULL,
    code_raw TEXT NOT NULL UNIQUE,
    gtin14 CHAR(14),
    sku_id BIGINT,
    product_name TEXT,
    status SMALLINT NOT NULL,
    receipt_doc_id BIGINT,
    receipt_line_id BIGINT,
    hu_id BIGINT,
    location_id BIGINT,
    ship_doc_id BIGINT,
    ship_line_id BIGINT,
    order_id BIGINT,
    FOREIGN KEY (batch_id) REFERENCES km_code_batch(id),
    FOREIGN KEY (sku_id) REFERENCES items(id),
    FOREIGN KEY (receipt_doc_id) REFERENCES docs(id),
    FOREIGN KEY (ship_doc_id) REFERENCES docs(id),
    FOREIGN KEY (hu_id) REFERENCES hus(id),
    FOREIGN KEY (location_id) REFERENCES locations(id),
    FOREIGN KEY (order_id) REFERENCES orders(id)
);
CREATE INDEX IF NOT EXISTS idx_km_code_batch_id ON km_code(batch_id);
CREATE INDEX IF NOT EXISTS idx_km_code_status ON km_code(status);
CREATE INDEX IF NOT EXISTS idx_km_code_gtin14 ON km_code(gtin14);
CREATE INDEX IF NOT EXISTS idx_km_code_sku_id ON km_code(sku_id);
CREATE INDEX IF NOT EXISTS idx_km_code_receipt_doc_id ON km_code(receipt_doc_id);
CREATE INDEX IF NOT EXISTS idx_km_code_ship_doc_id ON km_code(ship_doc_id);
CREATE INDEX IF NOT EXISTS idx_km_code_order_id ON km_code(order_id);

CREATE TABLE IF NOT EXISTS marking_order (
    id UUID PRIMARY KEY,
    order_id BIGINT NOT NULL,
    item_id BIGINT,
    gtin TEXT,
    requested_quantity INTEGER NOT NULL,
    request_number TEXT NOT NULL,
    status TEXT NOT NULL,
    notes TEXT,
    requested_at TEXT,
    codes_bound_at TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    FOREIGN KEY (order_id) REFERENCES orders(id),
    FOREIGN KEY (item_id) REFERENCES items(id)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_marking_order_request_number ON marking_order(request_number);
CREATE INDEX IF NOT EXISTS ix_marking_order_order_id ON marking_order(order_id);
CREATE INDEX IF NOT EXISTS ix_marking_order_status ON marking_order(status);
CREATE INDEX IF NOT EXISTS ix_marking_order_gtin ON marking_order(gtin);

CREATE TABLE IF NOT EXISTS marking_code_import (
    id UUID PRIMARY KEY,
    original_filename TEXT NOT NULL,
    storage_path TEXT NOT NULL,
    file_hash TEXT NOT NULL,
    source_type TEXT NOT NULL,
    detected_request_number TEXT,
    detected_gtin TEXT,
    detected_quantity INTEGER,
    matched_marking_order_id UUID,
    match_confidence NUMERIC(5,4),
    status TEXT NOT NULL,
    imported_rows INTEGER NOT NULL DEFAULT 0,
    valid_code_rows INTEGER NOT NULL DEFAULT 0,
    duplicate_code_rows INTEGER NOT NULL DEFAULT 0,
    error_message TEXT,
    created_at TEXT NOT NULL,
    processed_at TEXT,
    FOREIGN KEY (matched_marking_order_id) REFERENCES marking_order(id)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_marking_code_import_file_hash ON marking_code_import(file_hash);
CREATE INDEX IF NOT EXISTS ix_marking_code_import_status_created ON marking_code_import(status, created_at);
CREATE INDEX IF NOT EXISTS ix_marking_code_import_order_id ON marking_code_import(matched_marking_order_id);
CREATE INDEX IF NOT EXISTS ix_marking_code_import_request_number ON marking_code_import(detected_request_number);

CREATE TABLE IF NOT EXISTS marking_code (
    id UUID PRIMARY KEY,
    code TEXT NOT NULL,
    code_hash TEXT NOT NULL,
    gtin TEXT,
    marking_order_id UUID NOT NULL,
    import_id UUID NOT NULL,
    status TEXT NOT NULL,
    source_row_number INTEGER,
    printed_at TEXT,
    applied_at TEXT,
    reported_at TEXT,
    introduced_at TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    FOREIGN KEY (marking_order_id) REFERENCES marking_order(id),
    FOREIGN KEY (import_id) REFERENCES marking_code_import(id)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_marking_code_code ON marking_code(code);
CREATE INDEX IF NOT EXISTS ix_marking_code_code_hash ON marking_code(code_hash);
CREATE INDEX IF NOT EXISTS ix_marking_code_order_status ON marking_code(marking_order_id, status);
CREATE INDEX IF NOT EXISTS ix_marking_code_import_id ON marking_code(import_id);
CREATE INDEX IF NOT EXISTS ix_marking_code_gtin ON marking_code(gtin);

CREATE TABLE IF NOT EXISTS marking_print_batch (
    id UUID PRIMARY KEY,
    marking_order_id UUID NOT NULL,
    batch_number INTEGER NOT NULL,
    status TEXT NOT NULL,
    codes_count INTEGER NOT NULL DEFAULT 0,
    printer_target_type TEXT,
    printer_target_value TEXT,
    debug_layout INTEGER NOT NULL DEFAULT 0,
    reprint_of_batch_id UUID,
    printed_at TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    notes TEXT,
    FOREIGN KEY (marking_order_id) REFERENCES marking_order(id),
    FOREIGN KEY (reprint_of_batch_id) REFERENCES marking_print_batch(id)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_marking_print_batch_order_batch_number ON marking_print_batch(marking_order_id, batch_number);
CREATE INDEX IF NOT EXISTS ix_marking_print_batch_order_status ON marking_print_batch(marking_order_id, status);
CREATE INDEX IF NOT EXISTS ix_marking_print_batch_reprint_of_batch_id ON marking_print_batch(reprint_of_batch_id);

CREATE TABLE IF NOT EXISTS marking_print_batch_code (
    id UUID PRIMARY KEY,
    print_batch_id UUID NOT NULL,
    marking_code_id UUID NOT NULL,
    sequence_no INTEGER NOT NULL,
    created_at TEXT NOT NULL,
    FOREIGN KEY (print_batch_id) REFERENCES marking_print_batch(id),
    FOREIGN KEY (marking_code_id) REFERENCES marking_code(id)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_marking_print_batch_code_sequence ON marking_print_batch_code(print_batch_id, sequence_no);
CREATE UNIQUE INDEX IF NOT EXISTS ux_marking_print_batch_code_marking_code ON marking_print_batch_code(print_batch_id, marking_code_id);
CREATE INDEX IF NOT EXISTS ix_marking_print_batch_code_marking_code_id ON marking_print_batch_code(marking_code_id);

ALTER TABLE IF EXISTS items ADD COLUMN IF NOT EXISTS gtin TEXT;
ALTER TABLE IF EXISTS items ADD COLUMN IF NOT EXISTS uom TEXT;
ALTER TABLE IF EXISTS items ADD COLUMN IF NOT EXISTS base_uom TEXT NOT NULL DEFAULT 'шт';
ALTER TABLE IF EXISTS items ADD COLUMN IF NOT EXISTS default_packaging_id BIGINT;
ALTER TABLE IF EXISTS items ADD COLUMN IF NOT EXISTS brand TEXT;
ALTER TABLE IF EXISTS items ADD COLUMN IF NOT EXISTS volume TEXT;
ALTER TABLE IF EXISTS items ADD COLUMN IF NOT EXISTS shelf_life_months INTEGER;
ALTER TABLE IF EXISTS items ADD COLUMN IF NOT EXISTS max_qty_per_hu REAL;
ALTER TABLE IF EXISTS items ADD COLUMN IF NOT EXISTS tara_id BIGINT;
ALTER TABLE IF EXISTS items ADD COLUMN IF NOT EXISTS is_marked INTEGER NOT NULL DEFAULT 0;

ALTER TABLE IF EXISTS partners ADD COLUMN IF NOT EXISTS created_at TEXT;

ALTER TABLE IF EXISTS orders ADD COLUMN IF NOT EXISTS order_type TEXT NOT NULL DEFAULT 'CUSTOMER';

ALTER TABLE IF EXISTS docs ADD COLUMN IF NOT EXISTS partner_id BIGINT;
ALTER TABLE IF EXISTS docs ADD COLUMN IF NOT EXISTS order_id BIGINT;
ALTER TABLE IF EXISTS docs ADD COLUMN IF NOT EXISTS order_ref TEXT;
ALTER TABLE IF EXISTS docs ADD COLUMN IF NOT EXISTS shipping_ref TEXT;
ALTER TABLE IF EXISTS docs ADD COLUMN IF NOT EXISTS reason_code TEXT;
ALTER TABLE IF EXISTS docs ADD COLUMN IF NOT EXISTS comment TEXT;
ALTER TABLE IF EXISTS docs ADD COLUMN IF NOT EXISTS production_batch_no TEXT;

ALTER TABLE IF EXISTS doc_lines ADD COLUMN IF NOT EXISTS order_line_id BIGINT;
ALTER TABLE IF EXISTS doc_lines ADD COLUMN IF NOT EXISTS replaces_line_id BIGINT;
ALTER TABLE IF EXISTS doc_lines ADD COLUMN IF NOT EXISTS qty_input REAL;
ALTER TABLE IF EXISTS doc_lines ADD COLUMN IF NOT EXISTS uom_code TEXT;
ALTER TABLE IF EXISTS doc_lines ADD COLUMN IF NOT EXISTS from_hu TEXT;
ALTER TABLE IF EXISTS doc_lines ADD COLUMN IF NOT EXISTS to_hu TEXT;

ALTER TABLE IF EXISTS ledger ADD COLUMN IF NOT EXISTS hu TEXT;
ALTER TABLE IF EXISTS ledger ADD COLUMN IF NOT EXISTS hu_code TEXT;

ALTER TABLE IF EXISTS api_docs ADD COLUMN IF NOT EXISTS doc_type TEXT;
ALTER TABLE IF EXISTS api_docs ADD COLUMN IF NOT EXISTS doc_ref TEXT;
ALTER TABLE IF EXISTS api_docs ADD COLUMN IF NOT EXISTS partner_id BIGINT;
ALTER TABLE IF EXISTS api_docs ADD COLUMN IF NOT EXISTS from_location_id BIGINT;
ALTER TABLE IF EXISTS api_docs ADD COLUMN IF NOT EXISTS to_location_id BIGINT;
ALTER TABLE IF EXISTS api_docs ADD COLUMN IF NOT EXISTS from_hu TEXT;
ALTER TABLE IF EXISTS api_docs ADD COLUMN IF NOT EXISTS to_hu TEXT;
ALTER TABLE IF EXISTS api_docs ADD COLUMN IF NOT EXISTS device_id TEXT;

ALTER TABLE IF EXISTS api_events ADD COLUMN IF NOT EXISTS received_at TEXT;
ALTER TABLE IF EXISTS api_events ADD COLUMN IF NOT EXISTS device_id TEXT;
ALTER TABLE IF EXISTS api_events ADD COLUMN IF NOT EXISTS raw_json TEXT;

ALTER TABLE IF EXISTS tsd_devices ADD COLUMN IF NOT EXISTS platform TEXT NOT NULL DEFAULT 'TSD';

CREATE INDEX IF NOT EXISTS ix_docs_order ON docs(order_id);
CREATE INDEX IF NOT EXISTS ix_doc_lines_order_line ON doc_lines(order_line_id);
CREATE INDEX IF NOT EXISTS ix_doc_lines_replaces_line ON doc_lines(replaces_line_id);
CREATE INDEX IF NOT EXISTS ix_ledger_item_loc_hu ON ledger(item_id, location_id, hu);
CREATE INDEX IF NOT EXISTS ix_ledger_item_loc_hu_code ON ledger(item_id, location_id, hu_code);
CREATE INDEX IF NOT EXISTS ux_hus_hu_code ON hus(hu_code);

UPDATE items
SET base_uom = COALESCE(NULLIF(base_uom, ''), NULLIF(uom, ''), 'шт')
WHERE base_uom IS NULL OR base_uom = '';

UPDATE partners
SET created_at = TO_CHAR(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS')
WHERE created_at IS NULL OR created_at = '';

UPDATE orders
SET order_type = 'CUSTOMER'
WHERE order_type IS NULL OR order_type = '';

UPDATE ledger
SET hu_code = hu
WHERE (hu_code IS NULL OR hu_code = '')
  AND hu IS NOT NULL
  AND hu <> '';

UPDATE tsd_devices
SET platform = 'TSD'
WHERE platform IS NULL OR platform = '';

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = current_schema()
          AND table_name = 'orders'
          AND column_name = 'partner_id'
          AND is_nullable = 'NO'
    ) THEN
        ALTER TABLE orders ALTER COLUMN partner_id DROP NOT NULL;
    END IF;
END $$;

INSERT INTO hus(hu_code, status, created_at, created_by)
SELECT DISTINCT source.hu_code,
       'OPEN',
       TO_CHAR(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS'),
       'migration'
FROM (
    SELECT hu_code
    FROM ledger
    WHERE hu_code IS NOT NULL AND hu_code <> ''
    UNION ALL
    SELECT from_hu
    FROM doc_lines
    WHERE from_hu IS NOT NULL AND from_hu <> ''
    UNION ALL
    SELECT to_hu
    FROM doc_lines
    WHERE to_hu IS NOT NULL AND to_hu <> ''
) AS source
ON CONFLICT (hu_code) DO NOTHING;

UPDATE items i
SET is_marked = 1
WHERE COALESCE(i.is_marked, 0) = 0
  AND EXISTS (
      SELECT 1
      FROM km_code c
      WHERE c.sku_id = i.id
         OR (
            c.sku_id IS NULL
            AND c.gtin14 IS NOT NULL
            AND i.gtin IS NOT NULL
            AND (
                c.gtin14 = i.gtin
                OR (LENGTH(i.gtin) = 13 AND c.gtin14 = '0' || i.gtin)
            )
         )
  );
