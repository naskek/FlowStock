-- Неактивный COR/refill protocol. Исходные transfers, CONSUMED reservations,
-- события и закрытые receipts сохраняются; новая ревизия получает новый резерв.
CREATE TABLE marking_hu_correction_revision (
    id BIGSERIAL PRIMARY KEY,
    transfer_line_id BIGINT NOT NULL REFERENCES order_coverage_transfer_lines(id),
    adjustment_id BIGINT NOT NULL REFERENCES production_pallet_filling_adjustments(id),
    predecessor_id BIGINT NULL UNIQUE REFERENCES marking_hu_correction_revision(id),
    source_pallet_id BIGINT NOT NULL,
    source_doc_id BIGINT NOT NULL,
    source_line_id BIGINT NOT NULL,
    replacement_pallet_id BIGINT NOT NULL,
    replacement_line_id BIGINT NOT NULL UNIQUE,
    cor_doc_id BIGINT NOT NULL,
    created_xid xid8 NOT NULL DEFAULT pg_current_xact_id(),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (transfer_line_id, source_line_id),
    UNIQUE (adjustment_id, transfer_line_id)
);
CREATE TRIGGER trg_marking_hu_revision_immutable
    BEFORE UPDATE OR DELETE ON marking_hu_correction_revision
    FOR EACH ROW EXECUTE FUNCTION marking_synthetic_quantity_immutable();
CREATE UNIQUE INDEX ux_marking_hu_revision_root ON marking_hu_correction_revision(transfer_line_id)
    WHERE predecessor_id IS NULL;
CREATE TRIGGER trg_marking_hu_revision_no_truncate
    BEFORE TRUNCATE ON marking_hu_correction_revision
    FOR EACH STATEMENT EXECUTE FUNCTION marking_synthetic_quantity_immutable();

ALTER TABLE marking_hu_reservation
    ADD COLUMN correction_revision_id BIGINT NULL,
    ADD COLUMN predecessor_reservation_id BIGINT NULL UNIQUE REFERENCES marking_hu_reservation(id),
    ADD CONSTRAINT ck_marking_hu_reservation_revision CHECK (
        (correction_revision_id IS NULL) = (predecessor_reservation_id IS NULL));

-- Текущий scope выводится только из явных revision edges, никогда из HU/GTIN.
CREATE VIEW marking_hu_current_transfer_line AS
SELECT tl.*, COALESCE(rev.replacement_line_id,tl.source_doc_line_id) AS current_line_id,
       COALESCE(rev.replacement_pallet_id,t.production_pallet_id) AS current_pallet_id,
       rev.id AS revision_id
FROM order_coverage_transfer_lines tl
JOIN order_coverage_transfers t ON t.id=tl.transfer_id
LEFT JOIN marking_hu_correction_revision rev ON rev.transfer_line_id=tl.id
 AND NOT EXISTS (SELECT 1 FROM marking_hu_correction_revision next WHERE next.predecessor_id=rev.id);

CREATE VIEW marking_hu_current_reservation AS
SELECT r.* FROM marking_hu_reservation r
WHERE NOT EXISTS (SELECT 1 FROM marking_hu_reservation next WHERE next.predecessor_reservation_id=r.id);

