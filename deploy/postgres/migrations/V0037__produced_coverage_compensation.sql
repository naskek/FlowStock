BEGIN;

-- Persist the current INTERNAL order_line identity used by RETURN_PRODUCED compensation.
-- Historical source_order_line_id remains immutable provenance; the compensated id may point
-- to a recreated semantic-equivalent INTERNAL line and intentionally has no FK.
ALTER TABLE order_coverage_transfer_lines
    ADD COLUMN compensated_source_order_line_id BIGINT NULL;

ALTER TABLE order_coverage_transfer_lines
    ADD CONSTRAINT ck_order_coverage_transfer_lines_compensated_source
        CHECK (
            compensated_source_order_line_id IS NULL
            OR compensated_source_order_line_id > 0
        );

CREATE INDEX ix_order_coverage_transfer_lines_compensated_source
    ON order_coverage_transfer_lines(compensated_source_order_line_id, transfer_id)
    WHERE compensated_source_order_line_id IS NOT NULL;

COMMIT;
