BEGIN;

ALTER TABLE IF EXISTS orders
    ADD COLUMN IF NOT EXISTS marking_responsibility TEXT NOT NULL DEFAULT 'FLOWSTOCK';

UPDATE orders
SET marking_responsibility = 'FLOWSTOCK'
WHERE marking_responsibility IS NULL
   OR BTRIM(marking_responsibility) = ''
   OR marking_responsibility NOT IN ('FLOWSTOCK', 'CUSTOMER');

ALTER TABLE IF EXISTS orders
    DROP CONSTRAINT IF EXISTS ck_orders_marking_responsibility;

ALTER TABLE IF EXISTS orders
    ADD CONSTRAINT ck_orders_marking_responsibility
    CHECK (marking_responsibility IN ('FLOWSTOCK', 'CUSTOMER'));

CREATE TABLE IF NOT EXISTS marking_responsibility_audit (
    id BIGSERIAL PRIMARY KEY,
    order_id BIGINT NOT NULL REFERENCES orders(id) ON DELETE RESTRICT,
    old_responsibility TEXT,
    new_responsibility TEXT NOT NULL,
    reason TEXT,
    changed_at TEXT NOT NULL,
    changed_by_actor TEXT,
    changed_by_device_id TEXT
);
CREATE INDEX IF NOT EXISTS ix_marking_responsibility_audit_order
    ON marking_responsibility_audit(order_id, changed_at);

ALTER TABLE IF EXISTS order_lines
    ADD COLUMN IF NOT EXISTS cancelled_at TEXT NULL;

ALTER TABLE IF EXISTS order_lines
    ADD COLUMN IF NOT EXISTS cancelled_by_actor TEXT NULL;

ALTER TABLE IF EXISTS order_lines
    ADD COLUMN IF NOT EXISTS cancelled_by_device_id TEXT NULL;

ALTER TABLE IF EXISTS order_lines
    ADD COLUMN IF NOT EXISTS cancel_reason TEXT NULL;

ALTER TABLE IF EXISTS order_lines
    ADD COLUMN IF NOT EXISTS revision BIGINT NOT NULL DEFAULT 0;

CREATE INDEX IF NOT EXISTS ix_order_lines_active_order
    ON order_lines(order_id, id)
    WHERE cancelled_at IS NULL;

ALTER TABLE IF EXISTS marking_order
    ADD COLUMN IF NOT EXISTS order_line_id BIGINT NULL REFERENCES order_lines(id) ON DELETE RESTRICT;

ALTER TABLE IF EXISTS marking_order
    ADD COLUMN IF NOT EXISTS request_status TEXT NOT NULL DEFAULT 'NotRequested';

ALTER TABLE IF EXISTS marking_order
    ADD COLUMN IF NOT EXISTS last_excel_requested_at TEXT NULL;

ALTER TABLE IF EXISTS marking_order
    ADD COLUMN IF NOT EXISTS last_excel_request_hash TEXT NULL;

ALTER TABLE IF EXISTS marking_order
    DROP CONSTRAINT IF EXISTS ck_marking_order_request_status;

ALTER TABLE IF EXISTS marking_order
    ADD CONSTRAINT ck_marking_order_request_status
    CHECK (request_status IN ('NotRequested', 'ExcelRequested'));

WITH candidates AS (
    -- Only markable lines (item_types.enable_marking = TRUE) can be a canonical line scope.
    -- A task whose order_id and source_order_id are both set but point to different orders is
    -- an ambiguous link and must never be silently bound to a line.
    SELECT mo.id AS marking_order_id,
           ol.id AS order_line_id,
           COUNT(*) OVER (PARTITION BY mo.id) AS task_candidate_count
    FROM marking_order mo
    INNER JOIN order_lines ol ON ol.order_id = COALESCE(mo.order_id, mo.source_order_id)
    INNER JOIN items i ON i.id = ol.item_id
    INNER JOIN item_types it ON it.id = i.item_type_id
    WHERE mo.order_line_id IS NULL
      AND mo.status NOT IN ('Cancelled', 'Failed')
      AND COALESCE(it.enable_marking, FALSE) = TRUE
      AND NOT (
          mo.order_id IS NOT NULL
          AND mo.source_order_id IS NOT NULL
          AND mo.order_id <> mo.source_order_id
      )
      AND (
          (mo.item_id IS NOT NULL AND mo.item_id = ol.item_id)
          OR (
              NULLIF(BTRIM(mo.gtin), '') IS NOT NULL
              AND NULLIF(BTRIM(i.gtin), '') IS NOT NULL
              AND BTRIM(mo.gtin) = BTRIM(i.gtin)
          )
      )
),
unambiguous_tasks AS (
    SELECT marking_order_id,
           order_line_id
    FROM candidates
    WHERE task_candidate_count = 1
),
unambiguous_lines AS (
    SELECT order_line_id
    FROM unambiguous_tasks
    GROUP BY order_line_id
    HAVING COUNT(*) = 1
)
UPDATE marking_order mo
SET order_line_id = u.order_line_id
FROM unambiguous_tasks u
INNER JOIN unambiguous_lines l ON l.order_line_id = u.order_line_id
WHERE mo.id = u.marking_order_id;

