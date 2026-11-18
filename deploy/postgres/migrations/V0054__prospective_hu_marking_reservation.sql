-- #218: opt-in future INTERNAL -> CUSTOMER HU marking allocation.
-- HISTORICAL transfers are not enabled. No data migration/backfill and no
-- runtime activation: the producer must explicitly mark its fresh transfer.
ALTER TABLE order_coverage_transfers
    ADD COLUMN hu_marking_allocation_version TEXT NULL
    CHECK (hu_marking_allocation_version IS NULL OR hu_marking_allocation_version = 'v1');

CREATE FUNCTION marking_hu_allocation_version_immutable() RETURNS TRIGGER
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.hu_marking_allocation_version IS DISTINCT FROM OLD.hu_marking_allocation_version THEN
        RAISE EXCEPTION 'HU_MARKING_RETROACTIVE_ALLOCATION_FORBIDDEN' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER trg_marking_hu_allocation_version_immutable
    BEFORE UPDATE ON order_coverage_transfers
    FOR EACH ROW EXECUTE FUNCTION marking_hu_allocation_version_immutable();

CREATE TABLE marking_hu_reservation (
    id BIGSERIAL PRIMARY KEY,
    transfer_line_id BIGINT NOT NULL
        REFERENCES order_coverage_transfer_lines(id) ON DELETE RESTRICT,
    marking_order_id UUID NOT NULL REFERENCES marking_order(id) ON DELETE RESTRICT,
    marking_code_id UUID NULL REFERENCES marking_code(id) ON DELETE RESTRICT,
    pool_id UUID NULL REFERENCES marking_synthetic_quantity_pool(id) ON DELETE RESTRICT,
    quantity BIGINT NOT NULL CHECK (quantity > 0),
    state TEXT NOT NULL DEFAULT 'ACTIVE' CHECK (state IN ('ACTIVE','RELEASED')),
    created_actor TEXT NOT NULL CHECK (BTRIM(created_actor) <> ''),
    released_actor TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    released_at TIMESTAMPTZ NULL,
    CHECK ((marking_code_id IS NULL) <> (pool_id IS NULL)),
    CHECK (marking_code_id IS NULL OR quantity = 1),
    CHECK ((state = 'ACTIVE' AND released_at IS NULL AND released_actor IS NULL)
        OR (state = 'RELEASED' AND released_at IS NOT NULL
            AND BTRIM(COALESCE(released_actor,'')) <> ''))
);
CREATE UNIQUE INDEX ux_marking_hu_reservation_code_active
    ON marking_hu_reservation(marking_code_id)
    WHERE state = 'ACTIVE' AND marking_code_id IS NOT NULL;
CREATE UNIQUE INDEX ux_marking_hu_reservation_pool_line_active
    ON marking_hu_reservation(transfer_line_id, pool_id)
    WHERE state = 'ACTIVE' AND pool_id IS NOT NULL;
CREATE INDEX ix_marking_hu_reservation_transfer
    ON marking_hu_reservation(transfer_line_id, state);
CREATE INDEX ix_marking_hu_reservation_pool_active
    ON marking_hu_reservation(pool_id) WHERE state = 'ACTIVE';

CREATE TABLE marking_hu_reservation_event (
    id BIGSERIAL PRIMARY KEY,
    reservation_id BIGINT NOT NULL REFERENCES marking_hu_reservation(id) ON DELETE RESTRICT,
    action TEXT NOT NULL CHECK (action IN ('RESERVE','RETURN_PLAN')),
    quantity BIGINT NOT NULL CHECK (quantity > 0),
    actor TEXT NOT NULL CHECK (BTRIM(actor) <> ''),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (reservation_id, action)
);
CREATE TRIGGER trg_marking_hu_reservation_event_immutable
    BEFORE UPDATE OR DELETE ON marking_hu_reservation_event
    FOR EACH ROW EXECUTE FUNCTION marking_synthetic_quantity_immutable();
CREATE TRIGGER trg_marking_hu_reservation_event_no_truncate
    BEFORE TRUNCATE ON marking_hu_reservation_event
    FOR EACH STATEMENT EXECUTE FUNCTION marking_synthetic_quantity_immutable();

