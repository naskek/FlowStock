CREATE TABLE vat_rates (
    id BIGSERIAL PRIMARY KEY,
    name TEXT NOT NULL,
    rate NUMERIC(7,4) NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    sort_order INTEGER NOT NULL DEFAULT 0,
    CONSTRAINT ux_vat_rates_name UNIQUE (name),
    CONSTRAINT ux_vat_rates_rate UNIQUE (rate),
    CONSTRAINT ck_vat_rates_rate_non_negative CHECK (rate >= 0)
);

ALTER TABLE items
    ADD COLUMN default_sale_price_gross NUMERIC(19,4) NULL,
    ADD COLUMN default_sale_vat_rate_id BIGINT NULL,
    ADD CONSTRAINT ck_items_default_sale_price_gross_non_negative
        CHECK (default_sale_price_gross IS NULL OR default_sale_price_gross >= 0),
    ADD CONSTRAINT fk_items_default_sale_vat_rate
        FOREIGN KEY (default_sale_vat_rate_id) REFERENCES vat_rates(id) ON DELETE RESTRICT;

CREATE TABLE partner_item_sale_prices (
    id BIGSERIAL PRIMARY KEY,
    partner_id BIGINT NOT NULL,
    item_id BIGINT NOT NULL,
    unit_price_gross NUMERIC(19,4) NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    CONSTRAINT fk_partner_item_sale_prices_partner
        FOREIGN KEY (partner_id) REFERENCES partners(id) ON DELETE RESTRICT,
    CONSTRAINT fk_partner_item_sale_prices_item
        FOREIGN KEY (item_id) REFERENCES items(id) ON DELETE RESTRICT,
    CONSTRAINT ux_partner_item_sale_prices_partner_item
        UNIQUE (partner_id, item_id),
    CONSTRAINT ck_partner_item_sale_prices_price_non_negative
        CHECK (unit_price_gross >= 0)
);

ALTER TABLE order_lines
    ADD COLUMN unit_price_gross NUMERIC(19,4) NULL,
    ADD COLUMN vat_rate NUMERIC(7,4) NULL,
    ADD CONSTRAINT ck_order_lines_unit_price_gross_non_negative
        CHECK (unit_price_gross IS NULL OR unit_price_gross >= 0),
    ADD CONSTRAINT ck_order_lines_vat_rate_non_negative
        CHECK (vat_rate IS NULL OR vat_rate >= 0);

CREATE INDEX ix_partner_item_sale_prices_item_partner
    ON partner_item_sale_prices(item_id, partner_id);

CREATE INDEX ix_orders_customer_commercial_statistics
    ON orders(created_at, id)
    WHERE order_type = 'CUSTOMER'
      AND status NOT IN ('CANCELLED', 'MERGED');

CREATE INDEX ix_order_lines_customer_commercial_statistics
    ON order_lines(order_id)
    INCLUDE (item_id, qty_ordered, unit_price_gross, vat_rate)
    WHERE cancelled_at IS NULL;

CREATE INDEX ix_docs_customer_sales_statistics
    ON docs(closed_at, id, order_id)
    WHERE type = 'OUTBOUND'
      AND status = 'CLOSED'
      AND closed_at IS NOT NULL;

CREATE INDEX ix_doc_lines_sales_statistics
    ON doc_lines(doc_id, id)
    INCLUDE (order_line_id, item_id, qty)
    WHERE qty > 0::double precision;