-- Единое доказательство consumed source для preview и повторной проверки COR.
-- В частности, совпадение source task/GTIN без exact reservation недостаточно.
CREATE FUNCTION marking_hu_consumed_proven(p_reservation BIGINT)
RETURNS BOOLEAN LANGUAGE sql STABLE AS $$
SELECT EXISTS (
 SELECT 1 FROM marking_hu_reservation r
 JOIN order_coverage_transfer_lines tl ON tl.id=r.transfer_line_id
 JOIN order_coverage_transfers t ON t.id=tl.transfer_id
 JOIN marking_hu_allocation_attempt a ON a.transfer_line_id=tl.id
 LEFT JOIN marking_hu_correction_revision rev ON rev.id=r.correction_revision_id
 JOIN doc_lines dl ON dl.id=r.consumed_line_id
 JOIN docs d ON d.id=r.consumed_doc_id AND dl.doc_id=d.id
 JOIN production_pallets pp ON pp.id=COALESCE(rev.replacement_pallet_id,t.production_pallet_id)
 JOIN production_pallet_lines pcl ON pcl.production_pallet_id=pp.id AND pcl.doc_line_id=dl.id
 JOIN items i ON i.id=dl.item_id
 JOIN item_types it ON it.id=i.item_type_id
 JOIN marking_order mo ON mo.id=r.marking_order_id
 LEFT JOIN marking_code c ON c.id=r.marking_code_id
 LEFT JOIN marking_code_import imp ON imp.id=c.import_id
 LEFT JOIN marking_synthetic_quantity_pool p ON p.id=r.pool_id
 LEFT JOIN marking_synthetic_quantity_balance b ON b.pool_id=p.id
 WHERE r.id=p_reservation AND r.state='CONSUMED'
 AND t.compensation_kind IS NULL AND t.hu_marking_allocation_version='v1'
 AND a.item_id=i.id AND a.markable AND a.normalized_gtin=BTRIM(i.gtin)
 AND a.planned_quantity::NUMERIC=tl.transferred_qty::NUMERIC
 AND r.quantity<=a.planned_quantity
 AND r.consumed_line_id=COALESCE(rev.replacement_line_id,tl.source_doc_line_id)
 AND (rev.id IS NULL OR rev.transfer_line_id=tl.id)
 AND d.type='PRODUCTION_RECEIPT' AND d.status='CLOSED' AND d.order_id=t.target_order_id
 AND pp.prd_doc_id=d.id AND pp.order_id=d.order_id AND pp.hu_code=t.hu_code
 AND pp.status IN ('FILLED','CORRECTED')
 AND pcl.item_id=tl.item_id AND pcl.order_line_id=tl.target_order_line_id
 AND pcl.planned_qty=tl.transferred_qty AND pcl.filled_qty=pcl.planned_qty
 AND dl.item_id=tl.item_id AND dl.order_line_id=tl.target_order_line_id
 AND dl.qty=tl.transferred_qty AND it.enable_marking
 AND NOT EXISTS(SELECT 1 FROM doc_lines newer WHERE newer.replaces_line_id=dl.id)
 AND mo.source_type='PRODUCTION_ORDER' AND mo.source_order_id=t.source_order_id
 AND (mo.order_id IS NULL OR mo.order_id=t.source_order_id)
 AND (mo.order_line_id IS NULL OR mo.order_line_id=tl.source_order_line_id)
 AND mo.item_id=i.id AND BTRIM(mo.gtin)=BTRIM(i.gtin)
 AND mo.status NOT IN ('Cancelled','Failed')
 AND ((r.marking_code_id IS NOT NULL AND c.marking_order_id=mo.id
       AND c.status='Applied' AND c.reported_at IS NULL AND c.introduced_at IS NULL
       AND c.receipt_doc_id=d.id AND c.receipt_line_id=dl.id AND BTRIM(c.gtin)=BTRIM(i.gtin)
       AND ((c.code LIKE 'TEMP-CHZ-%' AND imp.source_type='temporary-chz-export'
             AND c.origin IN ('LegacySynthetic','HistoricalUnknown'))
         OR (c.code NOT LIKE 'TEMP-CHZ-%' AND imp.source_type IN ('csv','tsv')
             AND c.origin IN ('RealImport','LegacyRealImport')))
       AND NOT EXISTS(SELECT 1 FROM km_code k WHERE BTRIM(k.code_raw)=BTRIM(c.code)))
   OR (r.pool_id IS NOT NULL AND p.marking_order_id=mo.id AND p.item_id=i.id
       AND p.normalized_gtin=BTRIM(i.gtin) AND p.origin='SyntheticQuantity'
       AND p.source_type=mo.source_type AND p.order_line_id IS NOT DISTINCT FROM mo.order_line_id
       AND p.source_scope=JSONB_BUILD_OBJECT('source_type',BTRIM(mo.source_type),
           'source_order_id',mo.source_order_id,'order_id',mo.order_id)
       AND NOT EXISTS(SELECT 1 FROM marking_code history WHERE history.marking_order_id=mo.id)
       AND b.available_quantity>=0 AND b.applied_quantity>=r.quantity AND b.voided_quantity>=0
       AND b.issued_quantity::NUMERIC=b.available_quantity::NUMERIC+b.applied_quantity::NUMERIC+b.voided_quantity::NUMERIC
       AND b.applied_quantity=(SELECT COALESCE(SUM(rb.applied_quantity),0)
           FROM marking_synthetic_quantity_receipt_balance rb WHERE rb.pool_id=p.id)
       AND EXISTS(SELECT 1 FROM marking_synthetic_quantity_event e
          WHERE e.pool_id=p.id AND e.action='ASSIGN' AND e.quantity=r.quantity
            AND e.receipt_doc_id=d.id AND e.receipt_line_id=dl.id
            AND e.operation_id=MD5('quantity-prd:v1:'||d.id||':'||dl.id||':'||p.id)::UUID)
       AND (SELECT COUNT(*) FROM marking_synthetic_quantity_event e WHERE e.pool_id=p.id
            AND (e.receipt_doc_id=d.id OR e.receipt_line_id=dl.id))=1))
);
$$;