-- Each reservation state change appends its own durable audit row. Direct
-- updates cannot rewrite source/quantity or return without recording actor.
CREATE FUNCTION marking_hu_reservation_audit() RETURNS TRIGGER
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'HU_MARKING_RESERVATION_DELETE_FORBIDDEN' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' THEN
        IF OLD.state <> 'ACTIVE' OR NEW.state <> 'RELEASED'
           OR NEW.transfer_line_id IS DISTINCT FROM OLD.transfer_line_id
           OR NEW.marking_order_id IS DISTINCT FROM OLD.marking_order_id
           OR NEW.marking_code_id IS DISTINCT FROM OLD.marking_code_id
           OR NEW.pool_id IS DISTINCT FROM OLD.pool_id
           OR NEW.quantity IS DISTINCT FROM OLD.quantity
           OR NEW.created_actor IS DISTINCT FROM OLD.created_actor
           OR NEW.created_at IS DISTINCT FROM OLD.created_at
           OR NEW.released_at IS NULL
           OR BTRIM(COALESCE(NEW.released_actor,'')) = '' THEN
            RAISE EXCEPTION 'HU_MARKING_RESERVATION_IMMUTABLE' USING ERRCODE = '23514';
        END IF;
        INSERT INTO marking_hu_reservation_event(
            reservation_id, action, quantity, actor, created_at)
        VALUES(OLD.id, 'RETURN_PLAN', OLD.quantity, NEW.released_actor, NEW.released_at);
        RETURN NEW;
    END IF;
    INSERT INTO marking_hu_reservation_event(
        reservation_id, action, quantity, actor, created_at)
    VALUES(NEW.id, 'RESERVE', NEW.quantity, NEW.created_actor, NEW.created_at);
    RETURN NEW;
END;
$$;
CREATE TRIGGER trg_marking_hu_reservation_no_delete
    BEFORE DELETE ON marking_hu_reservation
    FOR EACH ROW EXECUTE FUNCTION marking_hu_reservation_audit();
CREATE TRIGGER trg_marking_hu_reservation_event_insert
    AFTER INSERT ON marking_hu_reservation
    FOR EACH ROW EXECUTE FUNCTION marking_hu_reservation_audit();
CREATE TRIGGER trg_marking_hu_reservation_event_return
    AFTER UPDATE ON marking_hu_reservation
    FOR EACH ROW EXECUTE FUNCTION marking_hu_reservation_audit();
CREATE TRIGGER trg_marking_hu_reservation_no_truncate
    BEFORE TRUNCATE ON marking_hu_reservation
    FOR EACH STATEMENT EXECUTE FUNCTION marking_synthetic_quantity_immutable();

-- A source receipt cannot consume or void reserved codes. Locks on source
-- code serialize a transfer with an in-flight legacy PRD code assignment.
CREATE FUNCTION marking_hu_guard_code_consumption() RETURNS TRIGGER
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF EXISTS (SELECT 1 FROM marking_hu_reservation r
                   WHERE r.marking_code_id = OLD.id AND r.state = 'ACTIVE') THEN
            RAISE EXCEPTION 'HU_MARKING_SOURCE_CODE_RESERVED' USING ERRCODE = '23514';
        END IF;
        RETURN OLD;
    END IF;
    IF EXISTS (SELECT 1 FROM marking_hu_reservation r
               WHERE r.marking_code_id = OLD.id AND r.state = 'ACTIVE')
       AND (NEW.status IS DISTINCT FROM OLD.status
         OR NEW.receipt_doc_id IS DISTINCT FROM OLD.receipt_doc_id
         OR NEW.receipt_line_id IS DISTINCT FROM OLD.receipt_line_id
         OR NEW.marking_order_id IS DISTINCT FROM OLD.marking_order_id
         OR NEW.gtin IS DISTINCT FROM OLD.gtin
         OR NEW.origin IS DISTINCT FROM OLD.origin
         OR NEW.import_id IS DISTINCT FROM OLD.import_id) THEN
        RAISE EXCEPTION 'HU_MARKING_SOURCE_CODE_RESERVED' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER trg_marking_hu_guard_code_update
    BEFORE UPDATE OR DELETE ON marking_code
    FOR EACH ROW EXECUTE FUNCTION marking_hu_guard_code_consumption();

-- Quantitative source pools remain immutable. The canonical apply writer
-- locks the pool before inserting an event; this guard also locks it when
-- events are inserted directly. ASSIGN/VOID may spend only *unreserved* free
-- balance, and can never silently spend adopted HU quantity.
CREATE FUNCTION marking_hu_guard_quantity_consumption() RETURNS TRIGGER
LANGUAGE plpgsql AS $$
DECLARE
    v_reserved BIGINT;
    v_free BIGINT;
