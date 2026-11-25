-- Только явный operator cutover на выбранной БД. Выполнять вместе с verify
-- в одной psql --single-transaction; пароль задаётся отдельно через \password.
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'flowstock_wpf') THEN
        CREATE ROLE flowstock_wpf LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE
            NOINHERIT NOREPLICATION NOBYPASSRLS;
    END IF;
END $$;
DO $$ BEGIN EXECUTE format('GRANT CONNECT ON DATABASE %I TO flowstock_wpf', current_database()); END $$;
GRANT USAGE ON SCHEMA public TO flowstock_wpf;
GRANT SELECT ON public.schema_migrations, public.items, public.item_types, public.vat_rates, public.partner_item_sale_prices, public.uoms, public.taras, public.item_packaging, public.locations, public.partners, public.docs, public.doc_lines, public.api_docs, public.ledger, public.orders, public.order_lines, public.order_receipt_plan_lines, public.production_pallets, public.production_pallet_lines, public.production_filling_completions, public.order_coverage_transfers, public.order_coverage_transfer_lines, public.stock_reservation_lines, public.hus, public.km_code, public.km_code_batch, public.marking_order, public.marking_code, public.marking_code_import, public.marking_print_batch, public.marking_print_batch_code, public.order_control_tasks, public.order_control_task_orders, public.order_control_task_hus, public.order_control_task_hu_lines TO flowstock_wpf;
GRANT INSERT (doc_uid, doc_id, status, created_at, doc_type, doc_ref, partner_id, from_location_id, to_location_id, from_hu, to_hu, device_id) ON public.api_docs TO flowstock_wpf;
GRANT INSERT (doc_id, replaces_line_id, order_line_id, production_purpose, item_id, qty, qty_input, uom_code, from_location_id, to_location_id, from_hu, to_hu, pack_single_hu) ON public.doc_lines TO flowstock_wpf;
GRANT INSERT (batch_id, code_raw, gtin14, sku_id, product_name, status, receipt_doc_id, receipt_line_id, hu_id, location_id, ship_doc_id, ship_line_id, order_id) ON public.km_code TO flowstock_wpf;
GRANT INSERT (order_id, file_name, file_hash, imported_at, imported_by, total_codes, error_count) ON public.km_code_batch TO flowstock_wpf;
GRANT DELETE ON public.doc_lines, public.km_code, public.km_code_batch TO flowstock_wpf;
GRANT UPDATE (is_marked) ON public.items TO flowstock_wpf;
-- PostgreSQL row locks require UPDATE on at least one column. Grant only
-- non-authoritative comments; no IDs, status, quantities or authorization fields.
GRANT UPDATE (comment) ON public.docs, public.orders TO flowstock_wpf;
GRANT UPDATE (status, doc_type, doc_ref, partner_id, from_location_id, to_location_id, from_hu, to_hu, device_id) ON public.api_docs TO flowstock_wpf;
GRANT UPDATE (status, receipt_doc_id, receipt_line_id, hu_id, location_id, ship_doc_id, ship_line_id, order_id) ON public.km_code TO flowstock_wpf;
GRANT UPDATE (order_id, total_codes, error_count) ON public.km_code_batch TO flowstock_wpf;
GRANT USAGE ON SEQUENCE public.doc_lines_id_seq, public.km_code_id_seq, public.km_code_batch_id_seq TO flowstock_wpf;
-- Global default, а не IN SCHEMA: schema-level REVOKE не отменяет global PUBLIC.
-- Применяется для фактического creator/migrator, запускающего script.
ALTER DEFAULT PRIVILEGES REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
