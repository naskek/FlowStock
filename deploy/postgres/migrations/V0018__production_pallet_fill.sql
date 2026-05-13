CREATE TABLE IF NOT EXISTS production_pallets (
    id BIGSERIAL PRIMARY KEY,
    prd_doc_id BIGINT NOT NULL REFERENCES docs(id),
    doc_line_id BIGINT NOT NULL REFERENCES doc_lines(id),
    order_id BIGINT NULL REFERENCES orders(id),
    order_line_id BIGINT NULL REFERENCES order_lines(id),
    item_id BIGINT NOT NULL REFERENCES items(id),
    hu_code TEXT NOT NULL,
    planned_qty DOUBLE PRECISION NOT NULL,
    to_location_id BIGINT NULL REFERENCES locations(id),
    status TEXT NOT NULL DEFAULT 'PLANNED',
    filled_at TEXT NULL,
    filled_by_device_id TEXT NULL,
    created_at TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_production_pallets_prd_doc_line
    ON production_pallets(prd_doc_id, doc_line_id);

CREATE UNIQUE INDEX IF NOT EXISTS ux_production_pallets_active_hu
    ON production_pallets(UPPER(BTRIM(hu_code)))
    WHERE status <> 'CANCELLED';

CREATE INDEX IF NOT EXISTS ix_production_pallets_prd_doc
    ON production_pallets(prd_doc_id);

CREATE INDEX IF NOT EXISTS ix_production_pallets_order_line_status
    ON production_pallets(order_line_id, status);