CREATE INDEX IF NOT EXISTS ix_marking_order_order_line_id
    ON marking_order(order_line_id)
    WHERE order_line_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_marking_order_active_order_line
    ON marking_order(order_line_id)
    WHERE order_line_id IS NOT NULL
      AND status NOT IN ('Cancelled', 'Failed');

ALTER TABLE IF EXISTS marking_code
    ADD COLUMN IF NOT EXISTS origin TEXT;

UPDATE marking_code c
SET origin = 'LegacySynthetic'
FROM marking_code_import i
WHERE c.import_id = i.id
  AND c.origin IS NULL
  AND (
      c.code LIKE 'TEMP-CHZ-%'
      OR i.source_type = 'temporary-chz-export'
      OR i.storage_path = '<temporary-chz-export>'
  );

UPDATE marking_code c
SET origin = 'LegacyRealImport'
FROM marking_code_import i
WHERE c.import_id = i.id
  AND c.origin IS NULL
  AND i.source_type IN ('csv', 'tsv')
  AND c.code NOT LIKE 'TEMP-CHZ-%';

UPDATE marking_code
SET origin = 'HistoricalUnknown'
WHERE origin IS NULL;

ALTER TABLE IF EXISTS marking_code
    ALTER COLUMN origin SET DEFAULT 'HistoricalUnknown';

ALTER TABLE IF EXISTS marking_code
    ALTER COLUMN origin SET NOT NULL;

ALTER TABLE IF EXISTS marking_code
    DROP CONSTRAINT IF EXISTS ck_marking_code_origin;

ALTER TABLE IF EXISTS marking_code
    ADD CONSTRAINT ck_marking_code_origin
    CHECK (origin IN ('RealImport', 'LegacySynthetic', 'LegacyRealImport', 'HistoricalUnknown'));

ALTER TABLE IF EXISTS marking_code
    DROP CONSTRAINT IF EXISTS ck_marking_code_status_quarantine;

ALTER TABLE IF EXISTS marking_code
    ADD CONSTRAINT ck_marking_code_status_quarantine
    CHECK (status <> 'Quarantined' OR origin IN ('RealImport', 'LegacyRealImport', 'HistoricalUnknown'));

-- PR 1 (SHADOW) ships only the NON-unique preflight index used to surface historical
-- real-like duplicates. The unique partial index for new origin = 'RealImport' is introduced
-- later, in the CSV confirm PR, immediately before confirm import is enabled; widening
-- uniqueness onto legacy real codes happens in a still later PR after a clean preflight.
CREATE INDEX IF NOT EXISTS ix_marking_code_real_code_hash_preflight
    ON marking_code(LOWER(BTRIM(code_hash)))
    WHERE origin IN ('RealImport', 'LegacyRealImport', 'HistoricalUnknown');

CREATE INDEX IF NOT EXISTS ix_marking_code_origin_status
    ON marking_code(origin, status);

CREATE TABLE IF NOT EXISTS marking_import_batch (
    id UUID PRIMARY KEY,
    order_id BIGINT NOT NULL REFERENCES orders(id) ON DELETE RESTRICT,
    order_line_id BIGINT NOT NULL REFERENCES order_lines(id) ON DELETE RESTRICT,
    marking_order_id UUID NULL REFERENCES marking_order(id) ON DELETE RESTRICT,
    original_filename TEXT NOT NULL,
    file_hash TEXT NOT NULL,
    file_size_bytes BIGINT NOT NULL DEFAULT 0,
    row_count INTEGER NOT NULL DEFAULT 0,
    status TEXT NOT NULL,
    target_marking_qty_snapshot NUMERIC(18,6) NOT NULL DEFAULT 0,
    coverage_snapshot_hash TEXT,
    expires_at TEXT,
    idempotency_key TEXT,
    created_at TEXT NOT NULL,
    created_by_actor TEXT,
    created_by_device_id TEXT,
    confirmed_at TEXT,
    confirmed_by_actor TEXT,
    confirmed_by_device_id TEXT,
    error_code TEXT,
    error_message TEXT,
    CHECK (status IN ('Previewed', 'Confirmed', 'Expired', 'Rejected'))
);
CREATE INDEX IF NOT EXISTS ix_marking_import_batch_line_status
    ON marking_import_batch(order_line_id, status, created_at);
