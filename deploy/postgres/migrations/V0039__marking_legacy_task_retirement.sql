CREATE TABLE marking_legacy_task_retirement_audit (
    id BIGSERIAL PRIMARY KEY,
    marking_order_id UUID NOT NULL UNIQUE,
    idempotency_key TEXT NOT NULL UNIQUE,
    order_id BIGINT NOT NULL REFERENCES orders(id) ON DELETE RESTRICT,
    order_line_id BIGINT NOT NULL REFERENCES order_lines(id) ON DELETE RESTRICT,
    order_line_revision BIGINT NOT NULL,
    item_id BIGINT NOT NULL REFERENCES items(id) ON DELETE RESTRICT,
    gtin_snapshot TEXT NOT NULL,
    target_quantity NUMERIC(18,6) NOT NULL CHECK (target_quantity > 0),
    task_requested_quantity INTEGER NOT NULL CHECK (task_requested_quantity > 0),
    candidate_code_quantity INTEGER NOT NULL CHECK (candidate_code_quantity > 0),
    candidate_reserved_legacy_quantity INTEGER NOT NULL CHECK (candidate_reserved_legacy_quantity > 0),
    remaining_task_count INTEGER NOT NULL CHECK (remaining_task_count > 0),
    remaining_applied_legacy_quantity INTEGER NOT NULL CHECK (remaining_applied_legacy_quantity >= 0),
    remaining_reserved_legacy_quantity INTEGER NOT NULL CHECK (remaining_reserved_legacy_quantity >= 0),
    remaining_voided_legacy_quantity INTEGER NOT NULL CHECK (remaining_voided_legacy_quantity >= 0),
    marking_code_import_id UUID NOT NULL,
    import_provenance_summary TEXT NOT NULL,
    reason TEXT NOT NULL CHECK (reason = 'REDUNDANT_RESERVED_ONLY_LEGACY_TASK'),
    status_before TEXT NOT NULL CHECK (status_before = 'Printed'),
    status_after TEXT NOT NULL CHECK (status_after = 'Cancelled'),
    preflight_hash_before TEXT NOT NULL,
    preflight_hash_after TEXT NOT NULL,
    eligibility_hash TEXT NOT NULL,
    actor TEXT NOT NULL,
    retired_at TEXT NOT NULL
);

CREATE INDEX ix_marking_legacy_task_retirement_line
    ON marking_legacy_task_retirement_audit(order_line_id, retired_at);

CREATE OR REPLACE FUNCTION prevent_marking_legacy_task_retirement_audit_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'marking_legacy_task_retirement_audit is immutable';
END;
$$;

CREATE TRIGGER trg_marking_legacy_task_retirement_audit_immutable
BEFORE UPDATE OR DELETE ON marking_legacy_task_retirement_audit
FOR EACH ROW EXECUTE FUNCTION prevent_marking_legacy_task_retirement_audit_mutation();

CREATE OR REPLACE FUNCTION prevent_retired_marking_order_reopen()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status IS DISTINCT FROM OLD.status
       AND NEW.status NOT IN ('Cancelled', 'Failed')
       AND EXISTS (
           SELECT 1
           FROM marking_legacy_task_retirement_audit audit
           WHERE audit.marking_order_id = OLD.id
       ) THEN
        RAISE EXCEPTION 'MARKING_LEGACY_TASK_RETIRED';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_marking_order_prevent_retired_reopen
BEFORE UPDATE OF status ON marking_order
FOR EACH ROW EXECUTE FUNCTION prevent_retired_marking_order_reopen();

CREATE OR REPLACE FUNCTION prevent_retired_marking_code_operational_reuse()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    retired_task_id UUID;
    previous_task_id UUID;
BEGIN
    retired_task_id := NEW.marking_order_id;
    previous_task_id := CASE WHEN TG_OP = 'UPDATE' THEN OLD.marking_order_id ELSE NULL END;
    IF EXISTS (
        SELECT 1 FROM marking_legacy_task_retirement_audit audit
        WHERE audit.marking_order_id IN (retired_task_id, previous_task_id)
    ) AND (
        TG_OP = 'INSERT'
        OR NEW.marking_order_id IS DISTINCT FROM OLD.marking_order_id
        OR NEW.import_id IS DISTINCT FROM OLD.import_id
        OR NEW.status IS DISTINCT FROM 'Reserved'
        OR NEW.printed_at IS NOT NULL
        OR NEW.applied_at IS NOT NULL
        OR NEW.reported_at IS NOT NULL
        OR NEW.introduced_at IS NOT NULL
        OR NEW.receipt_doc_id IS NOT NULL
        OR NEW.receipt_line_id IS NOT NULL
    ) THEN
        RAISE EXCEPTION 'MARKING_LEGACY_TASK_RETIRED';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_marking_code_prevent_retired_reuse
