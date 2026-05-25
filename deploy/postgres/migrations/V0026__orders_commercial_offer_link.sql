ALTER TABLE orders ADD COLUMN IF NOT EXISTS commercial_offer_id BIGINT REFERENCES commercial_offers(id);
CREATE INDEX IF NOT EXISTS ix_orders_commercial_offer ON orders(commercial_offer_id);
