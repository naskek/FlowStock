-- Issue #195: fail-closed symmetric bridge during legacy TEMP-CHZ -> quantity cutover.
-- V0048 ISSUE validates task/item provenance but originally did not check
-- existing legacy technical codes. Never amend deployed V0048: this separate
-- migration rejects the opposite writer ordering (legacy committed first).
-- Real DataMatrix codes are not classified as temporary by this guard.
CREATE FUNCTION marking_synthetic_quantity_reject_legacy_temp_overlap()
RETURNS TRIGGER
LANGUAGE plpgsql AS $$
BEGIN
    -- Also cover direct pool INSERT: serialize against the server's legacy
    -- writer, which locks marking_order FOR UPDATE before TEMP-CHZ INSERT.
    PERFORM 1 FROM marking_order
    WHERE id = NEW.marking_order_id
    FOR SHARE;

    -- Historical TEMP-CHZ provenance may have imperfect import/origin
    -- metadata. A matching prefix is enough to fail closed; we do not
    -- reclassify or delete any of those rows.
    IF EXISTS (
        SELECT 1 FROM marking_code c
        WHERE c.marking_order_id = NEW.marking_order_id
          AND c.code LIKE 'TEMP-CHZ-%'
    ) THEN
        RAISE EXCEPTION
            'QUANTITY_POOL_LEGACY_EXPORT_CONFLICT: technical TEMP-CHZ codes already exist for task %',
            NEW.marking_order_id
            USING ERRCODE = '23514';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_marking_synthetic_quantity_no_legacy_temp
    BEFORE INSERT ON marking_synthetic_quantity_pool
    FOR EACH ROW
    EXECUTE FUNCTION marking_synthetic_quantity_reject_legacy_temp_overlap();
