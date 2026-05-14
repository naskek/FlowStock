ALTER TABLE order_lines
    ADD COLUMN IF NOT EXISTS production_pallet_group TEXT NULL;

ALTER TABLE production_pallets
    ADD COLUMN IF NOT EXISTS pallet_no INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS pallet_count INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS printed_at TEXT NULL;

CREATE TABLE IF NOT EXISTS production_pallet_lines (
    id BIGSERIAL PRIMARY KEY,
    production_pallet_id BIGINT NOT NULL REFERENCES production_pallets(id) ON DELETE CASCADE,
    doc_line_id BIGINT NOT NULL REFERENCES doc_lines(id),
    order_line_id BIGINT NULL REFERENCES order_lines(id),
    item_id BIGINT NOT NULL REFERENCES items(id),
    planned_qty DOUBLE PRECISION NOT NULL,
    filled_qty DOUBLE PRECISION NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL
);

INSERT INTO production_pallet_lines(
    production_pallet_id,
    doc_line_id,
    order_line_id,
    item_id,
    planned_qty,
    filled_qty,
    created_at)
SELECT p.id,
       p.doc_line_id,
       p.order_line_id,
       p.item_id,
       p.planned_qty,
       CASE WHEN p.status = 'FILLED' THEN p.planned_qty ELSE 0 END,
       p.created_at
FROM production_pallets p
WHERE NOT EXISTS (
    SELECT 1
    FROM production_pallet_lines existing
    WHERE existing.production_pallet_id = p.id
      AND existing.doc_line_id = p.doc_line_id
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_production_pallet_lines_pallet_doc_line
    ON production_pallet_lines(production_pallet_id, doc_line_id);

CREATE INDEX IF NOT EXISTS ix_production_pallet_lines_order_line
    ON production_pallet_lines(order_line_id);

CREATE INDEX IF NOT EXISTS ix_order_lines_production_pallet_group
    ON order_lines(order_id, production_pallet_group)
    WHERE production_pallet_group IS NOT NULL AND BTRIM(production_pallet_group) <> '';

CREATE UNIQUE INDEX IF NOT EXISTS ux_production_pallets_prd_hu
    ON production_pallets(prd_doc_id, UPPER(BTRIM(hu_code)))
    WHERE status <> 'CANCELLED';
