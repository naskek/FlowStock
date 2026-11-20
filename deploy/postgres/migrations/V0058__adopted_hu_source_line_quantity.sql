-- Неактивный quantity consumer: source order-line сохраняется в pool,
-- target receipt принимается только по exact CONSUMED HU reservation.
CREATE OR REPLACE FUNCTION marking_hu_reserve_transfer(p_transfer_id BIGINT, p_actor TEXT)
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
    v_attempt marking_hu_allocation_attempt%ROWTYPE;
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
       OR v_t.hu_marking_allocation_xid IS DISTINCT FROM pg_current_xact_id()
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
        LEFT JOIN item_types it ON it.id = i.item_type_id
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
        v_gtin := NULLIF(BTRIM(v_line.current_gtin),'');
        IF COALESCE(v_line.enable_marking,FALSE) AND v_gtin IS NULL THEN
            RAISE EXCEPTION 'HU_MARKING_MISSING_GTIN' USING ERRCODE = '23514';
        END IF;
        v_line_qty := ROUND(v_line.transferred_qty)::BIGINT;
        v_remaining := v_line_qty;

        SELECT * INTO v_attempt FROM marking_hu_allocation_attempt
          WHERE transfer_line_id = v_line.id;
        IF FOUND THEN
            IF v_attempt.item_id <> v_line.item_id
               OR v_attempt.normalized_gtin IS DISTINCT FROM v_gtin
               OR v_attempt.planned_quantity <> v_line_qty
               OR v_attempt.markable IS DISTINCT FROM COALESCE(v_line.enable_marking,FALSE) THEN
                RAISE EXCEPTION 'HU_MARKING_REPLAY_SCOPE_CHANGED' USING ERRCODE = '23514';
            END IF;
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

        INSERT INTO marking_hu_allocation_attempt(
            transfer_line_id, item_id, normalized_gtin, markable,
            planned_quantity, actor)
        VALUES (v_line.id, v_line.item_id, v_gtin,
            COALESCE(v_line.enable_marking,FALSE), v_line_qty, p_actor);

        IF NOT COALESCE(v_line.enable_marking,FALSE) THEN CONTINUE; END IF;
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
                  -- Transfer snapshot retains the source line; consumption
                  -- requires exact target receipt proof rather than reassignment.
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
                IF v_free < 0 OR v_reserved > v_free THEN
                    RAISE EXCEPTION 'HU_MARKING_SOURCE_QUANTITY_INVALID'
                        USING ERRCODE = '23514';
                END IF;
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

CREATE OR REPLACE FUNCTION marking_synthetic_quantity_apply(
    p_operation_id UUID,
    p_pool_id UUID,
    p_action TEXT,
    p_quantity BIGINT,
    p_marking_order_id UUID,
    p_source_key TEXT,
    p_receipt_doc_id BIGINT,
    p_receipt_line_id BIGINT,
    p_actor TEXT
) RETURNS TABLE (
    issued_quantity BIGINT,
    available_quantity BIGINT,
    applied_quantity BIGINT,
    voided_quantity BIGINT,
    revision BIGINT
)
LANGUAGE plpgsql AS $$
DECLARE
    v_pool marking_synthetic_quantity_pool%ROWTYPE;
    v_task RECORD;
    v_previous marking_synthetic_quantity_event%ROWTYPE;
    v_free BIGINT;
    v_line_applied BIGINT;
