ALTER TABLE marking_order
    ADD COLUMN IF NOT EXISTS required_quantity INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS reserve_quantity INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS original_order_id BIGINT NULL REFERENCES orders(id) ON DELETE RESTRICT,
    ADD COLUMN IF NOT EXISTS original_order_line_id BIGINT NULL REFERENCES order_lines(id) ON DELETE RESTRICT;

UPDATE marking_order
SET required_quantity = requested_quantity
WHERE required_quantity = 0 AND requested_quantity > 0;

ALTER TABLE marking_order
    DROP CONSTRAINT IF EXISTS ck_marking_order_quantities;
ALTER TABLE marking_order
    ADD CONSTRAINT ck_marking_order_quantities CHECK (
        required_quantity >= 0
        AND reserve_quantity >= 0
        AND requested_quantity >= 0
        AND (
            requested_quantity = required_quantity + reserve_quantity
            OR (required_quantity = 0 AND reserve_quantity = 0)
        )
    );

CREATE OR REPLACE FUNCTION prevent_marking_order_request_provenance_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.required_quantity IS DISTINCT FROM OLD.required_quantity
       OR NEW.reserve_quantity IS DISTINCT FROM OLD.reserve_quantity
       OR NEW.requested_quantity IS DISTINCT FROM OLD.requested_quantity
       OR NEW.original_order_id IS DISTINCT FROM OLD.original_order_id
       OR NEW.original_order_line_id IS DISTINCT FROM OLD.original_order_line_id
       OR NEW.item_id IS DISTINCT FROM OLD.item_id
       OR NEW.gtin IS DISTINCT FROM OLD.gtin
       OR NEW.request_number IS DISTINCT FROM OLD.request_number THEN
        RAISE EXCEPTION 'marking_order request provenance is immutable';
    END IF;
    RETURN NEW;
END;
$$;
DROP TRIGGER IF EXISTS trg_marking_order_request_provenance_immutable ON marking_order;
CREATE TRIGGER trg_marking_order_request_provenance_immutable
BEFORE UPDATE ON marking_order
FOR EACH ROW EXECUTE FUNCTION prevent_marking_order_request_provenance_mutation();

