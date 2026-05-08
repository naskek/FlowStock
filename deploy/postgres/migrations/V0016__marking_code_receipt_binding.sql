BEGIN;

ALTER TABLE IF EXISTS marking_code
    ADD COLUMN IF NOT EXISTS receipt_doc_id BIGINT NULL;

ALTER TABLE IF EXISTS marking_code
    ADD COLUMN IF NOT EXISTS receipt_line_id BIGINT NULL;

CREATE INDEX IF NOT EXISTS ix_marking_code_receipt_line
    ON marking_code(receipt_line_id)
    WHERE receipt_line_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_marking_code_free_receipt
    ON marking_code(marking_order_id, status, gtin)
    WHERE receipt_line_id IS NULL;

COMMIT;