BEFORE INSERT OR UPDATE ON marking_code
FOR EACH ROW EXECUTE FUNCTION prevent_retired_marking_code_operational_reuse();

CREATE OR REPLACE FUNCTION prevent_retired_marking_order_reference()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    candidate_task_id UUID;
    previous_task_id UUID;
    task_column TEXT;
BEGIN
    task_column := CASE TG_TABLE_NAME
        WHEN 'marking_code_import' THEN 'matched_marking_order_id'
        ELSE 'marking_order_id'
    END;
    candidate_task_id := NULLIF(to_jsonb(NEW) ->> task_column, '')::uuid;
    previous_task_id := CASE WHEN TG_OP = 'UPDATE'
        THEN NULLIF(to_jsonb(OLD) ->> task_column, '')::uuid
        ELSE NULL
    END;

    IF (candidate_task_id IS NOT NULL OR previous_task_id IS NOT NULL)
       AND EXISTS (
           SELECT 1 FROM marking_legacy_task_retirement_audit audit
           WHERE audit.marking_order_id IN (candidate_task_id, previous_task_id)
       ) THEN
        IF TG_OP = 'INSERT'
           OR candidate_task_id IS DISTINCT FROM previous_task_id THEN
            RAISE EXCEPTION 'MARKING_LEGACY_TASK_RETIRED';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_marking_code_import_prevent_retired_reference
BEFORE INSERT OR UPDATE OF matched_marking_order_id ON marking_code_import
FOR EACH ROW EXECUTE FUNCTION prevent_retired_marking_order_reference();
CREATE TRIGGER trg_marking_request_scope_prevent_retired_reference
BEFORE INSERT ON marking_request_scope
FOR EACH ROW EXECUTE FUNCTION prevent_retired_marking_order_reference();
CREATE TRIGGER trg_marking_import_batch_prevent_retired_reference
BEFORE INSERT OR UPDATE OF marking_order_id ON marking_import_batch
FOR EACH ROW EXECUTE FUNCTION prevent_retired_marking_order_reference();
CREATE TRIGGER trg_marking_import_batch_request_prevent_retired_reference
BEFORE INSERT ON marking_import_batch_request
FOR EACH ROW EXECUTE FUNCTION prevent_retired_marking_order_reference();
CREATE TRIGGER trg_marking_print_batch_prevent_retired_reference
BEFORE INSERT OR UPDATE OF marking_order_id ON marking_print_batch
FOR EACH ROW EXECUTE FUNCTION prevent_retired_marking_order_reference();
CREATE TRIGGER trg_production_marking_transition_prevent_retired_reference
BEFORE INSERT ON production_marking_transition_audit
FOR EACH ROW EXECUTE FUNCTION prevent_retired_marking_order_reference();

CREATE OR REPLACE FUNCTION prevent_retired_marking_code_print_link()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM marking_code code_row
        INNER JOIN marking_legacy_task_retirement_audit audit
            ON audit.marking_order_id = code_row.marking_order_id
        WHERE code_row.id = NEW.marking_code_id
    ) THEN
        RAISE EXCEPTION 'MARKING_LEGACY_TASK_RETIRED';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_marking_print_batch_code_prevent_retired_link
BEFORE INSERT OR UPDATE OF marking_code_id ON marking_print_batch_code
FOR EACH ROW EXECUTE FUNCTION prevent_retired_marking_code_print_link();

CREATE OR REPLACE FUNCTION prevent_retired_scope_coverage()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.marking_request_scope_id IS NOT NULL
       AND EXISTS (
           SELECT 1
           FROM marking_request_scope scope
           INNER JOIN marking_legacy_task_retirement_audit audit
               ON audit.marking_order_id = scope.marking_order_id
           WHERE scope.id = NEW.marking_request_scope_id
       ) THEN
        RAISE EXCEPTION 'MARKING_LEGACY_TASK_RETIRED';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_marking_operational_coverage_prevent_retired_scope
BEFORE INSERT OR UPDATE OF marking_request_scope_id ON marking_operational_coverage
FOR EACH ROW EXECUTE FUNCTION prevent_retired_scope_coverage();
