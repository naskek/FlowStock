INSERT INTO client_blocks(block_key, is_enabled, updated_at)
VALUES ('pc_hu_correction', FALSE, NOW()::text)
ON CONFLICT (block_key) DO NOTHING;

DO $$
DECLARE
    unknown_statuses TEXT;
BEGIN
    SELECT string_agg(status, ', ' ORDER BY status)
    INTO unknown_statuses
    FROM (
        SELECT DISTINCT COALESCE(NULLIF(BTRIM(status), ''), '<EMPTY>') AS status
        FROM production_pallets
        WHERE COALESCE(NULLIF(BTRIM(status), ''), '<EMPTY>')
              NOT IN ('PLANNED', 'PRINTED', 'FILLED', 'CANCELLED', 'CORRECTED')
    ) unexpected;

    IF unknown_statuses IS NOT NULL THEN
        RAISE EXCEPTION
            'V0030 preflight: production_pallets contains unknown statuses: %',
            unknown_statuses;
    END IF;
END
$$;

ALTER TABLE production_pallets
    DROP CONSTRAINT IF EXISTS ck_production_pallets_status;

ALTER TABLE production_pallets
    ADD CONSTRAINT ck_production_pallets_status
    CHECK (status IN ('PLANNED', 'PRINTED', 'FILLED', 'CANCELLED', 'CORRECTED'));

DROP INDEX IF EXISTS ux_production_pallets_active_hu;
CREATE UNIQUE INDEX ux_production_pallets_active_hu
    ON production_pallets(upper(btrim(hu_code)))
    WHERE status IN ('PLANNED', 'PRINTED', 'FILLED');

DROP INDEX IF EXISTS ux_production_pallets_prd_hu;
CREATE UNIQUE INDEX ux_production_pallets_prd_hu
    ON production_pallets(prd_doc_id, upper(btrim(hu_code)))
    WHERE status IN ('PLANNED', 'PRINTED', 'FILLED');

CREATE TABLE production_pallet_filling_adjustments (
    id BIGSERIAL PRIMARY KEY,
    action_type TEXT NOT NULL CHECK (action_type IN ('CORRECT_FILLED', 'RESET_PARTIAL')),
    request_id UUID NOT NULL,
    payload_hash TEXT NOT NULL,
    source_pallet_id BIGINT NULL REFERENCES production_pallets(id),
    root_pallet_id BIGINT NULL REFERENCES production_pallets(id),
    source_prd_doc_id BIGINT NULL REFERENCES docs(id),
    cor_doc_id BIGINT NULL REFERENCES docs(id),
    replacement_pallet_id BIGINT NULL REFERENCES production_pallets(id),
    replacement_prd_doc_id BIGINT NULL REFERENCES docs(id),
    predecessor_adjustment_id BIGINT NULL REFERENCES production_pallet_filling_adjustments(id),
    reason_code TEXT NOT NULL,
    reason_text TEXT NOT NULL,
    actor_name TEXT NULL,
    device_name TEXT NULL,
    client_name TEXT NULL,
    client_version TEXT NULL,
    created_at TEXT NOT NULL,
    result_json TEXT NULL,
    CONSTRAINT ux_production_pallet_filling_adjustments_request UNIQUE(request_id),
    CONSTRAINT ck_production_pallet_filling_adjustments_reason
        CHECK (
            (action_type = 'CORRECT_FILLED' AND reason_code = 'ERRONEOUS_HU_FILL')
            OR
            (action_type = 'RESET_PARTIAL' AND reason_code = 'ERRONEOUS_PARTIAL_FILL')
        ),
    CONSTRAINT ck_production_pallet_filling_adjustments_completed_shape
        CHECK (
            result_json IS NULL
            OR (
                action_type = 'CORRECT_FILLED'
                AND source_pallet_id IS NOT NULL
                AND root_pallet_id IS NOT NULL
                AND source_prd_doc_id IS NOT NULL
                AND cor_doc_id IS NOT NULL
                AND replacement_pallet_id IS NOT NULL
                AND replacement_prd_doc_id IS NOT NULL
            )
            OR (
                action_type = 'RESET_PARTIAL'
                AND source_pallet_id IS NOT NULL
                AND root_pallet_id IS NOT NULL
                AND source_prd_doc_id IS NOT NULL
                AND cor_doc_id IS NULL
                AND replacement_pallet_id IS NULL
                AND replacement_prd_doc_id IS NULL
            )
        )
);

CREATE UNIQUE INDEX ux_production_pallet_filling_adjustments_source_correct
    ON production_pallet_filling_adjustments(source_pallet_id)
    WHERE action_type = 'CORRECT_FILLED' AND result_json IS NOT NULL;