BEGIN
    -- A waiting pool lock does not refresh an RR/SERIALIZABLE snapshot:
    -- the pool is immutable, so SELECT FOR UPDATE cannot force a 40001.
    -- Reject BEFORE validation, replay, locks or writes, including ISSUE.
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'synthetic quantity apply requires READ COMMITTED'
            USING ERRCODE = '25001';
    END IF;

    IF p_operation_id IS NULL OR p_pool_id IS NULL OR p_quantity IS NULL
       OR p_quantity <= 0 OR p_action IS NULL
       OR p_action NOT IN ('ISSUE', 'ASSIGN', 'RELEASE', 'VOID')
       OR p_actor IS NULL OR BTRIM(p_actor) = '' THEN
        RAISE EXCEPTION 'invalid synthetic quantity operation'
            USING ERRCODE = '22023';
    END IF;
    IF (p_action IN ('ISSUE', 'VOID') AND
        (p_receipt_doc_id IS NOT NULL OR p_receipt_line_id IS NOT NULL))
       OR (p_action IN ('ASSIGN', 'RELEASE') AND
           (COALESCE(p_receipt_doc_id, 0) <= 0 OR
            COALESCE(p_receipt_line_id, 0) <= 0)) THEN
        RAISE EXCEPTION 'invalid receipt scope for synthetic quantity operation'
            USING ERRCODE = '22023';
    END IF;
    IF (p_action = 'ISSUE' AND
        (p_marking_order_id IS NULL OR p_source_key IS NULL OR
         BTRIM(p_source_key) = '' OR BTRIM(p_source_key) <> p_source_key))
       OR (p_action <> 'ISSUE' AND
           (p_marking_order_id IS NOT NULL OR p_source_key IS NOT NULL)) THEN
        RAISE EXCEPTION 'invalid issue provenance'
            USING ERRCODE = '22023';
    END IF;

    -- Fast replay, including after a receipt is closed or task cancelled.
    -- Compare all operation identity fields, not operation_id alone.
    SELECT * INTO v_previous
    FROM marking_synthetic_quantity_event WHERE operation_id = p_operation_id;
    IF FOUND THEN
        SELECT * INTO STRICT v_pool
        FROM marking_synthetic_quantity_pool WHERE id = v_previous.pool_id;
        IF v_previous.pool_id IS DISTINCT FROM p_pool_id
           OR v_previous.action IS DISTINCT FROM p_action
           OR v_previous.quantity IS DISTINCT FROM p_quantity
           OR v_previous.receipt_doc_id IS DISTINCT FROM p_receipt_doc_id
           OR v_previous.receipt_line_id IS DISTINCT FROM p_receipt_line_id
           OR v_previous.actor IS DISTINCT FROM p_actor
           OR (p_action = 'ISSUE' AND
               (v_pool.marking_order_id IS DISTINCT FROM p_marking_order_id
                OR v_pool.source_key IS DISTINCT FROM p_source_key)) THEN
            RAISE EXCEPTION 'operation_id reused with a different payload'
                USING ERRCODE = '23505';
        END IF;
        RETURN QUERY SELECT b.issued_quantity, b.available_quantity,
                            b.applied_quantity, b.voided_quantity, b.revision
                     FROM marking_synthetic_quantity_balance b
                     WHERE b.pool_id = p_pool_id;
        RETURN;
    END IF;

    -- For assignment, take doc/line SHARE locks BEFORE pool lock:
    -- an in-flight document close/update must not invalidate our evidence.
    IF p_action = 'ASSIGN' THEN
        PERFORM 1
        FROM docs d
        INNER JOIN doc_lines dl ON dl.doc_id = d.id
        INNER JOIN marking_synthetic_quantity_pool p ON p.id = p_pool_id
        WHERE d.id = p_receipt_doc_id
          AND dl.id = p_receipt_line_id
          AND d.type = 'PRODUCTION_RECEIPT' AND d.status = 'DRAFT'
          AND dl.qty > 0 AND dl.item_id = p.item_id
          AND (p.order_line_id IS NULL OR dl.order_line_id=p.order_line_id
               OR EXISTS (
                 SELECT 1 FROM marking_hu_current_reservation r
                 JOIN marking_hu_current_transfer_line scope ON scope.id=r.transfer_line_id
                 JOIN order_coverage_transfers t ON t.id=scope.transfer_id
                 JOIN marking_order mo ON mo.id=r.marking_order_id
                 WHERE r.pool_id=p.id AND r.state='CONSUMED' AND r.quantity=p_quantity
                   AND r.consumed_doc_id=d.id AND r.consumed_line_id=dl.id
                   AND scope.current_line_id=dl.id AND scope.source_order_line_id=p.order_line_id
                   AND scope.target_order_line_id=dl.order_line_id AND scope.item_id=dl.item_id
                   AND t.target_order_id=d.order_id AND t.compensation_kind IS NULL
                   AND t.hu_marking_allocation_version='v1' AND mo.source_order_id=t.source_order_id
                   AND mo.order_line_id=p.order_line_id AND mo.item_id=p.item_id
                   AND BTRIM(mo.gtin)=p.normalized_gtin))
          AND NOT EXISTS (
              SELECT 1 FROM doc_lines newer WHERE newer.replaces_line_id = dl.id
          )
        FOR SHARE OF d, dl;
        IF NOT FOUND THEN
            RAISE EXCEPTION 'receipt line is not a matching active draft PRD line'
                USING ERRCODE = '23514';
        END IF;
    END IF;

    -- Lock source task/item BEFORE pool: both the metadata and eligibility
    -- remain valid until commit. Avoid pool -> order lock inversion.
    IF p_action = 'ISSUE' THEN
        SELECT mo.id, mo.order_line_id, mo.item_id,
               BTRIM(mo.gtin) AS normalized_gtin,
               BTRIM(mo.source_type) AS source_type
        INTO v_task
        FROM marking_order mo
        INNER JOIN items i ON i.id = mo.item_id
        INNER JOIN item_types it ON it.id = i.item_type_id
        WHERE mo.id = p_marking_order_id
          AND mo.status NOT IN ('Cancelled', 'Failed')
          AND COALESCE(it.enable_marking, FALSE)
          AND NULLIF(BTRIM(mo.gtin), '') IS NOT NULL
          AND NULLIF(BTRIM(i.gtin), '') IS NOT NULL
          AND BTRIM(mo.gtin) = BTRIM(i.gtin)
          AND NULLIF(BTRIM(mo.source_type), '') IS NOT NULL
        FOR SHARE OF mo, i, it;
        IF NOT FOUND THEN
            RAISE EXCEPTION 'cannot issue for ineligible marking task'
                USING ERRCODE = '23514';
        END IF;

        INSERT INTO marking_synthetic_quantity_pool (
            id, marking_order_id, order_line_id, item_id,
            normalized_gtin, source_type, source_key
        )
        SELECT p_pool_id, mo.id, mo.order_line_id, mo.item_id,
               BTRIM(mo.gtin), BTRIM(mo.source_type), p_source_key
        FROM marking_order mo
        INNER JOIN items i ON i.id = mo.item_id
        INNER JOIN item_types it ON it.id = i.item_type_id
        WHERE mo.id = p_marking_order_id
          AND mo.status NOT IN ('Cancelled', 'Failed')
          AND COALESCE(it.enable_marking, FALSE) = TRUE
          AND NULLIF(BTRIM(mo.gtin), '') IS NOT NULL
          AND NULLIF(BTRIM(i.gtin), '') IS NOT NULL
          AND BTRIM(mo.gtin) = BTRIM(i.gtin)
          AND NULLIF(BTRIM(mo.source_type), '') IS NOT NULL
        ON CONFLICT (id) DO NOTHING;
    END IF;

    SELECT * INTO v_pool FROM marking_synthetic_quantity_pool
    WHERE id = p_pool_id FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'synthetic quantity pool not found or task ineligible'
            USING ERRCODE = '23514';
    END IF;
    -- A PL/pgSQL RECORD without a query result is unassigned. Explicit
    -- branching (not AND short-circuit) is essential for ASSIGN/RELEASE/VOID.
    IF p_action = 'ISSUE' THEN
        IF v_pool.marking_order_id IS DISTINCT FROM p_marking_order_id
           OR v_pool.source_key IS DISTINCT FROM p_source_key
           OR v_pool.item_id IS DISTINCT FROM v_task.item_id
           OR v_pool.order_line_id IS DISTINCT FROM v_task.order_line_id
           OR v_pool.normalized_gtin IS DISTINCT FROM v_task.normalized_gtin
           OR v_pool.source_type IS DISTINCT FROM v_task.source_type THEN
            RAISE EXCEPTION 'issue source does not match pool provenance'
                USING ERRCODE = '23514';
        END IF;
    END IF;

    -- Recheck after the pool lock for an operation committed while we waited.
    SELECT * INTO v_previous
    FROM marking_synthetic_quantity_event WHERE operation_id = p_operation_id;
    IF FOUND THEN
        IF v_previous.pool_id IS DISTINCT FROM p_pool_id
           OR v_previous.action IS DISTINCT FROM p_action
           OR v_previous.quantity IS DISTINCT FROM p_quantity
           OR v_previous.receipt_doc_id IS DISTINCT FROM p_receipt_doc_id
           OR v_previous.receipt_line_id IS DISTINCT FROM p_receipt_line_id
           OR v_previous.actor IS DISTINCT FROM p_actor THEN
            RAISE EXCEPTION 'operation_id reused with a different payload'
                USING ERRCODE = '23505';
        END IF;
        RETURN QUERY SELECT b.issued_quantity, b.available_quantity,
                            b.applied_quantity, b.voided_quantity, b.revision
                     FROM marking_synthetic_quantity_balance b
                     WHERE b.pool_id = p_pool_id;
        RETURN;
    END IF;

    SELECT b.available_quantity INTO v_free
    FROM marking_synthetic_quantity_balance b WHERE b.pool_id = p_pool_id;

    IF p_action IN ('ASSIGN', 'VOID') AND v_free < p_quantity THEN
        RAISE EXCEPTION 'not enough free synthetic quantity'
            USING ERRCODE = '23514';
    END IF;
    IF p_action = 'RELEASE' THEN
        SELECT COALESCE(SUM(CASE WHEN action = 'ASSIGN' THEN quantity
                                 WHEN action = 'RELEASE' THEN -quantity
                                 ELSE 0 END), 0)::BIGINT
        INTO v_line_applied
        FROM marking_synthetic_quantity_event
        WHERE pool_id = p_pool_id
          AND receipt_doc_id = p_receipt_doc_id
          AND receipt_line_id = p_receipt_line_id;
        IF v_line_applied < p_quantity THEN
            RAISE EXCEPTION 'cannot release more than applied to receipt line'
                USING ERRCODE = '23514';
        END IF;
    END IF;

    INSERT INTO marking_synthetic_quantity_event(
        operation_id, pool_id, action, quantity,
        receipt_doc_id, receipt_line_id, actor
    ) VALUES (
        p_operation_id, p_pool_id, p_action, p_quantity,
        p_receipt_doc_id, p_receipt_line_id, p_actor
    );

    RETURN QUERY SELECT b.issued_quantity, b.available_quantity,
                        b.applied_quantity, b.voided_quantity, b.revision
                 FROM marking_synthetic_quantity_balance b
                 WHERE b.pool_id = p_pool_id;
END;
$$;

-- Not an enabled runtime API. Only the migration owner may invoke this
-- primitive until a separate authenticated integration explicitly grants use.
REVOKE EXECUTE ON FUNCTION marking_synthetic_quantity_apply(
    UUID, UUID, TEXT, BIGINT, UUID, TEXT, BIGINT, BIGINT, TEXT
) FROM PUBLIC;
