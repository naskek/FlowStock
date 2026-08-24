-- Canonical per-item ЧЗ applicability override and one-time legacy approval guards.
-- Migration runner owns the surrounding transaction.

ALTER TABLE items
    ADD COLUMN IF NOT EXISTS chz_marking_exempt BOOLEAN NOT NULL DEFAULT FALSE;

ALTER TABLE marking_synthetic_legacy_allowlist_subject
    ADD COLUMN IF NOT EXISTS subject_revision_at_cutover BIGINT;
ALTER TABLE marking_synthetic_legacy_allowlist_subject
    ADD CONSTRAINT ck_marking_allowlist_subject_revision_present
    CHECK (subject_revision_at_cutover IS NOT NULL) NOT VALID;

ALTER TABLE marking_grandfather_operational_allowance
    ADD COLUMN IF NOT EXISTS usable_quantity_cap NUMERIC(18,6);
UPDATE marking_grandfather_operational_allowance
SET usable_quantity_cap = approved_quantity
WHERE usable_quantity_cap IS NULL;
ALTER TABLE marking_grandfather_operational_allowance
    ALTER COLUMN usable_quantity_cap SET NOT NULL;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM marking_synthetic_legacy_allowlist
        GROUP BY order_line_id
        HAVING COUNT(*) > 1
    ) THEN
        RAISE EXCEPTION 'MARKING_LEGACY_ALLOWLIST_DUPLICATE_PARENT_DATA';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM marking_synthetic_legacy_allowlist_subject
        GROUP BY marking_subject_id
        HAVING COUNT(*) > 1
    ) THEN
        RAISE EXCEPTION 'MARKING_LEGACY_ALLOWLIST_DUPLICATE_SUBJECT_DATA';
    END IF;
END;
$$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_marking_legacy_allowlist_one_parent_per_line
    ON marking_synthetic_legacy_allowlist(order_line_id);

CREATE UNIQUE INDEX IF NOT EXISTS ux_marking_allowlist_subject_once
    ON marking_synthetic_legacy_allowlist_subject(marking_subject_id);

CREATE OR REPLACE FUNCTION prevent_marking_legacy_allowlist_parent_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'MARKING_LEGACY_ALLOWLIST_PARENT_IMMUTABLE';
END;
$$;

DROP TRIGGER IF EXISTS trg_marking_legacy_allowlist_parent_immutable
    ON marking_synthetic_legacy_allowlist;
CREATE TRIGGER trg_marking_legacy_allowlist_parent_immutable
BEFORE UPDATE OR DELETE ON marking_synthetic_legacy_allowlist
FOR EACH ROW EXECUTE FUNCTION prevent_marking_legacy_allowlist_parent_mutation();

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
       OR NEW.usable_quantity_cap > OLD.usable_quantity_cap
       OR NEW.usable_quantity_cap < 0
       OR OLD.retired_at IS NOT NULL THEN
        RAISE EXCEPTION 'grandfather allowance provenance and bounded capacity are immutable';
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION cap_grandfather_allowance_with_subject()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.subject_quantity < OLD.subject_quantity THEN
        WITH RECURSIVE lineage AS (
            SELECT NEW.id AS id, NEW.predecessor_subject_id AS predecessor_subject_id
            UNION ALL
            SELECT predecessor.id, predecessor.predecessor_subject_id
            FROM marking_production_subject predecessor
            INNER JOIN lineage child ON child.predecessor_subject_id = predecessor.id
        )
        UPDATE marking_grandfather_operational_allowance
        SET usable_quantity_cap = LEAST(usable_quantity_cap, NEW.subject_quantity)
        WHERE marking_subject_id IN (SELECT id FROM lineage)
          AND retired_at IS NULL;
    END IF;
    IF NEW.lifecycle = 'CANCELLED' AND OLD.lifecycle IS DISTINCT FROM NEW.lifecycle THEN
        WITH RECURSIVE lineage AS (
            SELECT NEW.id AS id, NEW.predecessor_subject_id AS predecessor_subject_id
            UNION ALL
            SELECT predecessor.id, predecessor.predecessor_subject_id
            FROM marking_production_subject predecessor
            INNER JOIN lineage child ON child.predecessor_subject_id = predecessor.id
        )
        UPDATE marking_grandfather_operational_allowance
        SET usable_quantity_cap = 0
        WHERE marking_subject_id IN (SELECT id FROM lineage)
          AND retired_at IS NULL;
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_marking_subject_grandfather_capacity
    ON marking_production_subject;
