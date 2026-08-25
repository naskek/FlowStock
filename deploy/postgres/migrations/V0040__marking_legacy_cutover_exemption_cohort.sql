-- V0040 is schema-only. The immutable cohort snapshot is written by the
-- SERIALIZABLE cutover enforce command after writers have been stopped.

CREATE TABLE marking_legacy_cutover_cohort (
    id BOOLEAN PRIMARY KEY DEFAULT TRUE CHECK (id),
    cutover_state_id BOOLEAN NOT NULL REFERENCES marking_cutover_state(id) ON DELETE RESTRICT,
    snapshot_schema_version INTEGER NOT NULL CHECK (snapshot_schema_version = 1),
    preflight_hash TEXT NOT NULL,
    snapshot_hash TEXT NOT NULL UNIQUE,
    captured_by TEXT NOT NULL,
    captured_at TEXT NOT NULL
);

CREATE TABLE marking_legacy_cutover_line_scope (
    id BIGSERIAL PRIMARY KEY,
    cohort_id BOOLEAN NOT NULL REFERENCES marking_legacy_cutover_cohort(id) ON DELETE RESTRICT,
    order_id BIGINT NOT NULL REFERENCES orders(id) ON DELETE RESTRICT,
    order_line_id BIGINT NOT NULL UNIQUE REFERENCES order_lines(id) ON DELETE RESTRICT,
    order_type TEXT NOT NULL,
    marking_responsibility TEXT NOT NULL,
    line_revision BIGINT NOT NULL,
    item_id_snapshot BIGINT NOT NULL REFERENCES items(id) ON DELETE RESTRICT,
    gtin_snapshot TEXT,
    frozen_quantity NUMERIC(18,6) NOT NULL CHECK (frozen_quantity > 0),
    shipped_quantity_at_cutover NUMERIC(18,6) NOT NULL CHECK (shipped_quantity_at_cutover >= 0),
    frozen_unshipped_legacy_quantity NUMERIC(18,6) NOT NULL
        CHECK (frozen_unshipped_legacy_quantity >= 0),
    production_need_snapshot NUMERIC(18,6) NOT NULL CHECK (production_need_snapshot >= 0),
    snapshot_hash TEXT NOT NULL UNIQUE,
    captured_by TEXT NOT NULL,
    captured_at TEXT NOT NULL,
    CHECK (shipped_quantity_at_cutover <= frozen_quantity),
    CHECK (frozen_unshipped_legacy_quantity = frozen_quantity - shipped_quantity_at_cutover),
    CHECK (
        (order_type = 'CUSTOMER')
        OR (order_type = 'INTERNAL' AND shipped_quantity_at_cutover = 0)
    )
);

CREATE INDEX ix_marking_legacy_cutover_line_scope_order
    ON marking_legacy_cutover_line_scope(order_id, order_line_id);

CREATE TABLE marking_legacy_cutover_subject_exemption (
    id UUID PRIMARY KEY,
    frozen_line_scope_id BIGINT NOT NULL
        REFERENCES marking_legacy_cutover_line_scope(id) ON DELETE RESTRICT,
    marking_subject_id UUID NOT NULL UNIQUE
        REFERENCES marking_production_subject(id) ON DELETE RESTRICT,
    item_id_snapshot BIGINT NOT NULL REFERENCES items(id) ON DELETE RESTRICT,
    gtin_snapshot TEXT NOT NULL,
    subject_revision_snapshot BIGINT NOT NULL,
    subject_quantity_snapshot NUMERIC(18,6) NOT NULL CHECK (subject_quantity_snapshot >= 0),
    granted_quantity NUMERIC(18,6) NOT NULL CHECK (granted_quantity > 0),
    active_quantity NUMERIC(18,6) NOT NULL CHECK (active_quantity >= 0),
    basis TEXT NOT NULL CHECK (basis IN ('CUTOVER_EXISTING', 'POST_CUTOVER_FROZEN_LINE_PLAN')),
    root_subject_id UUID NOT NULL REFERENCES marking_production_subject(id) ON DELETE RESTRICT,
    predecessor_subject_id UUID NULL REFERENCES marking_production_subject(id) ON DELETE RESTRICT,
    allocation_hash TEXT NOT NULL UNIQUE,
    granted_by TEXT NOT NULL,
    granted_at TEXT NOT NULL,
    changed_by TEXT,
    changed_at TEXT,
    change_reason TEXT,
    CHECK (NULLIF(BTRIM(gtin_snapshot), '') IS NOT NULL),
    CHECK (active_quantity <= granted_quantity),
    CHECK (granted_quantity <= subject_quantity_snapshot)
);

