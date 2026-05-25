CREATE TABLE IF NOT EXISTS volume_discount_rules (
    id BIGSERIAL PRIMARY KEY,
    scope_type TEXT NOT NULL,
    price_group_id BIGINT REFERENCES price_groups(id),
    partner_id BIGINT REFERENCES partners(id),
    item_type_id BIGINT REFERENCES item_types(id),
    item_id BIGINT REFERENCES items(id),
    min_qty REAL NOT NULL,
    discount_percent NUMERIC(7,4) NOT NULL,
    valid_from DATE,
    valid_to DATE,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    comment TEXT
);
CREATE INDEX IF NOT EXISTS ix_volume_discount_rules_scope ON volume_discount_rules(scope_type, is_active);