CREATE TRIGGER trg_marking_subject_grandfather_capacity
AFTER UPDATE OF subject_quantity, lifecycle ON marking_production_subject
FOR EACH ROW EXECUTE FUNCTION cap_grandfather_allowance_with_subject();

CREATE OR REPLACE FUNCTION validate_active_grandfather_coverage()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    allowance_quantity NUMERIC(18,6);
    allowance_subject_id UUID;
    allowance_retired_at TEXT;
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
    IF TG_OP = 'UPDATE'
       AND OLD.retired_at IS NULL
       AND NEW.retired_at IS NOT NULL THEN
        RETURN NEW;
    END IF;

    SELECT usable_quantity_cap, marking_subject_id, retired_at
    INTO allowance_quantity, allowance_subject_id, allowance_retired_at
    FROM marking_grandfather_operational_allowance
    WHERE id = NEW.grandfather_allowance_id
    FOR UPDATE;
    IF allowance_quantity IS NULL
       OR allowance_retired_at IS NOT NULL
       OR NEW.covered_quantity > allowance_quantity THEN
        RAISE EXCEPTION 'MARKING_GRANDFATHER_COVERAGE_EXCEEDS_USABLE_CAPACITY';
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

CREATE OR REPLACE FUNCTION item_has_protected_marking_lineage(candidate_item_id BIGINT)
RETURNS BOOLEAN LANGUAGE sql STABLE AS $$
    SELECT EXISTS (
        SELECT 1
        FROM marking_production_subject subject
        WHERE subject.item_id = candidate_item_id
          AND subject.lifecycle IN ('ACTIVE', 'COMPLETED')
        UNION ALL
        SELECT 1
        FROM marking_request_scope scope
        WHERE scope.item_id_snapshot = candidate_item_id
        UNION ALL
        SELECT 1
        FROM marking_ready_hu_fact fact
        WHERE fact.item_id_snapshot = candidate_item_id
        UNION ALL
        SELECT 1
        FROM marking_operational_coverage coverage
        INNER JOIN marking_production_subject subject
                ON subject.id = coverage.marking_subject_id
        WHERE subject.item_id = candidate_item_id
        UNION ALL
        SELECT 1
        FROM marking_grandfather_operational_allowance allowance
        INNER JOIN marking_production_subject subject
                ON subject.id = allowance.marking_subject_id
        WHERE subject.item_id = candidate_item_id
        UNION ALL
        SELECT 1
        FROM marking_code code
        INNER JOIN marking_order request ON request.id = code.marking_order_id
        LEFT JOIN order_lines line ON line.id = request.order_line_id
        WHERE COALESCE(request.item_id, line.item_id) = candidate_item_id
          AND (
              code.origin IN ('RealImport', 'LegacyRealImport', 'HistoricalUnknown')
              OR code.status = 'Quarantined'
              OR (code.origin = 'LegacySynthetic' AND code.status = 'Applied')
          )
    );
$$;

CREATE OR REPLACE FUNCTION validate_item_marking_applicability_transition()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    old_type_enabled BOOLEAN := FALSE;
    new_type_enabled BOOLEAN := FALSE;
    old_applicable BOOLEAN := FALSE;
    new_applicable BOOLEAN := FALSE;