BEGIN
    IF NEW.action NOT IN ('ASSIGN', 'VOID') THEN RETURN NEW; END IF;
    PERFORM 1 FROM marking_synthetic_quantity_pool p
      WHERE p.id = NEW.pool_id FOR UPDATE;
    SELECT COALESCE(SUM(r.quantity),0)::BIGINT INTO v_reserved
      FROM marking_hu_reservation r
      WHERE r.pool_id = NEW.pool_id AND r.state = 'ACTIVE';
    IF v_reserved > 0 THEN
        SELECT available_quantity INTO v_free
          FROM marking_synthetic_quantity_balance WHERE pool_id = NEW.pool_id;
        IF v_free - NEW.quantity < v_reserved THEN
            RAISE EXCEPTION 'HU_MARKING_SOURCE_QUANTITY_RESERVED'
                USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER trg_marking_hu_guard_quantity_event
    BEFORE INSERT ON marking_synthetic_quantity_event
    FOR EACH ROW EXECUTE FUNCTION marking_hu_guard_quantity_consumption();

-- Always invoked inside the same READ COMMITTED transfer transaction, after
-- ownership/doc_line mutation. The function does NOT infer proof from PRINTED.
-- The first invocation may reserve part of a transfer line (not enough source
-- code is never a reason to invent the missing coverage). Further invocations
-- return the same reserved amount, without opportunistic top-up.
CREATE FUNCTION marking_hu_reserve_transfer(p_transfer_id BIGINT, p_actor TEXT)
RETURNS TABLE(code_reserved BIGINT, quantity_reserved BIGINT)
LANGUAGE plpgsql AS $$
DECLARE
    v_t order_coverage_transfers%ROWTYPE;
    v_line RECORD;
    v_task RECORD;
    v_pool RECORD;
    v_code RECORD;
    v_remaining BIGINT;
    v_previous BIGINT;
    v_free BIGINT;
    v_reserved BIGINT;
    v_take BIGINT;
    v_codes BIGINT := 0;
    v_quantity BIGINT := 0;
    v_gtin TEXT;
    v_line_qty BIGINT;
