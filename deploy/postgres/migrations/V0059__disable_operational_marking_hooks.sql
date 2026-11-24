-- Excel ЧЗ не ведёт внутренний учёт маркировки.
-- V0036 могла быть установлена исторической веткой и отсутствует в active-main chain.
-- Отключаем только marking hooks на физических таблицах. История, FK и функции сохраняются.
DO $cutover$
DECLARE
    hook RECORD;
    relation REGCLASS;
    actual_function TEXT;
    actual_schema TEXT;
BEGIN
    FOR hook IN
        SELECT * FROM (VALUES
            ('items', 'trg_items_marking_subject_gtin_guard', 'guard_marking_subject_item_gtin_mutation'),
            ('production_pallet_lines', 'trg_production_pallet_line_marking_subject', 'sync_marking_production_subject'),
            ('production_pallets', 'trg_production_pallet_marking_subject_lifecycle', 'sync_marking_subject_pallet_lifecycle')
        ) AS hooks(table_name, trigger_name, function_name)
    LOOP
        relation := to_regclass(format('public.%I', hook.table_name));
        IF relation IS NULL THEN
            CONTINUE;
        END IF;

        SELECT p.proname, n.nspname INTO actual_function, actual_schema
        FROM pg_trigger t
        JOIN pg_proc p ON p.oid = t.tgfoid
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE t.tgrelid = relation AND t.tgname = hook.trigger_name AND NOT t.tgisinternal;

        IF FOUND THEN
            -- Не выключаем неизвестный физический guard под совпавшим именем.
            IF actual_schema <> 'public' OR actual_function <> hook.function_name THEN
                RAISE EXCEPTION 'Unexpected function for marking hook %.%: %.%',
                    hook.table_name, hook.trigger_name, actual_schema, actual_function;
            END IF;
            EXECUTE format('ALTER TABLE %s DISABLE TRIGGER %I', relation, hook.trigger_name);
        END IF;
    END LOOP;
END;
$cutover$;