BEGIN
    IF TG_OP = 'UPDATE' AND OLD.item_type_id IS NOT NULL THEN
        SELECT COALESCE(enable_marking, FALSE)
        INTO old_type_enabled
        FROM item_types
        WHERE id = OLD.item_type_id;
    END IF;

    IF NEW.item_type_id IS NOT NULL THEN
        SELECT COALESCE(enable_marking, FALSE)
        INTO new_type_enabled
        FROM item_types
        WHERE id = NEW.item_type_id;
    END IF;

    old_applicable := TG_OP = 'UPDATE'
        AND old_type_enabled
        AND NOT COALESCE(OLD.chz_marking_exempt, FALSE);
    new_applicable := new_type_enabled
        AND NOT COALESCE(NEW.chz_marking_exempt, FALSE);

    IF new_applicable
       AND NULLIF(BTRIM(NEW.gtin), '') IS NULL
       AND (
           TG_OP = 'INSERT'
           OR NOT old_applicable
           OR NULLIF(BTRIM(OLD.gtin), '') IS NOT NULL
       ) THEN
        RAISE EXCEPTION 'MARKING_GTIN_REQUIRED';
    END IF;

    IF TG_OP = 'UPDATE'
       AND old_applicable
       AND NOT new_applicable
       AND item_has_protected_marking_lineage(OLD.id) THEN
        RAISE EXCEPTION 'MARKING_APPLICABILITY_LINEAGE_EXISTS';
    END IF;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_items_marking_applicability_transition ON items;
CREATE TRIGGER trg_items_marking_applicability_transition
BEFORE INSERT OR UPDATE OF gtin, item_type_id, chz_marking_exempt ON items
FOR EACH ROW EXECUTE FUNCTION validate_item_marking_applicability_transition();

CREATE OR REPLACE FUNCTION validate_item_type_marking_applicability_transition()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    blocked_item_id BIGINT;
BEGIN
    IF COALESCE(OLD.enable_marking, FALSE) = COALESCE(NEW.enable_marking, FALSE) THEN
        RETURN NEW;
    END IF;

    PERFORM id
    FROM items
    WHERE item_type_id = OLD.id
    ORDER BY id
    FOR UPDATE;

    IF NOT COALESCE(OLD.enable_marking, FALSE)
       AND COALESCE(NEW.enable_marking, FALSE) THEN
        SELECT id INTO blocked_item_id
        FROM items
        WHERE item_type_id = OLD.id
          AND NOT COALESCE(chz_marking_exempt, FALSE)
          AND NULLIF(BTRIM(gtin), '') IS NULL
        ORDER BY id
        LIMIT 1;
        IF blocked_item_id IS NOT NULL THEN
            RAISE EXCEPTION 'MARKING_GTIN_REQUIRED: item_id=%', blocked_item_id;
        END IF;
    ELSIF COALESCE(OLD.enable_marking, FALSE)
          AND NOT COALESCE(NEW.enable_marking, FALSE) THEN
        SELECT id INTO blocked_item_id
        FROM items
        WHERE item_type_id = OLD.id
          AND NOT COALESCE(chz_marking_exempt, FALSE)
          AND item_has_protected_marking_lineage(id)
        ORDER BY id
        LIMIT 1;
        IF blocked_item_id IS NOT NULL THEN
            RAISE EXCEPTION 'MARKING_APPLICABILITY_LINEAGE_EXISTS: item_id=%', blocked_item_id;
        END IF;
    END IF;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_item_types_marking_applicability_transition ON item_types;
CREATE TRIGGER trg_item_types_marking_applicability_transition
BEFORE UPDATE OF enable_marking ON item_types
FOR EACH ROW EXECUTE FUNCTION validate_item_type_marking_applicability_transition();