CREATE TABLE IF NOT EXISTS marking_settings (
    id BOOLEAN PRIMARY KEY DEFAULT TRUE CHECK (id),
    default_reserve_quantity INTEGER NOT NULL DEFAULT 5 CHECK (default_reserve_quantity >= 0),
    updated_at TEXT NOT NULL,
    updated_by TEXT
);
INSERT INTO marking_settings(id, default_reserve_quantity, updated_at)
VALUES(TRUE, 5, to_char(NOW() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"'))
ON CONFLICT (id) DO NOTHING;

CREATE TABLE IF NOT EXISTS marking_production_subject (
    id UUID PRIMARY KEY,
    lifecycle TEXT NOT NULL DEFAULT 'ACTIVE',
    revision BIGINT NOT NULL DEFAULT 0,
    predecessor_subject_id UUID NULL REFERENCES marking_production_subject(id) ON DELETE RESTRICT,
    original_production_pallet_id BIGINT NULL,
    original_component_id BIGINT NULL,
    original_doc_line_id BIGINT NULL,
    original_order_id BIGINT NULL,
    original_order_line_id BIGINT NULL,
    current_production_pallet_id BIGINT NULL REFERENCES production_pallets(id) ON DELETE SET NULL,
    current_component_id BIGINT NULL,
    current_doc_id BIGINT NULL REFERENCES docs(id) ON DELETE SET NULL,
    current_doc_line_id BIGINT NULL REFERENCES doc_lines(id) ON DELETE SET NULL,
    current_order_id BIGINT NULL REFERENCES orders(id) ON DELETE SET NULL,
    current_order_line_id BIGINT NULL REFERENCES order_lines(id) ON DELETE SET NULL,
    item_id BIGINT NOT NULL REFERENCES items(id) ON DELETE RESTRICT,
    gtin TEXT NOT NULL,
    hu_id BIGINT NULL REFERENCES hus(id) ON DELETE SET NULL,
    hu_code_snapshot TEXT,
    subject_quantity NUMERIC(18,6) NOT NULL CHECK (subject_quantity >= 0),
    created_at TEXT NOT NULL,
    completed_at TEXT,
    cancelled_at TEXT,
    superseded_at TEXT,
    CHECK (lifecycle IN ('ACTIVE', 'CANCELLED', 'COMPLETED', 'SUPERSEDED')),
    CHECK (NULLIF(BTRIM(gtin), '') IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS ix_marking_subject_current_order
    ON marking_production_subject(current_order_id, current_order_line_id, lifecycle);
CREATE INDEX IF NOT EXISTS ix_marking_subject_item_gtin
    ON marking_production_subject(item_id, gtin, lifecycle);

CREATE OR REPLACE FUNCTION guard_marking_subject_item_gtin_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    normalized_gtin TEXT := NULLIF(BTRIM(NEW.gtin), '');
BEGIN
    IF NULLIF(BTRIM(NEW.gtin), '') IS NOT DISTINCT FROM NULLIF(BTRIM(OLD.gtin), '') THEN
        RETURN NEW;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM marking_production_subject subject
        WHERE subject.item_id = OLD.id
          AND subject.lifecycle IN ('ACTIVE', 'COMPLETED')
          AND (
              EXISTS (SELECT 1 FROM marking_request_scope scope WHERE scope.marking_subject_id = subject.id)
              OR EXISTS (SELECT 1 FROM marking_operational_coverage coverage WHERE coverage.marking_subject_id = subject.id)
              OR EXISTS (SELECT 1 FROM marking_ready_hu_fact fact WHERE fact.marking_subject_id = subject.id)
          )
    ) THEN
        RAISE EXCEPTION 'MARKING_SUBJECT_GTIN_IMMUTABLE';
    END IF;

    IF normalized_gtin IS NULL AND EXISTS (
        SELECT 1 FROM marking_production_subject subject
        WHERE subject.item_id = OLD.id AND subject.lifecycle = 'ACTIVE'
    ) THEN
        RAISE EXCEPTION 'MARKING_ACTIVE_SUBJECT_GTIN_REQUIRED';
    END IF;

    UPDATE marking_production_subject
    SET gtin = normalized_gtin,
        revision = revision + 1
    WHERE item_id = OLD.id
      AND lifecycle = 'ACTIVE'
      AND NOT EXISTS (
          SELECT 1 FROM marking_request_scope scope
          WHERE scope.marking_subject_id = marking_production_subject.id
      )
      AND NOT EXISTS (
          SELECT 1 FROM marking_operational_coverage coverage
          WHERE coverage.marking_subject_id = marking_production_subject.id
      )
      AND NOT EXISTS (
          SELECT 1 FROM marking_ready_hu_fact fact
          WHERE fact.marking_subject_id = marking_production_subject.id
      );
    RETURN NEW;
END;
$$;
DROP TRIGGER IF EXISTS trg_items_marking_subject_gtin_guard ON items;
CREATE TRIGGER trg_items_marking_subject_gtin_guard
BEFORE UPDATE OF gtin ON items
FOR EACH ROW EXECUTE FUNCTION guard_marking_subject_item_gtin_mutation();

ALTER TABLE production_pallet_lines
    ADD COLUMN IF NOT EXISTS marking_subject_id UUID NULL REFERENCES marking_production_subject(id) ON DELETE RESTRICT;

INSERT INTO marking_production_subject(
    id, lifecycle, revision,
    original_production_pallet_id, original_component_id, original_doc_line_id,
    original_order_id, original_order_line_id,
    current_production_pallet_id, current_component_id, current_doc_id,
    current_doc_line_id, current_order_id, current_order_line_id,
    item_id, gtin, hu_code_snapshot, subject_quantity, created_at)
SELECT (md5('flowstock-marking-subject:' || pll.id::text))::uuid,
       CASE WHEN pp.status = 'CORRECTED' THEN 'SUPERSEDED'
            WHEN pp.status = 'CANCELLED' THEN 'CANCELLED'
            WHEN pp.status = 'FILLED' THEN 'COMPLETED'
            ELSE 'ACTIVE' END,
       0,
       pp.id, pll.id, pll.doc_line_id,
       COALESCE(ol.order_id, pp.order_id, d.order_id), pll.order_line_id,
       pp.id, pll.id, pp.prd_doc_id,
       pll.doc_line_id, COALESCE(ol.order_id, pp.order_id, d.order_id), pll.order_line_id,
       pll.item_id, BTRIM(i.gtin), pp.hu_code, pll.planned_qty, pll.created_at
FROM production_pallet_lines pll
INNER JOIN production_pallets pp ON pp.id = pll.production_pallet_id
INNER JOIN docs d ON d.id = pp.prd_doc_id
INNER JOIN items i ON i.id = pll.item_id
INNER JOIN item_types it ON it.id = i.item_type_id
LEFT JOIN order_lines ol ON ol.id = pll.order_line_id
WHERE pll.marking_subject_id IS NULL
  AND COALESCE(it.enable_marking, FALSE) = TRUE
  AND NULLIF(BTRIM(i.gtin), '') IS NOT NULL
ON CONFLICT (id) DO NOTHING;

UPDATE production_pallet_lines pll
SET marking_subject_id = (md5('flowstock-marking-subject:' || pll.id::text))::uuid
WHERE pll.marking_subject_id IS NULL
  AND EXISTS (
      SELECT 1
      FROM marking_production_subject subject
      WHERE subject.id = (md5('flowstock-marking-subject:' || pll.id::text))::uuid
  );

CREATE UNIQUE INDEX IF NOT EXISTS ux_production_pallet_lines_marking_subject
    ON production_pallet_lines(marking_subject_id)
    WHERE marking_subject_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS marking_subject_ownership_audit (
    id BIGSERIAL PRIMARY KEY,
    marking_subject_id UUID NOT NULL REFERENCES marking_production_subject(id) ON DELETE RESTRICT,
    subject_revision BIGINT NOT NULL,
    old_order_id BIGINT,
    old_order_line_id BIGINT,
    new_order_id BIGINT,
    new_order_line_id BIGINT,
    reason TEXT NOT NULL,
    changed_at TEXT NOT NULL,
    changed_by_actor TEXT,
    payload_json TEXT
);
CREATE INDEX IF NOT EXISTS ix_marking_subject_ownership_audit_subject
    ON marking_subject_ownership_audit(marking_subject_id, subject_revision);

CREATE TABLE IF NOT EXISTS marking_request_scope (
    id UUID PRIMARY KEY,
    marking_order_id UUID NOT NULL REFERENCES marking_order(id) ON DELETE RESTRICT,
    marking_subject_id UUID NOT NULL REFERENCES marking_production_subject(id) ON DELETE RESTRICT,
    subject_revision BIGINT NOT NULL,
    component_id_snapshot BIGINT,
    production_pallet_id_snapshot BIGINT,
    doc_line_id_snapshot BIGINT,
    item_id_snapshot BIGINT NOT NULL REFERENCES items(id) ON DELETE RESTRICT,
    gtin_snapshot TEXT NOT NULL,
    scoped_quantity NUMERIC(18,6) NOT NULL CHECK (scoped_quantity > 0),
    original_order_id BIGINT,
    original_order_line_id BIGINT,
    created_at TEXT NOT NULL,
    CHECK (NULLIF(BTRIM(gtin_snapshot), '') IS NOT NULL)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_marking_request_scope_request_subject_revision
    ON marking_request_scope(marking_order_id, marking_subject_id, subject_revision);
CREATE INDEX IF NOT EXISTS ix_marking_request_scope_subject
    ON marking_request_scope(marking_subject_id, created_at);

-- Immutable request scope remains acquisition provenance. This separate monotonic
-- capacity is the only quantity from that scope that can satisfy current demand.
-- It may decrease on shrink/cancel, but can never increase again.
CREATE TABLE IF NOT EXISTS marking_request_scope_consumption (
    marking_request_scope_id UUID PRIMARY KEY REFERENCES marking_request_scope(id) ON DELETE RESTRICT,
    active_quantity NUMERIC(18,6) NOT NULL CHECK (active_quantity >= 0),
    updated_at TEXT NOT NULL,
    retired_at TEXT,
    retirement_reason TEXT
);

INSERT INTO marking_request_scope_consumption(
    marking_request_scope_id, active_quantity, updated_at)
SELECT scope.id, scope.scoped_quantity, scope.created_at
FROM marking_request_scope scope
ON CONFLICT (marking_request_scope_id) DO NOTHING;

CREATE OR REPLACE FUNCTION validate_marking_request_scope_consumption()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    immutable_quantity NUMERIC(18,6);
BEGIN
    SELECT scoped_quantity INTO immutable_quantity
    FROM marking_request_scope
    WHERE id = NEW.marking_request_scope_id;
    IF immutable_quantity IS NULL OR NEW.active_quantity > immutable_quantity THEN
        RAISE EXCEPTION 'MARKING_REQUEST_SCOPE_ACTIVE_QUANTITY_INVALID';
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.active_quantity > OLD.active_quantity THEN
        RAISE EXCEPTION 'MARKING_REQUEST_SCOPE_CAPACITY_CANNOT_INCREASE';
    END IF;
    RETURN NEW;
END;
$$;
DROP TRIGGER IF EXISTS trg_marking_request_scope_consumption_validate ON marking_request_scope_consumption;
CREATE TRIGGER trg_marking_request_scope_consumption_validate
BEFORE INSERT OR UPDATE ON marking_request_scope_consumption
FOR EACH ROW EXECUTE FUNCTION validate_marking_request_scope_consumption();

CREATE OR REPLACE FUNCTION create_marking_request_scope_consumption()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO marking_request_scope_consumption(
        marking_request_scope_id, active_quantity, updated_at)
    VALUES(NEW.id, NEW.scoped_quantity, NEW.created_at)
    ON CONFLICT (marking_request_scope_id) DO NOTHING;
    RETURN NEW;
END;
$$;
DROP TRIGGER IF EXISTS trg_marking_request_scope_consumption_create ON marking_request_scope;
CREATE TRIGGER trg_marking_request_scope_consumption_create
AFTER INSERT ON marking_request_scope
FOR EACH ROW EXECUTE FUNCTION create_marking_request_scope_consumption();

CREATE OR REPLACE FUNCTION prevent_marking_request_scope_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'marking_request_scope is immutable';
END;
$$;
DROP TRIGGER IF EXISTS trg_marking_request_scope_immutable ON marking_request_scope;
CREATE TRIGGER trg_marking_request_scope_immutable
BEFORE UPDATE OR DELETE ON marking_request_scope
FOR EACH ROW EXECUTE FUNCTION prevent_marking_request_scope_mutation();

-- V0027 kept these legacy ownership columns mandatory. A shared request can span
-- several adopted subjects/order lines, so they remain snapshots, not authority.
ALTER TABLE marking_import_batch ALTER COLUMN order_id DROP NOT NULL;
ALTER TABLE marking_import_batch ALTER COLUMN order_line_id DROP NOT NULL;

CREATE TABLE IF NOT EXISTS marking_import_file (
    id UUID PRIMARY KEY,
    import_batch_id UUID NOT NULL REFERENCES marking_import_batch(id) ON DELETE RESTRICT,
    original_filename TEXT NOT NULL,
    file_hash TEXT NOT NULL,
    file_size_bytes BIGINT NOT NULL CHECK (file_size_bytes >= 0),
    row_count INTEGER NOT NULL DEFAULT 0 CHECK (row_count >= 0),
    created_at TEXT NOT NULL,
    UNIQUE(import_batch_id, file_hash)
);

-- One operator Confirm can contain several files and several GTIN requests. The
-- legacy marking_import_batch.marking_order_id remains only a single-request
-- compatibility snapshot; this join is the authoritative batch/request lineage.
CREATE TABLE IF NOT EXISTS marking_import_batch_request (
    import_batch_id UUID NOT NULL REFERENCES marking_import_batch(id) ON DELETE RESTRICT,
    marking_order_id UUID NOT NULL REFERENCES marking_order(id) ON DELETE RESTRICT,
    required_quantity_snapshot INTEGER NOT NULL CHECK (required_quantity_snapshot >= 0),
    reserve_quantity_snapshot INTEGER NOT NULL CHECK (reserve_quantity_snapshot >= 0),
    requested_quantity_snapshot INTEGER NOT NULL CHECK (requested_quantity_snapshot >= 0),
    imported_before INTEGER NOT NULL CHECK (imported_before >= 0),
    imported_in_batch INTEGER NOT NULL CHECK (imported_in_batch >= 0),
    imported_after INTEGER NOT NULL CHECK (imported_after >= 0),
    scope_snapshot_hash TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY(import_batch_id, marking_order_id)
);
CREATE INDEX IF NOT EXISTS ix_marking_import_batch_request_order
    ON marking_import_batch_request(marking_order_id, created_at);
CREATE UNIQUE INDEX IF NOT EXISTS ux_marking_import_batch_order_idempotency
    ON marking_import_batch(order_id, idempotency_key)
    WHERE order_id IS NOT NULL
      AND order_line_id IS NULL
      AND idempotency_key IS NOT NULL;

CREATE TABLE IF NOT EXISTS marking_operational_coverage (
    id UUID PRIMARY KEY,
    marking_subject_id UUID NOT NULL REFERENCES marking_production_subject(id) ON DELETE RESTRICT,
    marking_request_scope_id UUID NULL REFERENCES marking_request_scope(id) ON DELETE RESTRICT,
    source_type TEXT NOT NULL,
    covered_quantity NUMERIC(18,6) NOT NULL CHECK (covered_quantity > 0),
    import_batch_id UUID NULL REFERENCES marking_import_batch(id) ON DELETE RESTRICT,
    grandfather_allowance_id UUID NULL,
    created_at TEXT NOT NULL,
    retired_at TEXT,
    retirement_reason TEXT,
    CHECK (source_type IN ('REAL_IMPORT', 'GRANDFATHER_ALLOWANCE')),
    CHECK (
        (source_type = 'REAL_IMPORT' AND marking_request_scope_id IS NOT NULL AND import_batch_id IS NOT NULL)
        OR (source_type = 'GRANDFATHER_ALLOWANCE' AND marking_request_scope_id IS NULL AND import_batch_id IS NULL)
    )
);
CREATE INDEX IF NOT EXISTS ix_marking_operational_coverage_subject
    ON marking_operational_coverage(marking_subject_id, source_type)
    WHERE retired_at IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_marking_operational_coverage_real_scope
    ON marking_operational_coverage(marking_request_scope_id, marking_subject_id)
    WHERE source_type = 'REAL_IMPORT' AND retired_at IS NULL;

-- covered_quantity is immutable aggregate provenance. active_quantity is a
-- monotonic consumable cap and is the only quantity used by operational gates.
CREATE TABLE IF NOT EXISTS marking_operational_coverage_consumption (
    operational_coverage_id UUID PRIMARY KEY REFERENCES marking_operational_coverage(id) ON DELETE RESTRICT,
    active_quantity NUMERIC(18,6) NOT NULL CHECK (active_quantity >= 0),
    updated_at TEXT NOT NULL,
    retired_at TEXT,
    retirement_reason TEXT
);

INSERT INTO marking_operational_coverage_consumption(
    operational_coverage_id, active_quantity, updated_at)
SELECT coverage.id, coverage.covered_quantity, coverage.created_at
FROM marking_operational_coverage coverage
ON CONFLICT (operational_coverage_id) DO NOTHING;

CREATE OR REPLACE FUNCTION validate_marking_operational_coverage_consumption()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    immutable_quantity NUMERIC(18,6);
BEGIN
    SELECT covered_quantity INTO immutable_quantity
    FROM marking_operational_coverage
    WHERE id = NEW.operational_coverage_id;
    IF immutable_quantity IS NULL OR NEW.active_quantity > immutable_quantity THEN
        RAISE EXCEPTION 'MARKING_OPERATIONAL_COVERAGE_ACTIVE_QUANTITY_INVALID';
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.active_quantity > OLD.active_quantity THEN
        RAISE EXCEPTION 'MARKING_OPERATIONAL_COVERAGE_CAPACITY_CANNOT_INCREASE';
    END IF;
    RETURN NEW;
END;
$$;
DROP TRIGGER IF EXISTS trg_marking_operational_coverage_consumption_validate ON marking_operational_coverage_consumption;
CREATE TRIGGER trg_marking_operational_coverage_consumption_validate
BEFORE INSERT OR UPDATE ON marking_operational_coverage_consumption
FOR EACH ROW EXECUTE FUNCTION validate_marking_operational_coverage_consumption();

CREATE OR REPLACE FUNCTION create_marking_operational_coverage_consumption()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO marking_operational_coverage_consumption(
        operational_coverage_id, active_quantity, updated_at)
    VALUES(NEW.id, NEW.covered_quantity, NEW.created_at)
    ON CONFLICT (operational_coverage_id) DO NOTHING;
    RETURN NEW;
END;
$$;
DROP TRIGGER IF EXISTS trg_marking_operational_coverage_consumption_create ON marking_operational_coverage;
CREATE TRIGGER trg_marking_operational_coverage_consumption_create
AFTER INSERT ON marking_operational_coverage
FOR EACH ROW EXECUTE FUNCTION create_marking_operational_coverage_consumption();

CREATE TABLE IF NOT EXISTS marking_operational_coverage_import_lineage (
    operational_coverage_id UUID NOT NULL REFERENCES marking_operational_coverage(id) ON DELETE RESTRICT,
    marking_code_import_id UUID NOT NULL REFERENCES marking_code_import(id) ON DELETE RESTRICT,
    created_at TEXT NOT NULL,
    PRIMARY KEY(operational_coverage_id, marking_code_import_id)
);

CREATE TABLE IF NOT EXISTS marking_synthetic_legacy_allowlist_subject (
    id UUID PRIMARY KEY,
    allowlist_id BIGINT NOT NULL REFERENCES marking_synthetic_legacy_allowlist(id) ON DELETE RESTRICT,
    marking_subject_id UUID NOT NULL REFERENCES marking_production_subject(id) ON DELETE RESTRICT,
    component_id_snapshot BIGINT,
    item_id_snapshot BIGINT NOT NULL REFERENCES items(id) ON DELETE RESTRICT,
    gtin_snapshot TEXT NOT NULL,
    subject_quantity_at_cutover NUMERIC(18,6) NOT NULL CHECK (subject_quantity_at_cutover >= 0),
    approved_quantity NUMERIC(18,6) NOT NULL CHECK (approved_quantity >= 0),
    preflight_hash TEXT NOT NULL,
    approved_at TEXT NOT NULL,
    approved_by TEXT NOT NULL,
    UNIQUE(allowlist_id, marking_subject_id, preflight_hash),
    CHECK (approved_quantity <= subject_quantity_at_cutover)
);

CREATE OR REPLACE FUNCTION validate_marking_allowlist_subject_approval()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    parent_cap NUMERIC(18,6);
    already_approved NUMERIC(18,6);
BEGIN
    SELECT allowed_synthetic_qty INTO parent_cap
    FROM marking_synthetic_legacy_allowlist
    WHERE id = NEW.allowlist_id
    FOR UPDATE;
    SELECT COALESCE(SUM(approved_quantity), 0) INTO already_approved
    FROM marking_synthetic_legacy_allowlist_subject
    WHERE allowlist_id = NEW.allowlist_id;
    IF already_approved + NEW.approved_quantity > parent_cap THEN
        RAISE EXCEPTION 'MARKING_GRANDFATHER_APPROVAL_EXCEEDS_ALLOWLIST';
    END IF;
    RETURN NEW;
END;
$$;
DROP TRIGGER IF EXISTS trg_marking_allowlist_subject_validate ON marking_synthetic_legacy_allowlist_subject;
CREATE TRIGGER trg_marking_allowlist_subject_validate
BEFORE INSERT ON marking_synthetic_legacy_allowlist_subject
FOR EACH ROW EXECUTE FUNCTION validate_marking_allowlist_subject_approval();

DROP TRIGGER IF EXISTS trg_marking_allowlist_subject_immutable ON marking_synthetic_legacy_allowlist_subject;
CREATE TRIGGER trg_marking_allowlist_subject_immutable
BEFORE UPDATE OR DELETE ON marking_synthetic_legacy_allowlist_subject
FOR EACH ROW EXECUTE FUNCTION prevent_marking_request_scope_mutation();

CREATE TABLE IF NOT EXISTS marking_grandfather_operational_allowance (
    id UUID PRIMARY KEY,
    allowlist_subject_id UUID NOT NULL UNIQUE REFERENCES marking_synthetic_legacy_allowlist_subject(id) ON DELETE RESTRICT,
    marking_subject_id UUID NOT NULL REFERENCES marking_production_subject(id) ON DELETE RESTRICT,
    approved_quantity NUMERIC(18,6) NOT NULL CHECK (approved_quantity >= 0),
    cutover_subject_revision BIGINT NOT NULL,
    preflight_hash TEXT NOT NULL,
    created_at TEXT NOT NULL,
    retired_at TEXT,
    retirement_reason TEXT
);

CREATE OR REPLACE FUNCTION protect_marking_grandfather_allowance()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'grandfather allowance cannot be deleted';
    END IF;
    IF NEW.id IS DISTINCT FROM OLD.id
       OR NEW.allowlist_subject_id IS DISTINCT FROM OLD.allowlist_subject_id
       OR NEW.marking_subject_id IS DISTINCT FROM OLD.marking_subject_id
       OR NEW.approved_quantity IS DISTINCT FROM OLD.approved_quantity
       OR NEW.cutover_subject_revision IS DISTINCT FROM OLD.cutover_subject_revision
       OR NEW.preflight_hash IS DISTINCT FROM OLD.preflight_hash
       OR NEW.created_at IS DISTINCT FROM OLD.created_at
       OR OLD.retired_at IS NOT NULL THEN
        RAISE EXCEPTION 'grandfather allowance provenance and quantity are immutable';
    END IF;
    RETURN NEW;
END;
$$;
DROP TRIGGER IF EXISTS trg_marking_grandfather_allowance_immutable ON marking_grandfather_operational_allowance;
CREATE TRIGGER trg_marking_grandfather_allowance_immutable
BEFORE UPDATE OR DELETE ON marking_grandfather_operational_allowance
FOR EACH ROW EXECUTE FUNCTION protect_marking_grandfather_allowance();

ALTER TABLE marking_operational_coverage
    ADD CONSTRAINT fk_marking_coverage_grandfather_allowance
    FOREIGN KEY (grandfather_allowance_id)
    REFERENCES marking_grandfather_operational_allowance(id) ON DELETE RESTRICT;
ALTER TABLE marking_operational_coverage
    ADD CONSTRAINT ck_marking_coverage_source_reference CHECK (
        (source_type = 'REAL_IMPORT' AND grandfather_allowance_id IS NULL)
        OR (source_type = 'GRANDFATHER_ALLOWANCE' AND grandfather_allowance_id IS NOT NULL)
    );

CREATE UNIQUE INDEX IF NOT EXISTS ux_marking_coverage_active_grandfather_allowance
    ON marking_operational_coverage(grandfather_allowance_id)
    WHERE source_type = 'GRANDFATHER_ALLOWANCE' AND retired_at IS NULL;

CREATE OR REPLACE FUNCTION validate_active_grandfather_coverage()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    allowance_quantity NUMERIC(18,6);
    allowance_subject_id UUID;
    lineage_matches BOOLEAN;
BEGIN
    IF NEW.source_type <> 'GRANDFATHER_ALLOWANCE' THEN
        RETURN NEW;
    END IF;
    IF TG_OP = 'UPDATE' AND (
        NEW.grandfather_allowance_id IS DISTINCT FROM OLD.grandfather_allowance_id
        OR NEW.covered_quantity IS DISTINCT FROM OLD.covered_quantity
        OR NEW.source_type IS DISTINCT FROM OLD.source_type
    ) THEN
        RAISE EXCEPTION 'MARKING_GRANDFATHER_COVERAGE_PROVENANCE_IMMUTABLE';
    END IF;

    SELECT approved_quantity, marking_subject_id
    INTO allowance_quantity, allowance_subject_id
    FROM marking_grandfather_operational_allowance
    WHERE id = NEW.grandfather_allowance_id
    FOR UPDATE;
    IF allowance_quantity IS NULL OR NEW.covered_quantity > allowance_quantity THEN
        RAISE EXCEPTION 'MARKING_GRANDFATHER_COVERAGE_EXCEEDS_APPROVED_QUANTITY';
    END IF;

    WITH RECURSIVE lineage AS (
        SELECT subject.id, subject.predecessor_subject_id
        FROM marking_production_subject subject
        WHERE subject.id = NEW.marking_subject_id
        UNION ALL
        SELECT predecessor.id, predecessor.predecessor_subject_id
        FROM marking_production_subject predecessor
        INNER JOIN lineage child ON child.predecessor_subject_id = predecessor.id
    )
    SELECT EXISTS(SELECT 1 FROM lineage WHERE id = allowance_subject_id)
    INTO lineage_matches;
    IF NOT COALESCE(lineage_matches, FALSE) THEN
        RAISE EXCEPTION 'MARKING_GRANDFATHER_ALLOWANCE_SUBJECT_LINEAGE_MISMATCH';
    END IF;
    RETURN NEW;
END;
$$;
DROP TRIGGER IF EXISTS trg_marking_coverage_grandfather_validate ON marking_operational_coverage;
CREATE TRIGGER trg_marking_coverage_grandfather_validate
BEFORE INSERT OR UPDATE ON marking_operational_coverage
FOR EACH ROW EXECUTE FUNCTION validate_active_grandfather_coverage();

CREATE TABLE IF NOT EXISTS marking_ready_hu_fact (
    id UUID PRIMARY KEY,
    marking_subject_id UUID NOT NULL REFERENCES marking_production_subject(id) ON DELETE RESTRICT,
    receipt_doc_id BIGINT NOT NULL REFERENCES docs(id) ON DELETE RESTRICT,
    receipt_line_id BIGINT NOT NULL REFERENCES doc_lines(id) ON DELETE RESTRICT,
    hu_id BIGINT NOT NULL REFERENCES hus(id) ON DELETE RESTRICT,
    hu_code_snapshot TEXT NOT NULL,
    item_id_snapshot BIGINT NOT NULL REFERENCES items(id) ON DELETE RESTRICT,
    gtin_snapshot TEXT NOT NULL,
    marked_quantity NUMERIC(18,6) NOT NULL CHECK (marked_quantity > 0),
    provenance TEXT NOT NULL,
    created_at TEXT NOT NULL,
    reversed_at TEXT,
    correction_reference TEXT,
    CHECK (provenance IN ('REAL_IMPORT', 'GRANDFATHERED', 'MIXED'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_marking_ready_hu_fact_active_subject_receipt
    ON marking_ready_hu_fact(marking_subject_id, receipt_line_id, hu_id)
    WHERE reversed_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_marking_ready_hu_fact_hu_item
    ON marking_ready_hu_fact(hu_id, item_id_snapshot)
    WHERE reversed_at IS NULL;

CREATE TABLE IF NOT EXISTS marking_ready_hu_fact_lineage (
    id UUID PRIMARY KEY,
    ready_hu_fact_id UUID NOT NULL REFERENCES marking_ready_hu_fact(id) ON DELETE RESTRICT,
    operational_coverage_id UUID NOT NULL REFERENCES marking_operational_coverage(id) ON DELETE RESTRICT,
    attributed_quantity NUMERIC(18,6) NOT NULL CHECK (attributed_quantity > 0),
    created_at TEXT NOT NULL,
    UNIQUE(ready_hu_fact_id, operational_coverage_id)
);

CREATE TABLE IF NOT EXISTS marking_ready_hu_grandfather_lineage (
    ready_hu_fact_id UUID PRIMARY KEY REFERENCES marking_ready_hu_fact(id) ON DELETE RESTRICT,
    marking_subject_id UUID NOT NULL REFERENCES marking_production_subject(id) ON DELETE RESTRICT,
    production_pallet_id_snapshot BIGINT,
    receipt_doc_id_snapshot BIGINT NOT NULL,
    receipt_line_id_snapshot BIGINT NOT NULL,
    ledger_balance_snapshot NUMERIC(18,6) NOT NULL CHECK (ledger_balance_snapshot > 0),
    preflight_hash TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE OR REPLACE FUNCTION cap_marking_subject_consumables(
    p_subject_id UUID,
    p_quantity NUMERIC,
    p_changed_at TEXT,
    p_reason TEXT)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    WITH ranked AS (
        SELECT consumption.marking_request_scope_id,
               consumption.active_quantity,
               COALESCE(SUM(consumption.active_quantity) OVER (
                   ORDER BY scope.created_at, scope.id
                   ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS preceding_quantity
        FROM marking_request_scope_consumption consumption
        INNER JOIN marking_request_scope scope
                ON scope.id = consumption.marking_request_scope_id
        WHERE scope.marking_subject_id = p_subject_id
          AND consumption.active_quantity > 0
    ), capped AS (
        SELECT marking_request_scope_id,
               LEAST(active_quantity, GREATEST(0, p_quantity - preceding_quantity)) AS new_quantity
        FROM ranked
    )
    UPDATE marking_request_scope_consumption consumption
    SET active_quantity = capped.new_quantity,
        updated_at = p_changed_at,
        retired_at = CASE WHEN capped.new_quantity <= 0
                          THEN COALESCE(consumption.retired_at, p_changed_at)
                          ELSE consumption.retired_at END,
        retirement_reason = CASE WHEN capped.new_quantity < consumption.active_quantity
                                 THEN COALESCE(consumption.retirement_reason, p_reason)
                                 ELSE consumption.retirement_reason END
    FROM capped
    WHERE consumption.marking_request_scope_id = capped.marking_request_scope_id
      AND capped.new_quantity < consumption.active_quantity;

    WITH ranked AS (
        SELECT consumption.operational_coverage_id,
               consumption.active_quantity,
               COALESCE(SUM(consumption.active_quantity) OVER (
                   ORDER BY coverage.created_at, coverage.id
                   ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS preceding_quantity
        FROM marking_operational_coverage_consumption consumption
        INNER JOIN marking_operational_coverage coverage
                ON coverage.id = consumption.operational_coverage_id
        WHERE coverage.marking_subject_id = p_subject_id
          AND coverage.retired_at IS NULL
          AND consumption.active_quantity > 0
    ), capped AS (
        SELECT operational_coverage_id,
               LEAST(active_quantity, GREATEST(0, p_quantity - preceding_quantity)) AS new_quantity
        FROM ranked
    )
    UPDATE marking_operational_coverage_consumption consumption
    SET active_quantity = capped.new_quantity,
        updated_at = p_changed_at,
        retired_at = CASE WHEN capped.new_quantity <= 0
                          THEN COALESCE(consumption.retired_at, p_changed_at)
                          ELSE consumption.retired_at END,
        retirement_reason = CASE WHEN capped.new_quantity < consumption.active_quantity
                                 THEN COALESCE(consumption.retirement_reason, p_reason)
                                 ELSE consumption.retirement_reason END
    FROM capped
    WHERE consumption.operational_coverage_id = capped.operational_coverage_id
      AND capped.new_quantity < consumption.active_quantity;
END;
$$;

CREATE OR REPLACE FUNCTION sync_marking_production_subject()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    resolved_gtin TEXT;
    resolved_order_id BIGINT;
    subject_uuid UUID;
    has_provenance BOOLEAN;
    previous_subject_quantity NUMERIC(18,6);
    changed_at TEXT := to_char(NOW() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"');
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.marking_subject_id IS NOT NULL THEN
            PERFORM cap_marking_subject_consumables(
                OLD.marking_subject_id, 0, changed_at, 'subject_cancelled');
            UPDATE marking_production_subject
            SET lifecycle = 'CANCELLED', revision = revision + 1,
                current_component_id = NULL, cancelled_at = COALESCE(cancelled_at, changed_at)
            WHERE id = OLD.marking_subject_id AND lifecycle <> 'COMPLETED';
            UPDATE marking_operational_coverage
            SET retired_at = COALESCE(retired_at, to_char(NOW() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"')),
                retirement_reason = COALESCE(retirement_reason, 'subject_cancelled')
            WHERE marking_subject_id = OLD.marking_subject_id AND retired_at IS NULL;
            UPDATE marking_grandfather_operational_allowance
            SET retired_at = COALESCE(retired_at, to_char(NOW() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"')),
                retirement_reason = COALESCE(retirement_reason, 'subject_cancelled')
            WHERE marking_subject_id = OLD.marking_subject_id AND retired_at IS NULL;
        END IF;
        RETURN OLD;
    END IF;

    SELECT NULLIF(BTRIM(i.gtin), ''), COALESCE(ol.order_id, pp.order_id, d.order_id)
    INTO resolved_gtin, resolved_order_id
    FROM items i
    INNER JOIN item_types it ON it.id = i.item_type_id AND COALESCE(it.enable_marking, FALSE) = TRUE
    INNER JOIN production_pallets pp ON pp.id = NEW.production_pallet_id
    INNER JOIN docs d ON d.id = pp.prd_doc_id
    LEFT JOIN order_lines ol ON ol.id = NEW.order_line_id
    WHERE i.id = NEW.item_id;

    IF resolved_gtin IS NULL THEN
        RETURN NEW;
    END IF;

    IF NEW.marking_subject_id IS NULL THEN
        subject_uuid := (md5('flowstock-marking-subject:' || NEW.id::text || ':' || clock_timestamp()::text || ':' || random()::text))::uuid;
        INSERT INTO marking_production_subject(
            id, lifecycle, revision,
            original_production_pallet_id, original_component_id, original_doc_line_id,
            original_order_id, original_order_line_id,
            current_production_pallet_id, current_component_id, current_doc_id,
            current_doc_line_id, current_order_id, current_order_line_id,
            item_id, gtin, hu_code_snapshot, subject_quantity, created_at)
        SELECT subject_uuid,
               CASE WHEN pp.status = 'CORRECTED' THEN 'SUPERSEDED'
                    WHEN pp.status = 'CANCELLED' THEN 'CANCELLED'
                    WHEN pp.status = 'FILLED' THEN 'COMPLETED'
                    ELSE 'ACTIVE' END,
               0,
               pp.id, NEW.id, NEW.doc_line_id,
               resolved_order_id, NEW.order_line_id,
               pp.id, NEW.id, pp.prd_doc_id,
               NEW.doc_line_id, resolved_order_id, NEW.order_line_id,
               NEW.item_id, resolved_gtin, pp.hu_code, NEW.planned_qty, NEW.created_at
        FROM production_pallets pp WHERE pp.id = NEW.production_pallet_id;
        NEW.marking_subject_id := subject_uuid;
        RETURN NEW;
    END IF;

    SELECT subject_quantity INTO previous_subject_quantity
    FROM marking_production_subject
    WHERE id = NEW.marking_subject_id
    FOR UPDATE;
    IF NEW.planned_qty < previous_subject_quantity THEN
        PERFORM cap_marking_subject_consumables(
            NEW.marking_subject_id,
            NEW.planned_qty::numeric,
            changed_at,
            'subject_quantity_decreased');
    END IF;

    SELECT EXISTS(
        SELECT 1 FROM marking_request_scope WHERE marking_subject_id = NEW.marking_subject_id
        UNION ALL SELECT 1 FROM marking_operational_coverage WHERE marking_subject_id = NEW.marking_subject_id
        UNION ALL SELECT 1 FROM marking_ready_hu_fact WHERE marking_subject_id = NEW.marking_subject_id
    ) INTO has_provenance;

    IF has_provenance AND EXISTS (
        SELECT 1 FROM marking_production_subject
        WHERE id = NEW.marking_subject_id
          AND (item_id <> NEW.item_id OR gtin <> resolved_gtin)
    ) THEN
        RAISE EXCEPTION 'MARKING_SUBJECT_ITEM_GTIN_MUTATION_FORBIDDEN';
    END IF;

    INSERT INTO marking_subject_ownership_audit(
        marking_subject_id, subject_revision, old_order_id, old_order_line_id,
        new_order_id, new_order_line_id, reason, changed_at)
    SELECT id, revision + 1, current_order_id, current_order_line_id,
           resolved_order_id, NEW.order_line_id, 'component_mutation',
           to_char(NOW() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"')
    FROM marking_production_subject
    WHERE id = NEW.marking_subject_id
      AND (current_order_id IS DISTINCT FROM resolved_order_id
           OR current_order_line_id IS DISTINCT FROM NEW.order_line_id);

    UPDATE marking_production_subject subject
    SET revision = subject.revision + 1,
        current_production_pallet_id = NEW.production_pallet_id,
        current_component_id = NEW.id,
        current_doc_line_id = NEW.doc_line_id,
        current_order_id = resolved_order_id,
        current_order_line_id = NEW.order_line_id,
        item_id = NEW.item_id,
        gtin = resolved_gtin,
        subject_quantity = NEW.planned_qty
    WHERE subject.id = NEW.marking_subject_id
      AND (subject.current_production_pallet_id IS DISTINCT FROM NEW.production_pallet_id
           OR subject.current_doc_line_id IS DISTINCT FROM NEW.doc_line_id
           OR subject.current_order_id IS DISTINCT FROM resolved_order_id
           OR subject.current_order_line_id IS DISTINCT FROM NEW.order_line_id
           OR subject.item_id IS DISTINCT FROM NEW.item_id
           OR subject.gtin IS DISTINCT FROM resolved_gtin
           OR subject.subject_quantity IS DISTINCT FROM NEW.planned_qty);
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_production_pallet_line_marking_subject ON production_pallet_lines;
CREATE TRIGGER trg_production_pallet_line_marking_subject
BEFORE INSERT OR UPDATE OR DELETE ON production_pallet_lines
FOR EACH ROW EXECUTE FUNCTION sync_marking_production_subject();

CREATE OR REPLACE FUNCTION sync_marking_subject_pallet_lifecycle()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    changed_at TEXT := to_char(NOW() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"');
BEGIN
    IF NEW.status = 'CANCELLED' AND OLD.status IS DISTINCT FROM NEW.status THEN
        PERFORM cap_marking_subject_consumables(
            subject.id, 0, changed_at, 'subject_cancelled')
        FROM marking_production_subject subject
        INNER JOIN production_pallet_lines component
                ON component.marking_subject_id = subject.id
        WHERE component.production_pallet_id = NEW.id
          AND subject.lifecycle <> 'COMPLETED';
        UPDATE marking_production_subject subject
        SET lifecycle = 'CANCELLED', revision = subject.revision + 1,
            cancelled_at = COALESCE(subject.cancelled_at, changed_at)
        FROM production_pallet_lines component
        WHERE component.production_pallet_id = NEW.id
          AND subject.id = component.marking_subject_id
          AND subject.lifecycle <> 'COMPLETED';
        UPDATE marking_operational_coverage coverage
        SET retired_at = COALESCE(coverage.retired_at, to_char(NOW() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"')),
            retirement_reason = COALESCE(coverage.retirement_reason, 'subject_cancelled')
        FROM production_pallet_lines component
        WHERE component.production_pallet_id = NEW.id
          AND coverage.marking_subject_id = component.marking_subject_id
          AND coverage.retired_at IS NULL;
        UPDATE marking_grandfather_operational_allowance allowance
        SET retired_at = COALESCE(allowance.retired_at, to_char(NOW() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"')),
            retirement_reason = COALESCE(allowance.retirement_reason, 'subject_cancelled')
        FROM production_pallet_lines component
        WHERE component.production_pallet_id = NEW.id
          AND allowance.marking_subject_id = component.marking_subject_id
          AND allowance.retired_at IS NULL;
    ELSIF NEW.status = 'FILLED' AND OLD.status IS DISTINCT FROM NEW.status THEN
        UPDATE marking_production_subject subject
        SET lifecycle = 'COMPLETED', revision = subject.revision + 1,
            completed_at = COALESCE(subject.completed_at, NEW.filled_at)
        FROM production_pallet_lines component
        WHERE component.production_pallet_id = NEW.id
          AND subject.id = component.marking_subject_id;
    ELSIF NEW.status = 'CORRECTED' AND OLD.status IS DISTINCT FROM NEW.status THEN
        UPDATE marking_production_subject subject
        SET lifecycle = 'SUPERSEDED', revision = subject.revision + 1,
            superseded_at = COALESCE(subject.superseded_at, changed_at)
        FROM production_pallet_lines component
        WHERE component.production_pallet_id = NEW.id
          AND subject.id = component.marking_subject_id;
    END IF;
    RETURN NEW;
END;
$$;
DROP TRIGGER IF EXISTS trg_production_pallet_marking_subject_lifecycle ON production_pallets;
CREATE TRIGGER trg_production_pallet_marking_subject_lifecycle
AFTER UPDATE OF status ON production_pallets
FOR EACH ROW EXECUTE FUNCTION sync_marking_subject_pallet_lifecycle();

CREATE UNIQUE INDEX IF NOT EXISTS ux_marking_code_real_hash
    ON marking_code(LOWER(BTRIM(code_hash)))
    WHERE origin = 'RealImport';

CREATE OR REPLACE FUNCTION prevent_real_marking_code_provenance_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.origin = 'RealImport' AND (
        NEW.code IS DISTINCT FROM OLD.code
        OR NEW.code_hash IS DISTINCT FROM OLD.code_hash
        OR NEW.gtin IS DISTINCT FROM OLD.gtin
        OR NEW.marking_order_id IS DISTINCT FROM OLD.marking_order_id
        OR NEW.import_id IS DISTINCT FROM OLD.import_id
        OR NEW.source_row_number IS DISTINCT FROM OLD.source_row_number
        OR NEW.origin IS DISTINCT FROM OLD.origin
        OR NEW.receipt_doc_id IS DISTINCT FROM OLD.receipt_doc_id
        OR NEW.receipt_line_id IS DISTINCT FROM OLD.receipt_line_id
    ) THEN
        RAISE EXCEPTION 'RealImport marking acquisition provenance is immutable';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_marking_code_real_provenance_immutable ON marking_code;
CREATE TRIGGER trg_marking_code_real_provenance_immutable
BEFORE UPDATE ON marking_code
FOR EACH ROW EXECUTE FUNCTION prevent_real_marking_code_provenance_mutation();

CREATE OR REPLACE FUNCTION prevent_real_marking_code_delete()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.origin = 'RealImport' THEN
        RAISE EXCEPTION 'RealImport marking code cannot be deleted';
    END IF;
    RETURN OLD;
END;
$$;

DROP TRIGGER IF EXISTS trg_marking_code_real_no_delete ON marking_code;
CREATE TRIGGER trg_marking_code_real_no_delete
BEFORE DELETE ON marking_code
FOR EACH ROW EXECUTE FUNCTION prevent_real_marking_code_delete();