CREATE FUNCTION marking_hu_revision_validate() RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
 IF current_setting('transaction_isolation')<>'read committed'
    OR NEW.created_xid<>pg_current_xact_id() OR NOT EXISTS (
  SELECT 1 FROM marking_hu_current_transfer_line scope
  JOIN order_coverage_transfers t ON t.id=scope.transfer_id
  JOIN marking_hu_allocation_attempt a ON a.transfer_line_id=scope.id
  JOIN production_pallet_filling_adjustments j ON j.id=NEW.adjustment_id
  JOIN production_pallet_filling_adjustment_lines edge ON edge.adjustment_id=j.id
     AND edge.line_kind='PALLET_COMPONENT' AND edge.source_doc_line_id=NEW.source_line_id
     AND edge.replacement_doc_line_id=NEW.replacement_line_id
  JOIN production_pallet_lines src ON src.id=edge.source_component_id
  JOIN production_pallet_lines dst ON dst.id=edge.replacement_component_id
  JOIN production_pallets sp ON sp.id=src.production_pallet_id
  JOIN production_pallets rp ON rp.id=dst.production_pallet_id
  JOIN doc_lines sl ON sl.id=NEW.source_line_id
  JOIN doc_lines rl ON rl.id=NEW.replacement_line_id
  JOIN docs sd ON sd.id=NEW.source_doc_id
  JOIN docs rd ON rd.id=rl.doc_id
  JOIN docs cor ON cor.id=NEW.cor_doc_id
  JOIN items i ON i.id=scope.item_id
  WHERE scope.id=NEW.transfer_line_id AND scope.current_line_id=NEW.source_line_id
    AND scope.current_pallet_id=NEW.source_pallet_id
    AND scope.revision_id IS NOT DISTINCT FROM NEW.predecessor_id
    AND t.hu_marking_allocation_version='v1' AND t.compensation_kind IS NULL
    AND a.item_id=i.id AND a.normalized_gtin IS NOT DISTINCT FROM NULLIF(BTRIM(i.gtin),'')
    AND a.planned_quantity::NUMERIC=scope.transferred_qty::NUMERIC
    AND j.action_type='CORRECT_FILLED' AND j.result_json IS NULL
    AND sp.id=NEW.source_pallet_id AND sp.status='CORRECTED' AND sp.prd_doc_id=sd.id
    AND rp.id=NEW.replacement_pallet_id AND rp.status IN ('PLANNED','PRINTED')
    AND sp.order_id=t.target_order_id AND rp.order_id=t.target_order_id
    AND sp.hu_code=t.hu_code AND rp.hu_code=t.hu_code AND sp.to_location_id=rp.to_location_id
    AND sd.status='CLOSED' AND sd.type='PRODUCTION_RECEIPT' AND sd.order_id=t.target_order_id
    AND sl.doc_id=sd.id AND sl.item_id=scope.item_id AND sl.order_line_id=scope.target_order_line_id
    AND sl.qty=scope.transferred_qty AND src.planned_qty=sl.qty AND src.filled_qty=sl.qty
    AND rd.status='DRAFT' AND rd.type='PRODUCTION_RECEIPT' AND rd.order_id=t.target_order_id
    AND rp.prd_doc_id=rd.id AND dst.doc_line_id=rl.id AND dst.item_id=sl.item_id
    AND dst.order_line_id=sl.order_line_id AND dst.planned_qty=sl.qty AND dst.filled_qty=0
    AND rl.item_id=sl.item_id AND rl.order_line_id=sl.order_line_id AND rl.qty=sl.qty
    AND rl.to_hu=sl.to_hu AND rl.to_location_id=sl.to_location_id
    AND cor.status='CLOSED' AND cor.type='INVENTORY_CORRECTION' AND cor.reason_code='ERRONEOUS_HU_FILL'
    AND EXISTS (SELECT 1 FROM production_pallet_filling_adjustment_lines inv
       JOIN ledger prior ON prior.id=inv.source_ledger_entry_id
       JOIN ledger undo ON undo.id=inv.generated_cor_ledger_entry_id
       WHERE inv.adjustment_id=j.id AND inv.line_kind='LEDGER_INVERSION'
         AND inv.source_doc_line_id=sl.id AND prior.doc_id=sd.id AND undo.doc_id=cor.id
         AND prior.item_id=sl.item_id AND undo.item_id=sl.item_id
         AND prior.hu_code=t.hu_code AND undo.hu_code=t.hu_code
         AND prior.location_id=sl.to_location_id AND undo.location_id=prior.location_id
         AND prior.qty_delta=sl.qty AND undo.qty_delta=-sl.qty)
 ) OR EXISTS (
  SELECT 1 FROM marking_hu_current_reservation r WHERE r.transfer_line_id=NEW.transfer_line_id
    AND (r.state<>'CONSUMED' OR r.consumed_doc_id<>NEW.source_doc_id
       OR r.consumed_line_id<>NEW.source_line_id OR NOT marking_hu_consumed_proven(r.id))
 ) THEN
  RAISE EXCEPTION 'HU_MARKING_COR_REVISION_INVALID' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END;
$$;
CREATE TRIGGER trg_marking_hu_revision_validate BEFORE INSERT ON marking_hu_correction_revision
 FOR EACH ROW EXECUTE FUNCTION marking_hu_revision_validate();

