-- #220. Read-only proof, NOT a cutover and NOT a customer consumption
-- authorization. A quantity here is an exclusive ACTIVE v1 reservation, never
-- an inference from PRINTED HU, an old source task or a matching GTIN alone.
-- All joins are revalidated live; historical/non-v1 transfers fail closed.
CREATE FUNCTION marking_hu_customer_reserved_coverage(p_order_id BIGINT)
RETURNS TABLE(order_line_id BIGINT, reserved_quantity BIGINT)
LANGUAGE plpgsql STABLE AS $$
DECLARE
    v RECORD;
    v_reserved BIGINT;
BEGIN
    IF p_order_id IS NULL OR p_order_id <= 0 THEN
        RAISE EXCEPTION 'HU_MARKING_CUSTOMER_INVALID_ORDER' USING ERRCODE='22023';
    END IF;
    FOR v IN
        SELECT t.id AS transfer_id, t.source_order_id, t.target_order_id,
               t.target_prd_doc_id, t.production_pallet_id, t.hu_code,
               t.hu_marking_allocation_version,
               l.id AS transfer_line_id, l.source_order_line_id,
               l.target_order_line_id, l.source_doc_line_id, l.item_id,
               l.transferred_qty,
               NULLIF(BTRIM(i.gtin),'') AS normalized_gtin,
               COALESCE(it.enable_marking,FALSE) AS markable
        FROM order_coverage_transfers t
        JOIN order_coverage_transfer_lines l ON l.transfer_id=t.id
        JOIN items i ON i.id=l.item_id
        LEFT JOIN item_types it ON it.id=i.item_type_id
        WHERE t.target_order_id=p_order_id AND t.compensation_kind IS NULL
        ORDER BY t.id,l.id
    LOOP
        IF v.hu_marking_allocation_version IS DISTINCT FROM 'v1'
           OR NOT EXISTS (
              SELECT 1 FROM marking_hu_allocation_attempt a
              WHERE a.transfer_line_id=v.transfer_line_id
                AND a.item_id=v.item_id
                AND a.normalized_gtin IS NOT DISTINCT FROM v.normalized_gtin
                AND a.markable=v.markable
                AND a.planned_quantity::NUMERIC=v.transferred_qty::NUMERIC)
           OR v.transferred_qty<=0
           OR v.transferred_qty::NUMERIC<>TRUNC(v.transferred_qty::NUMERIC) THEN
            RAISE EXCEPTION 'ADOPTED_HU_MARKING_UNVERIFIED: transfer % line %',
                v.transfer_id,v.transfer_line_id USING ERRCODE='23514';
        END IF;

        -- A live unfilled component must still belong to exactly this HU,
        -- target order and PRD/line. Any stale/superseded fact aborts.
        IF NOT EXISTS (
            SELECT 1 FROM production_pallets pp
            JOIN docs d ON d.id=pp.prd_doc_id
            JOIN production_pallet_lines pll ON pll.production_pallet_id=pp.id
            JOIN doc_lines dl ON dl.id=pll.doc_line_id
            JOIN order_lines ol ON ol.id=dl.order_line_id
            WHERE pp.id=v.production_pallet_id AND pp.hu_code=v.hu_code
              AND pp.order_id=v.target_order_id AND pp.prd_doc_id=v.target_prd_doc_id
              AND pp.status IN ('PLANNED','PRINTED') AND pp.filled_at IS NULL
              AND d.id=v.target_prd_doc_id AND d.order_id=v.target_order_id
              AND d.type='PRODUCTION_RECEIPT' AND d.status='DRAFT'
              AND pll.doc_line_id=v.source_doc_line_id
              AND pll.order_line_id=v.target_order_line_id
              AND pll.item_id=v.item_id AND pll.filled_qty=0 AND pll.filled_at IS NULL
              AND ABS(pll.planned_qty-v.transferred_qty)<=0.000001
              AND dl.id=v.source_doc_line_id AND dl.doc_id=v.target_prd_doc_id
              AND dl.order_line_id=v.target_order_line_id AND dl.item_id=v.item_id
              AND ABS(dl.qty-v.transferred_qty)<=0.000001
              AND NOT EXISTS (SELECT 1 FROM doc_lines newer WHERE newer.replaces_line_id=dl.id)
              AND ol.id=v.target_order_line_id AND ol.order_id=v.target_order_id
              AND ol.item_id=v.item_id
        ) THEN
            RAISE EXCEPTION 'ADOPTED_HU_MARKING_STALE: transfer % line %',
                v.transfer_id,v.transfer_line_id USING ERRCODE='23514';
        END IF;

        IF EXISTS (SELECT 1 FROM marking_hu_reservation r
                   WHERE r.transfer_line_id=v.transfer_line_id
                     AND r.state <> 'ACTIVE') THEN
            RAISE EXCEPTION 'ADOPTED_HU_MARKING_STATE_CONFLICT: transfer % line %',
                v.transfer_id,v.transfer_line_id USING ERRCODE='23514';
        END IF;
        SELECT COALESCE(SUM(r.quantity),0)::BIGINT INTO v_reserved
        FROM marking_hu_reservation r
        WHERE r.transfer_line_id=v.transfer_line_id AND r.state='ACTIVE';
        IF v_reserved<0 OR v_reserved::NUMERIC>v.transferred_qty::NUMERIC
           OR (NOT v.markable AND v_reserved>0)
           OR (v.markable AND v.normalized_gtin IS NULL) THEN
            RAISE EXCEPTION 'ADOPTED_HU_MARKING_OVERALLOCATED: transfer % line %',
                v.transfer_id,v.transfer_line_id USING ERRCODE='23514';
        END IF;

        IF EXISTS (
            SELECT 1 FROM marking_hu_reservation r
            LEFT JOIN marking_order mo ON mo.id=r.marking_order_id
            LEFT JOIN marking_code c ON c.id=r.marking_code_id
            LEFT JOIN marking_code_import imp ON imp.id=c.import_id
            LEFT JOIN marking_synthetic_quantity_pool p ON p.id=r.pool_id
            LEFT JOIN marking_synthetic_quantity_balance b ON b.pool_id=p.id
            WHERE r.transfer_line_id=v.transfer_line_id AND r.state='ACTIVE'
              AND (
                 mo.id IS NULL OR mo.source_type <> 'PRODUCTION_ORDER'
                 OR mo.source_order_id IS DISTINCT FROM v.source_order_id
                 OR (mo.order_id IS NOT NULL AND mo.order_id<>v.source_order_id)
                 OR mo.item_id IS DISTINCT FROM v.item_id
                 OR BTRIM(mo.gtin) IS DISTINCT FROM v.normalized_gtin
                 OR (mo.order_line_id IS NOT NULL AND mo.order_line_id<>v.source_order_line_id)
                 OR mo.status IN ('Cancelled','Failed')
                 OR (r.marking_code_id IS NOT NULL AND (
                     c.id IS NULL OR c.marking_order_id IS DISTINCT FROM mo.id
                     OR c.status NOT IN ('Reserved','Printed')
                     OR c.receipt_doc_id IS NOT NULL OR c.receipt_line_id IS NOT NULL
                     OR BTRIM(c.gtin) IS DISTINCT FROM v.normalized_gtin
                     OR NOT COALESCE(
                         (c.code LIKE 'TEMP-CHZ-%' AND imp.source_type='temporary-chz-export'
                          AND c.origin IN ('LegacySynthetic','HistoricalUnknown'))
                         OR (c.code NOT LIKE 'TEMP-CHZ-%' AND imp.source_type IN ('csv','tsv')
                          AND c.origin IN ('RealImport','LegacyRealImport')),FALSE)
                     OR EXISTS (SELECT 1 FROM km_code k
                                WHERE BTRIM(k.code_raw)=BTRIM(c.code))
                   ))
                 OR (r.pool_id IS NOT NULL AND (
                     p.id IS NULL OR p.marking_order_id IS DISTINCT FROM mo.id
                     OR p.item_id IS DISTINCT FROM v.item_id
                     OR p.normalized_gtin IS DISTINCT FROM v.normalized_gtin
                     OR p.order_line_id IS DISTINCT FROM mo.order_line_id
                     OR p.source_type <> 'PRODUCTION_ORDER'
                     OR p.origin <> 'SyntheticQuantity'
                     OR p.source_scope IS DISTINCT FROM JSONB_BUILD_OBJECT(
                          'source_type',BTRIM(mo.source_type),
                          'source_order_id',mo.source_order_id,'order_id',mo.order_id)
                     OR b.available_quantity IS NULL OR b.available_quantity<0
                     OR b.applied_quantity<0
                     OR b.issued_quantity<>b.available_quantity+b.applied_quantity+b.voided_quantity
                     OR EXISTS (SELECT 1 FROM marking_code history
                                WHERE history.marking_order_id=mo.id)
                   ))
              )
        ) THEN
            RAISE EXCEPTION 'ADOPTED_HU_MARKING_INVALID_SOURCE: transfer % line %',
                v.transfer_id,v.transfer_line_id USING ERRCODE='23514';
        END IF;

        IF EXISTS (
            SELECT 1 FROM marking_hu_reservation r
            JOIN marking_synthetic_quantity_balance b ON b.pool_id=r.pool_id
            WHERE r.transfer_line_id=v.transfer_line_id AND r.pool_id IS NOT NULL
              AND (SELECT COALESCE(SUM(active.quantity),0)
                   FROM marking_hu_reservation active
                   WHERE active.pool_id=r.pool_id AND active.state='ACTIVE')
                  > b.available_quantity
        ) THEN
            RAISE EXCEPTION 'ADOPTED_HU_MARKING_POOL_EXCESS: transfer % line %',
                v.transfer_id,v.transfer_line_id USING ERRCODE='23514';
        END IF;

        order_line_id:=v.target_order_line_id;
        reserved_quantity:=v_reserved;
        RETURN NEXT;
    END LOOP;
END;
$$;
REVOKE ALL ON FUNCTION marking_hu_customer_reserved_coverage(BIGINT) FROM PUBLIC;