CREATE INDEX ix_marking_legacy_cutover_subject_exemption_scope
    ON marking_legacy_cutover_subject_exemption(frozen_line_scope_id, marking_subject_id);

CREATE TABLE marking_outbound_fulfillment_attribution (
    id UUID PRIMARY KEY,
    outbound_doc_id BIGINT NOT NULL REFERENCES docs(id) ON DELETE RESTRICT,
    outbound_doc_line_id BIGINT NOT NULL UNIQUE REFERENCES doc_lines(id) ON DELETE RESTRICT,
    order_id BIGINT NOT NULL REFERENCES orders(id) ON DELETE RESTRICT,
    order_line_id BIGINT NOT NULL REFERENCES order_lines(id) ON DELETE RESTRICT,
    item_id BIGINT NOT NULL REFERENCES items(id) ON DELETE RESTRICT,
    hu_id BIGINT NULL REFERENCES hus(id) ON DELETE RESTRICT,
    normalized_hu TEXT NOT NULL,
    quantity NUMERIC(18,6) NOT NULL CHECK (quantity > 0),
    basis TEXT NOT NULL CHECK (basis IN ('LEGACY_EXEMPT', 'REAL_READY')),
    frozen_line_scope_id BIGINT NULL
        REFERENCES marking_legacy_cutover_line_scope(id) ON DELETE RESTRICT,
    real_ready_hu_fact_id UUID NULL REFERENCES marking_ready_hu_fact(id) ON DELETE RESTRICT,
    real_eligibility_decision_hash TEXT NOT NULL,
    decided_by TEXT NOT NULL,
    decided_at TEXT NOT NULL,
    UNIQUE(outbound_doc_id, normalized_hu),
    CHECK (NULLIF(BTRIM(normalized_hu), '') IS NOT NULL),
    CHECK (
        (basis = 'LEGACY_EXEMPT'
         AND frozen_line_scope_id IS NOT NULL
         AND real_ready_hu_fact_id IS NULL)
        OR
        (basis = 'REAL_READY'
         AND frozen_line_scope_id IS NULL
         AND real_ready_hu_fact_id IS NOT NULL)
    )
);

CREATE INDEX ix_marking_outbound_attribution_legacy_scope
    ON marking_outbound_fulfillment_attribution(frozen_line_scope_id, basis);