-- Нельзя закоммитить промежуточное разрешение RELEASE без нового резерва,
-- завершённого adjustment и полного immutable release audit.
CREATE FUNCTION marking_hu_revision_complete() RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
 IF NOT EXISTS (SELECT 1 FROM production_pallet_filling_adjustments j
    WHERE j.id=NEW.adjustment_id AND j.result_json IS NOT NULL
      AND j.source_pallet_id=NEW.source_pallet_id AND j.source_prd_doc_id=NEW.source_doc_id
      AND j.replacement_pallet_id=NEW.replacement_pallet_id AND j.cor_doc_id=NEW.cor_doc_id)
 OR EXISTS (
   SELECT 1 FROM marking_hu_reservation prior WHERE prior.transfer_line_id=NEW.transfer_line_id
    AND prior.consumed_doc_id=NEW.source_doc_id AND prior.consumed_line_id=NEW.source_line_id
    AND (NOT EXISTS(SELECT 1 FROM marking_hu_reservation child
        WHERE child.predecessor_reservation_id=prior.id AND child.correction_revision_id=NEW.id)
     OR (prior.marking_code_id IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM production_marking_transition_audit audit
        WHERE audit.adjustment_id=NEW.adjustment_id AND audit.marking_code_id=prior.marking_code_id
         AND audit.old_receipt_doc_id=NEW.source_doc_id AND audit.old_receipt_line_id=NEW.source_line_id
         AND audit.cor_doc_id=NEW.cor_doc_id AND audit.old_status='Applied' AND audit.new_status='Reserved'))
     OR (prior.pool_id IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM production_marking_quantity_release_audit audit
        JOIN marking_synthetic_quantity_event e ON e.id=audit.assignment_event_id
        WHERE audit.adjustment_id=NEW.adjustment_id AND audit.cor_doc_id=NEW.cor_doc_id
         AND e.pool_id=prior.pool_id AND e.receipt_doc_id=NEW.source_doc_id
         AND e.receipt_line_id=NEW.source_line_id AND e.quantity=prior.quantity)))
 ) THEN
  RAISE EXCEPTION 'HU_MARKING_COR_REVISION_INCOMPLETE' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END;
$$;
CREATE CONSTRAINT TRIGGER trg_marking_hu_revision_complete AFTER INSERT ON marking_hu_correction_revision
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION marking_hu_revision_complete();

CREATE FUNCTION marking_hu_prepare_correction(p_adjustment BIGINT,p_source BIGINT,p_replacement BIGINT,p_cor BIGINT)
RETURNS VOID LANGUAGE plpgsql AS $$
DECLARE scope RECORD;
BEGIN
 -- Caller already holds order/HU/doc/line/task/pool locks. No SKIP LOCKED or retry.
 FOR scope IN SELECT s.*,dl.doc_id FROM marking_hu_current_transfer_line s
   JOIN doc_lines dl ON dl.id=s.current_line_id WHERE s.current_pallet_id=p_source ORDER BY s.id
 LOOP
   PERFORM 1 FROM order_coverage_transfer_lines WHERE id=scope.id FOR UPDATE NOWAIT;
   PERFORM 1 FROM marking_hu_reservation r WHERE r.transfer_line_id=scope.id ORDER BY r.id FOR UPDATE NOWAIT;
   INSERT INTO marking_hu_correction_revision(transfer_line_id,adjustment_id,predecessor_id,
     source_pallet_id,source_doc_id,source_line_id,replacement_pallet_id,replacement_line_id,cor_doc_id)
   SELECT scope.id,p_adjustment,scope.revision_id,p_source,scope.doc_id,scope.current_line_id,
     p_replacement,edge.replacement_doc_line_id,p_cor
   FROM production_pallet_filling_adjustment_lines edge
   WHERE edge.adjustment_id=p_adjustment AND edge.line_kind='PALLET_COMPONENT'
     AND edge.source_doc_line_id=scope.current_line_id;
   IF NOT FOUND THEN RAISE EXCEPTION 'HU_MARKING_COR_COMPONENT_MISSING' USING ERRCODE='23514'; END IF;
 END LOOP;
END;
$$;

CREATE FUNCTION marking_hu_finish_correction(p_adjustment BIGINT) RETURNS VOID LANGUAGE plpgsql AS $$
BEGIN
 INSERT INTO marking_hu_reservation(transfer_line_id,marking_order_id,marking_code_id,pool_id,
     quantity,created_actor,correction_revision_id,predecessor_reservation_id)
 SELECT r.transfer_line_id,r.marking_order_id,r.marking_code_id,r.pool_id,r.quantity,
     'HU-CORRECTION',rev.id,r.id
 FROM marking_hu_correction_revision rev JOIN marking_hu_current_reservation r
   ON r.transfer_line_id=rev.transfer_line_id AND r.consumed_doc_id=rev.source_doc_id
      AND r.consumed_line_id=rev.source_line_id AND r.state='CONSUMED'
 WHERE rev.adjustment_id=p_adjustment AND rev.created_xid=pg_current_xact_id()
 ORDER BY r.id;
END;
$$;
REVOKE ALL ON FUNCTION marking_hu_prepare_correction(BIGINT,BIGINT,BIGINT,BIGINT) FROM PUBLIC;
REVOKE ALL ON FUNCTION marking_hu_finish_correction(BIGINT) FROM PUBLIC;


