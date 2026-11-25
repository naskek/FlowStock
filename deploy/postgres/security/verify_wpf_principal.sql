-- Общий effective-ACL gate для operator provisioning и WPF startup.
-- Никаких DDL, grants, repairs или SECURITY DEFINER helpers.
DO $policy$
DECLARE
    principal oid := (SELECT oid FROM pg_roles WHERE rolname = 'flowstock_wpf');
    relation record;
    attribute record;
    permission text;
    allowed boolean;
BEGIN
    IF principal IS NULL OR EXISTS (
        SELECT 1 FROM pg_roles WHERE oid = principal AND
        (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls OR rolinherit OR NOT rolcanlogin)
    ) OR EXISTS (SELECT 1 FROM pg_auth_members WHERE member = principal)
      OR EXISTS (SELECT 1 FROM pg_shdepend WHERE refclassid = 'pg_authid'::regclass
                  AND refobjid = principal AND deptype = 'o') THEN
        RAISE EXCEPTION 'WPF_DB_UNSAFE_ROLE';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_foreign_server WHERE has_server_privilege(principal, oid, 'USAGE'))
       OR EXISTS (SELECT 1 FROM pg_foreign_data_wrapper WHERE has_foreign_data_wrapper_privilege(principal, oid, 'USAGE')) THEN
        RAISE EXCEPTION 'WPF_DB_UNSAFE_FOREIGN_ACCESS';
    END IF;
    IF has_database_privilege(principal, current_database(), 'CREATE')
       OR EXISTS (SELECT 1 FROM pg_namespace WHERE nspname !~ '^pg_temp_[0-9]+$' AND
                   has_schema_privilege(principal, oid, 'CREATE')) THEN
        RAISE EXCEPTION 'WPF_DB_UNSAFE_DDL';
    END IF;
    -- PUBLIC/column grants на системные каталоги также могут обходить role/DDL API.
    IF EXISTS (
        SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='pg_catalog' AND c.relname IN
          ('pg_authid','pg_auth_members','pg_db_role_setting','pg_database','pg_namespace',
           'pg_class','pg_proc','pg_default_acl','pg_policy','pg_rewrite','pg_trigger',
           'pg_foreign_server','pg_user_mapping','pg_shdepend')
          AND (has_table_privilege(principal,c.oid,'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
            OR has_any_column_privilege(principal,c.oid,'INSERT,UPDATE,REFERENCES')
            OR (c.relname IN ('pg_authid','pg_user_mapping') AND has_any_column_privilege(principal,c.oid,'SELECT')))
    ) THEN RAISE EXCEPTION 'WPF_DB_UNSAFE_CATALOG'; END IF;
    -- Любой callable user routine вне pg_catalog требует отдельного review.
    -- Trigger-return functions нельзя вызвать как обычную функцию; invoker trigger
    -- остаётся ограничен ACL вызывающего principal.
    IF EXISTS (
        SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname NOT IN ('pg_catalog','information_schema')
          AND n.nspname !~ '^pg_(temp|toast)'
          AND (p.prorettype <> 'trigger'::regtype OR p.prosecdef)
          AND has_function_privilege(principal, p.oid, 'EXECUTE')
    ) THEN RAISE EXCEPTION 'WPF_DB_UNSAFE_ROUTINE'; END IF;
    -- Не допускаем future grants от любого creator для WPF/PUBLIC на relations.
    -- Для routines каждый доверенный creator обязан убрать global default PUBLIC EXECUTE.
    IF EXISTS (
        SELECT 1 FROM pg_default_acl d CROSS JOIN LATERAL aclexplode(d.defaclacl) a
        WHERE (a.grantee = principal OR (a.grantee = 0 AND d.defaclobjtype <> 'f'))
    ) THEN RAISE EXCEPTION 'WPF_DB_UNSAFE_DEFAULT_ACL'; END IF;
    IF EXISTS (
        SELECT 1 FROM (
            SELECT c.relowner AS owner FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname !~ '^pg_(temp|toast)'
            UNION SELECT p.proowner FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname !~ '^pg_(temp|toast)'
        ) creators
        LEFT JOIN pg_default_acl d ON d.defaclrole = creators.owner
            AND d.defaclnamespace = 0 AND d.defaclobjtype = 'f'
        CROSS JOIN LATERAL aclexplode(COALESCE(d.defaclacl, acldefault('f', creators.owner))) a
        WHERE a.grantee IN (0, principal)
    ) THEN RAISE EXCEPTION 'WPF_DB_UNSAFE_FUTURE_ROUTINE'; END IF;
    -- Referential actions run with table-owner authority; do not let a legacy
    -- business mutation cascade into any security source, even transitively.
    IF EXISTS (
        WITH RECURSIVE affected(oid) AS (
            SELECT c.oid FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='public' AND c.relname IN ('items','api_docs','doc_lines','km_code','km_code_batch','docs','orders')
            UNION
            SELECT fk.conrelid FROM affected a JOIN pg_constraint fk ON fk.confrelid=a.oid
            WHERE fk.contype='f' AND (fk.confdeltype IN ('c','n','d') OR fk.confupdtype IN ('c','n','d'))
        )
        SELECT 1 FROM affected a JOIN pg_class c ON c.oid=a.oid
        WHERE c.relname IN ('tsd_devices','pc_web_sessions','backup_grants','backup_credential_refs','backup_security_events')
    ) THEN RAISE EXCEPTION 'WPF_DB_UNSAFE_CASCADE'; END IF;
    FOR relation IN
        SELECT c.oid, c.relname, c.relkind, n.nspname
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname !~ '^pg_(temp|toast)'
          AND c.relkind IN ('r','p','v','m','f','S')
    LOOP
        IF relation.relkind = 'S' THEN
            FOREACH permission IN ARRAY ARRAY['USAGE','SELECT','UPDATE'] LOOP
                allowed := relation.nspname = 'public' AND relation.relname = ANY (ARRAY['doc_lines_id_seq','km_code_id_seq','km_code_batch_id_seq']::text[]) AND permission = 'USAGE';
                IF has_sequence_privilege(principal, relation.oid, permission) AND NOT allowed THEN
                    RAISE EXCEPTION 'WPF_DB_UNSAFE_SEQUENCE';
                END IF;
            END LOOP;
            CONTINUE;
        END IF;
        IF relation.nspname = 'public' AND has_table_privilege(principal, relation.oid, 'SELECT')
           AND (relation.relkind NOT IN ('r','p') OR EXISTS (
               SELECT 1 FROM pg_rewrite WHERE ev_class=relation.oid AND rulename <> '_RETURN'
           )) THEN RAISE EXCEPTION 'WPF_DB_UNSAFE_REWRITE'; END IF;
        FOREACH permission IN ARRAY ARRAY['SELECT','INSERT','UPDATE','DELETE','TRUNCATE','REFERENCES','TRIGGER'] LOOP
            allowed := relation.nspname = 'public' AND (
                (permission = 'SELECT' AND relation.relname = ANY (ARRAY['schema_migrations','items','item_types','vat_rates','partner_item_sale_prices','uoms','taras','item_packaging','locations','partners','docs','doc_lines','api_docs','ledger','orders','order_lines','order_receipt_plan_lines','production_pallets','production_pallet_lines','production_filling_completions','order_coverage_transfers','order_coverage_transfer_lines','stock_reservation_lines','hus','km_code','km_code_batch','marking_order','marking_code','marking_code_import','marking_print_batch','marking_print_batch_code','order_control_tasks','order_control_task_orders','order_control_task_hus','order_control_task_hu_lines']::text[]))
                OR (permission = 'DELETE' AND relation.relname = ANY (ARRAY['doc_lines','km_code','km_code_batch']::text[]))
);
            IF has_table_privilege(principal, relation.oid, permission) AND NOT allowed THEN
                RAISE EXCEPTION 'WPF_DB_UNSAFE_TABLE_ACL';
            END IF;
            -- Column grants/PUBLIC count even when table-level check is false.
            IF permission IN ('SELECT','INSERT','UPDATE','REFERENCES') THEN
                FOR attribute IN SELECT attnum, attname FROM pg_attribute
                                 WHERE attrelid = relation.oid AND attnum > 0 AND NOT attisdropped LOOP
                    IF has_column_privilege(principal, relation.oid, attribute.attnum, permission)
                       AND NOT (allowed OR (relation.nspname = 'public' AND permission = 'INSERT' AND (
                           (relation.relname = 'api_docs' AND attribute.attname = ANY (ARRAY['doc_uid','doc_id','status','created_at','doc_type','doc_ref','partner_id','from_location_id','to_location_id','from_hu','to_hu','device_id']::text[])) OR
                           (relation.relname = 'doc_lines' AND attribute.attname = ANY (ARRAY['doc_id','replaces_line_id','order_line_id','production_purpose','item_id','qty','qty_input','uom_code','from_location_id','to_location_id','from_hu','to_hu','pack_single_hu']::text[])) OR
                           (relation.relname = 'km_code' AND attribute.attname = ANY (ARRAY['batch_id','code_raw','gtin14','sku_id','product_name','status','receipt_doc_id','receipt_line_id','hu_id','location_id','ship_doc_id','ship_line_id','order_id']::text[])) OR
                           (relation.relname = 'km_code_batch' AND attribute.attname = ANY (ARRAY['order_id','file_name','file_hash','imported_at','imported_by','total_codes','error_count']::text[])))) OR (relation.nspname = 'public' AND permission = 'UPDATE' AND (
                           (relation.relname = 'km_code' AND attribute.attname = ANY (ARRAY['status','receipt_doc_id','receipt_line_id','hu_id','location_id','ship_doc_id','ship_line_id','order_id']::text[])) OR
                           (relation.relname = 'km_code_batch' AND attribute.attname = ANY (ARRAY['order_id','total_codes','error_count']::text[])) OR
                           (relation.relname = 'items' AND attribute.attname = 'is_marked') OR
                           (relation.relname IN ('docs','orders') AND attribute.attname = 'comment') OR
                           (relation.relname = 'api_docs' AND attribute.attname = ANY (ARRAY['status','doc_type','doc_ref','partner_id','from_location_id','to_location_id','from_hu','to_hu','device_id']::text[]))))) THEN
                        RAISE EXCEPTION 'WPF_DB_UNSAFE_COLUMN_ACL';
                    END IF;
                END LOOP;
            END IF;
        END LOOP;
        -- SECURITY DEFINER trigger может изменить authority chain косвенно.
        IF relation.nspname = 'public' AND relation.relname = ANY (ARRAY['api_docs','doc_lines','docs','orders','items','km_code','km_code_batch']::text[]) AND EXISTS (
            SELECT 1 FROM pg_trigger t JOIN pg_proc p ON p.oid = t.tgfoid
            WHERE t.tgrelid = relation.oid AND NOT t.tgisinternal AND p.prosecdef
        ) THEN RAISE EXCEPTION 'WPF_DB_UNSAFE_TRIGGER'; END IF;
    END LOOP;
END
$policy$;
