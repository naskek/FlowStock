-- #191: independent, opt-in quantitative journal. Not wired to current SHADOW
-- readers/writers. No migration of existing marking_code, no production backfill.
--
-- A pool is one stable provenance/scope, NOT an individual DataMatrix code.
-- Receipt ids in events are historical snapshots (no FK): existing cleanup
-- workflows can remove document rows without erasing this journal.
CREATE TABLE marking_synthetic_quantity_pool (
    id UUID PRIMARY KEY,
    marking_order_id UUID NOT NULL REFERENCES marking_order(id) ON DELETE RESTRICT,
    order_line_id BIGINT NULL,
    item_id BIGINT NOT NULL REFERENCES items(id) ON DELETE RESTRICT,
    normalized_gtin TEXT NOT NULL
        CHECK (normalized_gtin <> '' AND normalized_gtin = BTRIM(normalized_gtin)),
    source_type TEXT NOT NULL CHECK (BTRIM(source_type) <> ''),
    source_key TEXT NOT NULL CHECK (BTRIM(source_key) <> ''),
    origin TEXT NOT NULL DEFAULT 'SyntheticQuantity'
        CHECK (origin = 'SyntheticQuantity'),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (marking_order_id, source_key)
);

CREATE TABLE marking_synthetic_quantity_event (
    id BIGSERIAL PRIMARY KEY,
    operation_id UUID NOT NULL UNIQUE,
    pool_id UUID NOT NULL REFERENCES marking_synthetic_quantity_pool(id) ON DELETE RESTRICT,
    action TEXT NOT NULL CHECK (action IN ('ISSUE', 'ASSIGN', 'RELEASE', 'VOID')),
    quantity BIGINT NOT NULL CHECK (quantity > 0),
    receipt_doc_id BIGINT NULL,
    receipt_line_id BIGINT NULL,
    actor TEXT NOT NULL CHECK (BTRIM(actor) <> ''),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ck_marking_synthetic_quantity_event_receipt
        CHECK (
            (action IN ('ISSUE', 'VOID')
             AND receipt_doc_id IS NULL AND receipt_line_id IS NULL)
            OR
            (action IN ('ASSIGN', 'RELEASE')
             AND receipt_doc_id > 0 AND receipt_line_id > 0)
        )
);
CREATE INDEX ix_marking_synthetic_quantity_event_pool
    ON marking_synthetic_quantity_event(pool_id, id);
CREATE INDEX ix_marking_synthetic_quantity_event_receipt
    ON marking_synthetic_quantity_event(receipt_doc_id, receipt_line_id, pool_id)
    WHERE receipt_line_id IS NOT NULL;

-- No UPDATE/DELETE of pool provenance or historical events. Amend history
-- exclusively through a new, independently idempotent operation.
CREATE FUNCTION marking_synthetic_quantity_immutable() RETURNS TRIGGER
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'synthetic quantity history is append-only'
        USING ERRCODE = '55000';
END;
$$;
CREATE TRIGGER trg_marking_synthetic_quantity_pool_immutable
    BEFORE UPDATE OR DELETE ON marking_synthetic_quantity_pool
    FOR EACH ROW EXECUTE FUNCTION marking_synthetic_quantity_immutable();
CREATE TRIGGER trg_marking_synthetic_quantity_event_immutable
    BEFORE UPDATE OR DELETE ON marking_synthetic_quantity_event
    FOR EACH ROW EXECUTE FUNCTION marking_synthetic_quantity_immutable();

-- These read models are derived from immutable events, not persisted
-- counters. No possibility of a separately-maintained cache drifting.
CREATE VIEW marking_synthetic_quantity_balance AS
SELECT p.id AS pool_id,
       p.marking_order_id,
       p.order_line_id,
       p.item_id,
       p.normalized_gtin,
       p.source_type,
       p.source_key,
       p.origin,
       COALESCE(SUM(CASE WHEN e.action = 'ISSUE'
                         THEN e.quantity ELSE 0 END), 0)::BIGINT AS issued_quantity,
       COALESCE(SUM(CASE WHEN e.action IN ('ISSUE', 'RELEASE')
                         THEN e.quantity
                         WHEN e.action IN ('ASSIGN', 'VOID')
                         THEN -e.quantity ELSE 0 END), 0)::BIGINT AS available_quantity,
       COALESCE(SUM(CASE WHEN e.action = 'ASSIGN' THEN e.quantity
                         WHEN e.action = 'RELEASE' THEN -e.quantity
                         ELSE 0 END), 0)::BIGINT AS applied_quantity,
       COALESCE(SUM(CASE WHEN e.action = 'VOID' THEN e.quantity
                         ELSE 0 END), 0)::BIGINT AS voided_quantity,
       COUNT(e.id)::BIGINT AS revision
FROM marking_synthetic_quantity_pool p
LEFT JOIN marking_synthetic_quantity_event e ON e.pool_id = p.id
GROUP BY p.id;

CREATE VIEW marking_synthetic_quantity_receipt_balance AS
SELECT pool_id, receipt_doc_id, receipt_line_id,
       SUM(CASE WHEN action = 'ASSIGN' THEN quantity
                WHEN action = 'RELEASE' THEN -quantity
                ELSE 0 END)::BIGINT AS applied_quantity
FROM marking_synthetic_quantity_event
WHERE action IN ('ASSIGN', 'RELEASE')
GROUP BY pool_id, receipt_doc_id, receipt_line_id
HAVING SUM(CASE WHEN action = 'ASSIGN' THEN quantity
                WHEN action = 'RELEASE' THEN -quantity
                ELSE 0 END) <> 0;

-- One operation is ONE atomic PostgreSQL transaction. For a given pool,
-- SELECT FOR UPDATE serializes all debits, so two receipts cannot spend
-- the same quantity. The caller must use the same operation UUID on retry.
-- This is only a DB primitive: no production workflow calls it in SHADOW.
CREATE FUNCTION marking_synthetic_quantity_apply(
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
    IF p_action = 'ISSUE' AND
       (v_pool.marking_order_id IS DISTINCT FROM p_marking_order_id
        OR v_pool.source_key IS DISTINCT FROM p_source_key
        OR v_pool.item_id IS DISTINCT FROM v_task.item_id
        OR v_pool.order_line_id IS DISTINCT FROM v_task.order_line_id
        OR v_pool.normalized_gtin IS DISTINCT FROM v_task.normalized_gtin
        OR v_pool.source_type IS DISTINCT FROM v_task.source_type) THEN
        RAISE EXCEPTION 'issue source does not match pool provenance'
            USING ERRCODE = '23514';
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