-- Revision-aware validation and consumption; original migration contracts remain auditable.
CREATE OR REPLACE FUNCTION marking_hu_reservation_audit() RETURNS TRIGGER
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'HU_MARKING_RESERVATION_DELETE_FORBIDDEN' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' THEN
        IF OLD.state <> 'ACTIVE' OR NEW.state NOT IN ('RELEASED','CONSUMED')
           OR NEW.transfer_line_id IS DISTINCT FROM OLD.transfer_line_id
           OR NEW.marking_order_id IS DISTINCT FROM OLD.marking_order_id
           OR NEW.marking_code_id IS DISTINCT FROM OLD.marking_code_id
           OR NEW.pool_id IS DISTINCT FROM OLD.pool_id
           OR NEW.quantity IS DISTINCT FROM OLD.quantity
           OR NEW.created_actor IS DISTINCT FROM OLD.created_actor
           OR NEW.created_at IS DISTINCT FROM OLD.created_at
           OR NEW.correction_revision_id IS DISTINCT FROM OLD.correction_revision_id
           OR NEW.predecessor_reservation_id IS DISTINCT FROM OLD.predecessor_reservation_id THEN
            RAISE EXCEPTION 'HU_MARKING_RESERVATION_IMMUTABLE' USING ERRCODE = '23514';
        END IF;
        IF NEW.state = 'RELEASED' THEN
            IF NEW.released_at IS NULL OR BTRIM(COALESCE(NEW.released_actor,'')) = ''
               OR NEW.consumed_doc_id IS NOT NULL OR NEW.consumed_line_id IS NOT NULL
               OR NEW.consumed_actor IS NOT NULL OR NEW.consumed_at IS NOT NULL THEN
                RAISE EXCEPTION 'HU_MARKING_RESERVATION_IMMUTABLE' USING ERRCODE='23514';
            END IF;
            INSERT INTO marking_hu_reservation_event(
                reservation_id, action, quantity, actor, created_at)
            VALUES(OLD.id, 'RETURN_PLAN', OLD.quantity, NEW.released_actor, NEW.released_at);
        ELSE
            IF NEW.released_at IS NOT NULL OR NEW.released_actor IS NOT NULL
               OR NEW.consumed_doc_id IS NULL OR NEW.consumed_line_id IS NULL
               OR NEW.consumed_at IS NULL OR BTRIM(COALESCE(NEW.consumed_actor,''))='' THEN
                RAISE EXCEPTION 'HU_MARKING_RESERVATION_IMMUTABLE' USING ERRCODE='23514';
            END IF;
            -- Current dedicated PRD may differ from the adoption snapshot.
            -- The exact original transfer doc_line ID remains the identity.
            IF NOT EXISTS (
                SELECT 1 FROM order_coverage_transfer_lines tl
                JOIN order_coverage_transfers t ON t.id=tl.transfer_id
                JOIN marking_hu_current_transfer_line scope ON scope.id=tl.id
                JOIN production_pallets pp ON pp.id=scope.current_pallet_id
                JOIN docs d ON d.id=NEW.consumed_doc_id
                JOIN doc_lines dl ON dl.id=NEW.consumed_line_id
                WHERE tl.id=NEW.transfer_line_id
                  AND t.compensation_kind IS NULL
                  AND t.hu_marking_allocation_version='v1'
                  AND pp.order_id=t.target_order_id
                  AND pp.prd_doc_id=d.id AND pp.status='FILLED'
                  AND pp.hu_code=t.hu_code
                  AND d.order_id=t.target_order_id
                  AND d.type='PRODUCTION_RECEIPT' AND d.status='DRAFT'
                  AND dl.id=scope.current_line_id
                  AND scope.revision_id IS NOT DISTINCT FROM NEW.correction_revision_id
                  AND dl.doc_id=d.id AND dl.order_line_id=tl.target_order_line_id
                  AND dl.item_id=tl.item_id
                  AND ABS(dl.qty-tl.transferred_qty)<=0.000001
            ) THEN
                RAISE EXCEPTION 'HU_MARKING_CONSUME_SCOPE_INVALID' USING ERRCODE='23514';
            END IF;
            INSERT INTO marking_hu_reservation_event(
                reservation_id, action, quantity, actor, created_at)
            VALUES(OLD.id,'CONSUME',OLD.quantity,NEW.consumed_actor,NEW.consumed_at);
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.predecessor_reservation_id IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM marking_hu_reservation prior
        JOIN marking_hu_correction_revision rev ON rev.id=NEW.correction_revision_id
        LEFT JOIN marking_code c ON c.id=prior.marking_code_id
        LEFT JOIN marking_synthetic_quantity_receipt_balance rb ON rb.pool_id=prior.pool_id
          AND rb.receipt_doc_id=prior.consumed_doc_id AND rb.receipt_line_id=prior.consumed_line_id
        WHERE prior.id=NEW.predecessor_reservation_id AND prior.state='CONSUMED'
          AND prior.transfer_line_id=NEW.transfer_line_id AND prior.transfer_line_id=rev.transfer_line_id
          AND prior.marking_order_id=NEW.marking_order_id
          AND prior.marking_code_id IS NOT DISTINCT FROM NEW.marking_code_id
          AND prior.pool_id IS NOT DISTINCT FROM NEW.pool_id AND prior.quantity=NEW.quantity
          AND prior.consumed_doc_id=rev.source_doc_id AND prior.consumed_line_id=rev.source_line_id
          AND prior.correction_revision_id IS NOT DISTINCT FROM rev.predecessor_id
          AND rev.created_xid=pg_current_xact_id() AND NEW.state='ACTIVE'
          AND (prior.marking_code_id IS NULL OR (c.status='Reserved'
              AND c.receipt_doc_id IS NULL AND c.receipt_line_id IS NULL))
          AND (prior.pool_id IS NULL OR COALESCE(rb.applied_quantity,0)=0)
    ) THEN
        RAISE EXCEPTION 'HU_MARKING_COR_CHILD_INVALID' USING ERRCODE='23514';
    END IF;
    INSERT INTO marking_hu_reservation_event(
        reservation_id, action, quantity, actor, created_at)
    VALUES(NEW.id, 'RESERVE', NEW.quantity, NEW.created_actor, NEW.created_at);
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION marking_hu_guard_code_consumption() RETURNS TRIGGER
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF EXISTS (SELECT 1 FROM marking_hu_reservation r
                   WHERE r.marking_code_id = OLD.id AND r.state IN ('ACTIVE','CONSUMED')) THEN
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
         OR NEW.import_id IS DISTINCT FROM OLD.import_id
         OR NEW.code IS DISTINCT FROM OLD.code OR NEW.code_hash IS DISTINCT FROM OLD.code_hash) THEN
        RAISE EXCEPTION 'HU_MARKING_SOURCE_CODE_RESERVED' USING ERRCODE = '23514';
    END IF;
    IF NEW.status='Reserved' AND OLD.status='Applied'
       AND NEW.receipt_doc_id IS NULL AND NEW.receipt_line_id IS NULL AND NEW.applied_at IS NULL
       AND NEW.marking_order_id=OLD.marking_order_id AND NEW.import_id=OLD.import_id
       AND NEW.origin=OLD.origin AND NEW.gtin IS NOT DISTINCT FROM OLD.gtin
       AND NEW.code=OLD.code AND NEW.code_hash=OLD.code_hash
       AND OLD.reported_at IS NULL AND OLD.introduced_at IS NULL
       AND NEW.reported_at IS NULL AND NEW.introduced_at IS NULL
       AND EXISTS (SELECT 1 FROM marking_hu_current_reservation r
          JOIN marking_hu_correction_revision rev ON rev.transfer_line_id=r.transfer_line_id
            AND rev.source_doc_id=r.consumed_doc_id AND rev.source_line_id=r.consumed_line_id
          JOIN production_marking_transition_audit a ON a.adjustment_id=rev.adjustment_id
            AND a.marking_code_id=OLD.id AND a.cor_doc_id=rev.cor_doc_id
          WHERE r.marking_code_id=OLD.id AND r.state='CONSUMED'
            AND r.consumed_doc_id=OLD.receipt_doc_id AND r.consumed_line_id=OLD.receipt_line_id
            AND rev.created_xid=pg_current_xact_id()) THEN RETURN NEW; END IF;
    IF EXISTS (SELECT 1 FROM marking_hu_current_reservation r
               WHERE r.marking_code_id=OLD.id AND r.state='CONSUMED')
       AND (NEW.marking_order_id IS DISTINCT FROM OLD.marking_order_id
            OR NEW.gtin IS DISTINCT FROM OLD.gtin
            OR NEW.origin IS DISTINCT FROM OLD.origin
            OR NEW.import_id IS DISTINCT FROM OLD.import_id
            OR NEW.code IS DISTINCT FROM OLD.code OR NEW.code_hash IS DISTINCT FROM OLD.code_hash
            OR NOT EXISTS (
                SELECT 1 FROM marking_hu_current_reservation r
                WHERE r.marking_code_id=OLD.id AND r.state='CONSUMED'
                  AND NEW.receipt_doc_id=r.consumed_doc_id
                  AND NEW.receipt_line_id=r.consumed_line_id
                  AND NEW.status IN ('Applied','Reported','Circulated')
            )) THEN
        RAISE EXCEPTION 'HU_MARKING_CONSUMED_CODE_IMMUTABLE' USING ERRCODE='23514';
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION marking_hu_guard_quantity_consumption() RETURNS TRIGGER
LANGUAGE plpgsql AS $$
DECLARE
    v_reserved BIGINT;
    v_free BIGINT;