-- The persisted column remains a synchronized projection for compatibility read paths.
CREATE OR REPLACE FUNCTION calculate_order_marking_status(candidate_order_id BIGINT)
RETURNS TEXT LANGUAGE sql STABLE AS $$
WITH applicable_lines AS (
    SELECT line.id,
           line.item_id,
           line.qty_ordered,
           NULLIF(BTRIM(item.gtin), '') AS gtin
    FROM order_lines line
    INNER JOIN items item ON item.id = line.item_id
    INNER JOIN item_types item_type ON item_type.id = item.item_type_id
    WHERE line.order_id = candidate_order_id
      AND line.cancelled_at IS NULL
      AND line.qty_ordered > 0.000001
      AND COALESCE(item_type.enable_marking, FALSE)
      AND NOT COALESCE(item.chz_marking_exempt, FALSE)
), operational AS (
    SELECT subject.current_order_line_id AS order_line_id,
           SUM(consumption.active_quantity) AS covered_quantity
    FROM marking_production_subject subject
    INNER JOIN marking_operational_coverage coverage
            ON coverage.marking_subject_id = subject.id
           AND coverage.retired_at IS NULL
    INNER JOIN marking_operational_coverage_consumption consumption
            ON consumption.operational_coverage_id = coverage.id
           AND consumption.active_quantity > 0
    WHERE subject.current_order_line_id IN (SELECT id FROM applicable_lines)
      AND subject.lifecycle IN ('ACTIVE', 'COMPLETED')
      AND NOT EXISTS (
          SELECT 1
          FROM marking_ready_hu_fact fact
          WHERE fact.marking_subject_id = subject.id
            AND fact.reversed_at IS NULL
      )
    GROUP BY subject.current_order_line_id
), ledger_stock AS (
    SELECT item_id,
           UPPER(BTRIM(COALESCE(hu_code, hu))) AS hu_code,
           SUM(qty_delta) AS quantity
    FROM ledger
    WHERE NULLIF(BTRIM(COALESCE(hu_code, hu)), '') IS NOT NULL
    GROUP BY item_id, UPPER(BTRIM(COALESCE(hu_code, hu)))
    HAVING SUM(qty_delta) > 0.000001
), ready_fact AS (
    SELECT item_id_snapshot AS item_id,
           UPPER(BTRIM(hu_code_snapshot)) AS hu_code,
           SUM(marked_quantity) AS marked_quantity
    FROM marking_ready_hu_fact
    WHERE reversed_at IS NULL
    GROUP BY item_id_snapshot, UPPER(BTRIM(hu_code_snapshot))
), ready AS (
    SELECT plan.order_line_id,
           SUM(LEAST(plan.qty_planned, stock.quantity, fact.marked_quantity)) AS covered_quantity
    FROM order_receipt_plan_lines plan
    INNER JOIN applicable_lines line ON line.id = plan.order_line_id
    INNER JOIN ledger_stock stock
            ON stock.item_id = plan.item_id
           AND stock.hu_code = UPPER(BTRIM(plan.to_hu))
    INNER JOIN ready_fact fact
            ON fact.item_id = plan.item_id
           AND fact.hu_code = UPPER(BTRIM(plan.to_hu))
    WHERE plan.qty_planned > 0
      AND NULLIF(BTRIM(plan.to_hu), '') IS NOT NULL
    GROUP BY plan.order_line_id
), summary AS (
    SELECT COUNT(*) AS applicable_count,
           COALESCE(BOOL_AND(
               line.gtin IS NOT NULL
               AND COALESCE(operational.covered_quantity, 0)
                   + COALESCE(ready.covered_quantity, 0)
                   + 0.000001 >= line.qty_ordered
           ), FALSE) AS fully_covered
    FROM applicable_lines line
    LEFT JOIN operational ON operational.order_line_id = line.id
    LEFT JOIN ready ON ready.order_line_id = line.id
)
SELECT CASE
    WHEN applicable_count = 0 THEN 'NOT_REQUIRED'
    WHEN fully_covered THEN 'APPLIED'
    ELSE 'NOT_APPLIED'
END
FROM summary;
$$;

CREATE OR REPLACE FUNCTION refresh_marking_status_for_items(candidate_item_ids BIGINT[])
RETURNS VOID LANGUAGE plpgsql AS $$
DECLARE
    affected_order_ids BIGINT[];
BEGIN
    SELECT COALESCE(ARRAY_AGG(DISTINCT line.order_id ORDER BY line.order_id), ARRAY[]::BIGINT[])
    INTO affected_order_ids
    FROM order_lines line
    INNER JOIN orders order_row ON order_row.id = line.order_id
    WHERE line.item_id = ANY(candidate_item_ids)
      AND line.cancelled_at IS NULL
      AND order_row.status NOT IN ('CANCELLED', 'MERGED', 'SHIPPED');

    IF CARDINALITY(affected_order_ids) = 0 THEN
        RETURN;
    END IF;

    PERFORM id
    FROM orders
    WHERE id = ANY(affected_order_ids)
    ORDER BY id
    FOR UPDATE;

    UPDATE orders order_row
    SET marking_status = calculate_order_marking_status(order_row.id)
    WHERE order_row.id = ANY(affected_order_ids);
