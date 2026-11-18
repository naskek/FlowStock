-- #203: explicit isolation boundary for the opt-in storage primitive.
-- V0048 remains unchanged. Replace its function in place, retaining its
-- signature/owner/ACL and all operation semantics under READ COMMITTED.
-- V0050 is reserved by the separate PRD-assignment branch (#200).
-- No history rewrite, activation, retry or fallback.
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
          AND (p.order_line_id IS NULL OR
               dl.order_line_id = p.order_line_id)
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
