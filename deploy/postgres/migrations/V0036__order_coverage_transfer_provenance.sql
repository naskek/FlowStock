BEGIN;

-- Durable business provenance for INTERNAL -> CUSTOMER production coverage adoption.
-- Entity ids are intentionally stored as historical snapshots without foreign keys:
-- source/target order lines, PRD documents and production pallets can be physically removed
-- by existing cleanup workflows, while transfer provenance must survive them.
CREATE TABLE order_coverage_transfers (
    id BIGSERIAL PRIMARY KEY,
    transfer_type TEXT NOT NULL,
    source_order_id BIGINT NOT NULL,
    source_order_ref TEXT NOT NULL,
    source_order_status TEXT NOT NULL,
    target_order_id BIGINT NOT NULL,
    target_order_ref TEXT NOT NULL,
    source_prd_doc_id BIGINT NOT NULL,
    source_prd_doc_ref TEXT NOT NULL,
    target_prd_doc_id BIGINT NOT NULL,
    target_prd_doc_ref TEXT NOT NULL,
    production_pallet_id BIGINT NOT NULL,
    hu_code TEXT NOT NULL,
    pallet_status_at_transfer TEXT NOT NULL,
    printed_at_at_transfer TEXT NULL,
    transferred_qty DOUBLE PRECISION NOT NULL,
    created_at TEXT NOT NULL,
    compensation_kind TEXT NULL,
    compensated_at TEXT NULL,
    CONSTRAINT ck_order_coverage_transfers_type
        CHECK (transfer_type IN ('PLANNED_PALLET_ADOPTION')),
    CONSTRAINT ck_order_coverage_transfers_ids
        CHECK (
            source_order_id > 0
            AND target_order_id > 0
            AND source_prd_doc_id > 0
            AND target_prd_doc_id > 0
            AND production_pallet_id > 0
        ),
    CONSTRAINT ck_order_coverage_transfers_text
        CHECK (
            BTRIM(source_order_ref) <> ''
            AND BTRIM(target_order_ref) <> ''
            AND BTRIM(source_prd_doc_ref) <> ''
            AND BTRIM(target_prd_doc_ref) <> ''
            AND BTRIM(hu_code) <> ''
            AND BTRIM(pallet_status_at_transfer) <> ''
        ),
    CONSTRAINT ck_order_coverage_transfers_qty
        CHECK (transferred_qty > 0)
);

CREATE TABLE order_coverage_transfer_lines (
    id BIGSERIAL PRIMARY KEY,
    transfer_id BIGINT NOT NULL
        REFERENCES order_coverage_transfers(id) ON DELETE CASCADE,
    source_order_line_id BIGINT NOT NULL,
    target_order_line_id BIGINT NOT NULL,
    source_doc_line_id BIGINT NOT NULL,
    source_production_pallet_line_id BIGINT NULL,
    item_id BIGINT NOT NULL,
    source_qty_ordered_before DOUBLE PRECISION NOT NULL,
    transferred_qty DOUBLE PRECISION NOT NULL,
    source_production_purpose TEXT NOT NULL,
    source_production_pallet_group TEXT NULL,
    CONSTRAINT ck_order_coverage_transfer_lines_ids
        CHECK (
            source_order_line_id > 0
            AND target_order_line_id > 0
            AND source_doc_line_id > 0
            AND item_id > 0
            AND (source_production_pallet_line_id IS NULL OR source_production_pallet_line_id > 0)
        ),
    CONSTRAINT ck_order_coverage_transfer_lines_qty
        CHECK (
            source_qty_ordered_before >= 0
            AND transferred_qty > 0
        ),
    CONSTRAINT ck_order_coverage_transfer_lines_purpose
        CHECK (BTRIM(source_production_purpose) <> '')
);

CREATE INDEX ix_order_coverage_transfers_source_order
    ON order_coverage_transfers(source_order_id, id);

CREATE INDEX ix_order_coverage_transfers_target_order
    ON order_coverage_transfers(target_order_id, id);

CREATE INDEX ix_order_coverage_transfers_pallet
    ON order_coverage_transfers(production_pallet_id, id);

CREATE INDEX ix_order_coverage_transfer_lines_transfer
    ON order_coverage_transfer_lines(transfer_id, id);

CREATE INDEX ix_order_coverage_transfer_lines_source_line
    ON order_coverage_transfer_lines(source_order_line_id, transfer_id);

CREATE INDEX ix_order_coverage_transfer_lines_target_line
    ON order_coverage_transfer_lines(target_order_line_id, transfer_id);

COMMIT;
