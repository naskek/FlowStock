CREATE TABLE marking_request_export_batch (
    id uuid PRIMARY KEY,
    order_id bigint NOT NULL REFERENCES orders(id),
    expected_snapshot_hash text NOT NULL,
    post_export_snapshot_hash text NOT NULL,
    reserve_quantity integer NOT NULL CHECK (reserve_quantity >= 0),
    created_by text NOT NULL,
    created_at timestamptz NOT NULL,
    CONSTRAINT uq_marking_request_export_batch_order_snapshot
        UNIQUE (order_id, expected_snapshot_hash)
);

CREATE TABLE marking_request_export_batch_request (
    export_batch_id uuid NOT NULL REFERENCES marking_request_export_batch(id),
    marking_order_id uuid NOT NULL REFERENCES marking_order(id),
    item_id bigint NOT NULL REFERENCES items(id),
    item_name_snapshot text NOT NULL,
    gtin_snapshot text NOT NULL,
    required_quantity_snapshot integer NOT NULL CHECK (required_quantity_snapshot > 0),
    reserve_quantity_snapshot integer NOT NULL CHECK (reserve_quantity_snapshot >= 0),
    requested_quantity_snapshot integer NOT NULL CHECK (
        requested_quantity_snapshot = required_quantity_snapshot + reserve_quantity_snapshot),
    PRIMARY KEY (export_batch_id, marking_order_id),
    CONSTRAINT uq_marking_request_export_batch_request UNIQUE (marking_order_id)
);

CREATE OR REPLACE FUNCTION protect_marking_request_export_batch_immutable()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'MARKING_REQUEST_EXPORT_BATCH_IMMUTABLE';
END;
$$;

CREATE TRIGGER trg_marking_request_export_batch_immutable
BEFORE UPDATE OR DELETE ON marking_request_export_batch
FOR EACH ROW EXECUTE FUNCTION protect_marking_request_export_batch_immutable();

CREATE TRIGGER trg_marking_request_export_batch_request_immutable
BEFORE UPDATE OR DELETE ON marking_request_export_batch_request
FOR EACH ROW EXECUTE FUNCTION protect_marking_request_export_batch_immutable();
