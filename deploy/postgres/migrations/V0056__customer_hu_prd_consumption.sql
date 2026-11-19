-- #220: dormant CUSTOMER dedicated PRD consumption of exact prospective HU
-- reservations. Must be called by guarded Close in its READ COMMITTED TX.
CREATE FUNCTION marking_hu_consume_prd(p_doc_id BIGINT,p_actor TEXT,p_applied_at TEXT)
RETURNS BIGINT LANGUAGE plpgsql AS $$
DECLARE
    v_doc docs%ROWTYPE;
    r RECORD;
    c RECORD;
    p RECORD;
    v_operation UUID;
    v_total BIGINT := 0;
BEGIN
    IF current_setting('transaction_isolation')<>'read committed'
       OR p_doc_id IS NULL OR BTRIM(COALESCE(p_actor,''))=''
       OR BTRIM(COALESCE(p_applied_at,''))='' THEN
        RAISE EXCEPTION 'HU_MARKING_INVALID_CONSUME_REQUEST' USING ERRCODE='22023';
    END IF;
    SELECT * INTO v_doc FROM docs WHERE id=p_doc_id FOR UPDATE;
    IF NOT FOUND OR v_doc.type<>'PRODUCTION_RECEIPT'
       OR v_doc.status<>'DRAFT' OR v_doc.order_id IS NULL THEN
        RAISE EXCEPTION 'HU_MARKING_DRAFT_PRD_REQUIRED' USING ERRCODE='23514';
    END IF;
    -- All new transfers must have a recorded immutable zero/positive attempt.
    -- Old unallocated transfers are never silently treated as covered.
    IF EXISTS (
        SELECT 1 FROM order_coverage_transfers t
        JOIN order_coverage_transfer_lines tl ON tl.transfer_id=t.id
        JOIN doc_lines dl ON dl.id=tl.source_doc_line_id
        WHERE t.target_order_id=v_doc.order_id AND dl.doc_id=p_doc_id
          AND (t.compensation_kind IS NOT NULL
               OR t.hu_marking_allocation_version IS DISTINCT FROM 'v1'
               OR NOT EXISTS (SELECT 1 FROM marking_hu_allocation_attempt a
                              WHERE a.transfer_line_id=tl.id))
    ) THEN
        RAISE EXCEPTION 'HU_MARKING_UNVERIFIED_TRANSFER' USING ERRCODE='23514';
    END IF;
    FOR r IN
        SELECT mr.*,tl.item_id,tl.target_order_line_id,
               tl.source_order_line_id,tl.source_doc_line_id,
               tl.transferred_qty,t.source_order_id,t.target_order_id,
               t.production_pallet_id,t.hu_code,t.compensation_kind,
               pp.prd_doc_id AS pallet_doc,pp.order_id AS pallet_order,
               pp.status AS pallet_status,pp.hu_code AS pallet_hu,
               dl.doc_id AS current_doc,dl.order_line_id AS current_line,
               dl.item_id AS current_item,dl.qty AS current_qty
        FROM marking_hu_reservation mr
        JOIN order_coverage_transfer_lines tl ON tl.id=mr.transfer_line_id
        JOIN order_coverage_transfers t ON t.id=tl.transfer_id
        JOIN doc_lines dl ON dl.id=tl.source_doc_line_id
        JOIN production_pallets pp ON pp.id=t.production_pallet_id
        WHERE t.target_order_id=v_doc.order_id AND dl.doc_id=p_doc_id
        ORDER BY mr.id FOR UPDATE OF mr NOWAIT
    LOOP
        IF r.compensation_kind IS NOT NULL OR r.current_doc<>p_doc_id
           OR r.current_line IS DISTINCT FROM r.target_order_line_id
           OR r.current_item<>r.item_id
           OR ABS(r.current_qty-r.transferred_qty)>0.000001
           OR r.pallet_doc<>p_doc_id OR r.pallet_order<>r.target_order_id
           OR r.pallet_status<>'FILLED' OR r.pallet_hu<>r.hu_code
           OR NOT EXISTS(
               SELECT 1 FROM marking_order mo
               JOIN items i ON i.id=mo.item_id
               JOIN item_types it ON it.id=i.item_type_id
               WHERE mo.id=r.marking_order_id
                 AND mo.source_type='PRODUCTION_ORDER'
                 AND mo.source_order_id=r.source_order_id
                 AND (mo.order_id IS NULL OR mo.order_id=r.source_order_id)
                 AND (mo.order_line_id IS NULL OR mo.order_line_id=r.source_order_line_id)
                 AND mo.item_id=r.item_id AND BTRIM(mo.gtin)=BTRIM(i.gtin)
                 AND COALESCE(it.enable_marking,FALSE)
                 AND mo.status NOT IN ('Cancelled','Failed')
           ) THEN
            RAISE EXCEPTION 'HU_MARKING_PRD_SCOPE_CONFLICT' USING ERRCODE='23514';
        END IF;
        IF r.state='CONSUMED' THEN
            IF r.consumed_doc_id IS DISTINCT FROM p_doc_id
               OR r.consumed_line_id IS DISTINCT FROM r.source_doc_line_id THEN
                RAISE EXCEPTION 'HU_MARKING_PRD_REPLAY_CONFLICT' USING ERRCODE='23514';
            END IF;
        ELSIF r.state<>'ACTIVE' THEN
            RAISE EXCEPTION 'HU_MARKING_PRD_SOURCE_RELEASED' USING ERRCODE='23514';
        END IF;

        IF r.marking_code_id IS NOT NULL THEN
            SELECT code.*,imp.source_type AS import_source INTO c
            FROM marking_code code JOIN marking_code_import imp ON imp.id=code.import_id
            WHERE code.id=r.marking_code_id FOR UPDATE OF code NOWAIT;
            IF NOT FOUND OR c.marking_order_id IS DISTINCT FROM r.marking_order_id
               OR c.gtin IS DISTINCT FROM (SELECT i.gtin FROM items i WHERE i.id=r.item_id)
               OR NOT COALESCE(
                   (c.code LIKE 'TEMP-CHZ-%' AND c.import_source='temporary-chz-export'
                    AND c.origin IN ('LegacySynthetic','HistoricalUnknown'))
                   OR (c.code NOT LIKE 'TEMP-CHZ-%' AND c.import_source IN ('csv','tsv')
                    AND c.origin IN ('RealImport','LegacyRealImport')),FALSE)
               OR EXISTS(SELECT 1 FROM km_code k WHERE BTRIM(k.code_raw)=BTRIM(c.code)) THEN
                RAISE EXCEPTION 'HU_MARKING_PRD_INVALID_SOURCE_CODE' USING ERRCODE='23514';
            END IF;
            IF r.state='ACTIVE' AND
               (c.status NOT IN ('Reserved','Printed')
                OR c.receipt_doc_id IS NOT NULL OR c.receipt_line_id IS NOT NULL) THEN
                RAISE EXCEPTION 'HU_MARKING_PRD_CODE_ALREADY_USED' USING ERRCODE='23514';
            END IF;
            IF r.state='CONSUMED' AND
               (c.status NOT IN ('Applied','Reported','Circulated')
                OR c.receipt_doc_id<>p_doc_id
                OR c.receipt_line_id<>r.source_doc_line_id) THEN
                RAISE EXCEPTION 'HU_MARKING_PRD_CODE_REPLAY_INVALID' USING ERRCODE='23514';
            END IF;
        ELSE
            SELECT pool.id,pool.item_id,pool.marking_order_id,pool.normalized_gtin,
                   b.available_quantity INTO p
            FROM marking_synthetic_quantity_pool pool
            JOIN marking_synthetic_quantity_balance b ON b.pool_id=pool.id
            WHERE pool.id=r.pool_id FOR UPDATE OF pool NOWAIT;
            IF NOT FOUND OR p.marking_order_id IS DISTINCT FROM r.marking_order_id
               OR p.item_id<>r.item_id
               OR p.normalized_gtin IS DISTINCT FROM
                  (SELECT BTRIM(i.gtin) FROM items i WHERE i.id=r.item_id)
               OR (r.state='ACTIVE' AND p.available_quantity<r.quantity) THEN
                RAISE EXCEPTION 'HU_MARKING_PRD_INVALID_SOURCE_POOL' USING ERRCODE='23514';
            END IF;
            v_operation:=MD5('quantity-prd:v1:'||p_doc_id||':'
                             ||r.source_doc_line_id||':'||p.id)::UUID;
            IF r.state='CONSUMED' AND NOT EXISTS (
                SELECT 1 FROM marking_synthetic_quantity_event e
                WHERE e.operation_id=v_operation AND e.pool_id=p.id
                  AND e.action='ASSIGN' AND e.quantity=r.quantity
                  AND e.receipt_doc_id=p_doc_id
                  AND e.receipt_line_id=r.source_doc_line_id
            ) THEN
                RAISE EXCEPTION 'HU_MARKING_PRD_POOL_REPLAY_INVALID' USING ERRCODE='23514';
            END IF;
        END IF;

        IF r.state='ACTIVE' THEN
            UPDATE marking_hu_reservation
              SET state='CONSUMED',consumed_doc_id=p_doc_id,
                  consumed_line_id=r.source_doc_line_id,
                  consumed_actor=BTRIM(p_actor),consumed_at=NOW()
              WHERE id=r.id AND state='ACTIVE';
            IF r.marking_code_id IS NOT NULL THEN
                UPDATE marking_code SET status='Applied',
                    receipt_doc_id=p_doc_id,receipt_line_id=r.source_doc_line_id,
                    applied_at=COALESCE(applied_at,p_applied_at),
                    updated_at=p_applied_at
                WHERE id=r.marking_code_id;
            ELSE
                PERFORM * FROM marking_synthetic_quantity_apply(
                    v_operation,r.pool_id,'ASSIGN',r.quantity,
                    NULL::UUID,NULL::TEXT,p_doc_id,
                    r.source_doc_line_id,BTRIM(p_actor));
            END IF;
        END IF;
        v_total:=v_total+r.quantity;
    END LOOP;
    RETURN v_total;
