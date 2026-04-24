BEGIN;

ALTER TABLE IF EXISTS item_types
    ADD COLUMN IF NOT EXISTS enable_hu_distribution BOOLEAN NOT NULL DEFAULT FALSE;

CREATE TABLE IF NOT EXISTS order_receipt_plan_lines (
    id BIGSERIAL PRIMARY KEY,
    order_id BIGINT NOT NULL,
    order_line_id BIGINT NOT NULL,
    item_id BIGINT NOT NULL,
    qty_planned REAL NOT NULL,
    to_location_id BIGINT,
    to_hu TEXT,
    sort_order INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (order_id) REFERENCES orders(id) ON DELETE CASCADE,
    FOREIGN KEY (order_line_id) REFERENCES order_lines(id) ON DELETE CASCADE,
    FOREIGN KEY (item_id) REFERENCES items(id),
    FOREIGN KEY (to_location_id) REFERENCES locations(id)
);

CREATE INDEX IF NOT EXISTS ix_order_receipt_plan_order ON order_receipt_plan_lines(order_id, sort_order, id);
CREATE INDEX IF NOT EXISTS ix_order_receipt_plan_order_line ON order_receipt_plan_lines(order_line_id);
CREATE INDEX IF NOT EXISTS ix_order_receipt_plan_to_hu ON order_receipt_plan_lines(to_hu);

COMMIT;