END;
$$;

CREATE OR REPLACE FUNCTION refresh_item_marking_status_projection()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM refresh_marking_status_for_items(ARRAY[NEW.id]);
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_items_marking_status_projection ON items;
CREATE TRIGGER trg_items_marking_status_projection
AFTER UPDATE OF gtin, item_type_id, chz_marking_exempt ON items
FOR EACH ROW EXECUTE FUNCTION refresh_item_marking_status_projection();

CREATE OR REPLACE FUNCTION refresh_item_type_marking_status_projection()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    affected_item_ids BIGINT[];
BEGIN
    IF COALESCE(OLD.enable_marking, FALSE) = COALESCE(NEW.enable_marking, FALSE) THEN
        RETURN NEW;
    END IF;

    SELECT COALESCE(ARRAY_AGG(id ORDER BY id), ARRAY[]::BIGINT[])
    INTO affected_item_ids
    FROM items
    WHERE item_type_id = NEW.id;
    PERFORM refresh_marking_status_for_items(affected_item_ids);
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_item_types_marking_status_projection ON item_types;
CREATE TRIGGER trg_item_types_marking_status_projection
AFTER UPDATE OF enable_marking ON item_types
FOR EACH ROW EXECUTE FUNCTION refresh_item_type_marking_status_projection();

CREATE OR REPLACE FUNCTION validate_marking_allowlist_subject_approval()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    parent_cap NUMERIC(18,6);
    parent_line_id BIGINT;
    already_approved NUMERIC(18,6);
    current_subject marking_production_subject%ROWTYPE;
BEGIN
    SELECT allowed_synthetic_qty, order_line_id
    INTO parent_cap, parent_line_id
    FROM marking_synthetic_legacy_allowlist
    WHERE id = NEW.allowlist_id
    FOR UPDATE;

    IF parent_cap IS NULL THEN
        RAISE EXCEPTION 'MARKING_GRANDFATHER_PARENT_NOT_FOUND';
    END IF;

    SELECT * INTO current_subject
    FROM marking_production_subject
    WHERE id = NEW.marking_subject_id
    FOR UPDATE;

    IF current_subject.id IS NULL
       OR current_subject.current_order_line_id IS DISTINCT FROM parent_line_id
       OR current_subject.item_id IS DISTINCT FROM NEW.item_id_snapshot
       OR current_subject.gtin IS DISTINCT FROM NEW.gtin_snapshot
       OR current_subject.revision IS DISTINCT FROM NEW.subject_revision_at_cutover
       OR current_subject.subject_quantity IS DISTINCT FROM NEW.subject_quantity_at_cutover
       OR current_subject.current_component_id IS DISTINCT FROM NEW.component_id_snapshot
       OR current_subject.lifecycle <> 'ACTIVE' THEN
        RAISE EXCEPTION 'MARKING_GRANDFATHER_SUBJECT_SNAPSHOT_MISMATCH';
    END IF;

    SELECT COALESCE(SUM(approved_quantity), 0) INTO already_approved
    FROM marking_synthetic_legacy_allowlist_subject
    WHERE allowlist_id = NEW.allowlist_id;
    IF already_approved + NEW.approved_quantity > parent_cap THEN
        RAISE EXCEPTION 'MARKING_GRANDFATHER_APPROVAL_EXCEEDS_ALLOWLIST';
    END IF;
    RETURN NEW;
END;
$$;

-- Recreate the V0036 trigger with the strengthened exact-snapshot implementation.
DROP TRIGGER IF EXISTS trg_marking_allowlist_subject_validate
    ON marking_synthetic_legacy_allowlist_subject;
CREATE TRIGGER trg_marking_allowlist_subject_validate
BEFORE INSERT ON marking_synthetic_legacy_allowlist_subject
FOR EACH ROW EXECUTE FUNCTION validate_marking_allowlist_subject_approval();