CREATE INDEX IF NOT EXISTS ix_marking_import_batch_file_hash
    ON marking_import_batch(file_hash);
CREATE UNIQUE INDEX IF NOT EXISTS ux_marking_import_batch_idempotency
    ON marking_import_batch(order_line_id, idempotency_key)
    WHERE idempotency_key IS NOT NULL;

CREATE TABLE IF NOT EXISTS marking_code_import_row (
    id BIGSERIAL PRIMARY KEY,
    import_batch_id UUID NOT NULL REFERENCES marking_import_batch(id) ON DELETE CASCADE,
    source_row_number INTEGER NOT NULL,
    code_hash TEXT,
    code_preview TEXT,
    gtin TEXT,
    status TEXT NOT NULL,
    error_code TEXT,
    message TEXT,
    created_at TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_marking_code_import_row_batch_row
    ON marking_code_import_row(import_batch_id, source_row_number);
CREATE INDEX IF NOT EXISTS ix_marking_code_import_row_hash
    ON marking_code_import_row(code_hash)
    WHERE code_hash IS NOT NULL;

CREATE TABLE IF NOT EXISTS marking_synthetic_legacy_allowlist (
    id BIGSERIAL PRIMARY KEY,
    order_line_id BIGINT NOT NULL REFERENCES order_lines(id) ON DELETE RESTRICT,
    allowed_synthetic_qty INTEGER NOT NULL CHECK (allowed_synthetic_qty >= 0),
    target_qty_at_cutover NUMERIC(18,6) NOT NULL CHECK (target_qty_at_cutover >= 0),
    approved_at TEXT NOT NULL,
    approved_by TEXT NOT NULL,
    approved_by_device_id TEXT,
    preflight_hash TEXT NOT NULL,
    UNIQUE(order_line_id, preflight_hash)
);
CREATE INDEX IF NOT EXISTS ix_marking_synthetic_legacy_allowlist_line
    ON marking_synthetic_legacy_allowlist(order_line_id, approved_at);

CREATE TABLE IF NOT EXISTS marking_line_change_audit (
    id BIGSERIAL PRIMARY KEY,
    order_id BIGINT NOT NULL REFERENCES orders(id) ON DELETE RESTRICT,
    order_line_id BIGINT NOT NULL REFERENCES order_lines(id) ON DELETE RESTRICT,
    action TEXT NOT NULL,
    old_qty NUMERIC(18,6),
    new_qty NUMERIC(18,6),
    changed_at TEXT NOT NULL,
    changed_by_actor TEXT,
    changed_by_device_id TEXT,
    reason TEXT,
    preflight_hash TEXT,
    payload_json TEXT,
    CHECK (action IN ('cancel', 'set_quantity'))
);
CREATE INDEX IF NOT EXISTS ix_marking_line_change_audit_line
    ON marking_line_change_audit(order_line_id, changed_at);

CREATE TABLE IF NOT EXISTS marking_cutover_state (
    id BOOLEAN PRIMARY KEY DEFAULT TRUE CHECK (id),
    state TEXT NOT NULL,
    preflight_hash TEXT,
    preflight_generated_at TEXT,
    preflight_approved_at TEXT,
    preflight_approved_by TEXT,
    preflight_approved_by_device_id TEXT,
    enforced_at TEXT,
    enforced_by TEXT,
    enforced_by_device_id TEXT,
    updated_at TEXT NOT NULL,
    CHECK (state IN ('SHADOW', 'PREFLIGHT_READY', 'ENFORCED'))
);

INSERT INTO marking_cutover_state(id, state, updated_at)
VALUES(TRUE, 'SHADOW', to_char(NOW() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"'))
ON CONFLICT (id) DO NOTHING;

COMMIT;