BEGIN
    IF current_setting('transaction_isolation') <> 'read committed'
       OR p_transfer_id IS NULL OR p_transfer_id <= 0
       OR BTRIM(COALESCE(p_actor,'')) = '' THEN
        RAISE EXCEPTION 'HU_MARKING_INVALID_TRANSACTION' USING ERRCODE = '25000';
    END IF;

    SELECT * INTO v_t FROM order_coverage_transfers
        WHERE id = p_transfer_id FOR UPDATE;
    IF NOT FOUND OR v_t.hu_marking_allocation_version IS DISTINCT FROM 'v1'
       OR v_t.compensation_kind IS NOT NULL
       OR v_t.transfer_type <> 'PLANNED_PALLET_ADOPTION' THEN
        RAISE EXCEPTION 'HU_MARKING_NOT_PROSPECTIVE' USING ERRCODE = '23514';
    END IF;
    -- The transferred HU must still be an empty planned customer HU. No
    -- legacy/historical transfer is eligible for retrospective allocation.
    PERFORM 1 FROM production_pallets pp
        JOIN docs d ON d.id = pp.prd_doc_id
        WHERE pp.id = v_t.production_pallet_id
          AND pp.hu_code = v_t.hu_code
          AND pp.order_id = v_t.target_order_id
          AND pp.prd_doc_id = v_t.target_prd_doc_id
          AND pp.status IN ('PLANNED','PRINTED') AND pp.filled_at IS NULL
          AND d.order_id = v_t.target_order_id
          AND d.status = 'DRAFT' AND d.type = 'PRODUCTION_RECEIPT'
          AND NOT EXISTS (
              SELECT 1 FROM production_pallet_lines pll
              WHERE pll.production_pallet_id = pp.id
                AND (pll.filled_at IS NOT NULL OR pll.filled_qty > 0))
        FOR SHARE OF pp, d;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'HU_MARKING_TRANSFER_STALE' USING ERRCODE = '23514';
    END IF;

    FOR v_line IN
        SELECT l.*, i.gtin AS current_gtin, it.enable_marking,
               dl.id AS live_doc_line_id, dl.doc_id AS live_doc_id,
               dl.item_id AS live_item, dl.order_line_id AS live_order_line,
               dl.qty AS live_qty, ol.item_id AS target_item, ol.order_id AS target_order
        FROM order_coverage_transfer_lines l
        JOIN items i ON i.id = l.item_id
        JOIN item_types it ON it.id = i.item_type_id
        LEFT JOIN doc_lines dl ON dl.id = l.source_doc_line_id
        LEFT JOIN order_lines ol ON ol.id = l.target_order_line_id
        WHERE l.transfer_id = v_t.id ORDER BY l.id
    LOOP
        IF v_line.live_doc_line_id IS NULL
           OR v_line.live_doc_id <> v_t.target_prd_doc_id
           OR v_line.live_item <> v_line.item_id
           OR v_line.live_order_line <> v_line.target_order_line_id
           OR v_line.target_item IS DISTINCT FROM v_line.item_id
           OR v_line.target_order IS DISTINCT FROM v_t.target_order_id
           OR ABS(v_line.live_qty - v_line.transferred_qty) > 0.000001
           OR v_line.transferred_qty <= 0
           OR ABS(v_line.transferred_qty - ROUND(v_line.transferred_qty)) > 0.000001
           OR NOT EXISTS (
             SELECT 1 FROM production_pallet_lines pll
             WHERE pll.production_pallet_id = v_t.production_pallet_id
               AND pll.doc_line_id = v_line.source_doc_line_id
               AND pll.order_line_id = v_line.target_order_line_id
               AND pll.item_id = v_line.item_id
               AND ABS(pll.planned_qty - v_line.transferred_qty) <= 0.000001
               AND pll.filled_qty = 0 AND pll.filled_at IS NULL
           ) THEN
            RAISE EXCEPTION 'HU_MARKING_TRANSFER_LINE_STALE' USING ERRCODE = '23514';
        END IF;
        IF NOT COALESCE(v_line.enable_marking,FALSE) THEN CONTINUE; END IF;
        v_gtin := NULLIF(BTRIM(v_line.current_gtin),'');
        IF v_gtin IS NULL THEN
            RAISE EXCEPTION 'HU_MARKING_MISSING_GTIN' USING ERRCODE = '23514';
        END IF;
        v_line_qty := ROUND(v_line.transferred_qty)::BIGINT;
        v_remaining := v_line_qty;

        SELECT COUNT(*)::BIGINT INTO v_previous FROM marking_hu_reservation
          WHERE transfer_line_id = v_line.id;
        IF v_previous > 0 THEN
            IF EXISTS (SELECT 1 FROM marking_hu_reservation
                       WHERE transfer_line_id = v_line.id AND state <> 'ACTIVE') THEN
                RAISE EXCEPTION 'HU_MARKING_RELEASED_REPLAY' USING ERRCODE = '23514';
            END IF;
            SELECT COALESCE(SUM(quantity),0)::BIGINT INTO v_previous
              FROM marking_hu_reservation WHERE transfer_line_id = v_line.id;
            IF v_previous > v_line_qty THEN
                RAISE EXCEPTION 'HU_MARKING_OVERRESERVED' USING ERRCODE = '23514';
            END IF;
            -- Existing reservation amounts are never increased on replay.
            SELECT v_codes + COALESCE(SUM(quantity),0)::BIGINT INTO v_codes
              FROM marking_hu_reservation
              WHERE transfer_line_id = v_line.id AND marking_code_id IS NOT NULL;
            SELECT v_quantity + COALESCE(SUM(quantity),0)::BIGINT INTO v_quantity
              FROM marking_hu_reservation
              WHERE transfer_line_id = v_line.id AND pool_id IS NOT NULL;
            CONTINUE;
        END IF;

        -- Serialise source-task writer and other transfer reservations for
        -- this item/order/line. Source tasks are never reassigned to CUSTOMER.
        FOR v_task IN
            SELECT mo.* FROM marking_order mo
            WHERE mo.source_type = 'PRODUCTION_ORDER'
              AND mo.source_order_id = v_t.source_order_id
              AND (mo.order_id IS NULL OR mo.order_id = v_t.source_order_id)
              AND mo.item_id = v_line.item_id
              AND BTRIM(mo.gtin) = v_gtin
              AND (mo.order_line_id IS NULL
                  OR mo.order_line_id = v_line.source_order_line_id)
              AND mo.status NOT IN ('Cancelled','Failed')
            ORDER BY mo.id FOR UPDATE
        LOOP
            EXIT WHEN v_remaining = 0;
            -- A task cannot authorize both per-code and quantity. Historical
            -- Voided/Quarantined code rows also block mixed provenance.
            IF EXISTS (SELECT 1 FROM marking_code c
                       JOIN marking_synthetic_quantity_pool p ON p.marking_order_id = c.marking_order_id
                       WHERE c.marking_order_id = v_task.id) THEN
                RAISE EXCEPTION 'HU_MARKING_MIXED_TASK' USING ERRCODE = '23514';
            END IF;

            -- Source task may have different per-code imports. Unknown origin
            -- cannot be used to fabricate historical KM proof.
            FOR v_code IN
                SELECT c.id
                FROM marking_code c
                JOIN marking_code_import imp ON imp.id = c.import_id
                WHERE c.marking_order_id = v_task.id
                  AND c.status IN ('Reserved','Printed')
                  AND c.receipt_doc_id IS NULL AND c.receipt_line_id IS NULL
                  AND NULLIF(BTRIM(c.gtin),'') = v_gtin
                  AND (
                    (c.code LIKE 'TEMP-CHZ-%' AND imp.source_type = 'temporary-chz-export'
                     AND c.origin IN ('LegacySynthetic','HistoricalUnknown'))
                    OR (c.code NOT LIKE 'TEMP-CHZ-%' AND imp.source_type IN ('csv','tsv')
                     AND c.origin IN ('RealImport','LegacyRealImport'))
                  )
                  AND NOT EXISTS (
                    SELECT 1 FROM marking_hu_reservation r
                    WHERE r.marking_code_id = c.id AND r.state = 'ACTIVE'
                  )
                ORDER BY c.id LIMIT v_remaining FOR UPDATE OF c
            LOOP
                EXIT WHEN v_remaining = 0;
                INSERT INTO marking_hu_reservation(
                    transfer_line_id, marking_order_id, marking_code_id,
                    quantity, created_actor)
                VALUES (v_line.id, v_task.id, v_code.id, 1, p_actor);
                v_remaining := v_remaining - 1;
                v_codes := v_codes + 1;
            END LOOP;

            FOR v_pool IN
                SELECT p.id, b.available_quantity FROM marking_synthetic_quantity_pool p
                JOIN marking_synthetic_quantity_balance b ON b.pool_id = p.id
                WHERE p.marking_order_id = v_task.id
                  AND p.item_id = v_line.item_id
                  AND p.order_line_id IS NOT DISTINCT FROM v_task.order_line_id
                  AND p.normalized_gtin = v_gtin
                  AND p.source_type = 'PRODUCTION_ORDER'
                  AND p.source_scope = JSONB_BUILD_OBJECT(
                      'source_type', BTRIM(v_task.source_type),
                      'source_order_id', v_task.source_order_id, 'order_id', v_task.order_id)
                  AND b.available_quantity > 0 AND b.applied_quantity >= 0
                  AND b.voided_quantity >= 0
                  AND b.issued_quantity = b.available_quantity
                                            + b.applied_quantity + b.voided_quantity
                ORDER BY p.id FOR UPDATE OF p
            LOOP
                EXIT WHEN v_remaining = 0;
                SELECT available_quantity INTO v_free
                  FROM marking_synthetic_quantity_balance WHERE pool_id = v_pool.id;
                SELECT COALESCE(SUM(quantity),0)::BIGINT INTO v_reserved
                  FROM marking_hu_reservation
                  WHERE pool_id = v_pool.id AND state = 'ACTIVE';
                v_take := LEAST(v_remaining, GREATEST(0, v_free - v_reserved));
                IF v_take > 0 THEN
                    INSERT INTO marking_hu_reservation(
                        transfer_line_id, marking_order_id, pool_id,
                        quantity, created_actor)
                    VALUES (v_line.id, v_task.id, v_pool.id, v_take, p_actor);
                    v_remaining := v_remaining - v_take;
                    v_quantity := v_quantity + v_take;
                END IF;
            END LOOP;
        END LOOP;
        -- Shortage is an authentic zero/partial allocation. The consumer must
        -- request missing units from CUSTOMER and never infer PRINTED coverage.
    END LOOP;
    IF NOT EXISTS (SELECT 1 FROM order_coverage_transfer_lines
                   WHERE transfer_id = v_t.id) THEN
        RAISE EXCEPTION 'HU_MARKING_NO_TRANSFER_LINES' USING ERRCODE = '23514';
    END IF;
    RETURN QUERY SELECT v_codes, v_quantity;
