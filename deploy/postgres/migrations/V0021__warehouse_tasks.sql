CREATE TABLE IF NOT EXISTS warehouse_action_bundles (
    id BIGSERIAL PRIMARY KEY,
    bundle_ref TEXT NOT NULL,
    source TEXT NOT NULL,
    status TEXT NOT NULL,
    created_at TEXT NOT NULL,
    created_by TEXT NULL,
    approved_at TEXT NULL,
    approved_by TEXT NULL,
    executed_at TEXT NULL,
    completed_at TEXT NULL,
    rejected_at TEXT NULL,
    rejected_by TEXT NULL,
    comment TEXT NULL,
    error_code TEXT NULL,
    error_message TEXT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_warehouse_action_bundles_ref
    ON warehouse_action_bundles(UPPER(BTRIM(bundle_ref)));

CREATE INDEX IF NOT EXISTS ix_warehouse_action_bundles_status
    ON warehouse_action_bundles(status, created_at DESC);

CREATE TABLE IF NOT EXISTS warehouse_action_lines (
    id BIGSERIAL PRIMARY KEY,
    bundle_id BIGINT NOT NULL REFERENCES warehouse_action_bundles(id) ON DELETE CASCADE,
    line_no INTEGER NOT NULL,
    action_type TEXT NOT NULL,
    status TEXT NOT NULL,
    source_order_id BIGINT NULL REFERENCES orders(id),
    target_order_id BIGINT NULL REFERENCES orders(id),
    source_doc_id BIGINT NULL REFERENCES docs(id),
    target_doc_id BIGINT NULL REFERENCES docs(id),
    item_id BIGINT NULL REFERENCES items(id),
    hu_code TEXT NULL,
    from_location_id BIGINT NULL REFERENCES locations(id),
    to_location_id BIGINT NULL REFERENCES locations(id),
    qty DOUBLE PRECISION NULL,
    payload_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    result_json JSONB NULL,
    error_code TEXT NULL,
    error_message TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_warehouse_action_lines_bundle_line_no
    ON warehouse_action_lines(bundle_id, line_no);

CREATE INDEX IF NOT EXISTS ix_warehouse_action_lines_bundle
    ON warehouse_action_lines(bundle_id);

CREATE INDEX IF NOT EXISTS ix_warehouse_action_lines_hu_code
    ON warehouse_action_lines(UPPER(BTRIM(hu_code)))
    WHERE hu_code IS NOT NULL AND BTRIM(hu_code) <> '';

CREATE TABLE IF NOT EXISTS warehouse_tasks (
    id BIGSERIAL PRIMARY KEY,
    task_ref TEXT NOT NULL,
    bundle_id BIGINT NOT NULL REFERENCES warehouse_action_bundles(id) ON DELETE CASCADE,
    action_line_id BIGINT NOT NULL REFERENCES warehouse_action_lines(id) ON DELETE CASCADE,
    task_type TEXT NOT NULL,
    status TEXT NOT NULL,
    assigned_to_device_id TEXT NULL,
    assigned_to_user TEXT NULL,
    created_at TEXT NOT NULL,
    started_at TEXT NULL,
    executed_at TEXT NULL,
    confirmed_at TEXT NULL,
    cancelled_at TEXT NULL,
    comment TEXT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_warehouse_tasks_ref
    ON warehouse_tasks(UPPER(BTRIM(task_ref)));

CREATE INDEX IF NOT EXISTS ix_warehouse_tasks_bundle
    ON warehouse_tasks(bundle_id, status);

CREATE TABLE IF NOT EXISTS warehouse_task_lines (
    id BIGSERIAL PRIMARY KEY,
    task_id BIGINT NOT NULL REFERENCES warehouse_tasks(id) ON DELETE CASCADE,
    line_no INTEGER NOT NULL,
    expected_hu_code TEXT NULL,
    expected_item_id BIGINT NULL REFERENCES items(id),
    expected_qty DOUBLE PRECISION NULL,
    from_location_id BIGINT NULL REFERENCES locations(id),
    to_location_id BIGINT NULL REFERENCES locations(id),
    order_id BIGINT NULL REFERENCES orders(id),
    doc_id BIGINT NULL REFERENCES docs(id),
    status TEXT NOT NULL,
    scanned_hu_code TEXT NULL,
    scanned_location_id BIGINT NULL REFERENCES locations(id),
    scanned_at TEXT NULL,
    device_id TEXT NULL,
    operator_id TEXT NULL,
    error_code TEXT NULL,
    error_message TEXT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_warehouse_task_lines_task_line_no
    ON warehouse_task_lines(task_id, line_no);

CREATE INDEX IF NOT EXISTS ix_warehouse_task_lines_expected_hu
    ON warehouse_task_lines(UPPER(BTRIM(expected_hu_code)))
    WHERE expected_hu_code IS NOT NULL AND BTRIM(expected_hu_code) <> '';

CREATE TABLE IF NOT EXISTS warehouse_task_events (
    id BIGSERIAL PRIMARY KEY,
    task_id BIGINT NOT NULL REFERENCES warehouse_tasks(id) ON DELETE CASCADE,
    task_line_id BIGINT NULL REFERENCES warehouse_task_lines(id) ON DELETE SET NULL,
    event_type TEXT NOT NULL,
    event_at TEXT NOT NULL,
    device_id TEXT NULL,
    operator_id TEXT NULL,
    hu_code TEXT NULL,
    location_id BIGINT NULL REFERENCES locations(id),
    payload_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    message TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_warehouse_task_events_task
    ON warehouse_task_events(task_id, event_at DESC);

INSERT INTO client_blocks(block_key, is_enabled, updated_at)
VALUES
    ('tsd_warehouse_tasks', TRUE, TO_CHAR(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS'))
ON CONFLICT (block_key) DO NOTHING;
