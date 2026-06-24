CREATE TABLE IF NOT EXISTS order_control_tasks (
    id BIGSERIAL PRIMARY KEY,
    task_ref TEXT NOT NULL,
    status TEXT NOT NULL,
    created_at TEXT NOT NULL,
    created_by TEXT NULL,
    started_at TEXT NULL,
    completed_at TEXT NULL,
    cancelled_at TEXT NULL,
    cancelled_by TEXT NULL,
    assigned_to_device_id TEXT NULL,
    expected_hu_count INTEGER NOT NULL DEFAULT 0,
    checked_hu_count INTEGER NOT NULL DEFAULT 0,
    discrepancy_hu_count INTEGER NOT NULL DEFAULT 0,
    snapshot_hash TEXT NOT NULL,
    comment TEXT NULL,
    error_code TEXT NULL,
    error_message TEXT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_order_control_tasks_ref
    ON order_control_tasks(UPPER(BTRIM(task_ref)));

CREATE INDEX IF NOT EXISTS ix_order_control_tasks_active
    ON order_control_tasks(status, created_at DESC)
    WHERE status IN ('NEW', 'IN_EXECUTION');

CREATE TABLE IF NOT EXISTS order_control_task_orders (
    id BIGSERIAL PRIMARY KEY,
    task_id BIGINT NOT NULL REFERENCES order_control_tasks(id) ON DELETE CASCADE,
    order_id BIGINT NOT NULL REFERENCES orders(id),
    order_ref TEXT NOT NULL,
    partner_name TEXT NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_order_control_task_orders_task_order
    ON order_control_task_orders(task_id, order_id);

CREATE UNIQUE INDEX IF NOT EXISTS ux_order_control_task_orders_active_order
    ON order_control_task_orders(order_id)
    WHERE is_active;

CREATE INDEX IF NOT EXISTS ix_order_control_task_orders_task
    ON order_control_task_orders(task_id);

CREATE TABLE IF NOT EXISTS order_control_task_hus (
    id BIGSERIAL PRIMARY KEY,
    task_id BIGINT NOT NULL REFERENCES order_control_tasks(id) ON DELETE CASCADE,
    hu_code TEXT NOT NULL,
    normalized_hu TEXT NOT NULL,
    status TEXT NOT NULL,
    qty DOUBLE PRECISION NOT NULL DEFAULT 0,
    item_summary TEXT NOT NULL DEFAULT '',
    snapshot_hash TEXT NOT NULL,
    checked_at TEXT NULL,
    checked_by_device_id TEXT NULL,
    checked_by_operator TEXT NULL,
    error_code TEXT NULL,
    error_message TEXT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_order_control_task_hus_task_hu
    ON order_control_task_hus(task_id, normalized_hu);

CREATE INDEX IF NOT EXISTS ix_order_control_task_hus_active_hu
    ON order_control_task_hus(normalized_hu)
    WHERE status IN ('PENDING', 'CHECKED', 'DISCREPANCY');

CREATE INDEX IF NOT EXISTS ix_order_control_task_hus_task
    ON order_control_task_hus(task_id, status);

CREATE TABLE IF NOT EXISTS order_control_task_hu_lines (
    id BIGSERIAL PRIMARY KEY,
    task_hu_id BIGINT NOT NULL REFERENCES order_control_task_hus(id) ON DELETE CASCADE,
    task_id BIGINT NOT NULL REFERENCES order_control_tasks(id) ON DELETE CASCADE,
    order_id BIGINT NOT NULL REFERENCES orders(id),
    order_ref TEXT NOT NULL,
    order_line_id BIGINT NOT NULL REFERENCES order_lines(id),
    item_id BIGINT NOT NULL REFERENCES items(id),
    item_name TEXT NOT NULL DEFAULT '',
    qty DOUBLE PRECISION NOT NULL DEFAULT 0,
    location_id BIGINT NULL REFERENCES locations(id),
    location_code TEXT NULL,
    source_type TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_order_control_task_hu_lines_task
    ON order_control_task_hu_lines(task_id, task_hu_id);

CREATE TABLE IF NOT EXISTS order_control_events (
    id BIGSERIAL PRIMARY KEY,
    task_id BIGINT NOT NULL REFERENCES order_control_tasks(id) ON DELETE CASCADE,
    task_hu_id BIGINT NULL REFERENCES order_control_task_hus(id) ON DELETE SET NULL,
    event_type TEXT NOT NULL,
    event_at TEXT NOT NULL,
    device_id TEXT NULL,
    operator_id TEXT NULL,
    hu_code TEXT NULL,
    request_id TEXT NULL,
    payload_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    error_code TEXT NULL,
    message TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_order_control_events_task
    ON order_control_events(task_id, event_at DESC, id DESC);

CREATE UNIQUE INDEX IF NOT EXISTS ux_order_control_events_request
    ON order_control_events(task_id, UPPER(BTRIM(request_id)))
    WHERE request_id IS NOT NULL AND BTRIM(request_id) <> '';

INSERT INTO client_blocks(block_key, is_enabled, updated_at)
VALUES
    ('tsd_order_control', TRUE, TO_CHAR(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS'))
ON CONFLICT (block_key) DO NOTHING;
