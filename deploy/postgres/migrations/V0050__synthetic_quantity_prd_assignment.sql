-- #200: dormant quantity-only authorization primitive. No runtime call sites,
-- no grants, no Close/ledger/status changes and no change to SHADOW.
CREATE FUNCTION marking_synthetic_quantity_assign_prd(
    p_doc_id BIGINT, p_actor TEXT
) RETURNS BIGINT
LANGUAGE plpgsql AS $$
DECLARE
    v_doc docs%ROWTYPE;
    v_order orders%ROWTYPE;
    v_line RECORD;
    v_pool RECORD;
    v_task UUID;
    v_tasks UUID[] := ARRAY[]::UUID[];
    v_needed BIGINT;
    v_take BIGINT;
    v_total BIGINT := 0;
    v_replay BOOLEAN;
BEGIN
    -- Pool rows are immutable: FOR UPDATE does not create a new MVCC version.
    -- A repeatable-read snapshot could therefore overspend after a lock wait.
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'QUANTITY_PRD_READ_COMMITTED_REQUIRED' USING ERRCODE = '25000';
    END IF;
    IF p_doc_id IS NULL OR p_doc_id <= 0
       OR p_actor IS NULL OR BTRIM(p_actor) = '' THEN
        RAISE EXCEPTION 'QUANTITY_PRD_INVALID_REQUEST' USING ERRCODE = '22023';
    END IF;

    SELECT * INTO v_doc FROM docs WHERE id = p_doc_id FOR UPDATE;
    IF NOT FOUND OR v_doc.type <> 'PRODUCTION_RECEIPT'
       OR v_doc.status <> 'DRAFT' OR v_doc.order_id IS NULL THEN
        RAISE EXCEPTION 'QUANTITY_PRD_DRAFT_REQUIRED' USING ERRCODE = '23514';
    END IF;
    SELECT * INTO v_order FROM orders WHERE id = v_doc.order_id FOR SHARE;
    IF NOT FOUND OR v_order.order_type <> 'INTERNAL'
       OR v_order.status NOT IN ('DRAFT', 'ACCEPTED', 'IN_PROGRESS') THEN
        RAISE EXCEPTION 'QUANTITY_PRD_ACTIVE_INTERNAL_REQUIRED' USING ERRCODE = '23514';
    END IF;

    -- doc_lines deliberately has no FK to docs/replaces_line_id. Until all
    -- future runtime writers share a document lock, a row lock cannot stop
    -- insert/supersession phantoms. This dormant primitive conservatively
    -- freezes line mutations, including INSERT, for the caller transaction.
    LOCK TABLE doc_lines IN SHARE MODE;
    PERFORM 1 FROM doc_lines WHERE doc_id = p_doc_id ORDER BY id FOR UPDATE;
    PERFORM 1 FROM order_lines ol
    WHERE ol.id IN (SELECT order_line_id FROM doc_lines WHERE doc_id = p_doc_id)
    ORDER BY ol.id FOR SHARE;
    PERFORM 1 FROM items i
    WHERE i.id IN (SELECT item_id FROM doc_lines WHERE doc_id = p_doc_id)
    ORDER BY i.id FOR SHARE;
    PERFORM 1 FROM item_types it
    WHERE it.id IN (SELECT i.item_type_id FROM items i
                   JOIN doc_lines dl ON dl.item_id = i.id WHERE dl.doc_id = p_doc_id)
    ORDER BY it.id FOR SHARE;

    IF NOT EXISTS (SELECT 1 FROM doc_lines dl WHERE dl.doc_id = p_doc_id
                   AND dl.qty > 0 AND NOT EXISTS
                       (SELECT 1 FROM doc_lines n WHERE n.replaces_line_id = dl.id))
       OR EXISTS (
        SELECT 1 FROM doc_lines dl
        LEFT JOIN items i ON i.id = dl.item_id
        LEFT JOIN item_types it ON it.id = i.item_type_id
        LEFT JOIN order_lines ol ON ol.id = dl.order_line_id
        WHERE dl.doc_id = p_doc_id
          AND NOT EXISTS (SELECT 1 FROM doc_lines n WHERE n.replaces_line_id = dl.id)
          AND (dl.qty <= 0 OR dl.qty::TEXT IN ('NaN', 'Infinity', '-Infinity')
               OR dl.qty::NUMERIC <> TRUNC(dl.qty::NUMERIC)
               OR dl.qty::NUMERIC > 9223372036854775807::NUMERIC
               OR i.id IS NULL OR NOT COALESCE(it.enable_marking, FALSE)
               OR NULLIF(BTRIM(i.gtin), '') IS NULL
               OR (dl.order_line_id IS NOT NULL AND
                   (ol.id IS NULL OR ol.order_id <> v_doc.order_id OR ol.item_id <> dl.item_id)))
    ) THEN
        RAISE EXCEPTION 'QUANTITY_PRD_INVALID_LINES' USING ERRCODE = '23514';
    END IF;

    -- Lock ALL candidate tasks by stable identity before any pool. The FK
    -- marking_code -> marking_order also serializes real/legacy INSERT here.
    -- No SKIP LOCKED: wait and reread balances, never skip a higher priority.
    FOR v_task IN
        SELECT mo.id FROM marking_order mo
        WHERE (mo.source_order_id = v_doc.order_id OR mo.order_id = v_doc.order_id
               OR (mo.source_type = 'PRODUCTION_NEED' AND mo.source_order_id IS NULL))
          AND EXISTS (SELECT 1 FROM doc_lines dl JOIN items i ON i.id = dl.item_id
                      WHERE dl.doc_id = p_doc_id AND dl.qty > 0
                        AND NOT EXISTS (SELECT 1 FROM doc_lines n WHERE n.replaces_line_id = dl.id)
                        AND (mo.item_id = dl.item_id OR BTRIM(mo.gtin) = BTRIM(i.gtin)))
        ORDER BY mo.id FOR UPDATE
    LOOP
        v_tasks := ARRAY_APPEND(v_tasks, v_task);
    END LOOP;
    PERFORM 1 FROM marking_synthetic_quantity_pool p
    WHERE p.marking_order_id = ANY(v_tasks) ORDER BY p.id FOR UPDATE;

    IF EXISTS (SELECT 1 FROM marking_code WHERE marking_order_id = ANY(v_tasks))
       OR EXISTS (SELECT 1 FROM marking_code WHERE receipt_doc_id = p_doc_id
                  OR receipt_line_id IN (SELECT id FROM doc_lines WHERE doc_id = p_doc_id))
       OR EXISTS (SELECT 1 FROM km_code WHERE receipt_doc_id = p_doc_id
                  OR receipt_line_id IN (SELECT id FROM doc_lines WHERE doc_id = p_doc_id)) THEN
        RAISE EXCEPTION 'QUANTITY_PRD_PER_CODE_CONFLICT' USING ERRCODE = '23514';
    END IF;

    IF EXISTS (
        SELECT 1 FROM marking_synthetic_quantity_pool p
        JOIN marking_order mo ON mo.id = p.marking_order_id
        JOIN items i ON i.id = p.item_id
        WHERE p.marking_order_id = ANY(v_tasks)
          AND (mo.status IN ('Cancelled', 'Failed')
               OR p.item_id IS DISTINCT FROM mo.item_id
               OR p.order_line_id IS DISTINCT FROM mo.order_line_id
               OR p.normalized_gtin IS DISTINCT FROM BTRIM(mo.gtin)
               OR p.normalized_gtin IS DISTINCT FROM BTRIM(i.gtin)
               OR p.source_type IS DISTINCT FROM BTRIM(mo.source_type)
               OR p.source_type NOT IN ('PRODUCTION_ORDER', 'PRODUCTION_NEED')
               OR (mo.source_order_id IS NOT NULL AND mo.source_order_id <> v_doc.order_id)
               OR (mo.order_id IS NOT NULL AND mo.order_id <> v_doc.order_id)
               OR (p.source_type = 'PRODUCTION_ORDER' AND
                   mo.source_order_id IS DISTINCT FROM v_doc.order_id))
    ) OR EXISTS (
        SELECT 1 FROM marking_synthetic_quantity_balance b
        WHERE b.marking_order_id = ANY(v_tasks)
          AND (b.issued_quantity <= 0 OR b.available_quantity < 0
               OR b.applied_quantity < 0 OR b.voided_quantity < 0
               OR b.issued_quantity::NUMERIC <> b.available_quantity::NUMERIC
                   + b.applied_quantity::NUMERIC + b.voided_quantity::NUMERIC
               OR b.applied_quantity IS DISTINCT FROM
                  (SELECT COALESCE(SUM(r.applied_quantity), 0)::BIGINT
                   FROM marking_synthetic_quantity_receipt_balance r WHERE r.pool_id = b.pool_id))
    ) OR EXISTS (
        SELECT 1 FROM marking_synthetic_quantity_receipt_balance r
        JOIN marking_synthetic_quantity_pool p ON p.id = r.pool_id
        WHERE p.marking_order_id = ANY(v_tasks) AND r.applied_quantity <= 0
    ) THEN
        RAISE EXCEPTION 'QUANTITY_PRD_INVALID_SOURCE' USING ERRCODE = '23514';
    END IF;

    SELECT EXISTS (SELECT 1 FROM marking_synthetic_quantity_event
                   WHERE receipt_doc_id = p_doc_id OR receipt_line_id IN
                         (SELECT id FROM doc_lines WHERE doc_id = p_doc_id)) INTO v_replay;
    IF v_replay AND EXISTS (
        SELECT 1 FROM marking_synthetic_quantity_event e
        JOIN marking_synthetic_quantity_pool p ON p.id = e.pool_id
        LEFT JOIN doc_lines dl ON dl.id = e.receipt_line_id
        WHERE (e.receipt_doc_id = p_doc_id OR dl.doc_id = p_doc_id)
          AND (e.action <> 'ASSIGN' OR e.receipt_doc_id <> p_doc_id
               OR dl.doc_id IS DISTINCT FROM p_doc_id
               OR dl.qty <= 0 OR EXISTS (SELECT 1 FROM doc_lines n WHERE n.replaces_line_id = dl.id)
               OR NOT (p.marking_order_id = ANY(v_tasks)) OR p.item_id <> dl.item_id
               OR (p.order_line_id IS NOT NULL AND p.order_line_id IS DISTINCT FROM dl.order_line_id)
               OR e.operation_id <> MD5('quantity-prd:v1:' || p_doc_id || ':' || dl.id || ':' || p.id)::UUID)
    ) THEN
        RAISE EXCEPTION 'QUANTITY_PRD_HISTORY_CONFLICT' USING ERRCODE = '23514';
    END IF;

    FOR v_line IN SELECT dl.*, BTRIM(i.gtin) AS gtin FROM doc_lines dl
        JOIN items i ON i.id = dl.item_id
        WHERE dl.doc_id = p_doc_id AND dl.qty > 0
          AND NOT EXISTS (SELECT 1 FROM doc_lines n WHERE n.replaces_line_id = dl.id)
        ORDER BY dl.id
    LOOP
        v_needed := v_line.qty::NUMERIC::BIGINT;
        v_total := v_total + v_needed;
        IF v_replay THEN
            IF v_needed <> (SELECT COALESCE(SUM(applied_quantity), 0)
                            FROM marking_synthetic_quantity_receipt_balance
                            WHERE receipt_doc_id = p_doc_id AND receipt_line_id = v_line.id) THEN
                RAISE EXCEPTION 'QUANTITY_PRD_HISTORY_CONFLICT' USING ERRCODE = '23514';
            END IF;
            CONTINUE;
        END IF;
        FOR v_pool IN
            SELECT b.pool_id, b.available_quantity
            FROM marking_synthetic_quantity_balance b
            JOIN marking_order mo ON mo.id = b.marking_order_id
            WHERE b.marking_order_id = ANY(v_tasks)
              AND b.item_id = v_line.item_id AND b.normalized_gtin = v_line.gtin
              AND (b.order_line_id IS NULL OR b.order_line_id = v_line.order_line_id)
            ORDER BY CASE WHEN mo.source_order_id = v_doc.order_id THEN 0
                          WHEN mo.order_id = v_doc.order_id THEN 1 ELSE 2 END,
                     mo.created_at, mo.id, b.pool_id
        LOOP
            v_take := LEAST(v_needed, v_pool.available_quantity);
            IF v_take > 0 THEN
                PERFORM * FROM marking_synthetic_quantity_apply(
                    MD5('quantity-prd:v1:' || p_doc_id || ':' || v_line.id || ':' || v_pool.pool_id)::UUID,
                    v_pool.pool_id, 'ASSIGN', v_take, NULL, NULL, p_doc_id, v_line.id, BTRIM(p_actor));
                v_needed := v_needed - v_take;
            END IF;
            EXIT WHEN v_needed = 0;
        END LOOP;
        IF v_needed <> 0 THEN
            -- Raising aborts the entire statement, including earlier lines.
            RAISE EXCEPTION 'QUANTITY_PRD_SHORTAGE: line % missing %', v_line.id, v_needed
                USING ERRCODE = '23514';
        END IF;
    END LOOP;
    RETURN v_total;
END;
$$;

REVOKE EXECUTE ON FUNCTION marking_synthetic_quantity_assign_prd(BIGINT, TEXT) FROM PUBLIC;