BEGIN
    IF NEW.action = 'RELEASE' AND EXISTS (
        SELECT 1 FROM marking_hu_reservation r
        WHERE r.pool_id=NEW.pool_id AND r.state='CONSUMED'
          AND r.consumed_doc_id=NEW.receipt_doc_id
          AND r.consumed_line_id=NEW.receipt_line_id
          AND NOT EXISTS(SELECT 1 FROM marking_hu_correction_revision rev
            JOIN production_pallet_filling_adjustments j ON j.id=rev.adjustment_id
            JOIN marking_synthetic_quantity_event a ON a.pool_id=r.pool_id
              AND a.receipt_doc_id=r.consumed_doc_id AND a.receipt_line_id=r.consumed_line_id
              AND a.action='ASSIGN' AND a.quantity=r.quantity
            WHERE rev.transfer_line_id=r.transfer_line_id AND rev.source_doc_id=r.consumed_doc_id
              AND rev.source_line_id=r.consumed_line_id AND rev.created_xid=pg_current_xact_id()
              AND j.result_json IS NULL AND NEW.quantity=r.quantity
              AND NEW.operation_id=MD5('quantity-cor:v1:'||j.request_id||':'||a.id)::UUID)
    ) THEN
        RAISE EXCEPTION 'HU_MARKING_CONSUMED_QUANTITY_COR_BLOCKED'
            USING ERRCODE='23514';
    END IF;
    IF NEW.action NOT IN ('ASSIGN', 'VOID') THEN RETURN NEW; END IF;
    PERFORM 1 FROM marking_synthetic_quantity_pool p
      WHERE p.id = NEW.pool_id FOR UPDATE;
    SELECT COALESCE(SUM(r.quantity),0)::BIGINT INTO v_reserved
      FROM marking_hu_current_reservation r
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

