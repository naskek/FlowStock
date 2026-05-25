CREATE TABLE IF NOT EXISTS price_groups (
    id BIGSERIAL PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT,
    currency TEXT NOT NULL DEFAULT 'RUB',
    vat_mode TEXT NOT NULL DEFAULT 'INCLUDED',
    is_default BOOLEAN NOT NULL DEFAULT FALSE,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_price_groups_name ON price_groups(name);

CREATE TABLE IF NOT EXISTS partner_commercial_settings (
    partner_id BIGINT PRIMARY KEY REFERENCES partners(id),
    price_group_id BIGINT REFERENCES price_groups(id),
    default_discount_percent NUMERIC(7,4) NOT NULL DEFAULT 0,
    payment_terms TEXT,
    delivery_terms TEXT,
    valid_from DATE,
    valid_to DATE,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS item_prices (
    id BIGSERIAL PRIMARY KEY,
    item_id BIGINT NOT NULL REFERENCES items(id),
    price_group_id BIGINT NOT NULL REFERENCES price_groups(id),
    price NUMERIC(18,4) NOT NULL,
    currency TEXT NOT NULL DEFAULT 'RUB',
    vat_rate NUMERIC(7,4),
    vat_included BOOLEAN,
    uom_code TEXT,
    valid_from DATE NOT NULL,
    valid_to DATE,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    comment TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_item_prices_lookup ON item_prices(item_id, price_group_id, valid_from, valid_to);