-- Compatibility projection. Legacy exemption reduces the quantity that requires
-- real marking, but never contributes to real coverage or APPLIED by itself.
CREATE OR REPLACE FUNCTION calculate_order_marking_status(candidate_order_id BIGINT)
RETURNS TEXT LANGUAGE sql STABLE AS $$
WITH applicable_lines AS (
    SELECT line.id,
           line.qty_ordered,
           NULLIF(BTRIM(item.gtin), '') AS gtin,
           GREATEST(
               0,
               line.qty_ordered - LEAST(
                   line.qty_ordered,
                   COALESCE(scope.frozen_quantity, 0)
               )
           ) AS real_required_quantity
    FROM order_lines line
    INNER JOIN items item ON item.id = line.item_id
    INNER JOIN item_types item_type ON item_type.id = item.item_type_id
    LEFT JOIN marking_legacy_cutover_line_scope scope ON scope.order_line_id = line.id
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
           AND coverage.source_type = 'REAL_IMPORT'
    INNER JOIN marking_operational_coverage_consumption consumption
            ON consumption.operational_coverage_id = coverage.id
           AND consumption.active_quantity > 0
    WHERE subject.current_order_line_id IN (SELECT id FROM applicable_lines)
      AND subject.lifecycle IN ('ACTIVE', 'COMPLETED')
      AND NOT EXISTS (
          SELECT 1 FROM marking_ready_hu_fact fact
          WHERE fact.marking_subject_id = subject.id
            AND fact.reversed_at IS NULL
            AND fact.provenance = 'REAL_IMPORT'
      )
    GROUP BY subject.current_order_line_id
), ledger_stock AS (
    SELECT item_id, UPPER(BTRIM(COALESCE(hu_code, hu))) AS hu_code, SUM(qty_delta) AS quantity
    FROM ledger
    WHERE NULLIF(BTRIM(COALESCE(hu_code, hu)), '') IS NOT NULL
    GROUP BY item_id, UPPER(BTRIM(COALESCE(hu_code, hu)))
    HAVING SUM(qty_delta) > 0.000001
), ready_fact AS (
    SELECT item_id_snapshot AS item_id,
           UPPER(BTRIM(hu_code_snapshot)) AS hu_code,
           SUM(marked_quantity) AS marked_quantity
    FROM marking_ready_hu_fact
    WHERE reversed_at IS NULL AND provenance = 'REAL_IMPORT'
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
    WHERE plan.qty_planned > 0 AND NULLIF(BTRIM(plan.to_hu), '') IS NOT NULL
    GROUP BY plan.order_line_id
), real_shipped AS (
    SELECT order_line_id, SUM(quantity) AS covered_quantity
    FROM marking_outbound_fulfillment_attribution
    WHERE order_id = candidate_order_id AND basis = 'REAL_READY'
    GROUP BY order_line_id
), summary AS (
    SELECT COALESCE(SUM(line.real_required_quantity), 0) AS real_required_quantity,
           COALESCE(BOOL_AND(
               line.real_required_quantity <= 0.000001
               OR (
                   line.gtin IS NOT NULL
                   AND COALESCE(operational.covered_quantity, 0)
                       + COALESCE(ready.covered_quantity, 0)
                       + COALESCE(real_shipped.covered_quantity, 0)
                       + 0.000001 >= line.real_required_quantity
               )
           ), TRUE) AS fully_covered
    FROM applicable_lines line
    LEFT JOIN operational ON operational.order_line_id = line.id
    LEFT JOIN ready ON ready.order_line_id = line.id
    LEFT JOIN real_shipped ON real_shipped.order_line_id = line.id
)
SELECT CASE
    WHEN real_required_quantity <= 0.000001 THEN 'NOT_REQUIRED'
    WHEN fully_covered THEN 'APPLIED'
    ELSE 'NOT_APPLIED'
END
FROM summary;
$$;

CREATE OR REPLACE FUNCTION prevent_marking_legacy_cutover_snapshot_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'MARKING_LEGACY_CUTOVER_SNAPSHOT_IMMUTABLE';
END;
$$;

CREATE OR REPLACE FUNCTION rebalance_marking_legacy_line_exemptions(
    candidate_order_line_id BIGINT,
    changed_at_value TEXT,
    change_reason_value TEXT)
RETURNS VOID LANGUAGE plpgsql AS $$
DECLARE
    scope_cap NUMERIC(18,6);
    subject_row RECORD;