CREATE UNIQUE INDEX ux_production_pallet_filling_adjustments_cor_doc
    ON production_pallet_filling_adjustments(cor_doc_id)
    WHERE cor_doc_id IS NOT NULL;

CREATE UNIQUE INDEX ux_production_pallet_filling_adjustments_replacement_pallet
    ON production_pallet_filling_adjustments(replacement_pallet_id)
    WHERE replacement_pallet_id IS NOT NULL;

CREATE INDEX ix_production_pallet_filling_adjustments_source
    ON production_pallet_filling_adjustments(source_pallet_id, id);
CREATE INDEX ix_production_pallet_filling_adjustments_replacement
    ON production_pallet_filling_adjustments(replacement_pallet_id, id);

CREATE TABLE production_pallet_filling_adjustment_lines (
    id BIGSERIAL PRIMARY KEY,
    adjustment_id BIGINT NOT NULL
        REFERENCES production_pallet_filling_adjustments(id) ON DELETE CASCADE,
    line_kind TEXT NOT NULL
        CHECK (line_kind IN ('LEDGER_INVERSION', 'PALLET_COMPONENT', 'RESET_COMPONENT')),
    source_ledger_entry_id BIGINT NULL REFERENCES ledger(id),
    source_doc_line_id BIGINT NULL REFERENCES doc_lines(id),
    source_component_id BIGINT NULL REFERENCES production_pallet_lines(id),
    cor_doc_line_id BIGINT NULL REFERENCES doc_lines(id),
    generated_cor_ledger_entry_id BIGINT NULL REFERENCES ledger(id),
    replacement_doc_line_id BIGINT NULL REFERENCES doc_lines(id),
    replacement_component_id BIGINT NULL REFERENCES production_pallet_lines(id),
    item_id BIGINT NULL REFERENCES items(id),
    location_id BIGINT NULL REFERENCES locations(id),
    order_line_id BIGINT NULL REFERENCES order_lines(id),
    hu_code TEXT NULL,
    source_qty DOUBLE PRECISION NULL,
    correction_qty DOUBLE PRECISION NULL,
    planned_qty DOUBLE PRECISION NULL,
    old_filled_qty DOUBLE PRECISION NULL,
    old_filled_at TEXT NULL,
    detail_json TEXT NULL,
    CONSTRAINT ck_production_pallet_filling_adjustment_lines_shape
        CHECK (
            (
                line_kind = 'LEDGER_INVERSION'
                AND source_ledger_entry_id IS NOT NULL
                AND source_doc_line_id IS NOT NULL
                AND cor_doc_line_id IS NOT NULL
                AND generated_cor_ledger_entry_id IS NOT NULL
            )
            OR (
                line_kind = 'PALLET_COMPONENT'
                AND source_component_id IS NOT NULL
                AND replacement_doc_line_id IS NOT NULL
                AND replacement_component_id IS NOT NULL
            )
            OR (
                line_kind = 'RESET_COMPONENT'
                AND source_component_id IS NOT NULL
                AND old_filled_qty IS NOT NULL
                AND source_ledger_entry_id IS NULL
                AND cor_doc_line_id IS NULL
                AND generated_cor_ledger_entry_id IS NULL
                AND replacement_doc_line_id IS NULL
                AND replacement_component_id IS NULL
            )
        )
);

CREATE INDEX ix_production_pallet_filling_adjustment_lines_adjustment
    ON production_pallet_filling_adjustment_lines(adjustment_id, id);

CREATE TABLE production_marking_transition_audit (
    id BIGSERIAL PRIMARY KEY,
    adjustment_id BIGINT NOT NULL
        REFERENCES production_pallet_filling_adjustments(id),
    marking_code_id UUID NOT NULL REFERENCES marking_code(id),
    marking_order_id UUID NOT NULL REFERENCES marking_order(id),
    import_id UUID NOT NULL REFERENCES marking_code_import(id),
    origin TEXT NOT NULL,
    source_prd_doc_id BIGINT NOT NULL REFERENCES docs(id),
    cor_doc_id BIGINT NULL REFERENCES docs(id),
    old_receipt_doc_id BIGINT NULL REFERENCES docs(id),
    old_receipt_line_id BIGINT NULL REFERENCES doc_lines(id),
    old_applied_at TEXT NULL,
    old_status TEXT NOT NULL,
    new_status TEXT NOT NULL,
    reason_text TEXT NOT NULL,
    actor_name TEXT NULL,
    device_name TEXT NULL,
    changed_at TEXT NOT NULL
);

CREATE INDEX ix_production_marking_transition_audit_code
    ON production_marking_transition_audit(marking_code_id, id);
