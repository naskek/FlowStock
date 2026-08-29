ALTER TABLE production_pallets
    ADD COLUMN printed_label_fingerprint TEXT NULL,
    ADD COLUMN printed_label_fingerprint_version SMALLINT NULL;

ALTER TABLE production_pallets
    ADD CONSTRAINT ck_production_pallets_label_fingerprint_pair
        CHECK ((printed_label_fingerprint IS NULL) = (printed_label_fingerprint_version IS NULL)),
    ADD CONSTRAINT ck_production_pallets_label_fingerprint_format
        CHECK (
            printed_label_fingerprint IS NULL
            OR (
                printed_label_fingerprint_version = 1
                AND printed_label_fingerprint ~ '^[0-9a-f]{64}$'
                AND printed_at IS NOT NULL
            )
        );
