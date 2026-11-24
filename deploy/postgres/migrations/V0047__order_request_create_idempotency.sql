-- Existing requests remain unchanged. PostgreSQL allows multiple NULL values
-- under the unique constraint, keeping legacy clients compatible.
ALTER TABLE order_requests
    ADD COLUMN IF NOT EXISTS client_request_key UUID NULL,
    ADD COLUMN IF NOT EXISTS client_payload_hash TEXT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_order_requests_client_request_key
    ON order_requests(client_request_key);

ALTER TABLE order_requests
    ADD CONSTRAINT ck_order_requests_client_request_metadata
    CHECK ((client_request_key IS NULL) = (client_payload_hash IS NULL));
