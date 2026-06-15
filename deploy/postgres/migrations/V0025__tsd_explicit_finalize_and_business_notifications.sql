CREATE TABLE IF NOT EXISTS production_filling_completions (
    order_id BIGINT NOT NULL REFERENCES orders(id),
    operation_fingerprint TEXT NOT NULL,
    completed_at TIMESTAMPTZ NOT NULL,
    completed_by_device_id TEXT NULL,
    PRIMARY KEY (order_id, operation_fingerprint)
);

CREATE TABLE IF NOT EXISTS business_notifications (
    id BIGSERIAL PRIMARY KEY,
    event_type TEXT NOT NULL,
    severity TEXT NOT NULL DEFAULT 'INFO',
    title TEXT NOT NULL,
    message TEXT NOT NULL,
    entity_type TEXT NULL,
    entity_id BIGINT NULL,
    entity_ref TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    source TEXT NOT NULL DEFAULT 'SERVER',
    dedupe_key TEXT NOT NULL UNIQUE
);

CREATE INDEX IF NOT EXISTS ix_business_notifications_created_at
    ON business_notifications(created_at DESC, id DESC);

CREATE TABLE IF NOT EXISTS business_notification_reads (
    notification_id BIGINT NOT NULL REFERENCES business_notifications(id) ON DELETE CASCADE,
    reader_key TEXT NOT NULL,
    read_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (notification_id, reader_key)
);
