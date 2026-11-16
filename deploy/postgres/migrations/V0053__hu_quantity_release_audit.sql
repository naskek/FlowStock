-- Additive audit only; does not activate the quantity correction consumer.
CREATE TABLE production_marking_quantity_release_audit (
    -- Event identities are validated below. Their own immutable triggers retain
    -- them; event FKs would preempt the journal's canonical TRUNCATE rejection.
    assignment_event_id BIGINT PRIMARY KEY CHECK (assignment_event_id > 0),
    release_event_id BIGINT NOT NULL UNIQUE CHECK (release_event_id > 0),
    adjustment_id BIGINT NOT NULL REFERENCES production_pallet_filling_adjustments(id) ON DELETE RESTRICT,
    cor_doc_id BIGINT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE FUNCTION marking_quantity_release_audit_validate() RETURNS TRIGGER
LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM marking_synthetic_quantity_event a
        JOIN marking_synthetic_quantity_event r
          ON r.id = NEW.release_event_id AND r.action = 'RELEASE'
         AND r.pool_id = a.pool_id AND r.quantity = a.quantity
         AND r.receipt_doc_id = a.receipt_doc_id AND r.receipt_line_id = a.receipt_line_id
        JOIN production_pallet_filling_adjustments j ON j.id = NEW.adjustment_id
        JOIN docs d ON d.id = NEW.cor_doc_id
        WHERE a.id = NEW.assignment_event_id AND a.action = 'ASSIGN'
          AND j.action_type = 'CORRECT_FILLED' AND j.result_json IS NULL
          AND d.type = 'INVENTORY_CORRECTION' AND d.status = 'CLOSED'
          AND d.reason_code = 'ERRONEOUS_HU_FILL'
          AND r.operation_id = MD5('quantity-cor:v1:' || j.request_id || ':' || a.id)::UUID
    ) THEN
        RAISE EXCEPTION 'QUANTITY_COR_INVALID_AUDIT' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER trg_marking_quantity_release_audit_validate
    BEFORE INSERT ON production_marking_quantity_release_audit
    FOR EACH ROW EXECUTE FUNCTION marking_quantity_release_audit_validate();
CREATE TRIGGER trg_marking_quantity_release_audit_immutable
    BEFORE UPDATE OR DELETE ON production_marking_quantity_release_audit
    FOR EACH ROW EXECUTE FUNCTION marking_synthetic_quantity_immutable();
CREATE TRIGGER trg_marking_quantity_release_audit_no_truncate
    BEFORE TRUNCATE ON production_marking_quantity_release_audit
    FOR EACH STATEMENT EXECUTE FUNCTION marking_synthetic_quantity_immutable();
