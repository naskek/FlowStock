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