END;
$$;

-- RETURN_PLAN must run inside the same business transaction, AFTER validated
-- pallet/doc-line ownership is moved back and transfer marked compensated.
CREATE FUNCTION marking_hu_return_transfer(p_transfer_id BIGINT, p_actor TEXT)
RETURNS BIGINT LANGUAGE plpgsql AS $$
DECLARE
    v_t order_coverage_transfers%ROWTYPE;
    v_total BIGINT;
BEGIN
    IF current_setting('transaction_isolation') <> 'read committed'
       OR BTRIM(COALESCE(p_actor,'')) = '' THEN
        RAISE EXCEPTION 'HU_MARKING_INVALID_TRANSACTION' USING ERRCODE = '25000';
    END IF;
    SELECT * INTO v_t FROM order_coverage_transfers WHERE id = p_transfer_id FOR UPDATE;
    IF NOT FOUND OR v_t.hu_marking_allocation_version IS DISTINCT FROM 'v1'
       OR v_t.compensation_kind IS DISTINCT FROM 'RETURN_PLAN' THEN
        RAISE EXCEPTION 'HU_MARKING_RETURN_NOT_COMPENSATED' USING ERRCODE = '23514';
    END IF;
    -- Only a completed safe RETURN_PLAN may free an exclusive allocation.
    -- The canonical compensation can create a replacement source DRAFT PRD,
    -- therefore current source PRD identity need not equal its old snapshot.
    PERFORM 1 FROM production_pallets pp
      JOIN docs d ON d.id = pp.prd_doc_id
      WHERE pp.id = v_t.production_pallet_id
        AND pp.order_id = v_t.source_order_id
        AND pp.status = 'PLANNED' AND pp.filled_at IS NULL
        AND d.order_id = v_t.source_order_id
        AND d.type = 'PRODUCTION_RECEIPT' AND d.status = 'DRAFT'
        AND NOT EXISTS (
          SELECT 1 FROM production_pallet_lines pll
          WHERE pll.production_pallet_id = pp.id
            AND (pll.filled_at IS NOT NULL OR pll.filled_qty > 0))
      FOR SHARE OF pp,d;
    IF NOT FOUND OR EXISTS (
        SELECT 1 FROM order_coverage_transfer_lines tl
        LEFT JOIN doc_lines dl ON dl.id = tl.source_doc_line_id
        LEFT JOIN production_pallets pp ON pp.id = v_t.production_pallet_id
        LEFT JOIN order_lines restored ON restored.id = dl.order_line_id
        WHERE tl.transfer_id = v_t.id
          AND (dl.id IS NULL OR pp.id IS NULL
               OR dl.doc_id IS DISTINCT FROM pp.prd_doc_id
               OR dl.item_id IS DISTINCT FROM tl.item_id
               OR restored.order_id IS DISTINCT FROM v_t.source_order_id
               OR restored.item_id IS DISTINCT FROM tl.item_id
               OR ABS(dl.qty - tl.transferred_qty) > 0.000001
               OR NOT EXISTS (
                   SELECT 1 FROM production_pallet_lines pll
                   WHERE pll.production_pallet_id = pp.id
                     AND pll.doc_line_id = dl.id
                     AND pll.order_line_id = restored.id
                     AND pll.item_id = tl.item_id
                     AND ABS(pll.planned_qty - tl.transferred_qty) <= 0.000001
                     AND pll.filled_qty = 0 AND pll.filled_at IS NULL
               ))
    ) THEN
        RAISE EXCEPTION 'HU_MARKING_RETURN_OWNERSHIP_NOT_RESTORED' USING ERRCODE = '23514';
    END IF;
    SELECT COALESCE(SUM(r.quantity),0)::BIGINT INTO v_total
      FROM marking_hu_reservation r
      JOIN order_coverage_transfer_lines tl ON tl.id = r.transfer_line_id
      WHERE tl.transfer_id = v_t.id AND r.state = 'ACTIVE';
    UPDATE marking_hu_reservation r
    SET state = 'RELEASED', released_at = NOW(), released_actor = p_actor
    FROM order_coverage_transfer_lines tl
    WHERE tl.id = r.transfer_line_id
      AND tl.transfer_id = v_t.id AND r.state = 'ACTIVE';
    RETURN v_total;
END;
$$;
REVOKE ALL ON FUNCTION marking_hu_reserve_transfer(BIGINT,TEXT) FROM PUBLIC;
REVOKE ALL ON FUNCTION marking_hu_return_transfer(BIGINT,TEXT) FROM PUBLIC;