END;
$$;
REVOKE EXECUTE ON FUNCTION marking_hu_consume_prd(BIGINT,TEXT,TEXT) FROM PUBLIC;

-- V0052 is replaced below. The entire PRD Close still runs under the
-- existing internal OFF gate; production SHADOW is unchanged.

-- Неактивный hybrid consumer для общей PRD Close транзакции.
-- Публичный runtime gate отсутствует; SHADOW, Excel и HU correction не меняются.
CREATE OR REPLACE FUNCTION marking_assign_prd_close(
    p_doc_id BIGINT, p_actor TEXT, p_applied_at TEXT
) RETURNS BIGINT
LANGUAGE plpgsql AS $$
DECLARE
    v_doc docs%ROWTYPE;
    v_order orders%ROWTYPE;
    v_line RECORD;
    v_pool RECORD;
    v_task UUID;
    v_source RECORD;
    v_code UUID;
    v_bound BIGINT;
    v_tasks UUID[] := ARRAY[]::UUID[];
    v_needed BIGINT;
    v_take BIGINT;
    v_total BIGINT := 0;
    v_adopted BIGINT;
    v_replay BOOLEAN;
BEGIN
    -- Pool rows are immutable: FOR UPDATE does not create a new MVCC version.
    -- A repeatable-read snapshot could therefore overspend after a lock wait.
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'QUANTITY_PRD_READ_COMMITTED_REQUIRED' USING ERRCODE = '25000';
    END IF;
    IF p_doc_id IS NULL OR p_doc_id <= 0
       OR p_actor IS NULL OR BTRIM(p_actor) = '' OR NULLIF(BTRIM(p_applied_at), '') IS NULL THEN
        RAISE EXCEPTION 'QUANTITY_PRD_INVALID_REQUEST' USING ERRCODE = '22023';
    END IF;

    SELECT * INTO v_doc FROM docs WHERE id = p_doc_id FOR UPDATE;
    IF NOT FOUND OR v_doc.type <> 'PRODUCTION_RECEIPT'
       OR v_doc.status <> 'DRAFT' THEN
        RAISE EXCEPTION 'QUANTITY_PRD_DRAFT_REQUIRED' USING ERRCODE = '23514';
    END IF;
    SELECT * INTO v_order FROM orders WHERE id = v_doc.order_id FOR SHARE;
    IF v_doc.order_id IS NOT NULL AND (NOT FOUND OR v_order.order_type NOT IN ('INTERNAL', 'CUSTOMER')
       OR v_order.status NOT IN ('DRAFT', 'ACCEPTED', 'IN_PROGRESS')) THEN
        RAISE EXCEPTION 'QUANTITY_PRD_ACTIVE_ORDER_REQUIRED' USING ERRCODE = '23514';
    END IF;

    -- Conservative phantom protection until every line writer shares a doc lock.
    -- NOWAIT also makes lock upgrades safe for TSD, which inserted/moved lines
    -- earlier in this transaction. Contention aborts the whole fill/Close;
    -- never wait in an upgrade cycle or silently retry a partially applied fill.
    LOCK TABLE doc_lines IN SHARE ROW EXCLUSIVE MODE NOWAIT;
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
               OR (COALESCE(it.enable_marking, FALSE) AND NULLIF(BTRIM(i.gtin), '') IS NOT NULL
                   AND dl.qty::NUMERIC <> TRUNC(dl.qty::NUMERIC))
               OR dl.qty::NUMERIC > 9223372036854775807::NUMERIC
               OR i.id IS NULL
               OR (dl.order_line_id IS NOT NULL AND
                   (ol.id IS NULL OR ol.order_id IS DISTINCT FROM v_doc.order_id OR ol.item_id <> dl.item_id)))
    ) THEN
        RAISE EXCEPTION 'QUANTITY_PRD_INVALID_LINES' USING ERRCODE = '23514';
    END IF;

    -- Lock ALL candidate tasks by stable identity before any pool. The FK
    -- marking_code -> marking_order also serializes real/legacy INSERT here.
    -- No SKIP LOCKED: wait and reread balances, never skip a higher priority.
    FOR v_task IN
        SELECT mo.id FROM marking_order mo
        WHERE (mo.source_order_id = v_doc.order_id OR mo.order_id = v_doc.order_id
               OR (v_order.order_type = 'INTERNAL' AND mo.source_type = 'PRODUCTION_NEED'
                   AND mo.source_order_id IS NULL))
          AND (mo.source_order_id IS NULL OR mo.source_order_id = v_doc.order_id)
          AND (mo.order_id IS NULL OR mo.order_id = v_doc.order_id)
          AND (mo.source_type IN ('PRODUCTION_ORDER', 'PRODUCTION_NEED') OR mo.order_id = v_doc.order_id)
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

    -- A task with any per-code history cannot also authorize quantity, even
    -- when one side is voided. Separate, fully scoped tasks may coexist.
    IF EXISTS (SELECT 1 FROM marking_code c JOIN marking_synthetic_quantity_pool p
               ON p.marking_order_id = c.marking_order_id
               WHERE p.marking_order_id = ANY(v_tasks)) THEN
        RAISE EXCEPTION 'QUANTITY_PRD_PER_CODE_CONFLICT' USING ERRCODE = '23514';
    END IF;

    PERFORM 1 FROM marking_code c WHERE c.marking_order_id = ANY(v_tasks)
        OR c.receipt_doc_id = p_doc_id
        OR c.receipt_line_id IN (SELECT id FROM doc_lines WHERE doc_id = p_doc_id)
        ORDER BY c.id FOR UPDATE;
    PERFORM 1 FROM km_code c WHERE c.receipt_doc_id = p_doc_id
        OR c.receipt_line_id IN (SELECT id FROM doc_lines WHERE doc_id = p_doc_id)
        ORDER BY c.id FOR UPDATE;
    PERFORM 1 FROM km_code_batch b WHERE b.id IN (
        SELECT c.batch_id FROM km_code c WHERE c.receipt_doc_id = p_doc_id
            OR c.receipt_line_id IN (SELECT id FROM doc_lines WHERE doc_id = p_doc_id))
        ORDER BY b.id FOR SHARE;
    PERFORM 1 FROM marking_code_import i WHERE i.id IN (
        SELECT c.import_id FROM marking_code c WHERE c.marking_order_id = ANY(v_tasks)
            OR c.receipt_doc_id = p_doc_id
            OR c.receipt_line_id IN (SELECT id FROM doc_lines WHERE doc_id = p_doc_id))
        ORDER BY i.id FOR SHARE;

    -- Revalidate live evidence under locks; the diagnostic view is not an
    -- authorization. Never infer that HistoricalUnknown is a physical DM.
    IF EXISTS (
        SELECT 1 FROM marking_code c LEFT JOIN marking_code_import i ON i.id = c.import_id
        WHERE ((c.marking_order_id = ANY(v_tasks) AND c.status IN ('Reserved', 'Printed'))
               OR c.receipt_doc_id = p_doc_id
               OR c.receipt_line_id IN (SELECT id FROM doc_lines WHERE doc_id = p_doc_id))
          AND NOT COALESCE(
              (c.code LIKE 'TEMP-CHZ-%' AND i.source_type = 'temporary-chz-export'
               AND c.origin IN ('LegacySynthetic', 'HistoricalUnknown'))
              OR (c.code NOT LIKE 'TEMP-CHZ-%' AND i.source_type IN ('csv', 'tsv')
                  AND c.origin IN ('RealImport', 'LegacyRealImport')), FALSE)
    ) THEN
        RAISE EXCEPTION 'QUANTITY_PRD_UNVERIFIED_PER_CODE' USING ERRCODE = '23514';
    END IF;

    -- Validate existing bindings before counting them. Historical/voided facts
    -- are never reclassified or silently removed to make a receipt close.
    IF EXISTS (
        SELECT 1 FROM marking_code c
        LEFT JOIN doc_lines dl ON dl.id = c.receipt_line_id
        LEFT JOIN items i ON i.id = dl.item_id
        LEFT JOIN marking_order mo ON mo.id = c.marking_order_id
        WHERE (c.receipt_doc_id = p_doc_id OR dl.doc_id = p_doc_id)
          AND (c.receipt_doc_id IS DISTINCT FROM p_doc_id OR dl.doc_id IS DISTINCT FROM p_doc_id
               OR dl.qty <= 0 OR EXISTS (SELECT 1 FROM doc_lines n WHERE n.replaces_line_id = dl.id)
               OR c.status NOT IN ('Applied', 'Reported', 'Circulated')
               OR (NOT COALESCE(mo.id = ANY(v_tasks), FALSE)
                   AND NOT EXISTS(
                       SELECT 1 FROM marking_hu_reservation r
                       WHERE r.marking_code_id=c.id AND r.state='CONSUMED'
                         AND r.consumed_doc_id=p_doc_id AND r.consumed_line_id=dl.id
                   ))
               OR mo.status IN ('Cancelled', 'Failed')
               OR (mo.order_line_id IS NOT NULL AND mo.order_line_id IS DISTINCT FROM dl.order_line_id)
               OR (mo.item_id IS NOT NULL AND mo.item_id IS DISTINCT FROM dl.item_id)
               OR (NULLIF(BTRIM(mo.gtin), '') IS NOT NULL AND BTRIM(mo.gtin) IS DISTINCT FROM BTRIM(i.gtin))
               OR (NULLIF(BTRIM(c.gtin), '') IS NOT NULL AND BTRIM(c.gtin) IS DISTINCT FROM BTRIM(i.gtin)))
    ) OR EXISTS (
        SELECT 1 FROM km_code c LEFT JOIN doc_lines dl ON dl.id = c.receipt_line_id
        LEFT JOIN items i ON i.id = dl.item_id
        LEFT JOIN km_code_batch b ON b.id = c.batch_id
        WHERE (c.receipt_doc_id = p_doc_id OR dl.doc_id = p_doc_id)
          AND (c.receipt_doc_id IS DISTINCT FROM p_doc_id OR dl.doc_id IS DISTINCT FROM p_doc_id
               OR dl.qty <= 0 OR EXISTS (SELECT 1 FROM doc_lines n WHERE n.replaces_line_id = dl.id)
               OR (c.sku_id IS NOT NULL AND c.sku_id IS DISTINCT FROM dl.item_id)
               OR (c.sku_id IS NULL AND NULLIF(BTRIM(c.gtin14), '') IS NULL)
               OR (NULLIF(BTRIM(c.gtin14), '') IS NOT NULL AND BTRIM(c.gtin14) IS DISTINCT FROM
                  CASE WHEN LENGTH(BTRIM(i.gtin)) = 13 THEN '0' || BTRIM(i.gtin) ELSE BTRIM(i.gtin) END)
               OR (c.order_id IS NOT NULL AND c.order_id IS DISTINCT FROM v_doc.order_id)
               OR (b.order_id IS NOT NULL AND b.order_id IS DISTINCT FROM v_doc.order_id)
               OR c.status <> 1)
    ) THEN
        RAISE EXCEPTION 'QUANTITY_PRD_PER_CODE_BINDING_CONFLICT' USING ERRCODE = '23514';
    END IF;

    IF EXISTS (
        SELECT 1 FROM marking_synthetic_quantity_pool p
        JOIN marking_order mo ON mo.id = p.marking_order_id
        JOIN items i ON i.id = p.item_id
        WHERE p.marking_order_id = ANY(v_tasks)
          AND ((v_order.order_type IS DISTINCT FROM 'INTERNAL'
                     AND NOT (v_order.order_type='CUSTOMER'
                         AND p.source_type='PRODUCTION_ORDER'
                         AND mo.source_order_id=v_doc.order_id
                         AND mo.order_id=v_doc.order_id))
               OR mo.status IN ('Cancelled', 'Failed')
               OR p.item_id IS DISTINCT FROM mo.item_id
               OR p.order_line_id IS DISTINCT FROM mo.order_line_id
               OR p.normalized_gtin IS DISTINCT FROM BTRIM(mo.gtin)
               OR p.normalized_gtin IS DISTINCT FROM BTRIM(i.gtin)
               OR p.source_type IS DISTINCT FROM BTRIM(mo.source_type)
               OR p.source_scope IS DISTINCT FROM JSONB_BUILD_OBJECT(
                   'source_type', BTRIM(mo.source_type),
                   'source_order_id', mo.source_order_id, 'order_id', mo.order_id)
               OR p.source_type NOT IN ('PRODUCTION_ORDER', 'PRODUCTION_NEED')
               OR (mo.source_order_id IS NOT NULL AND mo.source_order_id <> v_doc.order_id)
               OR (mo.order_id IS NOT NULL AND mo.order_id <> v_doc.order_id)
               OR (p.source_type = 'PRODUCTION_ORDER' AND
                   mo.source_order_id IS DISTINCT FROM v_doc.order_id)
               OR (p.source_type = 'PRODUCTION_NEED' AND
                   (mo.source_order_id IS NOT NULL OR mo.order_id IS NOT NULL
                    OR p.order_line_id IS NOT NULL)))
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
               OR NOT EXISTS (SELECT 1 FROM items i JOIN item_types it ON it.id = i.item_type_id
                              WHERE i.id = dl.item_id AND it.enable_marking AND NULLIF(BTRIM(i.gtin), '') IS NOT NULL)
               OR (NOT (p.marking_order_id = ANY(v_tasks))
                   AND NOT EXISTS(
                       SELECT 1 FROM marking_hu_reservation r
                       WHERE r.pool_id=p.id AND r.state='CONSUMED'
                         AND r.consumed_doc_id=p_doc_id AND r.consumed_line_id=dl.id
                   ))
               OR p.item_id <> dl.item_id
               OR (p.order_line_id IS NOT NULL AND p.order_line_id IS DISTINCT FROM dl.order_line_id
                   AND NOT EXISTS(
                       SELECT 1 FROM marking_hu_reservation r
                       WHERE r.pool_id=p.id AND r.state='CONSUMED'
                         AND r.consumed_doc_id=p_doc_id AND r.consumed_line_id=dl.id
                   ))
               OR e.operation_id <> MD5('quantity-prd:v1:' || p_doc_id || ':' || dl.id || ':' || p.id)::UUID)
    ) THEN
        RAISE EXCEPTION 'QUANTITY_PRD_HISTORY_CONFLICT' USING ERRCODE = '23514';
    END IF;

    FOR v_line IN SELECT dl.*, BTRIM(i.gtin) AS gtin FROM doc_lines dl
        JOIN items i ON i.id = dl.item_id
        JOIN item_types it ON it.id = i.item_type_id
        WHERE it.enable_marking AND NULLIF(BTRIM(i.gtin), '') IS NOT NULL AND dl.doc_id = p_doc_id AND dl.qty > 0
          AND NOT EXISTS (SELECT 1 FROM doc_lines n WHERE n.replaces_line_id = dl.id)
        ORDER BY dl.id
    LOOP
        v_needed := v_line.qty::NUMERIC::BIGINT;
        v_total := v_total + v_needed;
        SELECT COALESCE(SUM(r.quantity),0)::BIGINT INTO v_adopted
        FROM marking_hu_reservation r
        JOIN order_coverage_transfer_lines tl ON tl.id=r.transfer_line_id
        JOIN order_coverage_transfers t ON t.id=tl.transfer_id
        JOIN production_pallets pp ON pp.id=t.production_pallet_id
        WHERE tl.source_doc_line_id=v_line.id
          AND r.state='ACTIVE' AND t.compensation_kind IS NULL
          AND t.hu_marking_allocation_version='v1'
          AND t.target_order_id=v_doc.order_id
          AND pp.prd_doc_id=p_doc_id AND pp.status='FILLED';
        IF v_adopted>v_needed THEN
            RAISE EXCEPTION 'HU_MARKING_PRD_OVERALLOCATED' USING ERRCODE='23514';
        END IF;
        v_needed:=v_needed-v_adopted;
        SELECT (SELECT COUNT(*) FROM marking_code WHERE receipt_doc_id = p_doc_id AND receipt_line_id = v_line.id)
             + (SELECT COUNT(*) FROM km_code WHERE receipt_doc_id = p_doc_id AND receipt_line_id = v_line.id)
            INTO v_bound;
        v_needed := v_needed - v_bound;
        IF v_needed < 0 THEN
            RAISE EXCEPTION 'QUANTITY_PRD_EXCESS_COVERAGE' USING ERRCODE = '23514';
        END IF;
        IF v_replay THEN
            IF v_needed <> (SELECT COALESCE(SUM(applied_quantity), 0)
                            FROM marking_synthetic_quantity_receipt_balance
                            WHERE receipt_doc_id = p_doc_id AND receipt_line_id = v_line.id) THEN
                RAISE EXCEPTION 'QUANTITY_PRD_HISTORY_CONFLICT' USING ERRCODE = '23514';
            END IF;
            CONTINUE;
        END IF;
        -- One ordering across representations: order-specific tasks first,
        -- then unscoped production need, followed by stable task/pool/code IDs.
        FOR v_source IN
            SELECT mo.* FROM marking_order mo
            WHERE mo.id = ANY(v_tasks) AND mo.status NOT IN ('Cancelled', 'Failed')
              AND (mo.item_id = v_line.item_id OR (mo.item_id IS NULL AND BTRIM(mo.gtin) = v_line.gtin))
              AND (NULLIF(BTRIM(mo.gtin), '') IS NULL OR BTRIM(mo.gtin) = v_line.gtin)
              AND (mo.order_line_id IS NULL OR mo.order_line_id = v_line.order_line_id)
            ORDER BY CASE WHEN mo.source_order_id = v_doc.order_id THEN 0
                          WHEN mo.order_id = v_doc.order_id THEN 1 ELSE 2 END,
                     mo.created_at, mo.id
        LOOP
            EXIT WHEN v_needed = 0;
            IF EXISTS (SELECT 1 FROM marking_synthetic_quantity_pool WHERE marking_order_id = v_source.id) THEN
                FOR v_pool IN
                    SELECT b.pool_id, b.available_quantity FROM marking_synthetic_quantity_balance b
                    WHERE b.marking_order_id = v_source.id ORDER BY b.pool_id
                LOOP
                    v_take := LEAST(v_needed, GREATEST(0,
                        v_pool.available_quantity - COALESCE((
                            SELECT SUM(r.quantity) FROM marking_hu_reservation r
                            WHERE r.pool_id=v_pool.pool_id AND r.state='ACTIVE'
                        ),0)));
                    IF v_take > 0 THEN
                        PERFORM * FROM marking_synthetic_quantity_apply(
                            MD5('quantity-prd:v1:' || p_doc_id || ':' || v_line.id || ':' || v_pool.pool_id)::UUID,
                            v_pool.pool_id, 'ASSIGN', v_take, NULL, NULL, p_doc_id, v_line.id, BTRIM(p_actor));
                        v_needed := v_needed - v_take;
                    END IF;
                    EXIT WHEN v_needed = 0;
                END LOOP;
            ELSE
                FOR v_code IN
                    SELECT c.id FROM marking_code c WHERE c.marking_order_id = v_source.id
                      AND c.receipt_doc_id IS NULL AND c.receipt_line_id IS NULL
                      AND c.status IN ('Reserved', 'Printed')
                      AND (NULLIF(BTRIM(c.gtin), '') IS NULL OR BTRIM(c.gtin) = v_line.gtin)
                    ORDER BY c.source_row_number NULLS LAST, c.created_at, c.id LIMIT v_needed
                LOOP
                    UPDATE marking_code SET status = 'Applied', receipt_doc_id = p_doc_id,
                        receipt_line_id = v_line.id, applied_at = COALESCE(applied_at, p_applied_at),
                        updated_at = p_applied_at WHERE id = v_code;
                    v_needed := v_needed - 1;
                END LOOP;
            END IF;
        END LOOP;
        IF v_needed <> 0 THEN
            -- Raising aborts the entire statement, including earlier lines.
            RAISE EXCEPTION 'QUANTITY_PRD_SHORTAGE: line % missing %', v_line.id, v_needed
                USING ERRCODE = '23514';
        END IF;
    END LOOP;
    -- Consume all proven adopted HU source allocations in this same
    -- validated Close transaction, or roll back customer task assignment.
    PERFORM marking_hu_consume_prd(p_doc_id,p_actor,p_applied_at);

    -- The two historical per-code tables may contain the same physical DM.
    -- Never count that identity twice, including a newly selected code.
    IF EXISTS (SELECT 1 FROM marking_code c JOIN km_code k ON BTRIM(k.code_raw) = BTRIM(c.code)
               WHERE c.receipt_doc_id = p_doc_id AND k.receipt_doc_id = p_doc_id) THEN
        RAISE EXCEPTION 'QUANTITY_PRD_DUPLICATE_REAL_CODE' USING ERRCODE = '23514';
    END IF;
    RETURN v_total;
END;
$$;

REVOKE EXECUTE ON FUNCTION marking_assign_prd_close(BIGINT, TEXT, TEXT) FROM PUBLIC;
