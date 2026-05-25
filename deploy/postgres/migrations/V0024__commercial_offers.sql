CREATE TABLE IF NOT EXISTS commercial_offers (
    id BIGSERIAL PRIMARY KEY,
    offer_ref TEXT NOT NULL UNIQUE,
    partner_id BIGINT NOT NULL REFERENCES partners(id),
    contact_person TEXT,
    contact_phone TEXT,
    contact_email TEXT,
    price_group_id BIGINT NOT NULL REFERENCES price_groups(id),
    status TEXT NOT NULL DEFAULT 'DRAFT',
    currency TEXT NOT NULL DEFAULT 'RUB',
    valid_until DATE,
    payment_terms TEXT,
    delivery_terms TEXT,
    comment TEXT,
    manager_name TEXT,
    subtotal NUMERIC(18,2) NOT NULL DEFAULT 0,
    discount_total NUMERIC(18,2) NOT NULL DEFAULT 0,
    total NUMERIC(18,2) NOT NULL DEFAULT 0,
    next_follow_up_at TIMESTAMPTZ,
    converted_order_id BIGINT REFERENCES orders(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    sent_at TIMESTAMPTZ,
    closed_at TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS ix_commercial_offers_partner ON commercial_offers(partner_id);
CREATE INDEX IF NOT EXISTS ix_commercial_offers_status ON commercial_offers(status);
CREATE INDEX IF NOT EXISTS ix_commercial_offers_valid_until ON commercial_offers(valid_until);

CREATE TABLE IF NOT EXISTS commercial_offer_lines (
    id BIGSERIAL PRIMARY KEY,
    offer_id BIGINT NOT NULL REFERENCES commercial_offers(id) ON DELETE CASCADE,
    line_no INTEGER NOT NULL,
    item_id BIGINT NOT NULL REFERENCES items(id),
    qty REAL NOT NULL,
    uom_code TEXT,
    base_price NUMERIC(18,4) NOT NULL,
    volume_discount_percent NUMERIC(7,4) NOT NULL DEFAULT 0,
    manual_discount_percent NUMERIC(7,4) NOT NULL DEFAULT 0,
    final_discount_percent NUMERIC(7,4) NOT NULL DEFAULT 0,
    final_price NUMERIC(18,4) NOT NULL,
    line_total NUMERIC(18,2) NOT NULL,
    comment TEXT
);
CREATE INDEX IF NOT EXISTS ix_commercial_offer_lines_offer ON commercial_offer_lines(offer_id);

CREATE TABLE IF NOT EXISTS commercial_offer_status_history (
    id BIGSERIAL PRIMARY KEY,
    offer_id BIGINT NOT NULL REFERENCES commercial_offers(id) ON DELETE CASCADE,
    old_status TEXT,
    new_status TEXT NOT NULL,
    comment TEXT,
    changed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    changed_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_commercial_offer_status_history_offer ON commercial_offer_status_history(offer_id);