BEGIN
    SELECT LEAST(scope.frozen_quantity,
                 GREATEST(0, CASE WHEN line.cancelled_at IS NULL THEN line.qty_ordered ELSE 0 END))
    INTO scope_cap
    FROM marking_legacy_cutover_line_scope scope
    INNER JOIN order_lines line ON line.id = scope.order_line_id
    WHERE scope.order_line_id = candidate_order_line_id
    FOR UPDATE OF scope, line;

    IF scope_cap IS NULL THEN
        RETURN;
    END IF;

    PERFORM exemption.id
    FROM marking_legacy_cutover_subject_exemption exemption
    INNER JOIN marking_legacy_cutover_line_scope scope ON scope.id = exemption.frozen_line_scope_id
    WHERE scope.order_line_id = candidate_order_line_id
    ORDER BY exemption.marking_subject_id
    FOR UPDATE OF exemption;

    -- Retain the active permission of every still-valid stable subject. A subject
    -- can only release its own quantity through lifecycle/quantity mutation; a
    -- newly created independent subject never steals an existing allocation.
    WITH bounded AS (
        SELECT exemption.id,
               CASE
                   WHEN subject.lifecycle = 'ACTIVE'
                    AND subject.current_order_line_id = candidate_order_line_id
                   THEN LEAST(exemption.active_quantity,
                              exemption.granted_quantity,
                              subject.subject_quantity)
                   ELSE 0
               END AS bounded_quantity
        FROM marking_legacy_cutover_subject_exemption exemption
        INNER JOIN marking_legacy_cutover_line_scope scope ON scope.id = exemption.frozen_line_scope_id
        INNER JOIN marking_production_subject subject ON subject.id = exemption.marking_subject_id
        WHERE scope.order_line_id = candidate_order_line_id
    )
    UPDATE marking_legacy_cutover_subject_exemption exemption
    SET active_quantity = bounded.bounded_quantity,
        changed_at = changed_at_value,
        changed_by = 'SERVER:legacy-exemption-rebalance',
        change_reason = change_reason_value
    FROM bounded
    WHERE exemption.id = bounded.id
      AND exemption.active_quantity IS DISTINCT FROM bounded.bounded_quantity;

    -- A line-level decrease can temporarily make retained allocations exceed the
    -- effective frozen cap before component triggers finish. Preserve cutover and
    -- canonical correction lineage first, then later independent plans. Production
    -- identity snapshots, never UUID ordering, define this exceptional trim order.
    WITH retained AS (
        SELECT exemption.id,
               exemption.active_quantity,
               COALESCE(SUM(exemption.active_quantity) OVER (
                   ORDER BY
                       CASE exemption.basis
                           WHEN 'CUTOVER_EXISTING' THEN 0
                           ELSE CASE WHEN exemption.predecessor_subject_id IS NOT NULL THEN 1 ELSE 2 END
                       END,
                       subject.created_at,
                       COALESCE(subject.original_component_id, subject.current_component_id),
                       COALESCE(subject.original_production_pallet_id, subject.current_production_pallet_id),
                       COALESCE(subject.original_doc_line_id, subject.current_doc_line_id)
                   ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS preceding_quantity
        FROM marking_legacy_cutover_subject_exemption exemption
        INNER JOIN marking_legacy_cutover_line_scope scope ON scope.id = exemption.frozen_line_scope_id
        INNER JOIN marking_production_subject subject ON subject.id = exemption.marking_subject_id
        WHERE scope.order_line_id = candidate_order_line_id
          AND exemption.active_quantity > 0
    )
    UPDATE marking_legacy_cutover_subject_exemption exemption
    SET active_quantity = LEAST(retained.active_quantity,
                                GREATEST(0, scope_cap - retained.preceding_quantity)),
        changed_at = changed_at_value,
        changed_by = 'SERVER:legacy-exemption-rebalance',
        change_reason = change_reason_value
    FROM retained
    WHERE exemption.id = retained.id
      AND exemption.active_quantity IS DISTINCT FROM
          LEAST(retained.active_quantity,
                GREATEST(0, scope_cap - retained.preceding_quantity));

    -- Restore an existing subject only inside its immutable grant after a prior
    -- safe trim. CUTOVER_EXISTING and canonical predecessor lineage retain
    -- priority over independent post-cutover plans; UUID never participates.
    WITH capacity AS (
        SELECT GREATEST(0, scope_cap - COALESCE(SUM(exemption.active_quantity), 0)) AS available_quantity
        FROM marking_legacy_cutover_subject_exemption exemption
        INNER JOIN marking_legacy_cutover_line_scope scope ON scope.id = exemption.frozen_line_scope_id
        WHERE scope.order_line_id = candidate_order_line_id
    ), eligible AS (
        SELECT exemption.id,
               LEAST(exemption.granted_quantity, subject.subject_quantity)
                   - exemption.active_quantity AS restorable_quantity,
               CASE exemption.basis
                   WHEN 'CUTOVER_EXISTING' THEN 0
                   ELSE CASE WHEN exemption.predecessor_subject_id IS NOT NULL THEN 1 ELSE 2 END
               END AS allocation_priority,
               subject.created_at,
               COALESCE(subject.original_component_id, subject.current_component_id) AS component_id,
               COALESCE(subject.original_production_pallet_id, subject.current_production_pallet_id) AS pallet_id,
               COALESCE(subject.original_doc_line_id, subject.current_doc_line_id) AS doc_line_id
        FROM marking_legacy_cutover_subject_exemption exemption
        INNER JOIN marking_legacy_cutover_line_scope scope ON scope.id = exemption.frozen_line_scope_id
        INNER JOIN marking_production_subject subject ON subject.id = exemption.marking_subject_id
        WHERE scope.order_line_id = candidate_order_line_id
          AND subject.lifecycle = 'ACTIVE'
          AND subject.current_order_line_id = candidate_order_line_id
          AND exemption.active_quantity < LEAST(exemption.granted_quantity, subject.subject_quantity)
    ), ranked AS (
        SELECT eligible.*,
               COALESCE(SUM(restorable_quantity) OVER (
                   ORDER BY allocation_priority, created_at, component_id, pallet_id, doc_line_id
                   ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS preceding_quantity
        FROM eligible
    )
    UPDATE marking_legacy_cutover_subject_exemption exemption
    SET active_quantity = exemption.active_quantity
                          + LEAST(ranked.restorable_quantity,
                                  GREATEST(0, capacity.available_quantity - ranked.preceding_quantity)),
        changed_at = changed_at_value,
        changed_by = 'SERVER:legacy-exemption-rebalance',
        change_reason = change_reason_value
    FROM ranked CROSS JOIN capacity
    WHERE exemption.id = ranked.id
      AND capacity.available_quantity > ranked.preceding_quantity;

    -- Grant a new stable subject only the frozen capacity left after every
    -- existing active allocation/restoration. The immutable grant equals the
    -- actually allocated quantity, so an unrelated subject cannot consume a
    -- later release that belongs to a canonical replacement.
    WITH RECURSIVE subject_lineage AS (
        SELECT subject.id AS subject_id,
               subject.id AS ancestor_id,
               subject.predecessor_subject_id,
               0 AS depth
        FROM marking_production_subject subject
        WHERE subject.current_order_line_id = candidate_order_line_id
          AND subject.lifecycle = 'ACTIVE'

        UNION ALL

        SELECT lineage.subject_id,
               predecessor.id,
               predecessor.predecessor_subject_id,
               lineage.depth + 1
        FROM subject_lineage lineage
        INNER JOIN marking_production_subject predecessor
                ON predecessor.id = lineage.predecessor_subject_id
    ), subject_roots AS (
        SELECT DISTINCT ON (subject_id)
               subject_id,
               ancestor_id AS root_subject_id
        FROM subject_lineage
        ORDER BY subject_id, depth DESC
    ), capacity AS (
        SELECT GREATEST(0, scope_cap - COALESCE(SUM(exemption.active_quantity), 0)) AS available_quantity
        FROM marking_legacy_cutover_subject_exemption exemption
        INNER JOIN marking_legacy_cutover_line_scope scope ON scope.id = exemption.frozen_line_scope_id
        WHERE scope.order_line_id = candidate_order_line_id
    ), candidates AS (
        SELECT scope.id AS frozen_line_scope_id,
               subject.id AS subject_id,
               subject.item_id,
               subject.gtin,
               subject.revision,
               subject.subject_quantity,
               subject.predecessor_subject_id,
               roots.root_subject_id,
               CASE WHEN subject.predecessor_subject_id IS NOT NULL THEN 0 ELSE 1 END AS allocation_priority,
               subject.created_at,
               COALESCE(subject.original_component_id, subject.current_component_id) AS component_id,
               COALESCE(subject.original_production_pallet_id, subject.current_production_pallet_id) AS pallet_id,
               COALESCE(subject.original_doc_line_id, subject.current_doc_line_id) AS doc_line_id
        FROM marking_legacy_cutover_line_scope scope
        INNER JOIN marking_production_subject subject
                ON subject.current_order_line_id = scope.order_line_id
               AND subject.lifecycle = 'ACTIVE'
        INNER JOIN subject_roots roots ON roots.subject_id = subject.id
        WHERE scope.order_line_id = candidate_order_line_id
          AND subject.subject_quantity > 0
          AND subject.item_id = scope.item_id_snapshot
          AND subject.gtin = scope.gtin_snapshot
          AND NOT EXISTS (
              SELECT 1
              FROM marking_legacy_cutover_subject_exemption existing
              WHERE existing.marking_subject_id = subject.id
          )
    ), ranked AS (
        SELECT candidates.*,
               COALESCE(SUM(subject_quantity) OVER (
                   ORDER BY allocation_priority, created_at, component_id, pallet_id, doc_line_id
                   ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS preceding_quantity
        FROM candidates
    ), allocated AS (
        SELECT ranked.*,
               LEAST(ranked.subject_quantity,
                     GREATEST(0, capacity.available_quantity - ranked.preceding_quantity)) AS allocated_quantity
        FROM ranked CROSS JOIN capacity
    )
    INSERT INTO marking_legacy_cutover_subject_exemption(
        id, frozen_line_scope_id, marking_subject_id, item_id_snapshot,
        gtin_snapshot, subject_revision_snapshot, subject_quantity_snapshot,
        granted_quantity, active_quantity, basis, root_subject_id,
        predecessor_subject_id, allocation_hash, granted_by, granted_at)
    SELECT (md5('v0040-subject-exemption:' || allocated.subject_id::text))::uuid,
           allocated.frozen_line_scope_id,
           allocated.subject_id,
           allocated.item_id,
           allocated.gtin,
           allocated.revision,
           allocated.subject_quantity,
           allocated.allocated_quantity,
           allocated.allocated_quantity,
           'POST_CUTOVER_FROZEN_LINE_PLAN',
           allocated.root_subject_id,
           allocated.predecessor_subject_id,
           md5('v0040-subject-exemption:' || allocated.frozen_line_scope_id::text || ':' || allocated.subject_id::text),
           'SERVER:legacy-exemption-rebalance',
           changed_at_value
    FROM allocated
    WHERE allocated.allocated_quantity > 0
    ON CONFLICT (marking_subject_id) DO NOTHING;

    FOR subject_row IN
        SELECT subject.id,
               subject.subject_quantity,
               COALESCE(exemption.active_quantity, 0) AS legacy_quantity
        FROM marking_production_subject subject
        LEFT JOIN marking_legacy_cutover_subject_exemption exemption
               ON exemption.marking_subject_id = subject.id
        WHERE subject.current_order_line_id = candidate_order_line_id
    LOOP
        PERFORM cap_marking_subject_consumables(
            subject_row.id,
            GREATEST(0, subject_row.subject_quantity - subject_row.legacy_quantity),
            changed_at_value,
            change_reason_value);
    END LOOP;

    UPDATE orders order_row
    SET marking_status = calculate_order_marking_status(order_row.id)
    WHERE order_row.id = (SELECT order_id FROM order_lines WHERE id = candidate_order_line_id);
END;
$$;

CREATE OR REPLACE FUNCTION sync_marking_legacy_exemption_after_component_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    affected_line_id BIGINT;
BEGIN
    affected_line_id := CASE WHEN TG_OP = 'DELETE' THEN OLD.order_line_id ELSE NEW.order_line_id END;
    IF affected_line_id IS NOT NULL THEN
        PERFORM rebalance_marking_legacy_line_exemptions(
            affected_line_id,
            to_char(NOW() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"'),
            CASE WHEN TG_OP = 'DELETE' THEN 'subject_cancelled' ELSE 'subject_quantity_changed' END);
    END IF;
    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER zz_marking_legacy_exemption_component_sync
AFTER INSERT OR UPDATE OF planned_qty, order_line_id, marking_subject_id OR DELETE
ON production_pallet_lines
FOR EACH ROW EXECUTE FUNCTION sync_marking_legacy_exemption_after_component_mutation();

CREATE OR REPLACE FUNCTION sync_marking_legacy_exemption_after_line_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM rebalance_marking_legacy_line_exemptions(
        NEW.id,
        to_char(NOW() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"'),
        CASE WHEN NEW.cancelled_at IS NOT NULL THEN 'line_cancelled' ELSE 'line_quantity_changed' END);
    RETURN NEW;
END;
$$;

CREATE TRIGGER zz_marking_legacy_exemption_line_sync
AFTER UPDATE OF qty_ordered, cancelled_at ON order_lines
FOR EACH ROW EXECUTE FUNCTION sync_marking_legacy_exemption_after_line_mutation();

CREATE OR REPLACE FUNCTION sync_marking_legacy_exemption_after_pallet_lifecycle()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    line_id_value BIGINT;
BEGIN
    IF NEW.status IS NOT DISTINCT FROM OLD.status THEN
        RETURN NEW;
    END IF;
    FOR line_id_value IN
        SELECT DISTINCT component.order_line_id
        FROM production_pallet_lines component
        WHERE component.production_pallet_id = NEW.id
          AND component.order_line_id IS NOT NULL
        ORDER BY component.order_line_id
    LOOP
        PERFORM rebalance_marking_legacy_line_exemptions(
            line_id_value,
            to_char(NOW() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"'),
            'pallet_lifecycle_changed');
    END LOOP;
    RETURN NEW;
END;
$$;

CREATE TRIGGER zz_marking_legacy_exemption_pallet_sync
AFTER UPDATE OF status ON production_pallets
FOR EACH ROW EXECUTE FUNCTION sync_marking_legacy_exemption_after_pallet_lifecycle();

CREATE TRIGGER trg_marking_legacy_cutover_cohort_immutable
BEFORE UPDATE OR DELETE ON marking_legacy_cutover_cohort
FOR EACH ROW EXECUTE FUNCTION prevent_marking_legacy_cutover_snapshot_mutation();

CREATE TRIGGER trg_marking_legacy_cutover_line_scope_immutable
BEFORE UPDATE OR DELETE ON marking_legacy_cutover_line_scope
FOR EACH ROW EXECUTE FUNCTION prevent_marking_legacy_cutover_snapshot_mutation();

CREATE OR REPLACE FUNCTION guard_marking_legacy_subject_exemption_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'MARKING_LEGACY_SUBJECT_EXEMPTION_DELETE_FORBIDDEN';
    END IF;

    IF NEW.id IS DISTINCT FROM OLD.id
       OR NEW.frozen_line_scope_id IS DISTINCT FROM OLD.frozen_line_scope_id
       OR NEW.marking_subject_id IS DISTINCT FROM OLD.marking_subject_id
       OR NEW.item_id_snapshot IS DISTINCT FROM OLD.item_id_snapshot
       OR NEW.gtin_snapshot IS DISTINCT FROM OLD.gtin_snapshot
       OR NEW.subject_revision_snapshot IS DISTINCT FROM OLD.subject_revision_snapshot
       OR NEW.subject_quantity_snapshot IS DISTINCT FROM OLD.subject_quantity_snapshot
       OR NEW.granted_quantity IS DISTINCT FROM OLD.granted_quantity
       OR NEW.basis IS DISTINCT FROM OLD.basis
       OR NEW.root_subject_id IS DISTINCT FROM OLD.root_subject_id
       OR NEW.predecessor_subject_id IS DISTINCT FROM OLD.predecessor_subject_id
       OR NEW.allocation_hash IS DISTINCT FROM OLD.allocation_hash
       OR NEW.granted_by IS DISTINCT FROM OLD.granted_by
       OR NEW.granted_at IS DISTINCT FROM OLD.granted_at THEN
        RAISE EXCEPTION 'MARKING_LEGACY_SUBJECT_EXEMPTION_PROVENANCE_IMMUTABLE';
    END IF;

    IF NEW.active_quantity > NEW.granted_quantity THEN
        RAISE EXCEPTION 'MARKING_LEGACY_SUBJECT_EXEMPTION_CAP_EXCEEDED';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_marking_legacy_subject_exemption_guard
BEFORE UPDATE OR DELETE ON marking_legacy_cutover_subject_exemption
FOR EACH ROW EXECUTE FUNCTION guard_marking_legacy_subject_exemption_mutation();

CREATE TRIGGER trg_marking_outbound_attribution_immutable
BEFORE UPDATE OR DELETE ON marking_outbound_fulfillment_attribution
FOR EACH ROW EXECUTE FUNCTION prevent_marking_legacy_cutover_snapshot_mutation();

CREATE OR REPLACE FUNCTION validate_marking_outbound_attribution_closed_document()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM docs document
        WHERE document.id = NEW.outbound_doc_id
          AND document.type = 'OUTBOUND'
          AND document.status = 'CLOSED'
    ) THEN
        RAISE EXCEPTION 'MARKING_OUTBOUND_ATTRIBUTION_REQUIRES_CLOSED_DOCUMENT';
    END IF;
    IF NOT EXISTS (
        SELECT 1
        FROM doc_lines line
        WHERE line.id = NEW.outbound_doc_line_id
          AND line.doc_id = NEW.outbound_doc_id
          AND line.order_line_id = NEW.order_line_id
          AND line.item_id = NEW.item_id
          AND line.qty = NEW.quantity
          AND UPPER(BTRIM(line.from_hu)) = NEW.normalized_hu
    ) THEN
        RAISE EXCEPTION 'MARKING_OUTBOUND_ATTRIBUTION_LINE_MISMATCH';
    END IF;
    RETURN NULL;
END;
$$;

CREATE CONSTRAINT TRIGGER trg_marking_outbound_attribution_closed_document
AFTER INSERT ON marking_outbound_fulfillment_attribution
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION validate_marking_outbound_attribution_closed_document();

CREATE OR REPLACE FUNCTION prevent_obsolete_marking_semantics_after_cutover()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    current_state TEXT;
BEGIN
    SELECT state INTO current_state FROM marking_cutover_state WHERE id = TRUE;
    IF current_state <> 'ENFORCED'
       OR NOT EXISTS (SELECT 1 FROM marking_legacy_cutover_cohort WHERE id = TRUE) THEN
        RETURN NEW;
    END IF;

    IF TG_TABLE_NAME = 'marking_code' THEN
        IF COALESCE(NEW.origin, '') <> 'RealImport' THEN
            RAISE EXCEPTION 'MARKING_SYNTHETIC_CUTOVER_WORKFLOW_OBSOLETE';
        END IF;
    ELSIF TG_TABLE_NAME = 'marking_operational_coverage' THEN
        IF COALESCE(NEW.source_type, '') <> 'REAL_IMPORT' THEN
            RAISE EXCEPTION 'MARKING_SYNTHETIC_CUTOVER_WORKFLOW_OBSOLETE';
        END IF;
    ELSIF TG_TABLE_NAME = 'marking_ready_hu_fact' THEN
        IF COALESCE(NEW.provenance, '') <> 'REAL_IMPORT' THEN
            RAISE EXCEPTION 'MARKING_SYNTHETIC_CUTOVER_WORKFLOW_OBSOLETE';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_marking_code_real_only_after_cutover
BEFORE INSERT ON marking_code
FOR EACH ROW EXECUTE FUNCTION prevent_obsolete_marking_semantics_after_cutover();

CREATE TRIGGER trg_marking_coverage_real_only_after_cutover
BEFORE INSERT ON marking_operational_coverage
FOR EACH ROW EXECUTE FUNCTION prevent_obsolete_marking_semantics_after_cutover();

CREATE TRIGGER trg_marking_ready_fact_real_only_after_cutover
BEFORE INSERT ON marking_ready_hu_fact
FOR EACH ROW EXECUTE FUNCTION prevent_obsolete_marking_semantics_after_cutover();