-- #220. Read-only proof, NOT a cutover and NOT a customer consumption
-- authorization. A quantity here is an exclusive ACTIVE v1 reservation, never
-- an inference from PRINTED HU, an prior source task or a matching GTIN alone.
-- All joins are revalidated live; historical/non-v1 transfers fail closed.
CREATE OR REPLACE FUNCTION marking_hu_customer_reserved_coverage(p_order_id BIGINT)
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
               live.doc_id AS target_prd_doc_id, l.current_pallet_id AS production_pallet_id, t.hu_code,
               t.hu_marking_allocation_version,
               l.id AS transfer_line_id, l.source_order_line_id,
               l.target_order_line_id, l.current_line_id AS source_doc_line_id, l.item_id,
               l.transferred_qty,
               NULLIF(BTRIM(i.gtin),'') AS normalized_gtin,
               COALESCE(it.enable_marking,FALSE) AS markable
        FROM order_coverage_transfers t
        JOIN marking_hu_current_transfer_line l ON l.transfer_id=t.id
        LEFT JOIN doc_lines live ON live.id=l.current_line_id
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

        IF EXISTS (SELECT 1 FROM marking_hu_current_reservation r
                   WHERE r.transfer_line_id=v.transfer_line_id
                     AND r.state <> 'ACTIVE') THEN
            RAISE EXCEPTION 'ADOPTED_HU_MARKING_STATE_CONFLICT: transfer % line %',
                v.transfer_id,v.transfer_line_id USING ERRCODE='23514';
        END IF;
        SELECT COALESCE(SUM(r.quantity),0)::BIGINT INTO v_reserved
        FROM marking_hu_current_reservation r
        WHERE r.transfer_line_id=v.transfer_line_id AND r.state='ACTIVE';
        IF v_reserved<0 OR v_reserved::NUMERIC>v.transferred_qty::NUMERIC
           OR (NOT v.markable AND v_reserved>0)
           OR (v.markable AND v.normalized_gtin IS NULL) THEN
            RAISE EXCEPTION 'ADOPTED_HU_MARKING_OVERALLOCATED: transfer % line %',
                v.transfer_id,v.transfer_line_id USING ERRCODE='23514';
        END IF;

        IF EXISTS (
            SELECT 1 FROM marking_hu_current_reservation r
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
            SELECT 1 FROM marking_hu_current_reservation r
            JOIN marking_synthetic_quantity_balance b ON b.pool_id=r.pool_id
            WHERE r.transfer_line_id=v.transfer_line_id AND r.pool_id IS NOT NULL
              AND (SELECT COALESCE(SUM(active.quantity),0)
                   FROM marking_hu_current_reservation active
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


-- #220: dormant CUSTOMER dedicated PRD consumption of exact prospective HU
-- reservations. Must be called by guarded Close in its READ COMMITTED TX.
CREATE OR REPLACE FUNCTION marking_hu_consume_prd(p_doc_id BIGINT,p_actor TEXT,p_applied_at TEXT)
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
       OR v_doc.status<>'DRAFT' THEN
        RAISE EXCEPTION 'HU_MARKING_DRAFT_PRD_REQUIRED' USING ERRCODE='23514';
    END IF;
    -- Standalone PRD remains valid under the existing hybrid Close contract.
    -- There can be no order-owned prospective HU transfer without an order.
    IF v_doc.order_id IS NULL THEN
        RETURN 0;
    END IF;
    -- All new transfers must have a recorded immutable zero/positive attempt.
    -- Old unallocated transfers are never silently treated as covered.
    IF EXISTS (
        SELECT 1 FROM order_coverage_transfers t
        JOIN marking_hu_current_transfer_line tl ON tl.transfer_id=t.id
        JOIN doc_lines dl ON dl.id=tl.current_line_id
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
               tl.source_order_line_id,tl.current_line_id AS source_doc_line_id,
               tl.transferred_qty,t.source_order_id,t.target_order_id,
               t.production_pallet_id,t.hu_code,t.compensation_kind,
               pp.prd_doc_id AS pallet_doc,pp.order_id AS pallet_order,
               pp.status AS pallet_status,pp.hu_code AS pallet_hu,
               dl.doc_id AS current_doc,dl.order_line_id AS current_line,
               dl.item_id AS current_item,dl.qty AS current_qty
        FROM marking_hu_reservation mr
        JOIN marking_hu_current_transfer_line tl ON tl.id=mr.transfer_line_id
        JOIN order_coverage_transfers t ON t.id=tl.transfer_id
        JOIN doc_lines dl ON dl.id=tl.current_line_id
        JOIN production_pallets pp ON pp.id=tl.current_pallet_id
        WHERE t.target_order_id=v_doc.order_id AND dl.doc_id=p_doc_id
          AND NOT EXISTS(SELECT 1 FROM marking_hu_reservation child WHERE child.predecessor_reservation_id=mr.id)
        ORDER BY mr.id FOR UPDATE OF mr NOWAIT
    LOOP
        IF r.compensation_kind IS NOT NULL OR r.current_doc<>p_doc_id
           OR r.current_line IS DISTINCT FROM r.target_order_line_id
           OR r.current_item<>r.item_id
           OR ABS(r.current_qty-r.transferred_qty)>0.000001
           OR r.pallet_doc<>p_doc_id OR r.pallet_order<>r.target_order_id
           OR r.pallet_status<>'FILLED' OR r.pallet_hu<>r.hu_code
           OR NOT EXISTS(SELECT 1 FROM marking_hu_allocation_attempt a
               JOIN items item ON item.id=r.item_id
               JOIN item_types kind ON kind.id=item.item_type_id
               WHERE a.transfer_line_id=r.transfer_line_id AND a.item_id=r.item_id
                 AND a.normalized_gtin IS NOT DISTINCT FROM BTRIM(item.gtin)
                 AND a.markable=COALESCE(kind.enable_marking,FALSE)
                 AND a.planned_quantity::NUMERIC=r.transferred_qty::NUMERIC)
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
            JOIN marking_order mo ON mo.id=pool.marking_order_id
            WHERE pool.id=r.pool_id AND pool.origin='SyntheticQuantity'
              AND pool.source_type='PRODUCTION_ORDER'
              AND pool.order_line_id IS NOT DISTINCT FROM mo.order_line_id
              AND pool.source_scope=JSONB_BUILD_OBJECT('source_type',BTRIM(mo.source_type),
                    'source_order_id',mo.source_order_id,'order_id',mo.order_id)
            FOR UPDATE OF pool NOWAIT;
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
               OR (mo.source_type = 'PRODUCTION_NEED' AND mo.source_order_id IS NULL))
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
                       SELECT 1 FROM marking_hu_current_reservation r
                       WHERE r.marking_code_id=c.id AND r.state='CONSUMED'
                         AND r.consumed_doc_id=p_doc_id AND r.consumed_line_id=dl.id
                   ))
               OR mo.status IN ('Cancelled', 'Failed')
               OR (mo.order_line_id IS NOT NULL AND mo.order_line_id IS DISTINCT FROM dl.order_line_id
                   AND NOT EXISTS(
                       SELECT 1 FROM marking_hu_current_reservation r
                       WHERE r.marking_code_id=c.id AND r.state='CONSUMED'
                         AND r.consumed_doc_id=p_doc_id AND r.consumed_line_id=dl.id
                   ))
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
                       SELECT 1 FROM marking_hu_current_reservation r
                       WHERE r.pool_id=p.id AND r.state='CONSUMED'
                         AND r.consumed_doc_id=p_doc_id AND r.consumed_line_id=dl.id
                   ))
               OR p.item_id <> dl.item_id
               OR (p.order_line_id IS NOT NULL AND p.order_line_id IS DISTINCT FROM dl.order_line_id
                   AND NOT EXISTS(
                       SELECT 1 FROM marking_hu_current_reservation r
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
        FROM marking_hu_current_reservation r
        JOIN marking_hu_current_transfer_line tl ON tl.id=r.transfer_line_id
        JOIN order_coverage_transfers t ON t.id=tl.transfer_id
        JOIN production_pallets pp ON pp.id=tl.current_pallet_id
        WHERE tl.current_line_id=v_line.id
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
                            SELECT SUM(r.quantity) FROM marking_hu_current_reservation r
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
                      AND NOT EXISTS (
                          SELECT 1 FROM marking_hu_current_reservation r
                          WHERE r.marking_code_id = c.id AND r.state = 'ACTIVE'
                      )
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



-- После любого consume RETURN_PLAN запрещён, включая planned replacement после COR.
CREATE OR REPLACE FUNCTION marking_hu_return_transfer(p_transfer_id BIGINT, p_actor TEXT)
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
    IF EXISTS (
        SELECT 1 FROM marking_hu_reservation r
        JOIN order_coverage_transfer_lines tl ON tl.id=r.transfer_line_id
        WHERE tl.transfer_id=v_t.id AND r.state='CONSUMED'
    ) OR EXISTS (SELECT 1 FROM marking_hu_correction_revision rev
        JOIN order_coverage_transfer_lines tl ON tl.id=rev.transfer_line_id WHERE tl.transfer_id=v_t.id) THEN
        RAISE EXCEPTION 'HU_MARKING_CONSUMED_RETURN_PLAN_BLOCKED'
            USING ERRCODE='23514';
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
