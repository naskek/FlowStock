using System.Globalization;
using System.Linq;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services;
using Npgsql;
using NpgsqlTypes;

namespace FlowStock.Data;

public sealed class PostgresDataStore : IDataStore, IOptimizedOrderReadModelStore, IOptimizedOrderListMetricsStore
{
    private readonly string _connectionString;
    private readonly NpgsqlConnection? _connection;
    private readonly NpgsqlTransaction? _transaction;
    private const string DocSelectBase =
        "SELECT d.id, d.doc_ref, d.type, d.status, d.created_at, d.closed_at, d.partner_id, d.order_id, d.order_ref, d.shipping_ref, d.reason_code, d.comment, p.name, p.code, " +
        "COALESCE(dl.line_count, 0) AS line_count, ad.device_id, ad.doc_uid, d.production_batch_no " +
        "FROM docs d " +
        "LEFT JOIN partners p ON p.id = d.partner_id " +
        "LEFT JOIN (SELECT dl.doc_id, COUNT(*) AS line_count FROM doc_lines dl WHERE dl.qty > 0 AND NOT EXISTS (SELECT 1 FROM doc_lines newer WHERE newer.replaces_line_id = dl.id) GROUP BY dl.doc_id) dl ON dl.doc_id = d.id " +
        "LEFT JOIN (SELECT doc_id, MAX(device_id) AS device_id, MAX(doc_uid) AS doc_uid FROM api_docs GROUP BY doc_id) ad ON ad.doc_id = d.id";
    private const string ProductionPalletSelectSql = @"
SELECT p.id,
       p.prd_doc_id,
       p.doc_line_id,
       p.order_id,
       p.order_line_id,
       p.item_id,
       i.name,
       p.hu_code,
       p.planned_qty,
       p.to_location_id,
       l.code,
       p.status,
       p.pallet_no,
       p.pallet_count,
       p.printed_at,
       p.filled_at,
       p.filled_by_device_id,
       p.created_at
FROM production_pallets p
INNER JOIN items i ON i.id = p.item_id
LEFT JOIN locations l ON l.id = p.to_location_id";
    private const string OrderSelectBase = @"
WITH order_scope AS (
    {ORDER_SCOPE}
),
order_base AS (
    SELECT o.id,
           o.order_ref,
           o.order_type,
           o.partner_id,
           o.due_date,
           o.status AS persisted_status,
           o.comment,
           o.created_at,
           p.name AS partner_name,
           p.code AS partner_code,
           COALESCE(o.bind_reserved_stock, FALSE) AS bind_reserved_stock,
           COALESCE(o.marking_status, 'NOT_REQUIRED') AS marking_status,
           o.marking_excel_generated_at,
           o.marking_printed_at
    FROM orders o
    INNER JOIN order_scope os ON os.id = o.id
    LEFT JOIN partners p ON p.id = o.partner_id
),
order_lines_scope AS (
    SELECT ol.id,
           ol.order_id,
           ol.item_id,
           ol.qty_ordered
    FROM order_lines ol
    INNER JOIN order_scope os ON os.id = ol.order_id
),
shipped_by_line AS (
    SELECT dl.order_line_id,
           SUM(dl.qty) AS qty_shipped
    FROM order_lines_scope ols
    INNER JOIN doc_lines dl ON dl.order_line_id = ols.id
    INNER JOIN docs d ON d.id = dl.doc_id
    WHERE d.status = 'CLOSED'
      AND d.type = 'OUTBOUND'
      AND dl.qty > 0
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
    GROUP BY dl.order_line_id
),
reserved_by_line AS (
    SELECT p.order_line_id,
           SUM(p.qty_planned) AS qty_reserved
    FROM order_receipt_plan_lines p
    INNER JOIN order_lines_scope ols ON ols.id = p.order_line_id
    WHERE p.qty_planned > 0
    GROUP BY p.order_line_id
),
direct_produced_by_line AS (
    SELECT dl.order_line_id,
           SUM(dl.qty) AS qty_received
    FROM order_lines_scope ols
    INNER JOIN doc_lines dl ON dl.order_line_id = ols.id
    INNER JOIN docs d ON d.id = dl.doc_id
    WHERE d.status = 'CLOSED'
      AND d.type = 'PRODUCTION_RECEIPT'
      AND dl.qty > 0
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
    GROUP BY dl.order_line_id
),
unlinked_produced_by_item AS (
    SELECT d.order_id,
           dl.item_id,
           SUM(dl.qty) AS qty_received
    FROM docs d
    INNER JOIN order_scope os ON os.id = d.order_id
    INNER JOIN doc_lines dl ON dl.doc_id = d.id
    WHERE d.status = 'CLOSED'
      AND d.type = 'PRODUCTION_RECEIPT'
      AND dl.order_line_id IS NULL
      AND dl.qty > 0
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
    GROUP BY d.order_id,
             dl.item_id
),
production_totals_by_order AS (
    SELECT d.order_id,
           SUM(dl.qty) AS qty_received
    FROM docs d
    INNER JOIN order_scope os ON os.id = d.order_id
    INNER JOIN doc_lines dl ON dl.doc_id = d.id
    WHERE d.status = 'CLOSED'
      AND d.type = 'PRODUCTION_RECEIPT'
      AND dl.qty > 0
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
    GROUP BY d.order_id
),
line_metrics_seed AS (
    SELECT ob.id AS order_id,
           ob.order_type,
           ob.persisted_status,
           ols.id AS order_line_id,
           ols.item_id,
           ols.qty_ordered,
           COALESCE(shipped.qty_shipped, 0) AS qty_shipped,
           COALESCE(reserved.qty_reserved, 0) AS qty_reserved,
           COALESCE(direct_produced.qty_received, 0) AS qty_direct_received,
           COALESCE(unlinked.qty_received, 0) AS qty_unlinked_item_received,
           GREATEST(0, ols.qty_ordered - COALESCE(direct_produced.qty_received, 0)) AS qty_direct_unfilled,
           ROW_NUMBER() OVER (
               PARTITION BY ob.id, ols.item_id
               ORDER BY ols.id DESC
           ) AS item_line_desc_rank,
           COALESCE(SUM(GREATEST(0, ols.qty_ordered - COALESCE(direct_produced.qty_received, 0))) OVER (
               PARTITION BY ob.id, ols.item_id
               ORDER BY ols.id
               ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING
           ), 0) AS qty_direct_unfilled_before
    FROM order_base ob
    LEFT JOIN order_lines_scope ols ON ols.order_id = ob.id
    LEFT JOIN shipped_by_line shipped ON shipped.order_line_id = ols.id
    LEFT JOIN reserved_by_line reserved ON reserved.order_line_id = ols.id
    LEFT JOIN direct_produced_by_line direct_produced ON direct_produced.order_line_id = ols.id
    LEFT JOIN unlinked_produced_by_item unlinked ON unlinked.order_id = ob.id
                                                 AND unlinked.item_id = ols.item_id
),
order_line_metrics AS (
    SELECT order_id,
           order_type,
           persisted_status,
           order_line_id,
           item_id,
           qty_ordered,
           qty_shipped,
           qty_reserved,
           qty_direct_received,
           qty_direct_received
           + CASE
                 WHEN qty_unlinked_item_received <= 0 THEN 0
                 WHEN item_line_desc_rank = 1 THEN GREATEST(0, qty_unlinked_item_received - qty_direct_unfilled_before)
                 ELSE GREATEST(0, LEAST(qty_unlinked_item_received - qty_direct_unfilled_before, qty_direct_unfilled))
             END AS qty_produced_total,
           CASE
               WHEN order_type = 'CUSTOMER' THEN qty_direct_received + qty_reserved
               ELSE qty_direct_received
           END AS qty_customer_ready
    FROM line_metrics_seed
),
status_summary AS (
    SELECT ob.id AS order_id,
           COUNT(olm.order_line_id) AS line_count,
           COALESCE(BOOL_AND(olm.qty_shipped + 0.000001 >= olm.qty_ordered), FALSE) AS fully_shipped,
           COALESCE(BOOL_AND(olm.qty_customer_ready + 0.000001 >= olm.qty_ordered), FALSE) AS fully_customer_ready,
           COALESCE(BOOL_AND(olm.qty_produced_total + 0.000001 >= olm.qty_ordered), FALSE) AS fully_produced,
           COALESCE(BOOL_OR(olm.qty_produced_total > 0.000001), FALSE) AS any_produced,
           COALESCE(MAX(production_totals.qty_received), 0) > 0.000001 AS any_posted_production
    FROM order_base ob
    LEFT JOIN order_line_metrics olm ON olm.order_id = ob.id
    LEFT JOIN production_totals_by_order production_totals ON production_totals.order_id = ob.id
    GROUP BY ob.id
),
doc_summary AS (
    SELECT d.order_id,
           MAX(CASE
               WHEN d.type = 'OUTBOUND' AND d.status = 'CLOSED' THEN d.closed_at
               ELSE NULL
           END) AS outbound_closed_at,
           MAX(CASE
               WHEN d.type = 'PRODUCTION_RECEIPT' AND d.status = 'CLOSED' THEN d.closed_at
               ELSE NULL
           END) AS production_closed_at
    FROM docs d
    INNER JOIN order_scope os ON os.id = d.order_id
    GROUP BY d.order_id
),
order_list_flags AS (
    SELECT ob.id AS order_id,
           COALESCE(BOOL_OR(olm.order_type = 'CUSTOMER'
                            AND olm.qty_ordered - olm.qty_shipped > 0.000001), FALSE) AS has_shipment_remaining,
           COALESCE(BOOL_OR(olm.qty_ordered
                            - CASE
                                  WHEN olm.order_type = 'CUSTOMER' THEN olm.qty_customer_ready
                                  ELSE olm.qty_produced_total
                              END > 0.000001), FALSE) AS has_receipt_remaining
    FROM order_base ob
    LEFT JOIN order_line_metrics olm ON olm.order_id = ob.id
    GROUP BY ob.id
),
pallet_source AS (
    SELECT COALESCE(pp.order_id, MAX(ol.order_id), d.order_id) AS order_id,
           pp.id,
           pp.status,
           pp.planned_qty
    FROM production_pallets pp
    INNER JOIN docs d ON d.id = pp.prd_doc_id
    LEFT JOIN production_pallet_lines pll ON pll.production_pallet_id = pp.id
    LEFT JOIN order_lines ol ON ol.id = pll.order_line_id
    WHERE d.type = 'PRODUCTION_RECEIPT'
    GROUP BY pp.id,
             pp.order_id,
             d.order_id,
             pp.status,
             pp.planned_qty
),
pallet_summary AS (
    SELECT ps.order_id,
           COUNT(*) FILTER (WHERE ps.status <> 'CANCELLED')::int AS planned_pallet_count,
           COALESCE(SUM(ps.planned_qty) FILTER (WHERE ps.status <> 'CANCELLED'), 0)::double precision AS planned_qty,
           COUNT(*) FILTER (WHERE ps.status = 'FILLED')::int AS filled_pallet_count,
           COALESCE(SUM(ps.planned_qty) FILTER (WHERE ps.status = 'FILLED'), 0)::double precision AS filled_qty
    FROM pallet_source ps
    INNER JOIN order_scope os ON os.id = ps.order_id
    GROUP BY ps.order_id
),
markable_line_need AS (
    SELECT olm.order_id,
           olm.item_id,
           NULLIF(BTRIM(i.gtin), '') AS gtin,
           CASE
               WHEN olm.order_type = 'INTERNAL' THEN GREATEST(0, olm.qty_ordered)
               ELSE GREATEST(0, olm.qty_ordered - olm.qty_shipped - olm.qty_reserved)
           END AS qty_for_marking
    FROM order_line_metrics olm
    INNER JOIN items i ON i.id = olm.item_id
    INNER JOIN item_types it ON it.id = i.item_type_id
    WHERE COALESCE(it.enable_marking, FALSE) = TRUE
      AND NULLIF(BTRIM(i.gtin), '') IS NOT NULL
),
markable_item_need AS (
    SELECT order_id,
           item_id,
           gtin,
           SUM(qty_for_marking) AS qty_for_marking
    FROM markable_line_need
    GROUP BY order_id, item_id, gtin
),
needed_marking_keys AS (
    SELECT DISTINCT item_id,
           gtin
    FROM markable_item_need
    WHERE qty_for_marking > 0
),
free_code_stats AS (
    SELECT COALESCE(mo.item_id, 0) AS item_id,
           COALESCE(NULLIF(BTRIM(COALESCE(mo.gtin, c.gtin)), ''), '') AS gtin,
           COUNT(*) AS codes_total
    FROM marking_code c
    INNER JOIN marking_order mo ON mo.id = c.marking_order_id
    WHERE c.status IN (@marking_code_status_reserved, @marking_code_status_printed)
      AND c.receipt_doc_id IS NULL
      AND c.receipt_line_id IS NULL
      AND mo.status NOT IN (@marking_status_cancelled, @marking_status_failed)
      AND (mo.source_type IN (@production_need_source_type, @production_order_source_type)
           OR mo.order_id IS NOT NULL)
      AND EXISTS (
          SELECT 1
          FROM needed_marking_keys need
          WHERE COALESCE(mo.item_id, 0) = need.item_id
             OR (need.gtin IS NOT NULL
                 AND COALESCE(NULLIF(BTRIM(COALESCE(mo.gtin, c.gtin)), ''), '') = COALESCE(need.gtin, ''))
      )
    GROUP BY COALESCE(mo.item_id, 0),
             COALESCE(NULLIF(BTRIM(COALESCE(mo.gtin, c.gtin)), ''), '')
),
bound_code_stats AS (
    SELECT ols.order_id,
           ols.item_id,
           COALESCE(NULLIF(BTRIM(i.gtin), ''), '') AS gtin,
           COUNT(*) AS codes_total
    FROM marking_code c
    INNER JOIN doc_lines dl ON dl.id = c.receipt_line_id
    INNER JOIN order_lines_scope ols ON ols.id = dl.order_line_id
    INNER JOIN items i ON i.id = ols.item_id
    WHERE c.status <> @marking_code_status_voided
    GROUP BY ols.order_id,
             ols.item_id,
             COALESCE(NULLIF(BTRIM(i.gtin), ''), '')
),
marking_rollup AS (
    SELECT ob.id AS order_id,
           EXISTS (
               SELECT 1
               FROM markable_line_need mln
               WHERE mln.order_id = ob.id
           ) AS marking_applies,
           EXISTS (
               SELECT 1
               FROM markable_line_need mln
               WHERE mln.order_id = ob.id
                 AND mln.qty_for_marking > 0
           ) AS marking_required,
           EXISTS (
               SELECT 1
               FROM markable_line_need mln
               WHERE mln.order_id = ob.id
           )
           AND NOT EXISTS (
               SELECT 1
               FROM markable_item_need need
               LEFT JOIN LATERAL (
                   SELECT COALESCE(SUM(free.codes_total), 0) AS total
                   FROM free_code_stats free
                   WHERE free.item_id = need.item_id
                      OR (need.gtin IS NOT NULL AND free.gtin = COALESCE(need.gtin, ''))
               ) free_total ON TRUE
               LEFT JOIN LATERAL (
                   SELECT COALESCE(SUM(bound.codes_total), 0) AS total
                   FROM bound_code_stats bound
                   WHERE bound.order_id = need.order_id
                     AND (bound.item_id = need.item_id
                          OR (need.gtin IS NOT NULL AND bound.gtin = COALESCE(need.gtin, '')))
               ) bound_total ON TRUE
               WHERE need.order_id = ob.id
                 AND need.qty_for_marking > 0
                 AND COALESCE(free_total.total, 0) + COALESCE(bound_total.total, 0) + 0.000001 < need.qty_for_marking
           ) AS marking_completed
    FROM order_base ob
)
SELECT ob.id,
       ob.order_ref,
       ob.order_type,
       ob.partner_id,
       ob.due_date,
       CASE
           WHEN ob.persisted_status = 'CANCELLED' THEN 'CANCELLED'
           WHEN ob.order_type = 'INTERNAL' THEN CASE
               WHEN ob.persisted_status = 'SHIPPED' THEN 'SHIPPED'
               WHEN COALESCE(ss.line_count, 0) > 0 AND COALESCE(ss.fully_produced, FALSE) THEN 'SHIPPED'
               ELSE 'IN_PROGRESS'
           END
           ELSE CASE
               WHEN ob.persisted_status = 'DRAFT' THEN 'DRAFT'
               WHEN COALESCE(ss.line_count, 0) > 0 AND COALESCE(ss.fully_shipped, FALSE) THEN 'SHIPPED'
               WHEN COALESCE(ss.line_count, 0) > 0 AND COALESCE(ss.fully_customer_ready, FALSE) THEN 'ACCEPTED'
               ELSE 'IN_PROGRESS'
           END
       END AS status,
       ob.comment,
       ob.created_at,
       ob.partner_name,
       ob.partner_code,
       ob.bind_reserved_stock,
       ob.marking_status,
       ob.marking_excel_generated_at,
       ob.marking_printed_at,
       COALESCE(mr.marking_required, FALSE),
       COALESCE(mr.marking_applies, FALSE),
       COALESCE(mr.marking_completed, FALSE),
       CASE
           WHEN ob.persisted_status = 'CANCELLED' THEN NULL
           WHEN ob.order_type = 'INTERNAL'
                AND COALESCE(ss.line_count, 0) > 0
                AND COALESCE(ss.fully_produced, FALSE) THEN ds.production_closed_at
           WHEN ob.order_type <> 'INTERNAL'
                AND COALESCE(ss.line_count, 0) > 0
                AND COALESCE(ss.fully_shipped, FALSE) THEN ds.outbound_closed_at
           ELSE NULL
       END AS shipped_at,
       COALESCE(olf.has_shipment_remaining, FALSE) AS has_shipment_remaining,
       COALESCE(olf.has_receipt_remaining, FALSE) AS has_receipt_remaining,
       COALESCE(ps.planned_pallet_count, 0) > 0 AS has_production_pallet_plan,
       COALESCE(ps.planned_pallet_count, 0) AS planned_pallet_count,
       COALESCE(ps.filled_pallet_count, 0) AS filled_pallet_count,
       COALESCE(ps.planned_qty, 0) AS planned_qty,
       COALESCE(ps.filled_qty, 0) AS filled_qty
FROM order_base ob
LEFT JOIN status_summary ss ON ss.order_id = ob.id
LEFT JOIN doc_summary ds ON ds.order_id = ob.id
LEFT JOIN marking_rollup mr ON mr.order_id = ob.id
LEFT JOIN order_list_flags olf ON olf.order_id = ob.id
LEFT JOIN pallet_summary ps ON ps.order_id = ob.id";

    public PostgresDataStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    private PostgresDataStore(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
        _connectionString = connection.ConnectionString;
    }

    public void Initialize()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        EnsureSchemaReady(connection);
    }

    public void ExecuteInTransaction(Action<IDataStore> work)
    {
        if (_connection != null && _transaction != null)
        {
            work(this);
            return;
        }

        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var scoped = new PostgresDataStore(connection, transaction);
        work(scoped);

        transaction.Commit();
    }

    public long CountLedgerEntries()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT COUNT(*) FROM ledger;");
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        });
    }

    public Item? FindItemByBarcode(string barcode)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT i.id, i.name, i.is_active, i.barcode, i.gtin, i.base_uom, i.default_packaging_id, i.brand, i.volume, i.shelf_life_months, i.max_qty_per_hu, i.tara_id, i.is_marked, t.name, i.item_type_id, it.name, it.is_visible_in_product_catalog, it.enable_min_stock_control, COALESCE(it.enable_marking, FALSE), i.min_stock_qty FROM items i LEFT JOIN taras t ON t.id = i.tara_id LEFT JOIN item_types it ON it.id = i.item_type_id WHERE i.barcode = @barcode OR i.gtin = @barcode");
            command.Parameters.AddWithValue("@barcode", barcode);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadItem(reader) : null;
        });
    }

    public Item? FindItemByGtin(string gtin)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT i.id, i.name, i.is_active, i.barcode, i.gtin, i.base_uom, i.default_packaging_id, i.brand, i.volume, i.shelf_life_months, i.max_qty_per_hu, i.tara_id, i.is_marked, t.name, i.item_type_id, it.name, it.is_visible_in_product_catalog, it.enable_min_stock_control, COALESCE(it.enable_marking, FALSE), i.min_stock_qty FROM items i LEFT JOIN taras t ON t.id = i.tara_id LEFT JOIN item_types it ON it.id = i.item_type_id WHERE i.gtin = @gtin");
            command.Parameters.AddWithValue("@gtin", gtin);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadItem(reader) : null;
        });
    }

    public Item? FindItemById(long id)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT i.id, i.name, i.is_active, i.barcode, i.gtin, i.base_uom, i.default_packaging_id, i.brand, i.volume, i.shelf_life_months, i.max_qty_per_hu, i.tara_id, i.is_marked, t.name, i.item_type_id, it.name, it.is_visible_in_product_catalog, it.enable_min_stock_control, COALESCE(it.enable_marking, FALSE), i.min_stock_qty FROM items i LEFT JOIN taras t ON t.id = i.tara_id LEFT JOIN item_types it ON it.id = i.item_type_id WHERE i.id = @id");
            command.Parameters.AddWithValue("@id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadItem(reader) : null;
        });
    }

    public IReadOnlyList<Item> GetItems(string? search)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildItemsQuery(search));
            if (!string.IsNullOrWhiteSpace(search))
            {
                command.Parameters.AddWithValue("@search", $"%{search.Trim()}%");
            }

            using var reader = command.ExecuteReader();
            var items = new List<Item>();
            while (reader.Read())
            {
                items.Add(ReadItem(reader));
            }

            return items;
        });
    }

    public long AddItem(Item item)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO items(name, is_active, barcode, gtin, base_uom, default_packaging_id, brand, volume, shelf_life_months, max_qty_per_hu, tara_id, is_marked, item_type_id, min_stock_qty)
VALUES(@name, @is_active, @barcode, @gtin, @base_uom, @default_packaging_id, @brand, @volume, @shelf_life_months, @max_qty_per_hu, @tara_id, @is_marked, @item_type_id, @min_stock_qty)
RETURNING id;
");
            command.Parameters.AddWithValue("@name", item.Name);
            command.Parameters.AddWithValue("@is_active", item.IsActive);
            command.Parameters.AddWithValue("@barcode", (object?)item.Barcode ?? DBNull.Value);
            command.Parameters.AddWithValue("@gtin", (object?)item.Gtin ?? DBNull.Value);
            command.Parameters.AddWithValue("@base_uom", item.BaseUom);
            command.Parameters.AddWithValue("@default_packaging_id", item.DefaultPackagingId.HasValue ? item.DefaultPackagingId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@brand", string.IsNullOrWhiteSpace(item.Brand) ? DBNull.Value : item.Brand.Trim());
            command.Parameters.AddWithValue("@volume", string.IsNullOrWhiteSpace(item.Volume) ? DBNull.Value : item.Volume.Trim());
            command.Parameters.AddWithValue("@shelf_life_months", item.ShelfLifeMonths.HasValue ? item.ShelfLifeMonths.Value : DBNull.Value);
            command.Parameters.AddWithValue("@max_qty_per_hu", item.MaxQtyPerHu.HasValue ? item.MaxQtyPerHu.Value : DBNull.Value);
            command.Parameters.AddWithValue("@tara_id", item.TaraId.HasValue ? item.TaraId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@is_marked", item.IsMarked ? 1 : 0);
            command.Parameters.AddWithValue("@item_type_id", item.ItemTypeId.HasValue ? item.ItemTypeId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@min_stock_qty", item.MinStockQty.HasValue ? item.MinStockQty.Value : DBNull.Value);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdateItemBarcode(long itemId, string barcode)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE items SET barcode = @barcode WHERE id = @id");
            command.Parameters.AddWithValue("@barcode", barcode);
            command.Parameters.AddWithValue("@id", itemId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void UpdateItem(Item item)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE items
SET name = @name,
    is_active = @is_active,
    barcode = @barcode,
    gtin = @gtin,
    base_uom = @base_uom,
    default_packaging_id = @default_packaging_id,
    brand = @brand,
    volume = @volume,
    shelf_life_months = @shelf_life_months,
    max_qty_per_hu = @max_qty_per_hu,
    tara_id = @tara_id,
    is_marked = @is_marked,
    item_type_id = @item_type_id,
    min_stock_qty = @min_stock_qty
WHERE id = @id;
");
            command.Parameters.AddWithValue("@name", item.Name);
            command.Parameters.AddWithValue("@is_active", item.IsActive);
            command.Parameters.AddWithValue("@barcode", (object?)item.Barcode ?? DBNull.Value);
            command.Parameters.AddWithValue("@gtin", (object?)item.Gtin ?? DBNull.Value);
            command.Parameters.AddWithValue("@base_uom", item.BaseUom);
            command.Parameters.AddWithValue("@default_packaging_id", item.DefaultPackagingId.HasValue ? item.DefaultPackagingId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@brand", string.IsNullOrWhiteSpace(item.Brand) ? DBNull.Value : item.Brand.Trim());
            command.Parameters.AddWithValue("@volume", string.IsNullOrWhiteSpace(item.Volume) ? DBNull.Value : item.Volume.Trim());
            command.Parameters.AddWithValue("@shelf_life_months", item.ShelfLifeMonths.HasValue ? item.ShelfLifeMonths.Value : DBNull.Value);
            command.Parameters.AddWithValue("@max_qty_per_hu", item.MaxQtyPerHu.HasValue ? item.MaxQtyPerHu.Value : DBNull.Value);
            command.Parameters.AddWithValue("@tara_id", item.TaraId.HasValue ? item.TaraId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@is_marked", item.IsMarked ? 1 : 0);
            command.Parameters.AddWithValue("@item_type_id", item.ItemTypeId.HasValue ? item.ItemTypeId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@min_stock_qty", item.MinStockQty.HasValue ? item.MinStockQty.Value : DBNull.Value);
            command.Parameters.AddWithValue("@id", item.Id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void DeleteItem(long itemId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM items WHERE id = @id");
            command.Parameters.AddWithValue("@id", itemId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public bool IsItemUsed(long itemId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT 1 FROM doc_lines WHERE item_id = @id LIMIT 1");
            command.Parameters.AddWithValue("@id", itemId);
            if (command.ExecuteScalar() != null)
            {
                return true;
            }

            using var orderCommand = CreateCommand(connection, "SELECT 1 FROM order_lines WHERE item_id = @id LIMIT 1");
            orderCommand.Parameters.AddWithValue("@id", itemId);
            if (orderCommand.ExecuteScalar() != null)
            {
                return true;
            }

            using var ledgerCommand = CreateCommand(connection, "SELECT 1 FROM ledger WHERE item_id = @id LIMIT 1");
            ledgerCommand.Parameters.AddWithValue("@id", itemId);
            return ledgerCommand.ExecuteScalar() != null;
        });
    }

    public void UpdateItemDefaultPackaging(long itemId, long? packagingId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE items SET default_packaging_id = @packaging_id WHERE id = @id");
            command.Parameters.AddWithValue("@packaging_id", packagingId.HasValue ? packagingId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@id", itemId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public IReadOnlyList<ItemPackaging> GetItemPackagings(long itemId, bool includeInactive)
    {
        return WithConnection(connection =>
        {
            var sql = @"
SELECT id, item_id, code, name, factor_to_base, is_active, sort_order
FROM item_packaging
WHERE item_id = @item_id";
            if (!includeInactive)
            {
                sql += " AND is_active = 1";
            }
            sql += " ORDER BY sort_order, name;";

            using var command = CreateCommand(connection, sql);
            command.Parameters.AddWithValue("@item_id", itemId);
            using var reader = command.ExecuteReader();
            var list = new List<ItemPackaging>();
            while (reader.Read())
            {
                list.Add(ReadItemPackaging(reader));
            }

            return list;
        });
    }

    public ItemPackaging? GetItemPackaging(long packagingId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT id, item_id, code, name, factor_to_base, is_active, sort_order
FROM item_packaging
WHERE id = @id;");
            command.Parameters.AddWithValue("@id", packagingId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadItemPackaging(reader) : null;
        });
    }

    public ItemPackaging? FindItemPackagingByCode(long itemId, string code)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT id, item_id, code, name, factor_to_base, is_active, sort_order
FROM item_packaging
WHERE item_id = @item_id AND code = @code;");
            command.Parameters.AddWithValue("@item_id", itemId);
            command.Parameters.AddWithValue("@code", code);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadItemPackaging(reader) : null;
        });
    }

    public long AddItemPackaging(ItemPackaging packaging)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO item_packaging(item_id, code, name, factor_to_base, is_active, sort_order)
VALUES(@item_id, @code, @name, @factor_to_base, @is_active, @sort_order)
RETURNING id;
");
            command.Parameters.AddWithValue("@item_id", packaging.ItemId);
            command.Parameters.AddWithValue("@code", packaging.Code);
            command.Parameters.AddWithValue("@name", packaging.Name);
            command.Parameters.AddWithValue("@factor_to_base", packaging.FactorToBase);
            command.Parameters.AddWithValue("@is_active", packaging.IsActive ? 1 : 0);
            command.Parameters.AddWithValue("@sort_order", packaging.SortOrder);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdateItemPackaging(ItemPackaging packaging)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE item_packaging
SET item_id = @item_id,
    code = @code,
    name = @name,
    factor_to_base = @factor_to_base,
    is_active = @is_active,
    sort_order = @sort_order
WHERE id = @id;
");
            command.Parameters.AddWithValue("@item_id", packaging.ItemId);
            command.Parameters.AddWithValue("@code", packaging.Code);
            command.Parameters.AddWithValue("@name", packaging.Name);
            command.Parameters.AddWithValue("@factor_to_base", packaging.FactorToBase);
            command.Parameters.AddWithValue("@is_active", packaging.IsActive ? 1 : 0);
            command.Parameters.AddWithValue("@sort_order", packaging.SortOrder);
            command.Parameters.AddWithValue("@id", packaging.Id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void DeactivateItemPackaging(long packagingId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE item_packaging SET is_active = 0 WHERE id = @id");
            command.Parameters.AddWithValue("@id", packagingId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public Location? FindLocationByCode(string code)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, code, name, max_hu_slots, auto_hu_distribution_enabled FROM locations WHERE code = @code");
            command.Parameters.AddWithValue("@code", code);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadLocation(reader) : null;
        });
    }

    public Location? FindLocationById(long id)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, code, name, max_hu_slots, auto_hu_distribution_enabled FROM locations WHERE id = @id");
            command.Parameters.AddWithValue("@id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadLocation(reader) : null;
        });
    }

    public IReadOnlyList<Location> GetLocations()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, code, name, max_hu_slots, auto_hu_distribution_enabled FROM locations ORDER BY code");
            using var reader = command.ExecuteReader();
            var locations = new List<Location>();
            while (reader.Read())
            {
                locations.Add(ReadLocation(reader));
            }

            return locations;
        });
    }

    public long AddLocation(Location location)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO locations(code, name, max_hu_slots, auto_hu_distribution_enabled)
VALUES(@code, @name, @max_hu_slots, @auto_hu_distribution_enabled)
RETURNING id;
");
            command.Parameters.AddWithValue("@code", location.Code);
            command.Parameters.AddWithValue("@name", location.Name);
            command.Parameters.AddWithValue("@max_hu_slots", location.MaxHuSlots.HasValue ? location.MaxHuSlots.Value : DBNull.Value);
            command.Parameters.AddWithValue("@auto_hu_distribution_enabled", location.AutoHuDistributionEnabled);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdateLocation(Location location)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE locations
SET code = @code,
    name = @name,
    max_hu_slots = @max_hu_slots,
    auto_hu_distribution_enabled = @auto_hu_distribution_enabled
WHERE id = @id;
");
            command.Parameters.AddWithValue("@code", location.Code);
            command.Parameters.AddWithValue("@name", location.Name);
            command.Parameters.AddWithValue("@max_hu_slots", location.MaxHuSlots.HasValue ? location.MaxHuSlots.Value : DBNull.Value);
            command.Parameters.AddWithValue("@auto_hu_distribution_enabled", location.AutoHuDistributionEnabled);
            command.Parameters.AddWithValue("@id", location.Id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void DeleteLocation(long locationId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM locations WHERE id = @id");
            command.Parameters.AddWithValue("@id", locationId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public bool IsLocationUsed(long locationId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT 1
FROM doc_lines
WHERE from_location_id = @id OR to_location_id = @id
LIMIT 1;
");
            command.Parameters.AddWithValue("@id", locationId);
            if (command.ExecuteScalar() != null)
            {
                return true;
            }

            using var ledgerCommand = CreateCommand(connection, "SELECT 1 FROM ledger WHERE location_id = @id LIMIT 1");
            ledgerCommand.Parameters.AddWithValue("@id", locationId);
            return ledgerCommand.ExecuteScalar() != null;
        });
    }

    public IReadOnlyList<Uom> GetUoms()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, name FROM uoms ORDER BY name");
            using var reader = command.ExecuteReader();
            var uoms = new List<Uom>();
            while (reader.Read())
            {
                uoms.Add(ReadUom(reader));
            }

            return uoms;
        });
    }

    public long AddUom(Uom uom)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO uoms(name)
VALUES(@name)
RETURNING id;
");
            command.Parameters.AddWithValue("@name", uom.Name);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public IReadOnlyList<WriteOffReason> GetWriteOffReasons()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, code, name FROM write_off_reasons ORDER BY name, code");
            using var reader = command.ExecuteReader();
            var reasons = new List<WriteOffReason>();
            while (reader.Read())
            {
                reasons.Add(ReadWriteOffReason(reader));
            }

            return reasons;
        });
    }

    public long AddWriteOffReason(WriteOffReason reason)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO write_off_reasons(code, name)
VALUES(@code, @name)
RETURNING id;
");
            command.Parameters.AddWithValue("@code", reason.Code);
            command.Parameters.AddWithValue("@name", reason.Name);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void DeleteWriteOffReason(long reasonId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM write_off_reasons WHERE id = @id");
            command.Parameters.AddWithValue("@id", reasonId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void DeleteUom(long uomId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM uoms WHERE id = @id");
            command.Parameters.AddWithValue("@id", uomId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public bool IsUomUsed(long uomId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT 1
FROM items i
JOIN uoms u ON LOWER(i.base_uom) = LOWER(u.name)
WHERE u.id = @id
LIMIT 1;
");
            command.Parameters.AddWithValue("@id", uomId);
            return command.ExecuteScalar() != null;
        });
    }

    public IReadOnlyList<Tara> GetTaras()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, name FROM taras ORDER BY name");
            using var reader = command.ExecuteReader();
            var list = new List<Tara>();
            while (reader.Read())
            {
                list.Add(ReadTara(reader));
            }

            return list;
        });
    }

    public long AddTara(Tara tara)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"INSERT INTO taras(name)
VALUES(@name)
RETURNING id;");
            command.Parameters.AddWithValue("@name", tara.Name);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdateTara(Tara tara)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE taras SET name = @name WHERE id = @id");
            command.Parameters.AddWithValue("@name", tara.Name);
            command.Parameters.AddWithValue("@id", tara.Id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void DeleteTara(long taraId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM taras WHERE id = @id");
            command.Parameters.AddWithValue("@id", taraId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public bool IsTaraUsed(long taraId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT 1 FROM items WHERE tara_id = @id LIMIT 1");
            command.Parameters.AddWithValue("@id", taraId);
            return command.ExecuteScalar() != null;
        });
    }

    public IReadOnlyList<ItemType> GetItemTypes(bool includeInactive)
    {
        return WithConnection(connection =>
        {
            var sql = @"
SELECT id, name, code, sort_order, is_active, is_visible_in_product_catalog, enable_min_stock_control, min_stock_uses_order_binding, enable_order_reservation, enable_hu_distribution, COALESCE(enable_marking, FALSE)
FROM item_types";
            if (!includeInactive)
            {
                sql += " WHERE is_active = TRUE";
            }

            sql += " ORDER BY sort_order, name;";
            using var command = CreateCommand(connection, sql);
            using var reader = command.ExecuteReader();
            var list = new List<ItemType>();
            while (reader.Read())
            {
                list.Add(ReadItemType(reader));
            }

            return list;
        });
    }

    public ItemType? GetItemType(long id)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT id, name, code, sort_order, is_active, is_visible_in_product_catalog, enable_min_stock_control, min_stock_uses_order_binding, enable_order_reservation, enable_hu_distribution, COALESCE(enable_marking, FALSE)
FROM item_types
WHERE id = @id
LIMIT 1;");
            command.Parameters.AddWithValue("@id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadItemType(reader) : null;
        });
    }

    public long AddItemType(ItemType itemType)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO item_types(name, code, sort_order, is_active, is_visible_in_product_catalog, enable_min_stock_control, min_stock_uses_order_binding, enable_order_reservation, enable_hu_distribution, enable_marking)
VALUES(@name, @code, @sort_order, @is_active, @is_visible_in_product_catalog, @enable_min_stock_control, @min_stock_uses_order_binding, @enable_order_reservation, @enable_hu_distribution, @enable_marking)
RETURNING id;");
            command.Parameters.AddWithValue("@name", itemType.Name);
            command.Parameters.AddWithValue("@code", string.IsNullOrWhiteSpace(itemType.Code) ? DBNull.Value : itemType.Code.Trim());
            command.Parameters.AddWithValue("@sort_order", itemType.SortOrder);
            command.Parameters.AddWithValue("@is_active", itemType.IsActive);
            command.Parameters.AddWithValue("@is_visible_in_product_catalog", itemType.IsVisibleInProductCatalog);
            command.Parameters.AddWithValue("@enable_min_stock_control", itemType.EnableMinStockControl);
            command.Parameters.AddWithValue("@min_stock_uses_order_binding", itemType.MinStockUsesOrderBinding);
            command.Parameters.AddWithValue("@enable_order_reservation", itemType.EnableOrderReservation);
            command.Parameters.AddWithValue("@enable_hu_distribution", itemType.EnableHuDistribution);
            command.Parameters.AddWithValue("@enable_marking", itemType.EnableMarking);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdateItemType(ItemType itemType)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE item_types
SET name = @name,
    code = @code,
    sort_order = @sort_order,
    is_active = @is_active,
    is_visible_in_product_catalog = @is_visible_in_product_catalog,
    enable_min_stock_control = @enable_min_stock_control,
    min_stock_uses_order_binding = @min_stock_uses_order_binding,
    enable_order_reservation = @enable_order_reservation,
    enable_hu_distribution = @enable_hu_distribution,
    enable_marking = @enable_marking
WHERE id = @id;");
            command.Parameters.AddWithValue("@name", itemType.Name);
            command.Parameters.AddWithValue("@code", string.IsNullOrWhiteSpace(itemType.Code) ? DBNull.Value : itemType.Code.Trim());
            command.Parameters.AddWithValue("@sort_order", itemType.SortOrder);
            command.Parameters.AddWithValue("@is_active", itemType.IsActive);
            command.Parameters.AddWithValue("@is_visible_in_product_catalog", itemType.IsVisibleInProductCatalog);
            command.Parameters.AddWithValue("@enable_min_stock_control", itemType.EnableMinStockControl);
            command.Parameters.AddWithValue("@min_stock_uses_order_binding", itemType.MinStockUsesOrderBinding);
            command.Parameters.AddWithValue("@enable_order_reservation", itemType.EnableOrderReservation);
            command.Parameters.AddWithValue("@enable_hu_distribution", itemType.EnableHuDistribution);
            command.Parameters.AddWithValue("@enable_marking", itemType.EnableMarking);
            command.Parameters.AddWithValue("@id", itemType.Id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void DeleteItemType(long itemTypeId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM item_types WHERE id = @id");
            command.Parameters.AddWithValue("@id", itemTypeId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void DeactivateItemType(long itemTypeId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE item_types SET is_active = FALSE WHERE id = @id");
            command.Parameters.AddWithValue("@id", itemTypeId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public bool IsItemTypeUsed(long itemTypeId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT 1 FROM items WHERE item_type_id = @id LIMIT 1");
            command.Parameters.AddWithValue("@id", itemTypeId);
            return command.ExecuteScalar() != null;
        });
    }

    public Partner? GetPartner(long id)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, name, code, created_at FROM partners WHERE id = @id");
            command.Parameters.AddWithValue("@id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadPartner(reader) : null;
        });
    }

    public Partner? FindPartnerByCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, name, code, created_at FROM partners WHERE code = @code LIMIT 1");
            command.Parameters.AddWithValue("@code", code.Trim());
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadPartner(reader) : null;
        });
    }

    public IReadOnlyList<Partner> GetPartners()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, name, code, created_at FROM partners ORDER BY name");
            using var reader = command.ExecuteReader();
            var partners = new List<Partner>();
            while (reader.Read())
            {
                partners.Add(ReadPartner(reader));
            }

            return partners;
        });
    }

    public long AddPartner(Partner partner)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO partners(name, code, created_at)
VALUES(@name, @code, @created_at)
RETURNING id;
");
            command.Parameters.AddWithValue("@name", partner.Name);
            command.Parameters.AddWithValue("@code", (object?)partner.Code ?? DBNull.Value);
            command.Parameters.AddWithValue("@created_at", ToDbDate(partner.CreatedAt));
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdatePartner(Partner partner)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE partners
SET name = @name,
    code = @code
WHERE id = @id;
");
            command.Parameters.AddWithValue("@name", partner.Name);
            command.Parameters.AddWithValue("@code", (object?)partner.Code ?? DBNull.Value);
            command.Parameters.AddWithValue("@id", partner.Id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void DeletePartner(long partnerId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM partners WHERE id = @id");
            command.Parameters.AddWithValue("@id", partnerId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public bool IsPartnerUsed(long partnerId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT 1 FROM docs WHERE partner_id = @id LIMIT 1");
            command.Parameters.AddWithValue("@id", partnerId);
            if (command.ExecuteScalar() != null)
            {
                return true;
            }

            using var orderCommand = CreateCommand(connection, "SELECT 1 FROM orders WHERE partner_id = @id LIMIT 1");
            orderCommand.Parameters.AddWithValue("@id", partnerId);
            return orderCommand.ExecuteScalar() != null;
        });
    }

    public Doc? FindDocByRef(string docRef)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, $"{DocSelectBase} WHERE d.doc_ref = @doc_ref");
            command.Parameters.AddWithValue("@doc_ref", docRef);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadDoc(reader) : null;
        });
    }

    public Doc? GetDoc(long id)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, $"{DocSelectBase} WHERE d.id = @id");
            command.Parameters.AddWithValue("@id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadDoc(reader) : null;
        });
    }

    public IReadOnlyList<Doc> GetDocs()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, $"{DocSelectBase} ORDER BY d.created_at DESC");
            using var reader = command.ExecuteReader();
            var docs = new List<Doc>();
            while (reader.Read())
            {
                docs.Add(ReadDoc(reader));
            }

            return docs;
        });
    }

    public IReadOnlyList<Doc> GetDocsByOrder(long orderId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, $"{DocSelectBase} WHERE d.order_id = @order_id ORDER BY d.created_at DESC");
            command.Parameters.AddWithValue("@order_id", orderId);
            using var reader = command.ExecuteReader();
            var docs = new List<Doc>();
            while (reader.Read())
            {
                docs.Add(ReadDoc(reader));
            }

            return docs;
        });
    }

    public int GetMaxDocRefSequenceByYear(int year)
    {
        if (year <= 0)
        {
            return 0;
        }

        return WithConnection(connection =>
        {
            var yearToken = year.ToString(CultureInfo.InvariantCulture);
            using var command = CreateCommand(connection, "SELECT doc_ref FROM docs WHERE doc_ref LIKE @pattern");
            command.Parameters.AddWithValue("@pattern", $"%-{yearToken}-%");
            using var reader = command.ExecuteReader();

            var max = 0;
            while (reader.Read())
            {
                var docRef = reader.GetString(0);
                if (string.IsNullOrWhiteSpace(docRef))
                {
                    continue;
                }

                var parts = docRef.Split('-', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                {
                    continue;
                }

                if (!string.Equals(parts[1], yearToken, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var suffix = parts[^1];
                if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                    && value > max)
                {
                    max = value;
                }
            }

            return max;
        });
    }

    public bool IsDocRefSequenceTaken(int year, int sequence)
    {
        if (year <= 0 || sequence <= 0)
        {
            return false;
        }

        return WithConnection(connection =>
        {
            var yearToken = year.ToString(CultureInfo.InvariantCulture);
            using var command = CreateCommand(connection, "SELECT doc_ref FROM docs WHERE doc_ref LIKE @pattern");
            command.Parameters.AddWithValue("@pattern", $"%-{yearToken}-%");
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var docRef = reader.GetString(0);
                if (string.IsNullOrWhiteSpace(docRef))
                {
                    continue;
                }

                var parts = docRef.Split('-', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                {
                    continue;
                }

                if (!string.Equals(parts[1], yearToken, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var suffix = parts[^1];
                if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                    && value == sequence)
                {
                    return true;
                }
            }

            return false;
        });
    }

    public long AddDoc(Doc doc)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO docs(doc_ref, type, status, created_at, closed_at, partner_id, order_id, order_ref, shipping_ref, reason_code, comment, production_batch_no)
VALUES(@doc_ref, @type, @status, @created_at, @closed_at, @partner_id, @order_id, @order_ref, @shipping_ref, @reason_code, @comment, @production_batch_no)
RETURNING id;
");
            command.Parameters.AddWithValue("@doc_ref", doc.DocRef);
            command.Parameters.AddWithValue("@type", DocTypeMapper.ToOpString(doc.Type));
            command.Parameters.AddWithValue("@status", DocTypeMapper.StatusToString(doc.Status));
            command.Parameters.AddWithValue("@created_at", ToDbDate(doc.CreatedAt));
            command.Parameters.AddWithValue("@closed_at", doc.ClosedAt.HasValue ? ToDbDate(doc.ClosedAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@partner_id", doc.PartnerId.HasValue ? doc.PartnerId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@order_id", doc.OrderId.HasValue ? doc.OrderId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@order_ref", string.IsNullOrWhiteSpace(doc.OrderRef) ? DBNull.Value : doc.OrderRef);
            command.Parameters.AddWithValue("@shipping_ref", string.IsNullOrWhiteSpace(doc.ShippingRef) ? DBNull.Value : doc.ShippingRef);
            command.Parameters.AddWithValue("@reason_code", string.IsNullOrWhiteSpace(doc.ReasonCode) ? DBNull.Value : doc.ReasonCode);
            command.Parameters.AddWithValue("@comment", string.IsNullOrWhiteSpace(doc.Comment) ? DBNull.Value : doc.Comment);
            command.Parameters.AddWithValue("@production_batch_no", string.IsNullOrWhiteSpace(doc.ProductionBatchNo) ? DBNull.Value : doc.ProductionBatchNo);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void DeleteDoc(long docId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM docs WHERE id = @id");
            command.Parameters.AddWithValue("@id", docId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public IReadOnlyList<DocLine> GetDocLines(long docId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT dl.id,
       dl.doc_id,
       dl.replaces_line_id,
       dl.order_line_id,
       dl.production_purpose,
       dl.item_id,
       dl.qty,
       dl.qty_input,
       dl.uom_code,
       dl.from_location_id,
       dl.to_location_id,
       dl.from_hu,
       dl.to_hu,
       dl.pack_single_hu
FROM doc_lines dl
WHERE dl.doc_id = @doc_id
  AND dl.qty > 0
  AND NOT EXISTS (
      SELECT 1
      FROM doc_lines newer
      WHERE newer.replaces_line_id = dl.id
  )
ORDER BY dl.id");
            command.Parameters.AddWithValue("@doc_id", docId);
            using var reader = command.ExecuteReader();
            var lines = new List<DocLine>();
            while (reader.Read())
            {
                lines.Add(ReadDocLine(reader));
            }

            return lines;
        });
    }

    public int CountLedgerEntriesByDocId(long docId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT COUNT(*) FROM ledger WHERE doc_id = @doc_id");
            command.Parameters.AddWithValue("@doc_id", docId);
            var result = command.ExecuteScalar();
            return result == null || result == DBNull.Value
                ? 0
                : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        });
    }

    public IReadOnlyList<DocLineView> GetDocLineViews(long docId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT dl.id, dl.order_line_id, dl.production_purpose, dl.item_id, i.name, i.barcode, dl.qty, dl.qty_input, dl.uom_code, i.base_uom, lf.code, lt.code, dl.from_hu, dl.to_hu, dl.pack_single_hu
FROM doc_lines dl
INNER JOIN items i ON i.id = dl.item_id
LEFT JOIN locations lf ON lf.id = dl.from_location_id
LEFT JOIN locations lt ON lt.id = dl.to_location_id
WHERE dl.doc_id = @doc_id
  AND dl.qty > 0
  AND NOT EXISTS (
      SELECT 1
      FROM doc_lines newer
      WHERE newer.replaces_line_id = dl.id
  )
ORDER BY dl.id;
");
            command.Parameters.AddWithValue("@doc_id", docId);
            using var reader = command.ExecuteReader();
            var lines = new List<DocLineView>();
            while (reader.Read())
            {
                lines.Add(new DocLineView
                {
                    Id = reader.GetInt64(0),
                    OrderLineId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    ProductionPurpose = ProductionLinePurposeMapper.FromDbValue(reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(1) ? null : reader.GetInt64(1)),
                    ItemId = reader.GetInt64(3),
                    ItemName = reader.GetString(4),
                    Barcode = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Qty = reader.GetDouble(6),
                    QtyInput = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                    UomCode = reader.IsDBNull(8) ? null : reader.GetString(8),
                    BaseUom = reader.IsDBNull(9) ? "èâ" : reader.GetString(9),
                    FromLocation = reader.IsDBNull(10) ? null : reader.GetString(10),
                    ToLocation = reader.IsDBNull(11) ? null : reader.GetString(11),
                    FromHu = reader.IsDBNull(12) ? null : reader.GetString(12),
                    ToHu = reader.IsDBNull(13) ? null : reader.GetString(13),
                    PackSingleHu = !reader.IsDBNull(14) && reader.GetBoolean(14)
                });
            }

            return lines;
        });
    }

    public long AddDocLine(DocLine line)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO doc_lines(doc_id, replaces_line_id, order_line_id, production_purpose, item_id, qty, qty_input, uom_code, from_location_id, to_location_id, from_hu, to_hu, pack_single_hu)
VALUES(@doc_id, @replaces_line_id, @order_line_id, @production_purpose, @item_id, @qty, @qty_input, @uom_code, @from_location_id, @to_location_id, @from_hu, @to_hu, @pack_single_hu)
RETURNING id;
");
            command.Parameters.AddWithValue("@doc_id", line.DocId);
            command.Parameters.AddWithValue("@replaces_line_id", line.ReplacesLineId.HasValue ? line.ReplacesLineId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@order_line_id", line.OrderLineId.HasValue ? line.OrderLineId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@production_purpose", ProductionLinePurposeMapper.ToDbValue(line.ProductionPurpose));
            command.Parameters.AddWithValue("@item_id", line.ItemId);
            command.Parameters.AddWithValue("@qty", line.Qty);
            command.Parameters.AddWithValue("@qty_input", line.QtyInput.HasValue ? line.QtyInput.Value : DBNull.Value);
            command.Parameters.AddWithValue("@uom_code", string.IsNullOrWhiteSpace(line.UomCode) ? DBNull.Value : line.UomCode);
            command.Parameters.AddWithValue("@from_location_id", (object?)line.FromLocationId ?? DBNull.Value);
            command.Parameters.AddWithValue("@to_location_id", (object?)line.ToLocationId ?? DBNull.Value);
            command.Parameters.AddWithValue("@from_hu", string.IsNullOrWhiteSpace(line.FromHu) ? DBNull.Value : line.FromHu);
            command.Parameters.AddWithValue("@to_hu", string.IsNullOrWhiteSpace(line.ToHu) ? DBNull.Value : line.ToHu);
            command.Parameters.AddWithValue("@pack_single_hu", line.PackSingleHu);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdateDocLineQty(long docLineId, double qty, double? qtyInput, string? uomCode)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE doc_lines SET qty = @qty, qty_input = @qty_input, uom_code = @uom_code WHERE id = @id");
            command.Parameters.AddWithValue("@qty", qty);
            command.Parameters.AddWithValue("@qty_input", qtyInput.HasValue ? qtyInput.Value : DBNull.Value);
            command.Parameters.AddWithValue("@uom_code", string.IsNullOrWhiteSpace(uomCode) ? DBNull.Value : uomCode);
            command.Parameters.AddWithValue("@id", docLineId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void UpdateDocLineHu(long docLineId, string? fromHu, string? toHu)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE doc_lines SET from_hu = @from_hu, to_hu = @to_hu WHERE id = @id");
            command.Parameters.AddWithValue("@from_hu", string.IsNullOrWhiteSpace(fromHu) ? DBNull.Value : fromHu);
            command.Parameters.AddWithValue("@to_hu", string.IsNullOrWhiteSpace(toHu) ? DBNull.Value : toHu);
            command.Parameters.AddWithValue("@id", docLineId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void UpdateDocLinePackSingleHu(long docLineId, bool packSingleHu)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE doc_lines SET pack_single_hu = @pack_single_hu WHERE id = @id");
            command.Parameters.AddWithValue("@pack_single_hu", packSingleHu);
            command.Parameters.AddWithValue("@id", docLineId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void UpdateDocLineOrderLineId(long docLineId, long? orderLineId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE doc_lines SET order_line_id = @order_line_id WHERE id = @id");
            command.Parameters.AddWithValue("@order_line_id", orderLineId.HasValue ? orderLineId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@id", docLineId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void DeleteDocLine(long docLineId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM doc_lines WHERE id = @id");
            command.Parameters.AddWithValue("@id", docLineId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void DeleteDocLines(long docId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM doc_lines WHERE doc_id = @doc_id");
            command.Parameters.AddWithValue("@doc_id", docId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void UpdateDocHeader(long docId, long? partnerId, string? orderRef, string? shippingRef)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE docs
SET partner_id = @partner_id,
    order_ref = @order_ref,
    shipping_ref = @shipping_ref
WHERE id = @id
");
            command.Parameters.AddWithValue("@partner_id", partnerId.HasValue ? partnerId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@order_ref", string.IsNullOrWhiteSpace(orderRef) ? DBNull.Value : orderRef);
            command.Parameters.AddWithValue("@shipping_ref", string.IsNullOrWhiteSpace(shippingRef) ? DBNull.Value : shippingRef);
            command.Parameters.AddWithValue("@id", docId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void UpdateDocReason(long docId, string? reasonCode)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE docs
SET reason_code = @reason_code
WHERE id = @id;
");
            command.Parameters.AddWithValue("@reason_code", string.IsNullOrWhiteSpace(reasonCode) ? DBNull.Value : reasonCode);
            command.Parameters.AddWithValue("@id", docId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void UpdateDocComment(long docId, string? comment)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE docs
SET comment = @comment
WHERE id = @id;
");
            command.Parameters.AddWithValue("@comment", string.IsNullOrWhiteSpace(comment) ? DBNull.Value : comment);
            command.Parameters.AddWithValue("@id", docId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void UpdateDocProductionBatch(long docId, string? productionBatchNo)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE docs
SET production_batch_no = @production_batch_no
WHERE id = @id;
");
            command.Parameters.AddWithValue("@production_batch_no", string.IsNullOrWhiteSpace(productionBatchNo) ? DBNull.Value : productionBatchNo);
            command.Parameters.AddWithValue("@id", docId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void UpdateDocOrder(long docId, long? orderId, string? orderRef)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE docs
SET order_id = @order_id,
    order_ref = @order_ref
WHERE id = @id;
");
            command.Parameters.AddWithValue("@order_id", orderId.HasValue ? orderId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@order_ref", string.IsNullOrWhiteSpace(orderRef) ? DBNull.Value : orderRef);
            command.Parameters.AddWithValue("@id", docId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void UpdateDocStatus(long docId, DocStatus status, DateTime? closedAt)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE docs SET status = @status, closed_at = @closed_at WHERE id = @id");
            command.Parameters.AddWithValue("@status", DocTypeMapper.StatusToString(status));
            command.Parameters.AddWithValue("@closed_at", closedAt.HasValue ? ToDbDate(closedAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@id", docId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public IReadOnlyList<ProductionPallet> PlanProductionPallets(long docId, DateTime createdAt)
    {
        return WithConnection(connection =>
        {
            using (var command = CreateCommand(connection, @"
INSERT INTO production_pallets(
    prd_doc_id,
    doc_line_id,
    order_id,
    order_line_id,
    item_id,
    hu_code,
    planned_qty,
    to_location_id,
    status,
    created_at)
WITH active_lines AS (
    SELECT d.id AS prd_doc_id,
           d.order_id,
           dl.id AS doc_line_id,
           dl.order_line_id,
           dl.item_id,
           BTRIM(dl.to_hu) AS hu_code,
           dl.qty,
           dl.to_location_id,
           UPPER(BTRIM(dl.to_hu)) AS hu_key
    FROM docs d
    INNER JOIN doc_lines dl ON dl.doc_id = d.id
    WHERE d.id = @doc_id
      AND d.type = @doc_type
      AND d.status <> @closed_status
      AND dl.qty > 0
      AND dl.to_hu IS NOT NULL
      AND BTRIM(dl.to_hu) <> ''
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
),
grouped AS (
    SELECT prd_doc_id,
           hu_key,
           COUNT(DISTINCT order_line_id) AS order_line_count,
           SUM(qty) AS total_qty
    FROM active_lines
    GROUP BY prd_doc_id, hu_key
),
representative AS (
    SELECT al.*,
           ROW_NUMBER() OVER (PARTITION BY al.prd_doc_id, al.hu_key ORDER BY al.doc_line_id) AS rn
    FROM active_lines al
)
SELECT al.prd_doc_id,
       al.doc_line_id,
       al.order_id,
       CASE WHEN g.order_line_count = 1 THEN al.order_line_id ELSE NULL END,
       al.item_id,
       al.hu_code,
       g.total_qty,
       al.to_location_id,
       @status,
       @created_at
FROM representative al
INNER JOIN grouped g ON g.prd_doc_id = al.prd_doc_id AND g.hu_key = al.hu_key
WHERE rn = 1
  AND NOT EXISTS (
      SELECT 1
      FROM production_pallets existing
      WHERE existing.prd_doc_id = al.prd_doc_id
        AND UPPER(BTRIM(existing.hu_code)) = UPPER(BTRIM(al.hu_code))
        AND existing.status <> @cancelled_status
  );
"))
            {
                command.Parameters.AddWithValue("@doc_id", docId);
                command.Parameters.AddWithValue("@doc_type", DocTypeMapper.ToOpString(DocType.ProductionReceipt));
                command.Parameters.AddWithValue("@closed_status", DocTypeMapper.StatusToString(DocStatus.Closed));
                command.Parameters.AddWithValue("@status", ProductionPalletStatus.Planned);
                command.Parameters.AddWithValue("@cancelled_status", ProductionPalletStatus.Cancelled);
                command.Parameters.AddWithValue("@created_at", ToDbDate(createdAt));
                command.ExecuteNonQuery();
            }

            using (var command = CreateCommand(connection, @"
INSERT INTO production_pallet_lines(
    production_pallet_id,
    doc_line_id,
    order_line_id,
    item_id,
    planned_qty,
    filled_qty,
    created_at)
SELECT p.id,
       dl.id,
       dl.order_line_id,
       dl.item_id,
       dl.qty,
       CASE WHEN p.status = @filled_status THEN dl.qty ELSE 0 END,
       @created_at
FROM production_pallets p
INNER JOIN doc_lines dl ON dl.doc_id = p.prd_doc_id
                       AND UPPER(BTRIM(dl.to_hu)) = UPPER(BTRIM(p.hu_code))
WHERE p.prd_doc_id = @doc_id
  AND p.status <> @cancelled_status
  AND dl.qty > 0
  AND NOT EXISTS (
      SELECT 1
      FROM doc_lines newer
      WHERE newer.replaces_line_id = dl.id
  )
  AND NOT EXISTS (
      SELECT 1
      FROM production_pallet_lines existing
      WHERE existing.production_pallet_id = p.id
        AND existing.doc_line_id = dl.id
  );
"))
            {
                command.Parameters.AddWithValue("@doc_id", docId);
                command.Parameters.AddWithValue("@filled_status", ProductionPalletStatus.Filled);
                command.Parameters.AddWithValue("@cancelled_status", ProductionPalletStatus.Cancelled);
                command.Parameters.AddWithValue("@created_at", ToDbDate(createdAt));
                command.ExecuteNonQuery();
            }

            return GetProductionPalletsByDoc(connection, docId);
        });
    }

    public string CreateProductionPalletHuCode(string? createdBy)
    {
        return WithConnection(connection =>
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                using var next = CreateCommand(connection, "SELECT nextval('hu_code_seq');");
                var value = Convert.ToInt64(next.ExecuteScalar() ?? 0L);
                var huCode = $"HU-{value:0000000}";

                using var insert = CreateCommand(connection, @"
INSERT INTO hus(hu_code, status, created_at, created_by)
VALUES(@hu_code, 'OPEN', @created_at, @created_by)
ON CONFLICT (hu_code) DO NOTHING;
");
                insert.Parameters.AddWithValue("@hu_code", huCode);
                insert.Parameters.AddWithValue("@created_at", ToDbDate(DateTime.Now));
                insert.Parameters.AddWithValue("@created_by", string.IsNullOrWhiteSpace(createdBy) ? DBNull.Value : createdBy.Trim());
                var affected = insert.ExecuteNonQuery();
                if (affected > 0)
                {
                    return huCode;
                }
            }

            throw new InvalidOperationException("Не удалось сгенерировать HU-код.");
        });
    }

    public IReadOnlyList<ProductionPallet> GetProductionPalletsByDoc(long docId)
    {
        return WithConnection(connection => GetProductionPalletsByDoc(connection, docId));
    }

    public ProductionPallet? GetProductionPalletByHu(string huCode)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, $@"
{ProductionPalletSelectSql}
WHERE UPPER(BTRIM(p.hu_code)) = UPPER(BTRIM(@hu_code))
ORDER BY CASE WHEN p.status = @cancelled_status THEN 1 ELSE 0 END,
         p.id
LIMIT 1;
");
            command.Parameters.AddWithValue("@hu_code", huCode);
            command.Parameters.AddWithValue("@cancelled_status", ProductionPalletStatus.Cancelled);
            using var reader = command.ExecuteReader();
            var pallet = reader.Read() ? ReadProductionPallet(reader) : null;
            reader.Close();
            if (pallet != null)
            {
                var pallets = new[] { pallet };
                AttachProductionPalletLines(connection, pallets);
                pallet = pallets[0];
            }

            return pallet;
        });
    }

    public IReadOnlyList<ProductionPalletWorkItem> GetActiveProductionPalletWorkItems()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT d.id,
       d.doc_ref,
       d.status,
       d.order_id,
       COALESCE(o.order_ref, d.order_ref),
       COUNT(*) AS planned_pallet_count,
       COALESCE(SUM(pp.planned_qty), 0) AS planned_qty,
       COUNT(*) FILTER (WHERE pp.status = @filled_status) AS filled_pallet_count,
       COALESCE(SUM(CASE WHEN pp.status = @filled_status THEN pp.planned_qty ELSE 0 END), 0) AS filled_qty
FROM production_pallets pp
INNER JOIN docs d ON d.id = pp.prd_doc_id
LEFT JOIN orders o ON o.id = d.order_id
WHERE d.type = @doc_type
  AND d.status <> @closed_status
  AND pp.status <> @cancelled_status
GROUP BY d.id,
         d.doc_ref,
         d.status,
         d.order_id,
         COALESCE(o.order_ref, d.order_ref)
HAVING COUNT(*) FILTER (WHERE pp.status = @filled_status) < COUNT(*)
ORDER BY d.created_at DESC,
         d.id DESC;
");
            command.Parameters.AddWithValue("@doc_type", DocTypeMapper.ToOpString(DocType.ProductionReceipt));
            command.Parameters.AddWithValue("@closed_status", DocTypeMapper.StatusToString(DocStatus.Closed));
            command.Parameters.AddWithValue("@filled_status", ProductionPalletStatus.Filled);
            command.Parameters.AddWithValue("@cancelled_status", ProductionPalletStatus.Cancelled);
            using var reader = command.ExecuteReader();
            var result = new List<ProductionPalletWorkItem>();
            while (reader.Read())
            {
                var plannedPalletCount = Convert.ToInt32(reader.GetInt64(5));
                var plannedQty = reader.GetDouble(6);
                var filledPalletCount = Convert.ToInt32(reader.GetInt64(7));
                var filledQty = reader.GetDouble(8);
                result.Add(new ProductionPalletWorkItem
                {
                    PrdDocId = reader.GetInt64(0),
                    PrdDocRef = reader.GetString(1),
                    PrdStatus = reader.GetString(2),
                    OrderId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    OrderRef = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Summary = new ProductionPalletSummary
                    {
                        PlannedPalletCount = plannedPalletCount,
                        PlannedQty = plannedQty,
                        FilledPalletCount = filledPalletCount,
                        FilledQty = filledQty,
                        RemainingPalletCount = plannedPalletCount - filledPalletCount,
                        RemainingQty = Math.Max(0, plannedQty - filledQty)
                    }
                });
            }

            return result;
        });
    }

    public bool HasProductionPallets(long docId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT 1
FROM production_pallets
WHERE prd_doc_id = @doc_id
  AND status <> @cancelled_status
LIMIT 1;
");
            command.Parameters.AddWithValue("@doc_id", docId);
            command.Parameters.AddWithValue("@cancelled_status", ProductionPalletStatus.Cancelled);
            return command.ExecuteScalar() != null;
        });
    }

    public void ClearPlannedProductionPalletPlan(long docId)
    {
        WithConnection(connection =>
        {
            using (var guard = CreateCommand(connection, @"
SELECT 1
FROM production_pallets
WHERE prd_doc_id = @doc_id
  AND status <> @cancelled_status
  AND status <> @planned_status
LIMIT 1;
"))
            {
                guard.Parameters.AddWithValue("@doc_id", docId);
                guard.Parameters.AddWithValue("@planned_status", ProductionPalletStatus.Planned);
                guard.Parameters.AddWithValue("@cancelled_status", ProductionPalletStatus.Cancelled);
                if (guard.ExecuteScalar() != null)
                {
                    throw new InvalidOperationException("План паллет уже напечатан или наполнен. Переназначение HU запрещено.");
                }
            }

            using (var command = CreateCommand(connection, @"
DELETE FROM production_pallet_lines pll
USING production_pallets pp
WHERE pp.id = pll.production_pallet_id
  AND pp.prd_doc_id = @doc_id;

DELETE FROM production_pallets
WHERE prd_doc_id = @doc_id;

DELETE FROM doc_lines
WHERE doc_id = @doc_id;
"))
            {
                command.Parameters.AddWithValue("@doc_id", docId);
                command.ExecuteNonQuery();
            }

            return 0;
        });
    }

    public double GetFilledProductionPalletQtyByOrderLine(long orderLineId, long? excludePalletId = null)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT COALESCE(SUM(pll.planned_qty), 0)
FROM production_pallet_lines pll
INNER JOIN production_pallets pp ON pp.id = pll.production_pallet_id
WHERE pll.order_line_id = @order_line_id
  AND pp.status = @filled_status
  AND (@exclude_pallet_id IS NULL OR pp.id <> @exclude_pallet_id);
");
            command.Parameters.AddWithValue("@order_line_id", orderLineId);
            command.Parameters.AddWithValue("@filled_status", ProductionPalletStatus.Filled);
            command.Parameters.AddWithValue("@exclude_pallet_id", excludePalletId.HasValue ? excludePalletId.Value : DBNull.Value);
            var result = command.ExecuteScalar();
            return result == null || result == DBNull.Value
                ? 0d
                : Convert.ToDouble(result, CultureInfo.InvariantCulture);
        });
    }

    public void MarkProductionPalletFilled(long palletId, DateTime filledAt, string? deviceId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE production_pallets
SET status = @filled_status,
    filled_at = @filled_at,
    filled_by_device_id = @device_id
WHERE id = @id
  AND status <> @filled_status
  AND status <> @cancelled_status;

UPDATE production_pallet_lines
SET filled_qty = planned_qty
WHERE production_pallet_id = @id;
");
            command.Parameters.AddWithValue("@id", palletId);
            command.Parameters.AddWithValue("@filled_status", ProductionPalletStatus.Filled);
            command.Parameters.AddWithValue("@cancelled_status", ProductionPalletStatus.Cancelled);
            command.Parameters.AddWithValue("@filled_at", ToDbDate(filledAt));
            command.Parameters.AddWithValue("@device_id", string.IsNullOrWhiteSpace(deviceId) ? DBNull.Value : deviceId.Trim());
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void UpdateProductionPalletHu(long palletId, string huCode)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE production_pallets
SET hu_code = @hu_code
WHERE id = @id
  AND status = @planned_status;

UPDATE doc_lines dl
SET to_hu = @hu_code
FROM production_pallet_lines pll
INNER JOIN production_pallets pp ON pp.id = pll.production_pallet_id
WHERE pll.doc_line_id = dl.id
  AND pp.id = @id
  AND pp.status = @planned_status;
");
            command.Parameters.AddWithValue("@id", palletId);
            command.Parameters.AddWithValue("@hu_code", huCode.Trim());
            command.Parameters.AddWithValue("@planned_status", ProductionPalletStatus.Planned);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public int MarkProductionPalletsPrintedByOrder(long orderId, DateTime printedAt)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE production_pallets pp
SET status = @printed_status,
    printed_at = @printed_at
FROM docs d
WHERE d.id = pp.prd_doc_id
  AND d.order_id = @order_id
  AND d.type = @doc_type
  AND pp.status = @planned_status;
");
            command.Parameters.AddWithValue("@order_id", orderId);
            command.Parameters.AddWithValue("@doc_type", DocTypeMapper.ToOpString(DocType.ProductionReceipt));
            command.Parameters.AddWithValue("@planned_status", ProductionPalletStatus.Planned);
            command.Parameters.AddWithValue("@printed_status", ProductionPalletStatus.Printed);
            command.Parameters.AddWithValue("@printed_at", ToDbDate(printedAt));
            return command.ExecuteNonQuery();
        });
    }

    public Order? GetOrder(long id)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, $@"
{BuildOrderSelectSql("SELECT @id::bigint AS id")}
");
            AddOrderSelectParameters(command);
            command.Parameters.AddWithValue("@id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadOrder(reader) : null;
        });
    }

    public IReadOnlyList<Order> GetOrders()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, $@"
{BuildOrderSelectSql("SELECT o.id FROM orders o")}
ORDER BY created_at DESC");
            AddOrderSelectParameters(command);
            using var reader = command.ExecuteReader();
            var orders = new List<Order>();
            while (reader.Read())
            {
                orders.Add(ReadOrder(reader));
            }

            return orders;
        });
    }

    public IReadOnlyList<Order> GetOrdersPage(bool includeInternal, string? query, int limit, int offset)
    {
        return WithConnection(connection =>
        {
            var normalized = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
            const string pageOrderScopeSql = @"
WITH candidate_orders AS (
    SELECT o.id,
           o.order_ref,
           o.order_type,
           o.status AS persisted_status
    FROM orders o
    LEFT JOIN partners p ON p.id = o.partner_id
    WHERE (@include_internal OR o.order_type = @customer_order_type)
      AND (
          @query IS NULL
          OR o.order_ref ILIKE @query_pattern
          OR p.name ILIKE @query_pattern
          OR p.code ILIKE @query_pattern
      )
),
candidate_order_lines AS (
    SELECT ol.id,
           ol.order_id,
           ol.item_id,
           ol.qty_ordered
    FROM order_lines ol
    INNER JOIN candidate_orders co ON co.id = ol.order_id
),
shipped_by_line AS (
    SELECT dl.order_line_id,
           SUM(dl.qty) AS qty_shipped
    FROM candidate_order_lines col
    INNER JOIN doc_lines dl ON dl.order_line_id = col.id
    INNER JOIN docs d ON d.id = dl.doc_id
    WHERE d.status = 'CLOSED'
      AND d.type = 'OUTBOUND'
      AND dl.qty > 0
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
    GROUP BY dl.order_line_id
),
reserved_by_line AS (
    SELECT p.order_line_id,
           SUM(p.qty_planned) AS qty_reserved
    FROM order_receipt_plan_lines p
    INNER JOIN candidate_order_lines col ON col.id = p.order_line_id
    WHERE p.qty_planned > 0
    GROUP BY p.order_line_id
),
direct_produced_by_line AS (
    SELECT dl.order_line_id,
           SUM(dl.qty) AS qty_received
    FROM candidate_order_lines col
    INNER JOIN doc_lines dl ON dl.order_line_id = col.id
    INNER JOIN docs d ON d.id = dl.doc_id
    WHERE d.status = 'CLOSED'
      AND d.type = 'PRODUCTION_RECEIPT'
      AND dl.qty > 0
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
    GROUP BY dl.order_line_id
),
unlinked_produced_by_item AS (
    SELECT d.order_id,
           dl.item_id,
           SUM(dl.qty) AS qty_received
    FROM docs d
    INNER JOIN candidate_orders co ON co.id = d.order_id
    INNER JOIN doc_lines dl ON dl.doc_id = d.id
    WHERE d.status = 'CLOSED'
      AND d.type = 'PRODUCTION_RECEIPT'
      AND dl.order_line_id IS NULL
      AND dl.qty > 0
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
    GROUP BY d.order_id,
             dl.item_id
),
production_totals_by_order AS (
    SELECT d.order_id,
           SUM(dl.qty) AS qty_received
    FROM docs d
    INNER JOIN candidate_orders co ON co.id = d.order_id
    INNER JOIN doc_lines dl ON dl.doc_id = d.id
    WHERE d.status = 'CLOSED'
      AND d.type = 'PRODUCTION_RECEIPT'
      AND dl.qty > 0
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
    GROUP BY d.order_id
),
line_metrics_seed AS (
    SELECT co.id AS order_id,
           co.order_type,
           co.persisted_status,
           col.id AS order_line_id,
           col.item_id,
           col.qty_ordered,
           COALESCE(shipped.qty_shipped, 0) AS qty_shipped,
           COALESCE(reserved.qty_reserved, 0) AS qty_reserved,
           COALESCE(direct_produced.qty_received, 0) AS qty_direct_received,
           COALESCE(unlinked.qty_received, 0) AS qty_unlinked_item_received,
           GREATEST(0, col.qty_ordered - COALESCE(direct_produced.qty_received, 0)) AS qty_direct_unfilled,
           ROW_NUMBER() OVER (
               PARTITION BY co.id, col.item_id
               ORDER BY col.id DESC
           ) AS item_line_desc_rank,
           COALESCE(SUM(GREATEST(0, col.qty_ordered - COALESCE(direct_produced.qty_received, 0))) OVER (
               PARTITION BY co.id, col.item_id
               ORDER BY col.id
               ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING
           ), 0) AS qty_direct_unfilled_before
    FROM candidate_orders co
    LEFT JOIN candidate_order_lines col ON col.order_id = co.id
    LEFT JOIN shipped_by_line shipped ON shipped.order_line_id = col.id
    LEFT JOIN reserved_by_line reserved ON reserved.order_line_id = col.id
    LEFT JOIN direct_produced_by_line direct_produced ON direct_produced.order_line_id = col.id
    LEFT JOIN unlinked_produced_by_item unlinked ON unlinked.order_id = co.id
                                                 AND unlinked.item_id = col.item_id
),
order_line_metrics AS (
    SELECT order_id,
           order_type,
           persisted_status,
           order_line_id,
           qty_ordered,
           qty_shipped,
           qty_reserved,
           qty_direct_received,
           qty_direct_received
           + CASE
                 WHEN qty_unlinked_item_received <= 0 THEN 0
                 WHEN item_line_desc_rank = 1 THEN GREATEST(0, qty_unlinked_item_received - qty_direct_unfilled_before)
                 ELSE GREATEST(0, LEAST(qty_unlinked_item_received - qty_direct_unfilled_before, qty_direct_unfilled))
             END AS qty_produced_total,
           CASE
               WHEN order_type = 'CUSTOMER' THEN qty_direct_received + qty_reserved
               ELSE qty_direct_received
           END AS qty_customer_ready
    FROM line_metrics_seed
),
status_summary AS (
    SELECT co.id AS order_id,
           COUNT(olm.order_line_id) AS line_count,
           COALESCE(BOOL_AND(olm.qty_shipped + 0.000001 >= olm.qty_ordered), FALSE) AS fully_shipped,
           COALESCE(BOOL_AND(olm.qty_customer_ready + 0.000001 >= olm.qty_ordered), FALSE) AS fully_customer_ready,
           COALESCE(BOOL_AND(olm.qty_produced_total + 0.000001 >= olm.qty_ordered), FALSE) AS fully_produced,
           COALESCE(BOOL_OR(olm.qty_produced_total > 0.000001), FALSE) AS any_produced,
           COALESCE(MAX(production_totals.qty_received), 0) > 0.000001 AS any_posted_production
    FROM candidate_orders co
    LEFT JOIN order_line_metrics olm ON olm.order_id = co.id
    LEFT JOIN production_totals_by_order production_totals ON production_totals.order_id = co.id
    GROUP BY co.id
),
effective_orders AS (
    SELECT co.id,
           co.order_ref,
           CASE
               WHEN co.persisted_status = 'CANCELLED' THEN 'CANCELLED'
               WHEN co.order_type = 'INTERNAL' THEN CASE
                   WHEN co.persisted_status = 'SHIPPED' THEN 'SHIPPED'
                   WHEN COALESCE(ss.line_count, 0) > 0 AND COALESCE(ss.fully_produced, FALSE) THEN 'SHIPPED'
                   ELSE 'IN_PROGRESS'
               END
               ELSE CASE
                   WHEN co.persisted_status = 'DRAFT' THEN 'DRAFT'
                   WHEN COALESCE(ss.line_count, 0) > 0 AND COALESCE(ss.fully_shipped, FALSE) THEN 'SHIPPED'
                   WHEN COALESCE(ss.line_count, 0) > 0 AND COALESCE(ss.fully_customer_ready, FALSE) THEN 'ACCEPTED'
                   ELSE 'IN_PROGRESS'
               END
           END AS effective_status
    FROM candidate_orders co
    LEFT JOIN status_summary ss ON ss.order_id = co.id
)
SELECT eo.id
FROM effective_orders eo
ORDER BY CASE eo.effective_status
    WHEN 'IN_PROGRESS' THEN 1
    WHEN 'ACCEPTED' THEN 2
    WHEN 'DRAFT' THEN 3
    WHEN 'SHIPPED' THEN 4
    WHEN 'CANCELLED' THEN 5
    ELSE 99
END,
eo.order_ref DESC
LIMIT @limit OFFSET @offset";
            using var command = CreateCommand(connection, $@"
SELECT *
FROM (
{BuildOrderSelectSql(pageOrderScopeSql)}
) paged_orders
ORDER BY CASE paged_orders.status
    WHEN 'IN_PROGRESS' THEN 1
    WHEN 'ACCEPTED' THEN 2
    WHEN 'DRAFT' THEN 3
    WHEN 'SHIPPED' THEN 4
    WHEN 'CANCELLED' THEN 5
    ELSE 99
END,
paged_orders.order_ref DESC");
            AddOrderSelectParameters(command);
            command.Parameters.AddWithValue("@include_internal", includeInternal);
            command.Parameters.AddWithValue("@customer_order_type", OrderStatusMapper.TypeToString(OrderType.Customer));
            command.Parameters.Add("@query", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(normalized) ? DBNull.Value : normalized;
            command.Parameters.Add("@query_pattern", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(normalized) ? DBNull.Value : $"%{normalized}%";
            command.Parameters.AddWithValue("@limit", limit);
            command.Parameters.AddWithValue("@offset", offset);
            using var reader = command.ExecuteReader();
            var orders = new List<Order>();
            while (reader.Read())
            {
                orders.Add(ReadOrder(reader));
            }

            return orders;
        });
    }

    public IReadOnlyDictionary<long, OrderListMetrics> GetOrderListMetrics(IReadOnlyCollection<long> orderIds)
    {
        var ids = orderIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<long, OrderListMetrics>();
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
WITH order_scope AS (
    SELECT o.id,
           o.order_type
    FROM orders o
    WHERE o.id = ANY(@order_ids)
),
order_line_scope AS (
    SELECT ol.id,
           ol.order_id,
           ol.item_id,
           ol.qty_ordered
    FROM order_lines ol
    INNER JOIN order_scope os ON os.id = ol.order_id
),
shipment_totals AS (
    SELECT dl.order_line_id,
           SUM(dl.qty) AS sum_qty
    FROM doc_lines dl
    INNER JOIN order_line_scope ols ON ols.id = dl.order_line_id
    INNER JOIN docs d ON d.id = dl.doc_id
    WHERE d.status = @closed_status
      AND d.type = @outbound_type
      AND dl.qty > 0
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
    GROUP BY dl.order_line_id
),
legacy_receipt_totals AS (
    SELECT dl.order_line_id,
           SUM(dl.qty) AS sum_qty
    FROM doc_lines dl
    INNER JOIN order_line_scope ols ON ols.id = dl.order_line_id
    INNER JOIN docs d ON d.id = dl.doc_id
    WHERE d.status = @closed_status
      AND d.type = @production_type
      AND dl.order_line_id IS NOT NULL
      AND dl.qty > 0
      AND NOT EXISTS (
          SELECT 1
          FROM production_pallets pp
          WHERE pp.prd_doc_id = d.id
            AND pp.status <> @pallet_cancelled_status
      )
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
    GROUP BY dl.order_line_id
),
filled_pallet_totals AS (
    SELECT pll.order_line_id,
           SUM(pll.planned_qty) AS sum_qty
    FROM production_pallet_lines pll
    INNER JOIN production_pallets pp ON pp.id = pll.production_pallet_id
    INNER JOIN order_line_scope ols ON ols.id = pll.order_line_id
    WHERE pp.status = @pallet_filled_status
      AND pll.planned_qty > 0
    GROUP BY pll.order_line_id
),
direct_receipt_totals AS (
    SELECT order_line_id,
           SUM(sum_qty) AS sum_qty
    FROM (
        SELECT order_line_id, sum_qty
        FROM legacy_receipt_totals
        UNION ALL
        SELECT order_line_id, sum_qty
        FROM filled_pallet_totals
    ) receipt_sources
    GROUP BY order_line_id
),
unlinked_receipt_totals AS (
    SELECT d.order_id,
           dl.item_id,
           SUM(dl.qty) AS sum_qty
    FROM docs d
    INNER JOIN order_scope os ON os.id = d.order_id
    INNER JOIN doc_lines dl ON dl.doc_id = d.id
    WHERE os.order_type = @internal_order_type
      AND d.status = @closed_status
      AND d.type = @production_type
      AND dl.order_line_id IS NULL
      AND dl.qty > 0
      AND NOT EXISTS (
          SELECT 1
          FROM production_pallets pp
          WHERE pp.prd_doc_id = d.id
            AND pp.status <> @pallet_cancelled_status
      )
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
    GROUP BY d.order_id,
             dl.item_id
),
reserved_totals AS (
    SELECT p.order_line_id,
           SUM(p.qty_planned) AS sum_qty
    FROM order_receipt_plan_lines p
    INNER JOIN order_line_scope ols ON ols.id = p.order_line_id
    WHERE p.qty_planned > 0
      AND p.to_hu IS NOT NULL
      AND p.to_hu <> ''
    GROUP BY p.order_line_id
),
line_seed AS (
    SELECT os.id AS order_id,
           os.order_type,
           ols.id AS order_line_id,
           ols.item_id,
           ols.qty_ordered,
           COALESCE(shipment.sum_qty, 0) AS qty_shipped,
           COALESCE(direct_receipt.sum_qty, 0) AS qty_direct_received,
           COALESCE(unlinked_receipt.sum_qty, 0) AS qty_unlinked_item_received,
           COALESCE(reserved.sum_qty, 0) AS qty_reserved,
           GREATEST(0, ols.qty_ordered - COALESCE(direct_receipt.sum_qty, 0)) AS qty_direct_unfilled,
           ROW_NUMBER() OVER (
               PARTITION BY os.id, ols.item_id
               ORDER BY ols.id DESC
           ) AS item_line_desc_rank,
           COALESCE(SUM(GREATEST(0, ols.qty_ordered - COALESCE(direct_receipt.sum_qty, 0))) OVER (
               PARTITION BY os.id, ols.item_id
               ORDER BY ols.id
               ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING
           ), 0) AS qty_direct_unfilled_before
    FROM order_scope os
    LEFT JOIN order_line_scope ols ON ols.order_id = os.id
    LEFT JOIN shipment_totals shipment ON shipment.order_line_id = ols.id
    LEFT JOIN direct_receipt_totals direct_receipt ON direct_receipt.order_line_id = ols.id
    LEFT JOIN unlinked_receipt_totals unlinked_receipt ON unlinked_receipt.order_id = os.id
                                                    AND unlinked_receipt.item_id = ols.item_id
    LEFT JOIN reserved_totals reserved ON reserved.order_line_id = ols.id
),
line_metrics AS (
    SELECT order_id,
           order_type,
           order_line_id,
           qty_ordered,
           qty_shipped,
           qty_direct_received
           + CASE
                 WHEN order_type <> @internal_order_type THEN 0
                 WHEN qty_unlinked_item_received <= 0 THEN 0
                 WHEN item_line_desc_rank = 1 THEN GREATEST(0, qty_unlinked_item_received - qty_direct_unfilled_before)
                 ELSE GREATEST(0, LEAST(qty_unlinked_item_received - qty_direct_unfilled_before, qty_direct_unfilled))
             END AS qty_produced,
           CASE
               WHEN order_type = @customer_order_type THEN qty_reserved
               ELSE 0
           END AS qty_reserved
    FROM line_seed
),
line_summary AS (
    SELECT order_id,
           COALESCE(BOOL_OR(order_type = @customer_order_type
                            AND qty_ordered - qty_shipped > 0.000001), FALSE) AS has_shipment_remaining,
           COALESCE(BOOL_OR(qty_ordered - (qty_produced + qty_reserved) > 0.000001), FALSE) AS has_receipt_remaining
    FROM line_metrics
    GROUP BY order_id
),
pallet_source AS (
    SELECT COALESCE(pp.order_id, MAX(ol.order_id), d.order_id) AS order_id,
           pp.id,
           pp.status,
           pp.planned_qty
    FROM production_pallets pp
    INNER JOIN docs d ON d.id = pp.prd_doc_id
    LEFT JOIN production_pallet_lines pll ON pll.production_pallet_id = pp.id
    LEFT JOIN order_lines ol ON ol.id = pll.order_line_id
    WHERE d.type = @production_type
    GROUP BY pp.id,
             pp.order_id,
             d.order_id,
             pp.status,
             pp.planned_qty
),
pallet_summary AS (
    SELECT ps.order_id,
           COUNT(*) FILTER (WHERE ps.status <> @pallet_cancelled_status)::int AS planned_pallet_count,
           COALESCE(SUM(ps.planned_qty) FILTER (WHERE ps.status <> @pallet_cancelled_status), 0)::double precision AS planned_qty,
           COUNT(*) FILTER (WHERE ps.status = @pallet_filled_status)::int AS filled_pallet_count,
           COALESCE(SUM(ps.planned_qty) FILTER (WHERE ps.status = @pallet_filled_status), 0)::double precision AS filled_qty
    FROM pallet_source ps
    INNER JOIN order_scope os ON os.id = ps.order_id
    GROUP BY ps.order_id
)
SELECT os.id,
       COALESCE(line_summary.has_shipment_remaining, FALSE) AS has_shipment_remaining,
       COALESCE(line_summary.has_receipt_remaining, FALSE) AS has_receipt_remaining,
       COALESCE(pallet_summary.planned_pallet_count, 0) AS planned_pallet_count,
       COALESCE(pallet_summary.planned_qty, 0) AS planned_qty,
       COALESCE(pallet_summary.filled_pallet_count, 0) AS filled_pallet_count,
       COALESCE(pallet_summary.filled_qty, 0) AS filled_qty
FROM order_scope os
LEFT JOIN line_summary ON line_summary.order_id = os.id
LEFT JOIN pallet_summary ON pallet_summary.order_id = os.id;
");
            command.Parameters.Add("@order_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value = ids;
            command.Parameters.AddWithValue("@closed_status", DocTypeMapper.StatusToString(DocStatus.Closed));
            command.Parameters.AddWithValue("@outbound_type", DocTypeMapper.ToOpString(DocType.Outbound));
            command.Parameters.AddWithValue("@production_type", DocTypeMapper.ToOpString(DocType.ProductionReceipt));
            command.Parameters.AddWithValue("@customer_order_type", OrderStatusMapper.TypeToString(OrderType.Customer));
            command.Parameters.AddWithValue("@internal_order_type", OrderStatusMapper.TypeToString(OrderType.Internal));
            command.Parameters.AddWithValue("@pallet_filled_status", ProductionPalletStatus.Filled);
            command.Parameters.AddWithValue("@pallet_cancelled_status", ProductionPalletStatus.Cancelled);

            using var reader = command.ExecuteReader();
            var metrics = new Dictionary<long, OrderListMetrics>();
            while (reader.Read())
            {
                var plannedPalletCount = reader.GetInt32(3);
                var plannedQty = reader.GetDouble(4);
                var filledPalletCount = reader.GetInt32(5);
                var filledQty = reader.GetDouble(6);
                metrics[reader.GetInt64(0)] = new OrderListMetrics
                {
                    OrderId = reader.GetInt64(0),
                    HasShipmentRemaining = reader.GetBoolean(1),
                    HasReceiptRemaining = reader.GetBoolean(2),
                    HasProductionPalletPlan = plannedPalletCount > 0,
                    PalletSummary = new ProductionPalletSummary
                    {
                        PlannedPalletCount = plannedPalletCount,
                        PlannedQty = plannedQty,
                        FilledPalletCount = filledPalletCount,
                        FilledQty = filledQty,
                        RemainingPalletCount = Math.Max(0, plannedPalletCount - filledPalletCount),
                        RemainingQty = Math.Max(0, plannedQty - filledQty)
                    }
                };
            }

            return metrics;
        });
    }

    public long AddOrder(Order order)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO orders(order_ref, order_type, partner_id, due_date, status, comment, created_at, bind_reserved_stock)
VALUES(@order_ref, @order_type, @partner_id, @due_date, @status, @comment, @created_at, @bind_reserved_stock)
RETURNING id;
");
            command.Parameters.AddWithValue("@order_ref", order.OrderRef);
            command.Parameters.AddWithValue("@order_type", OrderStatusMapper.TypeToString(order.Type));
            command.Parameters.AddWithValue("@partner_id", order.PartnerId.HasValue ? order.PartnerId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@due_date", order.DueDate.HasValue ? ToDbDateOnly(order.DueDate.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@status", OrderStatusMapper.StatusToString(order.Status));
            command.Parameters.AddWithValue("@comment", string.IsNullOrWhiteSpace(order.Comment) ? DBNull.Value : order.Comment);
            command.Parameters.AddWithValue("@created_at", ToDbDate(order.CreatedAt));
            command.Parameters.AddWithValue("@bind_reserved_stock", order.UseReservedStock);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdateOrder(Order order)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE orders
SET order_ref = @order_ref,
    order_type = @order_type,
    partner_id = @partner_id,
    due_date = @due_date,
    status = @status,
    comment = @comment,
    bind_reserved_stock = @bind_reserved_stock
WHERE id = @id;
");
            command.Parameters.AddWithValue("@order_ref", order.OrderRef);
            command.Parameters.AddWithValue("@order_type", OrderStatusMapper.TypeToString(order.Type));
            command.Parameters.AddWithValue("@partner_id", order.PartnerId.HasValue ? order.PartnerId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@due_date", order.DueDate.HasValue ? ToDbDateOnly(order.DueDate.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@status", OrderStatusMapper.StatusToString(order.Status));
            command.Parameters.AddWithValue("@comment", string.IsNullOrWhiteSpace(order.Comment) ? DBNull.Value : order.Comment);
            command.Parameters.AddWithValue("@bind_reserved_stock", order.UseReservedStock);
            command.Parameters.AddWithValue("@id", order.Id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void UpdateOrderStatus(long orderId, OrderStatus status)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE orders SET status = @status WHERE id = @id");
            command.Parameters.AddWithValue("@status", OrderStatusMapper.StatusToString(status));
            command.Parameters.AddWithValue("@id", orderId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public IReadOnlyList<MarkingOrderQueueRow> GetMarkingOrderQueue(bool includeCompleted)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
WITH shipped AS (
    SELECT dl.order_line_id, SUM(dl.qty) AS qty_shipped
    FROM doc_lines dl
    INNER JOIN docs d ON d.id = dl.doc_id
    WHERE d.status = @closed_status
      AND d.type = @outbound_type
      AND dl.order_line_id IS NOT NULL
      AND dl.qty > 0
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
    GROUP BY dl.order_line_id
),
reserved AS (
    SELECT order_line_id, SUM(qty_planned) AS qty_reserved
    FROM order_receipt_plan_lines
    WHERE qty_planned > 0
    GROUP BY order_line_id
),
line_need AS (
    SELECT ol.order_id,
           ol.id AS order_line_id,
           GREATEST(0, ol.qty_ordered - COALESCE(shipped.qty_shipped, 0) - COALESCE(reserved.qty_reserved, 0)) AS qty_for_marking
    FROM order_lines ol
    INNER JOIN items i ON i.id = ol.item_id
    INNER JOIN item_types it ON it.id = i.item_type_id
    LEFT JOIN shipped ON shipped.order_line_id = ol.id
    LEFT JOIN reserved ON reserved.order_line_id = ol.id
    WHERE COALESCE(it.enable_marking, FALSE) = TRUE
      AND NULLIF(BTRIM(i.gtin), '') IS NOT NULL
),
order_need AS (
    SELECT order_id,
           COUNT(*) FILTER (WHERE qty_for_marking > 0) AS line_count,
           COALESCE(SUM(qty_for_marking) FILTER (WHERE qty_for_marking > 0), 0) AS code_count
    FROM line_need
    GROUP BY order_id
),
task_code_stats AS (
    SELECT marking_order_id,
           COUNT(*) FILTER (WHERE status <> @marking_code_status_voided) AS codes_total,
           COUNT(*) FILTER (
               WHERE status IN (@marking_code_status_reserved, @marking_code_status_printed)
                 AND receipt_line_id IS NULL
                 AND receipt_doc_id IS NULL
           ) AS codes_free,
           COUNT(*) FILTER (
               WHERE status <> @marking_code_status_voided
                 AND (receipt_line_id IS NOT NULL OR receipt_doc_id IS NOT NULL)
           ) AS codes_bound
    FROM marking_code
    GROUP BY marking_order_id
)
SELECT NULL::uuid AS marking_order_id,
       o.id::bigint AS order_id,
       o.order_ref,
       o.partner_id,
       p.name,
       p.code,
       o.due_date,
       o.status,
       COALESCE(o.marking_status, 'NOT_REQUIRED') AS marking_status,
       COALESCE(order_need.line_count, 0) AS line_count,
       COALESCE(order_need.code_count, 0) AS code_count,
       COALESCE(o.marking_printed_at, o.marking_excel_generated_at) AS last_generated_at,
       o.created_at AS sort_created_at,
       NULL::text AS source_type,
       NULL::bigint AS source_order_id,
       NULL::bigint AS item_id,
       NULL::text AS item_name,
       NULL::text AS gtin,
       COALESCE(order_need.code_count, 0)::integer AS requested_quantity,
       COALESCE(o.marking_status, 'NOT_REQUIRED') AS task_status,
       0::integer AS codes_total,
       0::integer AS codes_free,
       0::integer AS codes_bound,
       NULL::text AS display_source,
       COALESCE(o.marking_status, 'NOT_REQUIRED') AS effective_status,
       CASE
           WHEN COALESCE(o.marking_status, 'NOT_REQUIRED') IN (@printed_status, @legacy_excel_generated_status) THEN 'Маркировка проведена'
           ELSE 'Маркировка не проведена'
       END AS display_status
FROM orders o
LEFT JOIN partners p ON p.id = o.partner_id
LEFT JOIN order_need ON order_need.order_id = o.id
WHERE COALESCE(order_need.line_count, 0) > 0
      AND (
      (o.status IN (@in_progress_status, @accepted_status)
       AND COALESCE(o.marking_status, 'NOT_REQUIRED') NOT IN (@printed_status, @legacy_excel_generated_status))
      OR (@include_completed = TRUE
          AND COALESCE(o.marking_status, 'NOT_REQUIRED') IN (@printed_status, @legacy_excel_generated_status))
  )
UNION ALL
SELECT mo.id AS marking_order_id,
       mo.order_id::bigint AS order_id,
       CASE
           WHEN mo.order_id IS NOT NULL THEN COALESCE(mo_o.order_ref, '')
           WHEN mo.source_type = @production_need_source_type THEN 'Потребность производства'
           WHEN mo.source_type = @production_order_source_type THEN 'Производственный заказ'
           ELSE COALESCE(i.name, 'Задача маркировки')
       END AS order_ref,
       NULL AS partner_id,
       CASE
           WHEN mo.order_id IS NOT NULL THEN p_mo.name
           WHEN mo.source_type = @production_need_source_type THEN 'Потребность производства'
           WHEN mo.source_type = @production_order_source_type THEN 'Производственный заказ'
           ELSE 'Задача маркировки'
       END AS name,
       p_mo.code AS code,
       mo_o.due_date AS due_date,
       COALESCE(mo_o.status, @in_progress_status) AS status,
       CASE
           WHEN mo.status IN (@marking_status_printed, @marking_status_completed, @marking_status_codes_bound, @marking_status_ready_for_print) THEN @printed_status
           ELSE @required_status
       END AS marking_status,
       1 AS line_count,
       mo.requested_quantity AS code_count,
       COALESCE(mo.codes_bound_at, mo.requested_at, mo.created_at) AS last_generated_at,
       mo.created_at AS sort_created_at,
       mo.source_type AS source_type,
       mo.source_order_id AS source_order_id,
       mo.item_id AS item_id,
       i.name AS item_name,
       COALESCE(mo.gtin, i.gtin) AS gtin,
       mo.requested_quantity AS requested_quantity,
       mo.status AS task_status,
       COALESCE(task_code_stats.codes_total, 0)::integer AS codes_total,
       COALESCE(task_code_stats.codes_free, 0)::integer AS codes_free,
       COALESCE(task_code_stats.codes_bound, 0)::integer AS codes_bound,
       CASE
           WHEN mo.source_type = @production_need_source_type THEN 'Потребность производства'
           WHEN mo.source_type = @production_order_source_type THEN 'Производственный заказ'
           ELSE COALESCE(mo_o.order_ref, 'Задача маркировки')
       END AS display_source,
       CASE
           WHEN COALESCE(task_code_stats.codes_total, 0) >= mo.requested_quantity THEN @marking_status_completed
           ELSE mo.status
       END AS effective_status,
       CASE
           WHEN COALESCE(task_code_stats.codes_total, 0) >= mo.requested_quantity THEN 'Выполнена'
           ELSE mo.status
       END AS display_status
FROM marking_order mo
LEFT JOIN items i ON i.id = mo.item_id
LEFT JOIN orders mo_o ON mo_o.id = mo.order_id
LEFT JOIN partners p_mo ON p_mo.id = mo_o.partner_id
LEFT JOIN task_code_stats ON task_code_stats.marking_order_id = mo.id
WHERE (mo.source_type IN (@production_need_source_type, @production_order_source_type)
       OR mo.order_id IS NOT NULL)
  AND mo.status NOT IN (@marking_status_cancelled, @marking_status_failed)
  AND (
      @include_completed = TRUE
      OR COALESCE(task_code_stats.codes_total, 0) < mo.requested_quantity
  )
ORDER BY due_date NULLS LAST, sort_created_at, order_id NULLS LAST;
");
            command.Parameters.AddWithValue("@closed_status", DocTypeMapper.StatusToString(DocStatus.Closed));
            command.Parameters.AddWithValue("@outbound_type", DocTypeMapper.ToOpString(DocType.Outbound));
            command.Parameters.AddWithValue("@in_progress_status", OrderStatusMapper.StatusToString(OrderStatus.InProgress));
            command.Parameters.AddWithValue("@accepted_status", OrderStatusMapper.StatusToString(OrderStatus.Accepted));
            command.Parameters.AddWithValue("@printed_status", MarkingStatusMapper.ToString(MarkingStatus.Printed));
            command.Parameters.AddWithValue("@required_status", MarkingStatusMapper.ToString(MarkingStatus.Required));
            command.Parameters.AddWithValue("@legacy_excel_generated_status", "EXCEL_GENERATED");
            command.Parameters.AddWithValue("@production_need_source_type", MarkingNeedCreationService.ProductionNeedSourceType);
            command.Parameters.AddWithValue("@production_order_source_type", MarkingNeedCreationService.ProductionOrderSourceType);
            command.Parameters.AddWithValue("@marking_status_codes_bound", MarkingOrderStatus.CodesBound);
            command.Parameters.AddWithValue("@marking_status_ready_for_print", MarkingOrderStatus.ReadyForPrint);
            command.Parameters.AddWithValue("@marking_status_printed", MarkingOrderStatus.Printed);
            command.Parameters.AddWithValue("@marking_status_completed", MarkingOrderStatus.Completed);
            command.Parameters.AddWithValue("@marking_status_cancelled", MarkingOrderStatus.Cancelled);
            command.Parameters.AddWithValue("@marking_status_failed", MarkingOrderStatus.Failed);
            command.Parameters.AddWithValue("@marking_code_status_reserved", MarkingCodeStatus.Reserved);
            command.Parameters.AddWithValue("@marking_code_status_printed", MarkingCodeStatus.Printed);
            command.Parameters.AddWithValue("@marking_code_status_voided", MarkingCodeStatus.Voided);
            command.Parameters.AddWithValue("@include_completed", includeCompleted);
            using var reader = command.ExecuteReader();
            var rows = new List<MarkingOrderQueueRow>();
            while (reader.Read())
            {
                rows.Add(new MarkingOrderQueueRow
                {
                    MarkingOrderId = reader.IsDBNull(0) ? null : reader.GetGuid(0),
                    OrderId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    OrderRef = reader.GetString(2),
                    PartnerName = reader.IsDBNull(4) ? null : reader.GetString(4),
                    PartnerCode = reader.IsDBNull(5) ? null : reader.GetString(5),
                    DueDate = reader.IsDBNull(6) ? null : FromDbDate(reader.GetString(6)),
                    OrderStatus = OrderStatusMapper.StatusFromString(reader.GetString(7)) ?? OrderStatus.InProgress,
                    MarkingStatus = MarkingStatusMapper.FromString(reader.GetString(8)),
                    MarkingLineCount = reader.GetInt32(9),
                    MarkingCodeCount = Convert.ToDouble(reader.GetValue(10), CultureInfo.InvariantCulture),
                    LastGeneratedAt = FromDbDate(reader.IsDBNull(11) ? null : reader.GetString(11)),
                    SourceType = reader.IsDBNull(13) ? null : reader.GetString(13),
                    SourceOrderId = reader.IsDBNull(14) ? null : reader.GetInt64(14),
                    ItemId = reader.IsDBNull(15) ? null : reader.GetInt64(15),
                    ItemName = reader.IsDBNull(16) ? null : reader.GetString(16),
                    Gtin = reader.IsDBNull(17) ? null : reader.GetString(17),
                    RequestedQuantity = reader.IsDBNull(18) ? 0 : reader.GetInt32(18),
                    TaskStatus = reader.IsDBNull(19) ? null : reader.GetString(19),
                    CodesTotal = reader.IsDBNull(20) ? 0 : reader.GetInt32(20),
                    CodesFree = reader.IsDBNull(21) ? 0 : reader.GetInt32(21),
                    CodesBound = reader.IsDBNull(22) ? 0 : reader.GetInt32(22),
                    DisplaySource = reader.IsDBNull(23) ? null : reader.GetString(23),
                    EffectiveStatus = reader.IsDBNull(24) ? null : reader.GetString(24),
                    DisplayStatus = reader.IsDBNull(25) ? null : reader.GetString(25)
                });
            }

            return rows;
        });
    }

    public IReadOnlyList<MarkingOrderLineCandidate> GetMarkingOrderLineCandidates(IReadOnlyCollection<long> orderIds)
    {
        if (orderIds.Count == 0)
        {
            return Array.Empty<MarkingOrderLineCandidate>();
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
WITH selected_orders AS (
    SELECT UNNEST(@order_ids::bigint[]) AS order_id
),
shipped AS (
    SELECT dl.order_line_id, SUM(dl.qty) AS qty_shipped
    FROM doc_lines dl
    INNER JOIN docs d ON d.id = dl.doc_id
    WHERE d.status = @closed_status
      AND d.type = @outbound_type
      AND dl.order_line_id IS NOT NULL
      AND dl.qty > 0
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
    GROUP BY dl.order_line_id
),
reserved AS (
    SELECT order_line_id, SUM(qty_planned) AS qty_reserved
    FROM order_receipt_plan_lines
    WHERE qty_planned > 0
    GROUP BY order_line_id
)
SELECT ol.order_id,
       ol.id,
       i.name,
       BTRIM(i.gtin) AS gtin,
       ol.qty_ordered,
       COALESCE(shipped.qty_shipped, 0) AS qty_shipped,
       COALESCE(reserved.qty_reserved, 0) AS qty_reserved,
       GREATEST(0, ol.qty_ordered - COALESCE(shipped.qty_shipped, 0) - COALESCE(reserved.qty_reserved, 0)) AS qty_for_marking
FROM order_lines ol
INNER JOIN selected_orders so ON so.order_id = ol.order_id
INNER JOIN orders o ON o.id = ol.order_id
INNER JOIN items i ON i.id = ol.item_id
INNER JOIN item_types it ON it.id = i.item_type_id
LEFT JOIN shipped ON shipped.order_line_id = ol.id
LEFT JOIN reserved ON reserved.order_line_id = ol.id
WHERE o.status IN (@in_progress_status, @accepted_status)
  AND COALESCE(it.enable_marking, FALSE) = TRUE
  AND NULLIF(BTRIM(i.gtin), '') IS NOT NULL
ORDER BY i.name, BTRIM(i.gtin), ol.id;
");
            command.Parameters.AddWithValue("@order_ids", orderIds.Distinct().ToArray());
            command.Parameters.AddWithValue("@closed_status", DocTypeMapper.StatusToString(DocStatus.Closed));
            command.Parameters.AddWithValue("@outbound_type", DocTypeMapper.ToOpString(DocType.Outbound));
            command.Parameters.AddWithValue("@in_progress_status", OrderStatusMapper.StatusToString(OrderStatus.InProgress));
            command.Parameters.AddWithValue("@accepted_status", OrderStatusMapper.StatusToString(OrderStatus.Accepted));
            using var reader = command.ExecuteReader();
            var rows = new List<MarkingOrderLineCandidate>();
            while (reader.Read())
            {
                rows.Add(new MarkingOrderLineCandidate
                {
                    OrderId = reader.GetInt64(0),
                    OrderLineId = reader.GetInt64(1),
                    ItemName = reader.GetString(2),
                    Gtin = reader.GetString(3),
                    ItemTypeEnableMarking = true,
                    QtyOrdered = Convert.ToDouble(reader.GetValue(4), CultureInfo.InvariantCulture),
                    ShippedQty = Convert.ToDouble(reader.GetValue(5), CultureInfo.InvariantCulture),
                    ReservedQty = Convert.ToDouble(reader.GetValue(6), CultureInfo.InvariantCulture),
                    QtyForMarking = Convert.ToDouble(reader.GetValue(7), CultureInfo.InvariantCulture)
                });
            }

            return rows;
        });
    }

    public IReadOnlyList<MarkingOrder> GetMarkingOrdersByIds(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0)
        {
            return Array.Empty<MarkingOrder>();
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildMarkingOrderQuery("WHERE mo.id = ANY(@ids::uuid[])"));
            command.Parameters.AddWithValue("@ids", ids.Distinct().ToArray());
            using var reader = command.ExecuteReader();
            var rows = new List<MarkingOrder>();
            while (reader.Read())
            {
                rows.Add(ReadMarkingOrder(reader));
            }

            return rows;
        });
    }

    public IReadOnlyList<MarkingOrder> GetMarkingOrdersByItemIds(IReadOnlyCollection<long> itemIds)
    {
        if (itemIds.Count == 0)
        {
            return Array.Empty<MarkingOrder>();
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildMarkingOrderQuery("WHERE mo.item_id = ANY(@item_ids::bigint[])"));
            command.Parameters.AddWithValue("@item_ids", itemIds.Distinct().ToArray());
            using var reader = command.ExecuteReader();
            var rows = new List<MarkingOrder>();
            while (reader.Read())
            {
                rows.Add(ReadMarkingOrder(reader));
            }

            return rows;
        });
    }

    public void AddMarkingOrder(MarkingOrder order)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO marking_order(
    id,
    order_id,
    item_id,
    gtin,
    requested_quantity,
    request_number,
    status,
    notes,
    source_type,
    source_order_id,
    requested_at,
    codes_bound_at,
    created_at,
    updated_at)
VALUES(
    @id,
    @order_id,
    @item_id,
    @gtin,
    @requested_quantity,
    @request_number,
    @status,
    @notes,
    @source_type,
    @source_order_id,
    @requested_at,
    @codes_bound_at,
    @created_at,
    @updated_at);");
            command.Parameters.AddWithValue("@id", order.Id);
            command.Parameters.AddWithValue("@order_id", order.OrderId.HasValue ? order.OrderId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@item_id", order.ItemId.HasValue ? order.ItemId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@gtin", string.IsNullOrWhiteSpace(order.Gtin) ? DBNull.Value : order.Gtin.Trim());
            command.Parameters.AddWithValue("@requested_quantity", order.RequestedQuantity);
            command.Parameters.AddWithValue("@request_number", order.RequestNumber);
            command.Parameters.AddWithValue("@status", string.IsNullOrWhiteSpace(order.Status) ? MarkingOrderStatus.Draft : order.Status.Trim());
            command.Parameters.AddWithValue("@notes", string.IsNullOrWhiteSpace(order.Notes) ? DBNull.Value : order.Notes.Trim());
            command.Parameters.AddWithValue("@source_type", string.IsNullOrWhiteSpace(order.SourceType) ? DBNull.Value : order.SourceType.Trim());
            command.Parameters.AddWithValue("@source_order_id", order.SourceOrderId.HasValue ? order.SourceOrderId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@requested_at", order.RequestedAt.HasValue ? ToDbDate(order.RequestedAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@codes_bound_at", order.CodesBoundAt.HasValue ? ToDbDate(order.CodesBoundAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@created_at", ToDbDate(order.CreatedAt));
            command.Parameters.AddWithValue("@updated_at", ToDbDate(order.UpdatedAt));
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void MarkMarkingOrdersPrinted(IReadOnlyCollection<Guid> ids, DateTime printedAt)
    {
        if (ids.Count == 0)
        {
            return;
        }

        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE marking_order
SET status = @status,
    codes_bound_at = COALESCE(codes_bound_at, @printed_at),
    updated_at = @printed_at
WHERE id = ANY(@ids::uuid[]);");
            command.Parameters.AddWithValue("@ids", ids.Distinct().ToArray());
            command.Parameters.AddWithValue("@status", MarkingOrderStatus.Printed);
            command.Parameters.AddWithValue("@printed_at", ToDbDate(printedAt));
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void MarkOrdersPrinted(IReadOnlyCollection<long> orderIds, DateTime printedAt)
    {
        if (orderIds.Count == 0)
        {
            return;
        }

        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE orders
SET marking_status = @status,
    marking_excel_generated_at = @generated_at,
    marking_printed_at = @printed_at
WHERE id = ANY(@order_ids::bigint[]);
");
            command.Parameters.AddWithValue("@status", MarkingStatusMapper.ToString(MarkingStatus.Printed));
            command.Parameters.AddWithValue("@generated_at", ToDbDate(printedAt));
            command.Parameters.AddWithValue("@printed_at", ToDbDate(printedAt));
            command.Parameters.AddWithValue("@order_ids", orderIds.Distinct().ToArray());
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void UpdateOrderMarkingStatusForBackfill(long orderId, MarkingStatus status, DateTime timestamp)
    {
        WithConnection(connection =>
        {
            var statusText = MarkingStatusMapper.ToString(status);
            var sql = status == MarkingStatus.Printed
                ? @"
UPDATE orders
SET marking_status = @status,
    marking_excel_generated_at = COALESCE(marking_excel_generated_at, @timestamp),
    marking_printed_at = COALESCE(marking_printed_at, @timestamp)
WHERE id = @id
  AND COALESCE(marking_status, 'NOT_REQUIRED') <> @printed_status;
"
                : @"
UPDATE orders
SET marking_status = @status
WHERE id = @id
  AND COALESCE(marking_status, 'NOT_REQUIRED') <> @printed_status;
";

            using var command = CreateCommand(connection, sql);
            command.Parameters.AddWithValue("@id", orderId);
            command.Parameters.AddWithValue("@status", statusText);
            if (status == MarkingStatus.Printed)
            {
                command.Parameters.AddWithValue("@timestamp", ToDbDate(timestamp));
            }
            command.Parameters.AddWithValue("@printed_status", MarkingStatusMapper.ToString(MarkingStatus.Printed));
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public IReadOnlyList<OrderLine> GetOrderLines(long orderId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, order_id, item_id, qty_ordered, production_purpose, production_pallet_group FROM order_lines WHERE order_id = @order_id ORDER BY id");
            command.Parameters.AddWithValue("@order_id", orderId);
            using var reader = command.ExecuteReader();
            var lines = new List<OrderLine>();
            while (reader.Read())
            {
                lines.Add(ReadOrderLine(reader));
            }

            return lines;
        });
    }

    public IReadOnlyList<OrderLineView> GetOrderLineViews(long orderId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
WITH pallet_metrics AS (
    SELECT pll.order_line_id,
           COUNT(DISTINCT pp.id) FILTER (WHERE pp.status <> @pallet_cancelled_status)::int AS planned_pallet_count,
           COUNT(DISTINCT pp.id) FILTER (WHERE pp.status = @pallet_filled_status)::int AS filled_pallet_count,
           COALESCE(SUM(pll.planned_qty) FILTER (WHERE pp.status <> @pallet_cancelled_status), 0)::double precision AS planned_pallet_qty,
           COALESCE(SUM(pll.planned_qty) FILTER (WHERE pp.status = @pallet_filled_status), 0)::double precision AS filled_pallet_qty
    FROM production_pallet_lines pll
    INNER JOIN production_pallets pp ON pp.id = pll.production_pallet_id
    WHERE pp.order_id = @order_id
      AND pll.order_line_id IS NOT NULL
    GROUP BY pll.order_line_id
)
SELECT ol.id,
       ol.order_id,
       ol.item_id,
       i.name,
       i.barcode,
       i.gtin,
       ol.qty_ordered,
       ol.production_purpose,
       ol.production_pallet_group,
       COALESCE(pm.planned_pallet_count, 0),
       COALESCE(pm.filled_pallet_count, 0),
       COALESCE(pm.planned_pallet_qty, 0),
       COALESCE(pm.filled_pallet_qty, 0)
FROM order_lines ol
INNER JOIN items i ON i.id = ol.item_id
LEFT JOIN pallet_metrics pm ON pm.order_line_id = ol.id
WHERE ol.order_id = @order_id
ORDER BY i.name, ol.id;
");
            command.Parameters.AddWithValue("@order_id", orderId);
            command.Parameters.AddWithValue("@pallet_filled_status", ProductionPalletStatus.Filled);
            command.Parameters.AddWithValue("@pallet_cancelled_status", ProductionPalletStatus.Cancelled);
            using var reader = command.ExecuteReader();
            var lines = new List<OrderLineView>();
            while (reader.Read())
            {
                lines.Add(new OrderLineView
                {
                    Id = reader.GetInt64(0),
                    OrderId = reader.GetInt64(1),
                    ItemId = reader.GetInt64(2),
                    ItemName = reader.GetString(3),
                    Barcode = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Gtin = reader.IsDBNull(5) ? null : reader.GetString(5),
                    QtyOrdered = reader.GetDouble(6),
                    ProductionPurpose = ProductionLinePurposeMapper.FromDbValue(reader.IsDBNull(7) ? null : reader.GetString(7)),
                    ProductionPalletGroup = reader.IsDBNull(8) ? null : reader.GetString(8),
                    PlannedPalletCount = reader.GetInt32(9),
                    FilledPalletCount = reader.GetInt32(10),
                    PlannedPalletQty = reader.GetDouble(11),
                    FilledPalletQty = reader.GetDouble(12)
                });
            }

            return lines;
        });
    }

    public IReadOnlyList<OrderReceiptLine> GetOrderReceiptRemaining(long orderId)
    {
        return GetOrderReceiptRemainingCore(orderId, includeReservedStock: true);
    }

    public IReadOnlyList<OrderReceiptLine> GetOrderReceiptRemainingWithoutReservedStock(long orderId)
    {
        return GetOrderReceiptRemainingCore(orderId, includeReservedStock: false);
    }

    private IReadOnlyList<OrderReceiptLine> GetOrderReceiptRemainingCore(long orderId, bool includeReservedStock)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
WITH order_line_scope AS (
    SELECT ol.id,
           ol.order_id,
           ol.item_id,
           ol.qty_ordered,
           ol.production_purpose
    FROM order_lines ol
    WHERE ol.order_id = @order_id
),
legacy_receipt_totals AS (
    SELECT dl.order_line_id,
           SUM(dl.qty) AS sum_qty
    FROM doc_lines dl
    INNER JOIN docs d ON d.id = dl.doc_id
    INNER JOIN order_line_scope ols ON ols.id = dl.order_line_id
    WHERE d.status = @status
      AND d.type = @doc_type
      AND dl.order_line_id IS NOT NULL
      AND dl.qty > 0
      AND NOT EXISTS (
          SELECT 1
          FROM production_pallets pp
          WHERE pp.prd_doc_id = d.id
            AND pp.status <> @pallet_cancelled_status
      )
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
    GROUP BY dl.order_line_id
),
filled_pallet_totals AS (
    SELECT pll.order_line_id,
           SUM(pll.planned_qty) AS sum_qty
    FROM production_pallet_lines pll
    INNER JOIN production_pallets pp ON pp.id = pll.production_pallet_id
    INNER JOIN order_line_scope ols ON ols.id = pll.order_line_id
    WHERE pp.status = @pallet_filled_status
      AND pll.planned_qty > 0
    GROUP BY pll.order_line_id
),
receipt_totals AS (
    SELECT order_line_id,
           SUM(sum_qty) AS sum_qty
    FROM (
        SELECT order_line_id, sum_qty
        FROM legacy_receipt_totals
        UNION ALL
        SELECT order_line_id, sum_qty
        FROM filled_pallet_totals
    ) receipt_sources
    GROUP BY order_line_id
),
reserved_totals AS (
    SELECT p.order_line_id,
           SUM(p.qty_planned) AS sum_qty
    FROM order_receipt_plan_lines p
    INNER JOIN order_line_scope ols ON ols.id = p.order_line_id
    WHERE p.qty_planned > 0
      AND p.to_hu IS NOT NULL
      AND p.to_hu <> ''
    GROUP BY p.order_line_id
)
SELECT ols.id,
       ols.order_id,
       ols.item_id,
       i.name,
       ols.qty_ordered,
       ols.production_purpose,
       (COALESCE(receipt.sum_qty, 0)
        + CASE
              WHEN @include_reserved_stock = 1
                   AND o.order_type = @customer_order_type THEN COALESCE(reserved.sum_qty, 0)
              ELSE 0
          END) AS received_qty,
       (ols.qty_ordered
        - (COALESCE(receipt.sum_qty, 0)
           + CASE
                 WHEN @include_reserved_stock = 1
                      AND o.order_type = @customer_order_type THEN COALESCE(reserved.sum_qty, 0)
                 ELSE 0
             END)) AS remaining
FROM order_line_scope ols
INNER JOIN orders o ON o.id = ols.order_id
INNER JOIN items i ON i.id = ols.item_id
LEFT JOIN receipt_totals receipt ON receipt.order_line_id = ols.id
LEFT JOIN reserved_totals reserved ON reserved.order_line_id = ols.id
ORDER BY ols.id;
");
            command.Parameters.AddWithValue("@order_id", orderId);
            command.Parameters.AddWithValue("@status", DocTypeMapper.StatusToString(DocStatus.Closed));
            command.Parameters.AddWithValue("@doc_type", DocTypeMapper.ToOpString(DocType.ProductionReceipt));
            command.Parameters.AddWithValue("@customer_order_type", OrderStatusMapper.TypeToString(OrderType.Customer));
            command.Parameters.AddWithValue("@include_reserved_stock", includeReservedStock ? 1 : 0);
            command.Parameters.AddWithValue("@pallet_filled_status", ProductionPalletStatus.Filled);
            command.Parameters.AddWithValue("@pallet_cancelled_status", ProductionPalletStatus.Cancelled);
            using var reader = command.ExecuteReader();
            var lines = new List<OrderReceiptLine>();
            while (reader.Read())
            {
                lines.Add(new OrderReceiptLine
                {
                    OrderLineId = reader.GetInt64(0),
                    OrderId = reader.GetInt64(1),
                    ItemId = reader.GetInt64(2),
                    ItemName = reader.GetString(3),
                    QtyOrdered = reader.GetDouble(4),
                    ProductionPurpose = ProductionLinePurposeMapper.FromDbValue(reader.IsDBNull(5) ? null : reader.GetString(5)),
                    QtyReceived = reader.GetDouble(6),
                    QtyRemaining = reader.GetDouble(7)
                });
            }

            return lines;
        });
    }

    public IReadOnlyDictionary<long, double> GetUnlinkedProductionTotalsByItem(long orderId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT dl.item_id,
       SUM(dl.qty) AS qty_received
FROM docs d
INNER JOIN doc_lines dl ON dl.doc_id = d.id
WHERE d.order_id = @order_id
  AND d.status = @status
  AND d.type = @doc_type
  AND dl.order_line_id IS NULL
  AND dl.qty > 0
  AND NOT EXISTS (
      SELECT 1
      FROM doc_lines newer
      WHERE newer.replaces_line_id = dl.id
  )
GROUP BY dl.item_id;
");
            command.Parameters.AddWithValue("@order_id", orderId);
            command.Parameters.AddWithValue("@status", DocTypeMapper.StatusToString(DocStatus.Closed));
            command.Parameters.AddWithValue("@doc_type", DocTypeMapper.ToOpString(DocType.ProductionReceipt));
            using var reader = command.ExecuteReader();
            var totals = new Dictionary<long, double>();
            while (reader.Read())
            {
                totals[reader.GetInt64(0)] = reader.GetDouble(1);
            }

            return totals;
        });
    }

    public IReadOnlyList<ProductionNeedRow> GetProductionNeedRows(bool includeZeroNeed)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
WITH item_snapshot AS (
    SELECT i.id AS item_id,
           i.name AS item_name,
           i.gtin,
           COALESCE(it.name, 'Без типа') AS item_type_name,
           COALESCE(it.enable_min_stock_control, FALSE) AS enable_min_stock_control,
           COALESCE(i.min_stock_qty, 0) AS min_stock_qty
    FROM items i
    LEFT JOIN item_types it ON it.id = i.item_type_id
),
stock_by_item AS (
    SELECT l.item_id,
           SUM(l.qty_delta) AS physical_stock_qty
    FROM ledger l
    GROUP BY l.item_id
),
reserved_hu AS (
    SELECT DISTINCT p.item_id,
           UPPER(BTRIM(p.to_hu)) AS hu_code
    FROM order_receipt_plan_lines p
    INNER JOIN orders o ON o.id = p.order_id
    INNER JOIN item_types it ON it.id = (SELECT item_type_id FROM items WHERE id = p.item_id)
    WHERE o.order_type = @customer_order_type
      AND o.status NOT IN (@shipped_order_status, @cancelled_order_status)
      AND p.qty_planned > 0
      AND p.to_hu IS NOT NULL
      AND BTRIM(p.to_hu) <> ''
),
reserved_customer_stock AS (
    SELECT l.item_id,
           SUM(l.qty_delta) AS reserved_customer_order_qty
    FROM ledger l
    INNER JOIN reserved_hu rh ON rh.item_id = l.item_id
                              AND rh.hu_code = UPPER(BTRIM(COALESCE(l.hu_code, l.hu)))
    GROUP BY l.item_id
),
active_doc_lines AS (
    SELECT dl.id,
           dl.doc_id,
           dl.order_line_id,
           dl.item_id,
           dl.qty
    FROM doc_lines dl
    WHERE dl.qty > 0
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
),
customer_order_lines AS (
    SELECT ol.id,
           ol.order_id,
           ol.item_id,
           ol.qty_ordered
    FROM order_lines ol
    INNER JOIN orders o ON o.id = ol.order_id
    WHERE o.order_type = @customer_order_type
      AND o.status NOT IN (@draft_order_status, @shipped_order_status, @cancelled_order_status)
),
customer_legacy_receipt_by_line AS (
    SELECT dl.order_line_id,
           SUM(dl.qty) AS qty_received
    FROM active_doc_lines dl
    INNER JOIN docs d ON d.id = dl.doc_id
    INNER JOIN customer_order_lines col ON col.id = dl.order_line_id
    WHERE d.status = @closed_doc_status
      AND d.type = @production_doc_type
      AND NOT EXISTS (
          SELECT 1
          FROM production_pallets pp
          WHERE pp.prd_doc_id = d.id
            AND pp.status <> @cancelled_pallet_status
      )
    GROUP BY dl.order_line_id
),
customer_filled_pallet_by_line AS (
    SELECT ppl.order_line_id,
           SUM(ppl.planned_qty) AS qty_received
    FROM production_pallet_lines ppl
    INNER JOIN production_pallets pp ON pp.id = ppl.production_pallet_id
    INNER JOIN customer_order_lines col ON col.id = ppl.order_line_id
    WHERE pp.status = @filled_pallet_status
      AND ppl.planned_qty > 0
    GROUP BY ppl.order_line_id
),
customer_receipt_by_line AS (
    SELECT order_line_id,
           SUM(qty_received) AS qty_received
    FROM (
        SELECT order_line_id, qty_received
        FROM customer_legacy_receipt_by_line
        UNION ALL
        SELECT order_line_id, qty_received
        FROM customer_filled_pallet_by_line
    ) customer_receipt_sources
    GROUP BY order_line_id
),
customer_reserved_by_line AS (
    SELECT p.order_line_id,
           SUM(p.qty_planned) AS qty_reserved
    FROM order_receipt_plan_lines p
    INNER JOIN customer_order_lines col ON col.id = p.order_line_id
    WHERE p.qty_planned > 0
    GROUP BY p.order_line_id
),
need_by_item AS (
    SELECT col.item_id,
           SUM(GREATEST(0, col.qty_ordered - COALESCE(receipt.qty_received, 0) - COALESCE(reserved.qty_reserved, 0))) AS order_qty
    FROM customer_order_lines col
    LEFT JOIN customer_receipt_by_line receipt ON receipt.order_line_id = col.id
    LEFT JOIN customer_reserved_by_line reserved ON reserved.order_line_id = col.id
    GROUP BY col.item_id
),
internal_order_lines AS (
    SELECT ol.id,
           ol.order_id,
           ol.item_id,
           ol.qty_ordered
    FROM order_lines ol
    INNER JOIN orders o ON o.id = ol.order_id
    WHERE o.order_type = @internal_order_type
      AND o.status NOT IN (@shipped_order_status, @cancelled_order_status)
),
internal_direct_by_line AS (
    SELECT dl.order_line_id,
           SUM(dl.qty) AS qty_received
    FROM active_doc_lines dl
    INNER JOIN docs d ON d.id = dl.doc_id
    INNER JOIN internal_order_lines iol ON iol.id = dl.order_line_id
    WHERE d.status = @closed_doc_status
      AND d.type = @production_doc_type
      AND NOT EXISTS (
          SELECT 1
          FROM production_pallets pp
          WHERE pp.prd_doc_id = d.id
            AND pp.status <> @cancelled_pallet_status
      )
    GROUP BY dl.order_line_id
),
internal_filled_pallet_by_line AS (
    SELECT ppl.order_line_id,
           SUM(
               CASE
                   WHEN ppl.filled_qty > 0 THEN ppl.filled_qty
                   ELSE ppl.planned_qty
               END
           ) AS qty_received
    FROM production_pallet_lines ppl
    INNER JOIN production_pallets pp ON pp.id = ppl.production_pallet_id
    INNER JOIN internal_order_lines iol ON iol.id = ppl.order_line_id
    WHERE pp.status = @filled_pallet_status
      AND pp.status <> @cancelled_pallet_status
    GROUP BY ppl.order_line_id
),
internal_receipt_by_line AS (
    SELECT order_line_id,
           SUM(qty_received) AS qty_received
    FROM (
        SELECT order_line_id, qty_received
        FROM internal_direct_by_line
        UNION ALL
        SELECT order_line_id, qty_received
        FROM internal_filled_pallet_by_line
    ) internal_receipt_sources
    GROUP BY order_line_id
),
internal_unlinked_by_item AS (
    SELECT d.order_id,
           dl.item_id,
           SUM(dl.qty) AS qty_received
    FROM docs d
    INNER JOIN active_doc_lines dl ON dl.doc_id = d.id
    INNER JOIN orders o ON o.id = d.order_id
    WHERE o.order_type = @internal_order_type
      AND o.status NOT IN (@shipped_order_status, @cancelled_order_status)
      AND d.status = @closed_doc_status
      AND d.type = @production_doc_type
      AND dl.order_line_id IS NULL
      AND NOT EXISTS (
          SELECT 1
          FROM production_pallets pp
          WHERE pp.prd_doc_id = d.id
            AND pp.status <> @cancelled_pallet_status
      )
    GROUP BY d.order_id,
             dl.item_id
),
internal_line_seed AS (
    SELECT iol.order_id,
           iol.id AS order_line_id,
           iol.item_id,
           iol.qty_ordered,
           COALESCE(receipt.qty_received, 0) AS qty_direct_received,
           COALESCE(unlinked.qty_received, 0) AS qty_unlinked_item_received,
           GREATEST(0, iol.qty_ordered - COALESCE(receipt.qty_received, 0)) AS qty_direct_unfilled,
           ROW_NUMBER() OVER (
               PARTITION BY iol.order_id, iol.item_id
               ORDER BY iol.id DESC
           ) AS item_line_desc_rank,
           COALESCE(SUM(GREATEST(0, iol.qty_ordered - COALESCE(receipt.qty_received, 0))) OVER (
               PARTITION BY iol.order_id, iol.item_id
               ORDER BY iol.id
               ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING
           ), 0) AS qty_direct_unfilled_before
    FROM internal_order_lines iol
    LEFT JOIN internal_receipt_by_line receipt ON receipt.order_line_id = iol.id
    LEFT JOIN internal_unlinked_by_item unlinked ON unlinked.order_id = iol.order_id
                                                 AND unlinked.item_id = iol.item_id
),
planned_internal_by_line AS (
    SELECT item_id,
           GREATEST(0, qty_ordered - (
               qty_direct_received
               + CASE
                     WHEN qty_unlinked_item_received <= 0 THEN 0
                     WHEN item_line_desc_rank = 1 THEN GREATEST(0, qty_unlinked_item_received - qty_direct_unfilled_before)
                     ELSE GREATEST(0, LEAST(qty_unlinked_item_received - qty_direct_unfilled_before, qty_direct_unfilled))
                 END
           )) AS qty_remaining
    FROM internal_line_seed
),
planned_internal_by_item AS (
    SELECT item_id,
           SUM(qty_remaining) AS planned_internal_stock_qty
    FROM planned_internal_by_line
    WHERE qty_remaining > 0
    GROUP BY item_id
),
filled_pallet_by_item AS (
    SELECT ppl.item_id,
           SUM(
               CASE
                   WHEN ppl.filled_qty > 0 THEN ppl.filled_qty
                   ELSE ppl.planned_qty
               END
           ) AS filled_pallet_qty
    FROM production_pallets pp
    INNER JOIN docs d ON d.id = pp.prd_doc_id
    INNER JOIN production_pallet_lines ppl ON ppl.production_pallet_id = pp.id
    LEFT JOIN orders o ON o.id = COALESCE(pp.order_id, d.order_id)
    WHERE d.type = @production_doc_type
      AND d.status <> @closed_doc_status
      AND pp.status = @filled_pallet_status
      AND pp.status <> @cancelled_pallet_status
      AND (o.id IS NULL OR o.status NOT IN (@shipped_order_status, @cancelled_order_status))
    GROUP BY ppl.item_id
),
item_ids AS (
    SELECT item_id FROM item_snapshot
    UNION
    SELECT item_id FROM stock_by_item
    UNION
    SELECT item_id FROM need_by_item
    UNION
    SELECT item_id FROM planned_internal_by_item
    UNION
    SELECT item_id FROM filled_pallet_by_item
    UNION
    SELECT item_id FROM reserved_customer_stock
)
SELECT ids.item_id,
       CURRENT_DATE::text,
       snapshot.gtin,
       COALESCE(snapshot.item_name, '#' || ids.item_id::text) AS item_name,
       COALESCE(snapshot.item_type_name, 'Без типа') AS item_type_name,
       COALESCE(stock.physical_stock_qty, 0) - COALESCE(reserved_stock.reserved_customer_order_qty, 0) AS free_stock_qty,
       CASE
           WHEN COALESCE(snapshot.enable_min_stock_control, FALSE) THEN GREATEST(0, COALESCE(snapshot.min_stock_qty, 0))
           ELSE 0
       END AS min_stock_qty,
       GREATEST(0, COALESCE(need.order_qty, 0)) AS to_close_orders_qty,
       GREATEST(0, CASE
           WHEN COALESCE(snapshot.enable_min_stock_control, FALSE)
               THEN COALESCE(snapshot.min_stock_qty, 0) - (COALESCE(stock.physical_stock_qty, 0) - COALESCE(reserved_stock.reserved_customer_order_qty, 0))
           ELSE 0
       END - COALESCE(planned.planned_internal_stock_qty, 0)) AS to_min_stock_qty,
       COALESCE(planned.planned_internal_stock_qty, 0) AS open_internal_order_qty,
       COALESCE(filled.filled_pallet_qty, 0) AS filled_pallet_qty
FROM item_ids ids
LEFT JOIN item_snapshot snapshot ON snapshot.item_id = ids.item_id
LEFT JOIN stock_by_item stock ON stock.item_id = ids.item_id
LEFT JOIN reserved_customer_stock reserved_stock ON reserved_stock.item_id = ids.item_id
LEFT JOIN need_by_item need ON need.item_id = ids.item_id
LEFT JOIN planned_internal_by_item planned ON planned.item_id = ids.item_id
LEFT JOIN filled_pallet_by_item filled ON filled.item_id = ids.item_id
WHERE @include_zero = TRUE
   OR GREATEST(0, COALESCE(need.order_qty, 0))
      + GREATEST(0, CASE
          WHEN COALESCE(snapshot.enable_min_stock_control, FALSE)
              THEN COALESCE(snapshot.min_stock_qty, 0) - (COALESCE(stock.physical_stock_qty, 0) - COALESCE(reserved_stock.reserved_customer_order_qty, 0))
          ELSE 0
      END - COALESCE(planned.planned_internal_stock_qty, 0)) > 0
   OR COALESCE(planned.planned_internal_stock_qty, 0) > 0
   OR COALESCE(filled.filled_pallet_qty, 0) > 0
ORDER BY
    (GREATEST(0, COALESCE(need.order_qty, 0))
     + GREATEST(0, CASE
         WHEN COALESCE(snapshot.enable_min_stock_control, FALSE)
             THEN COALESCE(snapshot.min_stock_qty, 0) - (COALESCE(stock.physical_stock_qty, 0) - COALESCE(reserved_stock.reserved_customer_order_qty, 0))
         ELSE 0
     END - COALESCE(planned.planned_internal_stock_qty, 0))) DESC,
    COALESCE(snapshot.item_type_name, 'Без типа'),
    COALESCE(snapshot.item_name, '#' || ids.item_id::text),
    ids.item_id;
");
            command.Parameters.AddWithValue("@include_zero", includeZeroNeed);
            command.Parameters.AddWithValue("@customer_order_type", OrderStatusMapper.TypeToString(OrderType.Customer));
            command.Parameters.AddWithValue("@internal_order_type", OrderStatusMapper.TypeToString(OrderType.Internal));
            command.Parameters.AddWithValue("@draft_order_status", OrderStatusMapper.StatusToString(OrderStatus.Draft));
            command.Parameters.AddWithValue("@shipped_order_status", OrderStatusMapper.StatusToString(OrderStatus.Shipped));
            command.Parameters.AddWithValue("@cancelled_order_status", OrderStatusMapper.StatusToString(OrderStatus.Cancelled));
            command.Parameters.AddWithValue("@closed_doc_status", DocTypeMapper.StatusToString(DocStatus.Closed));
            command.Parameters.AddWithValue("@outbound_doc_type", DocTypeMapper.ToOpString(DocType.Outbound));
            command.Parameters.AddWithValue("@production_doc_type", DocTypeMapper.ToOpString(DocType.ProductionReceipt));
            command.Parameters.AddWithValue("@filled_pallet_status", ProductionPalletStatus.Filled);
            command.Parameters.AddWithValue("@cancelled_pallet_status", ProductionPalletStatus.Cancelled);
            using var reader = command.ExecuteReader();
            var rows = new List<ProductionNeedRow>();
            while (reader.Read())
            {
                var minStockQty = reader.GetDouble(6);
                var toCloseOrdersQty = reader.GetDouble(7);
                var toMinStockQty = reader.GetDouble(8);
                var openInternalOrderQty = reader.GetDouble(9);
                var filledPalletQty = reader.GetDouble(10);
                var qtyToCreate = toMinStockQty;
                rows.Add(new ProductionNeedRow
                {
                    ItemId = reader.GetInt64(0),
                    NeedDate = DateTime.Today,
                    Gtin = reader.IsDBNull(2) ? null : reader.GetString(2),
                    ItemName = reader.GetString(3),
                    ItemTypeName = reader.GetString(4),
                    FreeStockQty = reader.GetDouble(5),
                    MinStockQty = minStockQty,
                    ToCloseOrdersQty = toCloseOrdersQty,
                    ToMinStockQty = toMinStockQty,
                    OpenInternalOrderQty = openInternalOrderQty,
                    FilledPalletQty = filledPalletQty,
                    QtyToCreate = qtyToCreate,
                    CanCreateOrder = qtyToCreate > 0.000001d,
                    Reason = BuildProductionNeedReason(minStockQty, toMinStockQty, openInternalOrderQty, qtyToCreate),
                    TotalToMakeQty = toCloseOrdersQty + toMinStockQty
                });
            }

            return rows;
        });
    }

    public IReadOnlyList<OrderReceiptPlanLine> GetOrderReceiptPlanLines(long orderId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT p.id,
       p.order_id,
       p.order_line_id,
       p.item_id,
       i.name,
       p.qty_planned,
       p.to_location_id,
       l.code,
       l.name,
       p.to_hu,
       p.sort_order
FROM order_receipt_plan_lines p
INNER JOIN items i ON i.id = p.item_id
LEFT JOIN locations l ON l.id = p.to_location_id
WHERE p.order_id = @order_id
ORDER BY p.sort_order, p.id;
");
            command.Parameters.AddWithValue("@order_id", orderId);
            using var reader = command.ExecuteReader();
            var lines = new List<OrderReceiptPlanLine>();
            while (reader.Read())
            {
                lines.Add(new OrderReceiptPlanLine
                {
                    Id = reader.GetInt64(0),
                    OrderId = reader.GetInt64(1),
                    OrderLineId = reader.GetInt64(2),
                    ItemId = reader.GetInt64(3),
                    ItemName = reader.GetString(4),
                    QtyPlanned = reader.GetDouble(5),
                    ToLocationId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    ToLocationCode = reader.IsDBNull(7) ? null : reader.GetString(7),
                    ToLocationName = reader.IsDBNull(8) ? null : reader.GetString(8),
                    ToHu = reader.IsDBNull(9) ? null : reader.GetString(9),
                    SortOrder = reader.IsDBNull(10) ? 0 : reader.GetInt32(10)
                });
            }

            return lines;
        });
    }

    private static string BuildProductionNeedReason(double minStockQty, double toMinStockQty, double openInternalOrderQty, double qtyToCreate)
    {
        if (qtyToCreate > 0.000001d)
        {
            return "Требуется пополнение склада до минимального остатка.";
        }

        if (minStockQty <= 0.000001d)
        {
            return "Для товара не задан минимальный остаток.";
        }

        if (toMinStockQty <= 0.000001d && openInternalOrderQty > 0.000001d)
        {
            return "Потребность уже покрыта открытой внутренней работой.";
        }

        return "Свободный остаток уже покрывает минимальный уровень.";
    }

    public IReadOnlyCollection<string> GetReservedOrderReceiptHuCodes(long? excludeOrderId = null)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT DISTINCT p.to_hu
FROM order_receipt_plan_lines p
INNER JOIN orders o ON o.id = p.order_id
WHERE p.to_hu IS NOT NULL
  AND p.to_hu <> ''
  AND o.status <> @shipped_status
  AND o.status <> @cancelled_status
  AND (@exclude_order_id IS NULL OR p.order_id <> @exclude_order_id);
");
            command.Parameters.AddWithValue("@shipped_status", OrderStatusMapper.StatusToString(OrderStatus.Shipped));
            command.Parameters.AddWithValue("@cancelled_status", OrderStatusMapper.StatusToString(OrderStatus.Cancelled));
            command.Parameters.AddWithValue("@exclude_order_id", excludeOrderId.HasValue ? excludeOrderId.Value : DBNull.Value);
            using var reader = command.ExecuteReader();
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                {
                    continue;
                }

                var hu = reader.GetString(0).Trim();
                if (!string.IsNullOrWhiteSpace(hu))
                {
                    result.Add(hu);
                }
            }

            return result.ToList();
        });
    }

    public void ReplaceOrderReceiptPlanLines(long orderId, IReadOnlyList<OrderReceiptPlanLine> lines)
    {
        WithConnection(connection =>
        {
            var normalizedHuCodes = (lines ?? Array.Empty<OrderReceiptPlanLine>())
                .Where(line => line.QtyPlanned > 0 && !string.IsNullOrWhiteSpace(line.ToHu))
                .Select(line => line.ToHu!.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (normalizedHuCodes.Length > 0)
            {
                using var orderTypeCommand = CreateCommand(connection, @"
SELECT order_type
FROM orders
WHERE id = @order_id
LIMIT 1;
");
                orderTypeCommand.Parameters.AddWithValue("@order_id", orderId);
                var orderTypeValue = orderTypeCommand.ExecuteScalar() as string;
                if (string.IsNullOrWhiteSpace(orderTypeValue))
                {
                    throw new InvalidOperationException("Заказ не найден.");
                }

                if (string.Equals(orderTypeValue, OrderStatusMapper.TypeToString(OrderType.Customer), StringComparison.OrdinalIgnoreCase))
                {
                    using var conflictCommand = CreateCommand(connection, @"
SELECT p.to_hu, o.order_ref
FROM order_receipt_plan_lines p
INNER JOIN orders o ON o.id = p.order_id
WHERE p.order_id <> @order_id
  AND p.to_hu IS NOT NULL
  AND p.to_hu <> ''
  AND o.order_type = @customer_order_type
  AND o.status <> @shipped_status
  AND o.status <> @cancelled_status
  AND UPPER(TRIM(p.to_hu)) = ANY(@hu_codes)
LIMIT 1;
");
                    conflictCommand.Parameters.AddWithValue("@order_id", orderId);
                    conflictCommand.Parameters.AddWithValue("@customer_order_type", OrderStatusMapper.TypeToString(OrderType.Customer));
                    conflictCommand.Parameters.AddWithValue("@shipped_status", OrderStatusMapper.StatusToString(OrderStatus.Shipped));
                    conflictCommand.Parameters.AddWithValue("@cancelled_status", OrderStatusMapper.StatusToString(OrderStatus.Cancelled));
                    conflictCommand.Parameters.AddWithValue("@hu_codes", normalizedHuCodes);
                    using var conflictReader = conflictCommand.ExecuteReader();
                    if (conflictReader.Read())
                    {
                        var huCode = conflictReader.IsDBNull(0) ? string.Empty : conflictReader.GetString(0);
                        var orderRef = conflictReader.IsDBNull(1) ? string.Empty : conflictReader.GetString(1);
                        throw new InvalidOperationException($"HU '{huCode}' уже зарезервирован за активным клиентским заказом '{orderRef}'.");
                    }
                }
            }

            using (var deleteCommand = CreateCommand(connection, "DELETE FROM order_receipt_plan_lines WHERE order_id = @order_id"))
            {
                deleteCommand.Parameters.AddWithValue("@order_id", orderId);
                deleteCommand.ExecuteNonQuery();
            }

            if (lines == null || lines.Count == 0)
            {
                return 0;
            }

            using var insertCommand = CreateCommand(connection, @"
INSERT INTO order_receipt_plan_lines(order_id, order_line_id, item_id, qty_planned, to_location_id, to_hu, sort_order)
VALUES(@order_id, @order_line_id, @item_id, @qty_planned, @to_location_id, @to_hu, @sort_order);
");
            foreach (var line in lines)
            {
                insertCommand.Parameters.Clear();
                insertCommand.Parameters.AddWithValue("@order_id", orderId);
                insertCommand.Parameters.AddWithValue("@order_line_id", line.OrderLineId);
                insertCommand.Parameters.AddWithValue("@item_id", line.ItemId);
                insertCommand.Parameters.AddWithValue("@qty_planned", line.QtyPlanned);
                insertCommand.Parameters.AddWithValue("@to_location_id", line.ToLocationId.HasValue ? line.ToLocationId.Value : DBNull.Value);
                insertCommand.Parameters.AddWithValue("@to_hu", string.IsNullOrWhiteSpace(line.ToHu) ? DBNull.Value : line.ToHu.Trim());
                insertCommand.Parameters.AddWithValue("@sort_order", line.SortOrder);
                insertCommand.ExecuteNonQuery();
            }

            return 0;
        });
    }

    public IReadOnlyList<OrderShipmentLine> GetOrderShipmentRemaining(long orderId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT ol.id,
       ol.order_id,
       ol.item_id,
       i.name,
       ol.qty_ordered,
       COALESCE(s.sum_qty, 0) AS shipped_qty,
       (ol.qty_ordered - COALESCE(s.sum_qty, 0)) AS remaining
FROM order_lines ol
INNER JOIN items i ON i.id = ol.item_id
LEFT JOIN (
    SELECT dl.order_line_id, SUM(dl.qty) AS sum_qty
    FROM doc_lines dl
    INNER JOIN docs d ON d.id = dl.doc_id
    WHERE d.status = @status
      AND d.type = @doc_type
      AND d.order_id = @order_id
      AND dl.order_line_id IS NOT NULL
      AND dl.qty > 0
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
    GROUP BY dl.order_line_id
) s ON s.order_line_id = ol.id
WHERE ol.order_id = @order_id
ORDER BY ol.id;
");
            command.Parameters.AddWithValue("@order_id", orderId);
            command.Parameters.AddWithValue("@status", DocTypeMapper.StatusToString(DocStatus.Closed));
            command.Parameters.AddWithValue("@doc_type", DocTypeMapper.ToOpString(DocType.Outbound));
            using var reader = command.ExecuteReader();
            var lines = new List<OrderShipmentLine>();
            while (reader.Read())
            {
                lines.Add(new OrderShipmentLine
                {
                    OrderLineId = reader.GetInt64(0),
                    OrderId = reader.GetInt64(1),
                    ItemId = reader.GetInt64(2),
                    ItemName = reader.GetString(3),
                    QtyOrdered = reader.GetDouble(4),
                    QtyShipped = reader.GetDouble(5),
                    QtyRemaining = reader.GetDouble(6)
                });
            }

            return lines;
        });
    }

    public long AddOrderLine(OrderLine line)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO order_lines(order_id, item_id, qty_ordered, production_purpose, production_pallet_group)
VALUES(@order_id, @item_id, @qty_ordered, @production_purpose, @production_pallet_group)
RETURNING id;
");
            command.Parameters.AddWithValue("@order_id", line.OrderId);
            command.Parameters.AddWithValue("@item_id", line.ItemId);
            command.Parameters.AddWithValue("@qty_ordered", line.QtyOrdered);
            command.Parameters.AddWithValue("@production_purpose", ProductionLinePurposeMapper.ToDbValue(line.ProductionPurpose));
            command.Parameters.AddWithValue("@production_pallet_group", string.IsNullOrWhiteSpace(line.ProductionPalletGroup) ? DBNull.Value : line.ProductionPalletGroup.Trim());
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdateOrderLineQty(long orderLineId, double qtyOrdered)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE order_lines SET qty_ordered = @qty_ordered WHERE id = @id");
            command.Parameters.AddWithValue("@qty_ordered", qtyOrdered);
            command.Parameters.AddWithValue("@id", orderLineId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void UpdateOrderLinePurpose(long orderLineId, ProductionLinePurpose purpose)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE order_lines SET production_purpose = @production_purpose WHERE id = @id");
            command.Parameters.AddWithValue("@production_purpose", ProductionLinePurposeMapper.ToDbValue(purpose));
            command.Parameters.AddWithValue("@id", orderLineId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void UpdateOrderLineProductionPalletGroup(long orderLineId, string? groupCode)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE order_lines SET production_pallet_group = @production_pallet_group WHERE id = @id");
            command.Parameters.AddWithValue("@production_pallet_group", string.IsNullOrWhiteSpace(groupCode) ? DBNull.Value : groupCode.Trim());
            command.Parameters.AddWithValue("@id", orderLineId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void DeleteOrderLine(long orderLineId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM order_lines WHERE id = @id");
            command.Parameters.AddWithValue("@id", orderLineId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void DeleteOrderLines(long orderId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM order_lines WHERE order_id = @order_id");
            command.Parameters.AddWithValue("@order_id", orderId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void DeleteOrder(long orderId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM orders WHERE id = @id");
            command.Parameters.AddWithValue("@id", orderId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public IReadOnlyDictionary<long, double> GetLedgerTotalsByItem()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT item_id, COALESCE(SUM(qty_delta), 0) FROM ledger GROUP BY item_id");
            using var reader = command.ExecuteReader();
            var totals = new Dictionary<long, double>();
            while (reader.Read())
            {
                totals[reader.GetInt64(0)] = reader.GetDouble(1);
            }

            return totals;
        });
    }

    public IReadOnlyDictionary<long, double> GetShippedTotalsByOrder(long orderId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT dl.item_id, COALESCE(SUM(dl.qty), 0)
FROM docs d
INNER JOIN doc_lines dl ON dl.doc_id = d.id
WHERE d.type = @type
  AND d.status = @status
  AND d.order_id = @order_id
  AND dl.qty > 0
  AND NOT EXISTS (
      SELECT 1
      FROM doc_lines newer
      WHERE newer.replaces_line_id = dl.id
  )
GROUP BY dl.item_id;
");
            command.Parameters.AddWithValue("@type", DocTypeMapper.ToOpString(DocType.Outbound));
            command.Parameters.AddWithValue("@status", DocTypeMapper.StatusToString(DocStatus.Closed));
            command.Parameters.AddWithValue("@order_id", orderId);
            using var reader = command.ExecuteReader();
            var totals = new Dictionary<long, double>();
            while (reader.Read())
            {
                totals[reader.GetInt64(0)] = reader.GetDouble(1);
            }

            return totals;
        });
    }

    public IReadOnlyDictionary<long, double> GetShippedTotalsByOrderLine(long orderId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT dl.order_line_id, COALESCE(SUM(dl.qty), 0)
FROM docs d
INNER JOIN doc_lines dl ON dl.doc_id = d.id
WHERE d.type = @type
  AND d.status = @status
  AND d.order_id = @order_id
  AND dl.order_line_id IS NOT NULL
  AND dl.qty > 0
  AND NOT EXISTS (
      SELECT 1
      FROM doc_lines newer
      WHERE newer.replaces_line_id = dl.id
  )
GROUP BY dl.order_line_id;
");
            command.Parameters.AddWithValue("@type", DocTypeMapper.ToOpString(DocType.Outbound));
            command.Parameters.AddWithValue("@status", DocTypeMapper.StatusToString(DocStatus.Closed));
            command.Parameters.AddWithValue("@order_id", orderId);
            using var reader = command.ExecuteReader();
            var totals = new Dictionary<long, double>();
            while (reader.Read())
            {
                totals[reader.GetInt64(0)] = reader.GetDouble(1);
            }

            return totals;
        });
    }

    public DateTime? GetOrderShippedAt(long orderId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT MAX(closed_at)
FROM docs
WHERE type = @type AND status = @status AND order_id = @order_id;
");
            command.Parameters.AddWithValue("@type", DocTypeMapper.ToOpString(DocType.Outbound));
            command.Parameters.AddWithValue("@status", DocTypeMapper.StatusToString(DocStatus.Closed));
            command.Parameters.AddWithValue("@order_id", orderId);
            var result = command.ExecuteScalar() as string;
            return FromDbDate(result);
        });
    }

    public bool HasOutboundDocs(long orderId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT 1
FROM docs
WHERE type = @type AND order_id = @order_id
LIMIT 1;
");
            command.Parameters.AddWithValue("@type", DocTypeMapper.ToOpString(DocType.Outbound));
            command.Parameters.AddWithValue("@order_id", orderId);
            return command.ExecuteScalar() != null;
        });
    }

    public void AddLedgerEntry(LedgerEntry entry)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO ledger(ts, doc_id, item_id, location_id, qty_delta, hu_code, hu)
VALUES(@ts, @doc_id, @item_id, @location_id, @qty_delta, @hu_code, @hu);
");
            command.Parameters.AddWithValue("@ts", ToDbDate(entry.Timestamp));
            command.Parameters.AddWithValue("@doc_id", entry.DocId);
            command.Parameters.AddWithValue("@item_id", entry.ItemId);
            command.Parameters.AddWithValue("@location_id", entry.LocationId);
            command.Parameters.AddWithValue("@qty_delta", entry.QtyDelta);
            command.Parameters.AddWithValue("@hu_code", string.IsNullOrWhiteSpace(entry.HuCode) ? DBNull.Value : entry.HuCode);
            command.Parameters.AddWithValue("@hu", string.IsNullOrWhiteSpace(entry.HuCode) ? DBNull.Value : entry.HuCode);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public IReadOnlyList<StockRow> GetStock(string? search)
    {
        return WithConnection(connection =>
        {
            var rawRows = new List<(
                long ItemId,
                string ItemName,
                string? Barcode,
                string LocationCode,
                string? Hu,
                double Qty,
                string BaseUom,
                long? ItemTypeId,
                string? ItemTypeName,
                bool ItemTypeEnableMinStockControl,
                bool ItemTypeMinStockUsesOrderBinding,
                bool ItemTypeEnableOrderReservation,
                double? MinStockQty)>();
            {
                using var command = CreateCommand(connection, BuildStockQuery(search));
                if (!string.IsNullOrWhiteSpace(search))
                {
                    command.Parameters.AddWithValue("@search", $"%{search.Trim()}%");
                }

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rawRows.Add((
                        ItemId: reader.GetInt64(0),
                        ItemName: reader.GetString(1),
                        Barcode: reader.IsDBNull(2) ? null : reader.GetString(2),
                        LocationCode: reader.GetString(3),
                        Hu: reader.IsDBNull(4) ? null : reader.GetString(4),
                        Qty: reader.GetDouble(5),
                        BaseUom: reader.IsDBNull(6) ? "шт" : reader.GetString(6),
                        ItemTypeId: reader.IsDBNull(7) ? null : reader.GetInt64(7),
                        ItemTypeName: reader.IsDBNull(8) ? null : reader.GetString(8),
                        ItemTypeEnableMinStockControl: !reader.IsDBNull(9) && reader.GetBoolean(9),
                        ItemTypeMinStockUsesOrderBinding: !reader.IsDBNull(10) && reader.GetBoolean(10),
                        ItemTypeEnableOrderReservation: !reader.IsDBNull(11) && reader.GetBoolean(11),
                        MinStockQty: reader.IsDBNull(12) ? null : reader.GetDouble(12)));
                }
            }

            var physicalLedgerStockByItem = rawRows
                .GroupBy(row => row.ItemId)
                .ToDictionary(group => group.Key, group => group.Sum(x => x.Qty));
            var reservedHuKeys = GetHuOrderContextRows(connection)
                .Where(row => row.ItemId > 0
                              && row.ReservedCustomerOrderId.HasValue
                              && !string.IsNullOrWhiteSpace(row.HuCode))
                .Select(row => (row.ItemId, HuCode: row.HuCode.Trim().ToUpperInvariant()))
                .ToHashSet();
            var reservedCustomerQtyByItem = rawRows
                .Where(row => !string.IsNullOrWhiteSpace(row.Hu))
                .GroupBy(row => (row.ItemId, HuCode: row.Hu!.Trim().ToUpperInvariant()))
                .Where(group => reservedHuKeys.Contains(group.Key))
                .GroupBy(group => group.Key.ItemId)
                .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Sum(x => x.Qty)));

            var rows = rawRows
                .Select(row =>
                {
                    var physicalLedgerStockQty = physicalLedgerStockByItem.TryGetValue(row.ItemId, out var physicalQty)
                        ? physicalQty
                        : 0;
                    var reservedCustomerOrderQty = reservedCustomerQtyByItem.TryGetValue(row.ItemId, out var reservedQty)
                        ? reservedQty
                        : 0;
                    var availableForMinStockQty = MinStockControlCalculator.CalculateAvailableForMinStock(
                        physicalLedgerStockQty,
                        reservedCustomerOrderQty,
                        row.ItemTypeMinStockUsesOrderBinding);
                    return new StockRow
                    {
                        ItemId = row.ItemId,
                        ItemName = row.ItemName,
                        Barcode = row.Barcode,
                        LocationCode = row.LocationCode,
                        Hu = row.Hu,
                        Qty = row.Qty,
                        BaseUom = row.BaseUom,
                        ItemTypeId = row.ItemTypeId,
                        ItemTypeName = row.ItemTypeName,
                        ItemTypeEnableMinStockControl = row.ItemTypeEnableMinStockControl,
                        ItemTypeMinStockUsesOrderBinding = row.ItemTypeMinStockUsesOrderBinding,
                        ItemTypeEnableOrderReservation = row.ItemTypeEnableOrderReservation,
                        MinStockQty = row.MinStockQty,
                        ReservedCustomerOrderQty = reservedCustomerOrderQty,
                        AvailableForMinStockQty = availableForMinStockQty
                    };
                })
                .ToList();

            return rows;
        });
    }

    public double GetLedgerBalance(long itemId, long locationId)
    {
        return GetLedgerBalance(itemId, locationId, null);
    }

    public double GetLedgerBalance(long itemId, long locationId, string? huCode)
    {
        return WithConnection(connection =>
        {
            var sql = @"
SELECT COALESCE(SUM(qty_delta), 0)
FROM ledger
WHERE item_id = @item_id AND location_id = @location_id";
            if (string.IsNullOrWhiteSpace(huCode))
            {
                sql += " AND hu_code IS NULL AND hu IS NULL";
            }
            else
            {
                sql += " AND (hu_code = @hu OR (hu_code IS NULL AND hu = @hu))";
            }

            using var command = CreateCommand(connection, sql);
            command.Parameters.AddWithValue("@item_id", itemId);
            command.Parameters.AddWithValue("@location_id", locationId);
            if (!string.IsNullOrWhiteSpace(huCode))
            {
                command.Parameters.AddWithValue("@hu", huCode);
            }
            var result = command.ExecuteScalar();
            return result == null || result == DBNull.Value ? 0 : Convert.ToDouble(result, CultureInfo.InvariantCulture);
        });
    }

    public IReadOnlyList<string?> GetHuCodesByLocation(long locationId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT COALESCE(hu_code, hu)
FROM ledger
WHERE location_id = @location_id
GROUP BY COALESCE(hu_code, hu)
HAVING COALESCE(SUM(qty_delta), 0) > 0
ORDER BY COALESCE(hu_code, hu);
");
            command.Parameters.AddWithValue("@location_id", locationId);
            using var reader = command.ExecuteReader();
            var list = new List<string?>();
            while (reader.Read())
            {
                list.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
            }

            return list;
        });
    }

    public IReadOnlyList<string> GetAllHuCodes()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT COALESCE(hu_code, hu)
FROM ledger
WHERE hu_code IS NOT NULL OR hu IS NOT NULL
GROUP BY COALESCE(hu_code, hu)
ORDER BY COALESCE(hu_code, hu);
");
            using var reader = command.ExecuteReader();
            var list = new List<string>();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                {
                    list.Add(reader.GetString(0));
                }
            }

            return list;
        });
    }

    public IReadOnlyList<Item> GetItemsByLocationAndHu(long locationId, string? huCode)
    {
        return WithConnection(connection =>
        {
            var sql = @"
SELECT i.id, i.name, i.is_active, i.barcode, i.gtin, i.base_uom, i.default_packaging_id, i.brand, i.volume, i.shelf_life_months, i.max_qty_per_hu, i.tara_id, i.is_marked, t.name, NULL::bigint, NULL::text, FALSE, FALSE, FALSE, NULL::double precision
FROM ledger l
INNER JOIN items i ON i.id = l.item_id
LEFT JOIN taras t ON t.id = i.tara_id
WHERE l.location_id = @location_id";
            if (string.IsNullOrWhiteSpace(huCode))
            {
                sql += " AND l.hu_code IS NULL AND l.hu IS NULL";
            }
            else
            {
                sql += " AND (l.hu_code = @hu OR (l.hu_code IS NULL AND l.hu = @hu))";
            }
            sql += @"
GROUP BY
    i.id,
    i.name,
    i.is_active,
    i.barcode,
    i.gtin,
    i.base_uom,
    i.default_packaging_id,
    i.brand,
    i.volume,
    i.shelf_life_months,
    i.max_qty_per_hu,
    i.tara_id,
    i.is_marked,
    t.name
HAVING COALESCE(SUM(l.qty_delta), 0) > 0
ORDER BY i.name;";

            using var command = CreateCommand(connection, sql);
            command.Parameters.AddWithValue("@location_id", locationId);
            if (!string.IsNullOrWhiteSpace(huCode))
            {
                command.Parameters.AddWithValue("@hu", huCode);
            }
            using var reader = command.ExecuteReader();
            var items = new List<Item>();
            while (reader.Read())
            {
                items.Add(ReadItem(reader));
            }

            return items;
        });
    }

    public double GetAvailableQty(long itemId, long locationId, string? huCode)
    {
        return GetLedgerBalance(itemId, locationId, huCode);
    }

    public IReadOnlyDictionary<string, double> GetLedgerTotalsByHu()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT COALESCE(hu_code, hu), COALESCE(SUM(qty_delta), 0)
FROM ledger
WHERE hu_code IS NOT NULL OR hu IS NOT NULL
GROUP BY COALESCE(hu_code, hu);
");
            using var reader = command.ExecuteReader();
            var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                {
                    continue;
                }

                totals[reader.GetString(0)] = reader.GetDouble(1);
            }

            return totals;
        });
    }

    public IReadOnlyList<HuStockRow> GetHuStockRows()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT COALESCE(hu_code, hu), item_id, location_id, COALESCE(SUM(qty_delta), 0) AS qty
FROM ledger
WHERE hu_code IS NOT NULL OR hu IS NOT NULL
GROUP BY COALESCE(hu_code, hu), item_id, location_id
HAVING COALESCE(SUM(qty_delta), 0) != 0;
");
            using var reader = command.ExecuteReader();
            var rows = new List<HuStockRow>();
            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                {
                    continue;
                }

                rows.Add(new HuStockRow
                {
                    HuCode = reader.GetString(0),
                    ItemId = reader.GetInt64(1),
                    LocationId = reader.GetInt64(2),
                    Qty = reader.GetDouble(3)
                });
            }

            return rows;
        });
    }

    public IReadOnlyList<HuOrderContextRow> GetHuOrderContextRows()
    {
        return WithConnection(GetHuOrderContextRows);
    }

    private IReadOnlyList<HuOrderContextRow> GetHuOrderContextRows(NpgsqlConnection connection)
    {
        using var command = CreateCommand(connection, @"
WITH hu_stock AS (
    SELECT UPPER(TRIM(COALESCE(hu_code, hu))) AS hu_code,
           item_id
    FROM ledger
    WHERE COALESCE(hu_code, hu) IS NOT NULL
      AND COALESCE(hu_code, hu) <> ''
    GROUP BY UPPER(TRIM(COALESCE(hu_code, hu))), item_id
    HAVING COALESCE(SUM(qty_delta), 0) <> 0
),
origin_candidates AS (
    SELECT dl.item_id,
           UPPER(TRIM(dl.to_hu)) AS hu_code,
           d.order_id AS origin_internal_order_id,
           COALESCE(d.order_ref, o.order_ref) AS origin_internal_order_ref,
           ROW_NUMBER() OVER (
               PARTITION BY dl.item_id, UPPER(TRIM(dl.to_hu))
               ORDER BY COALESCE(d.closed_at, d.created_at), d.id
           ) AS rn
    FROM doc_lines dl
    INNER JOIN docs d ON d.id = dl.doc_id
    INNER JOIN orders o ON o.id = d.order_id
    WHERE d.type = @prd_type
      AND d.status = @closed_status
      AND d.order_id IS NOT NULL
      AND o.order_type = @internal_order_type
      AND dl.qty > 0
      AND dl.to_hu IS NOT NULL
      AND dl.to_hu <> ''
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
),
origin_map AS (
    SELECT item_id,
           hu_code,
           origin_internal_order_id,
           origin_internal_order_ref
    FROM origin_candidates
    WHERE rn = 1
),
reserved_candidates AS (
    SELECT p.item_id,
           UPPER(TRIM(p.to_hu)) AS hu_code,
           p.order_id AS reserved_customer_order_id,
           o.order_ref AS reserved_customer_order_ref,
           o.partner_id AS reserved_customer_id,
           partner.name AS reserved_customer_name,
           0 AS source_priority,
           o.created_at AS source_order_created_at,
           p.id AS source_id
    FROM order_receipt_plan_lines p
    INNER JOIN orders o ON o.id = p.order_id
    LEFT JOIN partners partner ON partner.id = o.partner_id
    WHERE p.qty_planned > 0
      AND p.to_hu IS NOT NULL
      AND p.to_hu <> ''
      AND o.order_type = @customer_order_type
      AND o.status <> @shipped_status
      AND o.status <> @cancelled_status

    UNION ALL

    SELECT dl.item_id,
           UPPER(TRIM(dl.to_hu)) AS hu_code,
           d.order_id AS reserved_customer_order_id,
           COALESCE(d.order_ref, o.order_ref) AS reserved_customer_order_ref,
           o.partner_id AS reserved_customer_id,
           partner.name AS reserved_customer_name,
           1 AS source_priority,
           o.created_at AS source_order_created_at,
           dl.id AS source_id
    FROM doc_lines dl
    INNER JOIN docs d ON d.id = dl.doc_id
    INNER JOIN orders o ON o.id = d.order_id
    LEFT JOIN partners partner ON partner.id = o.partner_id
    WHERE d.type = @prd_type
      AND d.status = @closed_status
      AND d.order_id IS NOT NULL
      AND o.order_type = @customer_order_type
      AND o.status <> @shipped_status
      AND o.status <> @cancelled_status
      AND dl.qty > 0
      AND dl.to_hu IS NOT NULL
      AND dl.to_hu <> ''
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
),
reserved_ranked AS (
    SELECT item_id,
           hu_code,
           reserved_customer_order_id,
           reserved_customer_order_ref,
           reserved_customer_id,
           reserved_customer_name,
           ROW_NUMBER() OVER (
               PARTITION BY item_id, hu_code
               ORDER BY source_priority, source_order_created_at, reserved_customer_order_id, source_id
           ) AS rn
    FROM reserved_candidates
),
reserved_map AS (
    SELECT item_id,
           hu_code,
           reserved_customer_order_id,
           reserved_customer_order_ref,
           reserved_customer_id,
           reserved_customer_name
    FROM reserved_ranked
    WHERE rn = 1
)
SELECT hs.hu_code,
       hs.item_id,
       om.origin_internal_order_id,
       om.origin_internal_order_ref,
       rm.reserved_customer_order_id,
       rm.reserved_customer_order_ref,
       rm.reserved_customer_id,
       rm.reserved_customer_name
FROM hu_stock hs
LEFT JOIN origin_map om ON om.item_id = hs.item_id AND om.hu_code = hs.hu_code
LEFT JOIN reserved_map rm ON rm.item_id = hs.item_id AND rm.hu_code = hs.hu_code;
");
        command.Parameters.AddWithValue("@prd_type", DocTypeMapper.ToOpString(DocType.ProductionReceipt));
        command.Parameters.AddWithValue("@closed_status", DocTypeMapper.StatusToString(DocStatus.Closed));
        command.Parameters.AddWithValue("@internal_order_type", OrderStatusMapper.TypeToString(OrderType.Internal));
        command.Parameters.AddWithValue("@customer_order_type", OrderStatusMapper.TypeToString(OrderType.Customer));
        command.Parameters.AddWithValue("@shipped_status", OrderStatusMapper.StatusToString(OrderStatus.Shipped));
        command.Parameters.AddWithValue("@cancelled_status", OrderStatusMapper.StatusToString(OrderStatus.Cancelled));

        using var reader = command.ExecuteReader();
        var rows = new List<HuOrderContextRow>();
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            rows.Add(new HuOrderContextRow
            {
                HuCode = reader.GetString(0),
                ItemId = reader.GetInt64(1),
                OriginInternalOrderId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                OriginInternalOrderRef = reader.IsDBNull(3) ? null : reader.GetString(3),
                ReservedCustomerOrderId = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                ReservedCustomerOrderRef = reader.IsDBNull(5) ? null : reader.GetString(5),
                ReservedCustomerId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                ReservedCustomerName = reader.IsDBNull(7) ? null : reader.GetString(7)
            });
        }

        return rows;
    }

    public HuRecord CreateHuRecord(string? createdBy)
    {
        return WithConnection(connection =>
        {
            var createdAt = DateTime.Now;
            var ownsTransaction = _transaction == null;
            if (ownsTransaction)
            {
                using var begin = connection.CreateCommand();
                begin.CommandText = "BEGIN;";
                begin.ExecuteNonQuery();
            }

            try
            {
                using var insert = CreateCommand(connection, @"
INSERT INTO hus(hu_code, status, created_at, created_by)
VALUES('', 'ACTIVE', @created_at, @created_by)
RETURNING id;
");
                insert.Parameters.AddWithValue("@created_at", ToDbDate(createdAt));
                insert.Parameters.AddWithValue("@created_by", string.IsNullOrWhiteSpace(createdBy) ? DBNull.Value : createdBy.Trim());
                var id = (long)(insert.ExecuteScalar() ?? 0L);
                var code = $"HU-{id:000000}";

                using var update = CreateCommand(connection, "UPDATE hus SET hu_code = @hu_code WHERE id = @id");
                update.Parameters.AddWithValue("@hu_code", code);
                update.Parameters.AddWithValue("@id", id);
                update.ExecuteNonQuery();

                if (ownsTransaction)
                {
                    using var commit = connection.CreateCommand();
                    commit.CommandText = "COMMIT;";
                    commit.ExecuteNonQuery();
                }

                return new HuRecord
                {
                    Id = id,
                    Code = code,
                    Status = "ACTIVE",
                    CreatedAt = createdAt,
                    CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim()
                };
            }
            catch
            {
                if (ownsTransaction)
                {
                    using var rollback = connection.CreateCommand();
                    rollback.CommandText = "ROLLBACK;";
                    rollback.ExecuteNonQuery();
                }

                throw;
            }
        });
    }

    public HuRecord CreateHuRecord(string code, string? createdBy)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("HU не задан.", nameof(code));
        }

        return WithConnection(connection =>
        {
            var normalized = code.Trim().ToUpperInvariant();
            var createdAt = DateTime.Now;

            using var insert = CreateCommand(connection, @"
INSERT INTO hus(hu_code, status, created_at, created_by)
VALUES(@hu_code, 'ACTIVE', @created_at, @created_by)
ON CONFLICT (hu_code) DO NOTHING;
");
            insert.Parameters.AddWithValue("@hu_code", normalized);
            insert.Parameters.AddWithValue("@created_at", ToDbDate(createdAt));
            insert.Parameters.AddWithValue("@created_by", string.IsNullOrWhiteSpace(createdBy) ? DBNull.Value : createdBy.Trim());
            insert.ExecuteNonQuery();

            using var select = CreateCommand(connection, @"
SELECT id, hu_code, status, created_at, created_by, closed_at, note
FROM hus
WHERE hu_code = @code
LIMIT 1;
");
            select.Parameters.AddWithValue("@code", normalized);
            using var reader = select.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("Не удалось создать HU.");
            }

            return ReadHuRecord(reader);
        });
    }

    public HuRecord? GetHuByCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT id, hu_code, status, created_at, created_by, closed_at, note
FROM hus
WHERE hu_code = @code
LIMIT 1;
");
            command.Parameters.AddWithValue("@code", code.Trim());
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadHuRecord(reader) : null;
        });
    }

    public IReadOnlyList<HuRecord> GetHus(string? search, int take)
    {
        return WithConnection(connection =>
        {
            var normalizedTake = take < 1 ? 1 : take;
            if (normalizedTake > 10000)
            {
                normalizedTake = 10000;
            }

            var sql = @"
SELECT id, hu_code, status, created_at, created_by, closed_at, note
FROM hus";
            if (!string.IsNullOrWhiteSpace(search))
            {
                sql += " WHERE hu_code ILIKE @search";
            }

            sql += "\nORDER BY id DESC LIMIT @take;";

            using var command = CreateCommand(connection, sql);
            if (!string.IsNullOrWhiteSpace(search))
            {
                command.Parameters.AddWithValue("@search", $"%{search.Trim()}%");
            }
            command.Parameters.AddWithValue("@take", normalizedTake);
            using var reader = command.ExecuteReader();
            var list = new List<HuRecord>();
            while (reader.Read())
            {
                list.Add(ReadHuRecord(reader));
            }

            return list;
        });
    }

    public void CloseHu(string code, string? closedBy, string? note)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE hus
SET status = @status,
    closed_at = @closed_at,
    note = @note
WHERE hu_code = @code;
");
            command.Parameters.AddWithValue("@status", "CLOSED");
            command.Parameters.AddWithValue("@closed_at", ToDbDate(DateTime.Now));
            command.Parameters.AddWithValue("@note", string.IsNullOrWhiteSpace(note) ? DBNull.Value : note.Trim());
            command.Parameters.AddWithValue("@code", code.Trim());
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public IReadOnlyList<HuLedgerRow> GetHuLedgerRows(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Array.Empty<HuLedgerRow>();
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT i.id, i.name, i.base_uom, l.id, l.code, COALESCE(SUM(led.qty_delta), 0) AS qty
FROM ledger led
INNER JOIN items i ON i.id = led.item_id
INNER JOIN locations l ON l.id = led.location_id
WHERE (led.hu_code = @hu OR (led.hu_code IS NULL AND led.hu = @hu))
GROUP BY i.id, i.name, i.base_uom, l.id, l.code
HAVING SUM(led.qty_delta) != 0
ORDER BY i.name, l.code;
");
            command.Parameters.AddWithValue("@hu", code.Trim());
            using var reader = command.ExecuteReader();
            var rows = new List<HuLedgerRow>();
            while (reader.Read())
            {
                rows.Add(new HuLedgerRow
                {
                    HuCode = code.Trim(),
                    ItemId = reader.GetInt64(0),
                    ItemName = reader.GetString(1),
                    BaseUom = reader.IsDBNull(2) ? "шт" : reader.GetString(2),
                    LocationId = reader.GetInt64(3),
                    LocationCode = reader.GetString(4),
                    Qty = reader.GetDouble(5)
                });
            }

            return rows;
        });
    }

    public bool IsEventImported(string eventId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT 1 FROM imported_events WHERE event_id = @event_id LIMIT 1");
            command.Parameters.AddWithValue("@event_id", eventId);
            return command.ExecuteScalar() != null;
        });
    }

    public void AddImportedEvent(ImportedEvent ev)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO imported_events(event_id, imported_at, source_file, device_id)
VALUES(@event_id, @imported_at, @source_file, @device_id);
");
            command.Parameters.AddWithValue("@event_id", ev.EventId);
            command.Parameters.AddWithValue("@imported_at", ToDbDate(ev.ImportedAt));
            command.Parameters.AddWithValue("@source_file", ev.SourceFile);
            command.Parameters.AddWithValue("@device_id", string.IsNullOrWhiteSpace(ev.DeviceId) ? DBNull.Value : ev.DeviceId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public long AddImportError(ImportError err)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO import_errors(event_id, reason, raw_json, created_at)
VALUES(@event_id, @reason, @raw_json, @created_at)
RETURNING id;
");
            command.Parameters.AddWithValue("@event_id", (object?)err.EventId ?? DBNull.Value);
            command.Parameters.AddWithValue("@reason", err.Reason);
            command.Parameters.AddWithValue("@raw_json", err.RawJson);
            command.Parameters.AddWithValue("@created_at", ToDbDate(err.CreatedAt));
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public IReadOnlyList<ImportError> GetImportErrors(string? reason)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildImportErrorsQuery(reason));
            if (!string.IsNullOrWhiteSpace(reason))
            {
                command.Parameters.AddWithValue("@reason", reason.Trim());
            }

            using var reader = command.ExecuteReader();
            var errors = new List<ImportError>();
            while (reader.Read())
            {
                errors.Add(ReadImportError(reader));
            }

            return errors;
        });
    }

    public ImportError? GetImportError(long id)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, event_id, reason, raw_json, created_at FROM import_errors WHERE id = @id");
            command.Parameters.AddWithValue("@id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadImportError(reader) : null;
        });
    }

    public void DeleteImportError(long id)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM import_errors WHERE id = @id");
            command.Parameters.AddWithValue("@id", id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    private T WithConnection<T>(Func<NpgsqlConnection, T> action)
    {
        if (_connection != null)
        {
            return action(_connection);
        }

        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        return action(connection);
    }

    private NpgsqlCommand CreateCommand(NpgsqlConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        if (_transaction != null)
        {
            command.Transaction = _transaction;
        }

        return command;
    }

    private static Item ReadItem(NpgsqlDataReader reader)
    {
        var baseUom = reader.IsDBNull(5) ? null : reader.GetString(5);
        return new Item
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
            Barcode = reader.IsDBNull(3) ? null : reader.GetString(3),
            Gtin = reader.IsDBNull(4) ? null : reader.GetString(4),
            IsActive = reader.IsDBNull(2) || reader.GetBoolean(2),
            BaseUom = string.IsNullOrWhiteSpace(baseUom) ? "èâ" : baseUom,
            DefaultPackagingId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
            Brand = reader.IsDBNull(7) ? null : reader.GetString(7),
            Volume = reader.IsDBNull(8) ? null : reader.GetString(8),
            ShelfLifeMonths = reader.IsDBNull(9) ? null : reader.GetInt32(9),
            MaxQtyPerHu = reader.IsDBNull(10) ? null : Convert.ToDouble(reader.GetValue(10), CultureInfo.InvariantCulture),
            TaraId = reader.IsDBNull(11) ? null : reader.GetInt64(11),
            IsMarked = !reader.IsDBNull(12) && Convert.ToInt32(reader.GetValue(12), CultureInfo.InvariantCulture) != 0,
            TaraName = reader.IsDBNull(13) ? null : reader.GetString(13),
            ItemTypeId = reader.IsDBNull(14) ? null : reader.GetInt64(14),
            ItemTypeName = reader.IsDBNull(15) ? null : reader.GetString(15),
            ItemTypeIsVisibleInProductCatalog = !reader.IsDBNull(16) && reader.GetBoolean(16),
            ItemTypeEnableMinStockControl = !reader.IsDBNull(17) && reader.GetBoolean(17),
            ItemTypeEnableMarking = !reader.IsDBNull(18) && reader.GetBoolean(18),
            MinStockQty = reader.IsDBNull(19) ? null : Convert.ToDouble(reader.GetValue(19), CultureInfo.InvariantCulture)
        };
    }

    private static ItemType ReadItemType(NpgsqlDataReader reader)
    {
        return new ItemType
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
            Code = reader.IsDBNull(2) ? null : reader.GetString(2),
            SortOrder = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            IsActive = !reader.IsDBNull(4) && reader.GetBoolean(4),
            IsVisibleInProductCatalog = !reader.IsDBNull(5) && reader.GetBoolean(5),
            EnableMinStockControl = !reader.IsDBNull(6) && reader.GetBoolean(6),
            MinStockUsesOrderBinding = !reader.IsDBNull(7) && reader.GetBoolean(7),
            EnableOrderReservation = !reader.IsDBNull(8) && reader.GetBoolean(8),
            EnableHuDistribution = !reader.IsDBNull(9) && reader.GetBoolean(9),
            EnableMarking = reader.FieldCount > 10 && !reader.IsDBNull(10) && reader.GetBoolean(10)
        };
    }

    private static ItemPackaging ReadItemPackaging(NpgsqlDataReader reader)
    {
        return new ItemPackaging
        {
            Id = reader.GetInt64(0),
            ItemId = reader.GetInt64(1),
            Code = reader.GetString(2),
            Name = reader.GetString(3),
            FactorToBase = reader.GetDouble(4),
            IsActive = reader.GetInt64(5) == 1,
            SortOrder = reader.GetInt32(6)
        };
    }

    private static Location ReadLocation(NpgsqlDataReader reader)
    {
        return new Location
        {
            Id = reader.GetInt64(0),
            Code = reader.GetString(1),
            Name = reader.GetString(2),
            MaxHuSlots = reader.IsDBNull(3) ? null : reader.GetInt32(3),
            AutoHuDistributionEnabled = reader.IsDBNull(4) || reader.GetBoolean(4)
        };
    }

    private static Tara ReadTara(NpgsqlDataReader reader)
    {
        return new Tara
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1)
        };
    }

    private static Uom ReadUom(NpgsqlDataReader reader)
    {
        return new Uom
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1)
        };
    }

    private static WriteOffReason ReadWriteOffReason(NpgsqlDataReader reader)
    {
        return new WriteOffReason
        {
            Id = reader.GetInt64(0),
            Code = reader.GetString(1),
            Name = reader.GetString(2)
        };
    }

    private static Partner ReadPartner(NpgsqlDataReader reader)
    {
        return new Partner
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
            Code = reader.IsDBNull(2) ? null : reader.GetString(2),
            CreatedAt = FromDbDate(reader.IsDBNull(3) ? null : reader.GetString(3)) ?? DateTime.MinValue
        };
    }

    private static Doc ReadDoc(NpgsqlDataReader reader)
    {
        var type = DocTypeMapper.FromOpString(reader.GetString(2)) ?? DocType.Inbound;
        var status = DocTypeMapper.StatusFromString(reader.GetString(3)) ?? DocStatus.Draft;

        long? partnerId = null;
        long? orderId = null;
        string? orderRef = null;
        string? shippingRef = null;
        string? reasonCode = null;
        string? comment = null;
        string? productionBatchNo = null;
        string? partnerName = null;
        string? partnerCode = null;
        var lineCount = 0;
        string? sourceDeviceId = null;
        string? apiDocUid = null;

        if (reader.FieldCount > 6 && !reader.IsDBNull(6))
        {
            partnerId = reader.GetInt64(6);
        }

        if (reader.FieldCount > 7 && !reader.IsDBNull(7))
        {
            orderId = reader.GetInt64(7);
        }

        if (reader.FieldCount > 8 && !reader.IsDBNull(8))
        {
            orderRef = reader.GetString(8);
        }

        if (reader.FieldCount > 9 && !reader.IsDBNull(9))
        {
            shippingRef = reader.GetString(9);
        }

        if (reader.FieldCount > 10 && !reader.IsDBNull(10))
        {
            reasonCode = reader.GetString(10);
        }

        if (reader.FieldCount > 11 && !reader.IsDBNull(11))
        {
            comment = reader.GetString(11);
        }

        if (reader.FieldCount > 12 && !reader.IsDBNull(12))
        {
            partnerName = reader.GetString(12);
        }

        if (reader.FieldCount > 13 && !reader.IsDBNull(13))
        {
            partnerCode = reader.GetString(13);
        }

        if (reader.FieldCount > 14 && !reader.IsDBNull(14))
        {
            lineCount = Convert.ToInt32(reader.GetInt64(14));
        }

        if (reader.FieldCount > 15 && !reader.IsDBNull(15))
        {
            sourceDeviceId = reader.GetString(15);
        }

        if (reader.FieldCount > 16 && !reader.IsDBNull(16))
        {
            apiDocUid = reader.GetString(16);
        }

        if (reader.FieldCount > 17 && !reader.IsDBNull(17))
        {
            productionBatchNo = reader.GetString(17);
        }

        return new Doc
        {
            Id = reader.GetInt64(0),
            DocRef = reader.GetString(1),
            Type = type,
            Status = status,
            CreatedAt = FromDbDate(reader.GetString(4)) ?? DateTime.MinValue,
            ClosedAt = reader.IsDBNull(5) ? null : FromDbDate(reader.GetString(5)),
            PartnerId = partnerId,
            OrderId = orderId,
            OrderRef = orderRef,
            ShippingRef = shippingRef,
            ReasonCode = reasonCode,
            Comment = comment,
            ProductionBatchNo = productionBatchNo,
            PartnerName = partnerName,
            PartnerCode = partnerCode,
            LineCount = lineCount,
            SourceDeviceId = sourceDeviceId,
            ApiDocUid = apiDocUid
        };
    }

    private static DocLine ReadDocLine(NpgsqlDataReader reader)
    {
        return new DocLine
        {
            Id = reader.GetInt64(0),
            DocId = reader.GetInt64(1),
            ReplacesLineId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
            OrderLineId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
            ProductionPurpose = ProductionLinePurposeMapper.FromDbValue(reader.FieldCount > 4 && !reader.IsDBNull(4) ? reader.GetString(4) : null, reader.IsDBNull(3) ? null : reader.GetInt64(3)),
            ItemId = reader.GetInt64(5),
            Qty = reader.GetDouble(6),
            QtyInput = reader.IsDBNull(7) ? null : reader.GetDouble(7),
            UomCode = reader.IsDBNull(8) ? null : reader.GetString(8),
            FromLocationId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
            ToLocationId = reader.IsDBNull(10) ? null : reader.GetInt64(10),
            FromHu = reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : null,
            ToHu = reader.FieldCount > 12 && !reader.IsDBNull(12) ? reader.GetString(12) : null,
            PackSingleHu = reader.FieldCount > 13 && !reader.IsDBNull(13) && reader.GetBoolean(13)
        };
    }

    private static void AddOrderSelectParameters(NpgsqlCommand command)
    {
        command.Parameters.AddWithValue("@marking_code_status_reserved", MarkingCodeStatus.Reserved);
        command.Parameters.AddWithValue("@marking_code_status_printed", MarkingCodeStatus.Printed);
        command.Parameters.AddWithValue("@marking_code_status_voided", MarkingCodeStatus.Voided);
        command.Parameters.AddWithValue("@marking_status_cancelled", MarkingOrderStatus.Cancelled);
        command.Parameters.AddWithValue("@marking_status_failed", MarkingOrderStatus.Failed);
        command.Parameters.AddWithValue("@production_need_source_type", MarkingNeedCreationService.ProductionNeedSourceType);
        command.Parameters.AddWithValue("@production_order_source_type", MarkingNeedCreationService.ProductionOrderSourceType);
    }

    private static string BuildOrderSelectSql(string orderScopeSql)
    {
        return OrderSelectBase.Replace("{ORDER_SCOPE}", orderScopeSql, StringComparison.Ordinal);
    }

    private IReadOnlyList<ProductionPallet> GetProductionPalletsByDoc(NpgsqlConnection connection, long docId)
    {
        using var command = CreateCommand(connection, $@"
{ProductionPalletSelectSql}
WHERE p.prd_doc_id = @doc_id
ORDER BY p.id;
");
        command.Parameters.AddWithValue("@doc_id", docId);
        using var reader = command.ExecuteReader();
        var pallets = new List<ProductionPallet>();
        while (reader.Read())
        {
            pallets.Add(ReadProductionPallet(reader));
        }

        reader.Close();
        AttachProductionPalletLines(connection, pallets);
        return pallets;
    }

    private void AttachProductionPalletLines(NpgsqlConnection connection, IList<ProductionPallet> pallets)
    {
        if (pallets.Count == 0)
        {
            return;
        }

        using var command = CreateCommand(connection, @"
SELECT pll.id,
       pll.production_pallet_id,
       pll.doc_line_id,
       pll.order_line_id,
       pll.item_id,
       i.name,
       i.brand,
       i.base_uom,
       pll.planned_qty,
       pll.filled_qty,
       pll.created_at
FROM production_pallet_lines pll
INNER JOIN items i ON i.id = pll.item_id
WHERE pll.production_pallet_id = ANY(@ids)
ORDER BY pll.production_pallet_id, pll.id;
");
        command.Parameters.AddWithValue("@ids", pallets.Select(pallet => pallet.Id).ToArray());
        using var reader = command.ExecuteReader();
        var byPallet = new Dictionary<long, List<ProductionPalletComponentLine>>();
        while (reader.Read())
        {
            var line = new ProductionPalletComponentLine
            {
                Id = reader.GetInt64(0),
                ProductionPalletId = reader.GetInt64(1),
                DocLineId = reader.GetInt64(2),
                OrderLineId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                ItemId = reader.GetInt64(4),
                ItemName = reader.GetString(5),
                Brand = reader.IsDBNull(6) ? null : reader.GetString(6),
                Uom = string.IsNullOrWhiteSpace(reader.IsDBNull(7) ? null : reader.GetString(7)) ? "шт" : reader.GetString(7),
                PlannedQty = reader.GetDouble(8),
                FilledQty = reader.GetDouble(9),
                CreatedAt = FromDbDate(reader.GetString(10)) ?? DateTime.MinValue
            };
            if (!byPallet.TryGetValue(line.ProductionPalletId, out var lines))
            {
                lines = new List<ProductionPalletComponentLine>();
                byPallet[line.ProductionPalletId] = lines;
            }

            lines.Add(line);
        }

        for (var index = 0; index < pallets.Count; index++)
        {
            var pallet = pallets[index];
            if (!byPallet.TryGetValue(pallet.Id, out var lines) || lines.Count == 0)
            {
                lines = new List<ProductionPalletComponentLine>
                {
                    new()
                    {
                        ProductionPalletId = pallet.Id,
                        DocLineId = pallet.DocLineId,
                        OrderLineId = pallet.OrderLineId,
                        ItemId = pallet.ItemId,
                        ItemName = pallet.ItemName,
                        PlannedQty = pallet.PlannedQty,
                        FilledQty = string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase) ? pallet.PlannedQty : 0,
                        CreatedAt = pallet.CreatedAt
                    }
                };
            }

            pallets[index] = new ProductionPallet
            {
                Id = pallet.Id,
                PrdDocId = pallet.PrdDocId,
                DocLineId = pallet.DocLineId,
                OrderId = pallet.OrderId,
                OrderLineId = pallet.OrderLineId,
                ItemId = pallet.ItemId,
                ItemName = pallet.ItemName,
                HuCode = pallet.HuCode,
                PlannedQty = lines.Sum(line => line.PlannedQty),
                ToLocationId = pallet.ToLocationId,
                ToLocationCode = pallet.ToLocationCode,
                Status = pallet.Status,
                PalletNo = pallet.PalletNo,
                PalletCount = pallet.PalletCount,
                PrintedAt = pallet.PrintedAt,
                FilledAt = pallet.FilledAt,
                FilledByDeviceId = pallet.FilledByDeviceId,
                CreatedAt = pallet.CreatedAt,
                Lines = lines
            };
        }
    }

    private static ProductionPallet ReadProductionPallet(NpgsqlDataReader reader)
    {
        return new ProductionPallet
        {
            Id = reader.GetInt64(0),
            PrdDocId = reader.GetInt64(1),
            DocLineId = reader.GetInt64(2),
            OrderId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
            OrderLineId = reader.IsDBNull(4) ? null : reader.GetInt64(4),
            ItemId = reader.GetInt64(5),
            ItemName = reader.GetString(6),
            HuCode = reader.GetString(7),
            PlannedQty = reader.GetDouble(8),
            ToLocationId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
            ToLocationCode = reader.IsDBNull(10) ? null : reader.GetString(10),
            Status = reader.GetString(11),
            PalletNo = reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
            PalletCount = reader.IsDBNull(13) ? 0 : reader.GetInt32(13),
            PrintedAt = FromDbDate(reader.IsDBNull(14) ? null : reader.GetString(14)),
            FilledAt = FromDbDate(reader.IsDBNull(15) ? null : reader.GetString(15)),
            FilledByDeviceId = reader.IsDBNull(16) ? null : reader.GetString(16),
            CreatedAt = FromDbDate(reader.GetString(17)) ?? DateTime.MinValue
        };
    }

    private static Order ReadOrder(NpgsqlDataReader reader)
    {
        var type = OrderStatusMapper.TypeFromString(reader.IsDBNull(2) ? null : reader.GetString(2)) ?? OrderType.Customer;
        var status = OrderStatusMapper.StatusFromString(reader.GetString(5)) ?? OrderStatus.Accepted;

        var dueDate = reader.IsDBNull(4) ? null : FromDbDate(reader.GetString(4));
        var comment = reader.IsDBNull(6) ? null : reader.GetString(6);
        var partnerName = reader.IsDBNull(8) ? null : reader.GetString(8);
        var partnerCode = reader.IsDBNull(9) ? null : reader.GetString(9);
        var useReservedStock = reader.FieldCount > 10 && !reader.IsDBNull(10) && reader.GetBoolean(10);
        var rawMarkingStatus = reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : null;
        var markingStatus = MarkingStatusMapper.FromString(rawMarkingStatus);
        var markingExcelGeneratedAt = reader.FieldCount > 12 ? FromDbDate(reader.IsDBNull(12) ? null : reader.GetString(12)) : null;
        var markingPrintedAt = reader.FieldCount > 13 ? FromDbDate(reader.IsDBNull(13) ? null : reader.GetString(13)) : null;
        var markingRequired = reader.FieldCount > 14 && !reader.IsDBNull(14) && reader.GetBoolean(14);
        var markingApplies = reader.FieldCount > 15 && !reader.IsDBNull(15) && reader.GetBoolean(15);
        var markingCodeCovered = reader.FieldCount > 16 && !reader.IsDBNull(16) && reader.GetBoolean(16);
        var shippedAt = reader.FieldCount > 17 ? FromDbDate(reader.IsDBNull(17) ? null : reader.GetString(17)) : null;
        var listMetricsLoaded = reader.FieldCount > 24;
        var hasShipmentRemaining = listMetricsLoaded && !reader.IsDBNull(18) && reader.GetBoolean(18);
        var hasReceiptRemaining = listMetricsLoaded && !reader.IsDBNull(19) && reader.GetBoolean(19);
        var hasProductionPalletPlan = listMetricsLoaded && !reader.IsDBNull(20) && reader.GetBoolean(20);
        var plannedPalletCount = listMetricsLoaded && !reader.IsDBNull(21) ? reader.GetInt32(21) : 0;
        var filledPalletCount = listMetricsLoaded && !reader.IsDBNull(22) ? reader.GetInt32(22) : 0;
        var plannedQty = listMetricsLoaded && !reader.IsDBNull(23) ? reader.GetDouble(23) : 0d;
        var filledQty = listMetricsLoaded && !reader.IsDBNull(24) ? reader.GetDouble(24) : 0d;

        return new Order
        {
            Id = reader.GetInt64(0),
            OrderRef = reader.GetString(1),
            Type = type,
            PartnerId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
            DueDate = dueDate,
            Status = status,
            Comment = comment,
            CreatedAt = FromDbDate(reader.GetString(7)) ?? DateTime.MinValue,
            ShippedAt = shippedAt,
            PartnerName = partnerName,
            PartnerCode = partnerCode,
            UseReservedStock = useReservedStock,
            MarkingStatus = markingStatus,
            IsLegacyExcelGeneratedMarkingStatus = string.Equals(rawMarkingStatus, "EXCEL_GENERATED", StringComparison.OrdinalIgnoreCase),
            MarkingRequired = markingRequired,
            MarkingApplies = markingApplies,
            MarkingCodeCovered = markingCodeCovered,
            MarkingExcelGeneratedAt = markingExcelGeneratedAt,
            MarkingPrintedAt = markingPrintedAt,
            ListMetricsLoaded = listMetricsLoaded,
            HasShipmentRemaining = hasShipmentRemaining,
            HasProductionPalletPlan = hasProductionPalletPlan,
            NeedsProductionPalletPlan = status is not (OrderStatus.Shipped or OrderStatus.Cancelled) && hasReceiptRemaining,
            PlannedPalletCount = plannedPalletCount,
            FilledPalletCount = filledPalletCount,
            PlannedQty = plannedQty,
            FilledQty = filledQty
        };
    }

    private static OrderLine ReadOrderLine(NpgsqlDataReader reader)
    {
        return new OrderLine
        {
            Id = reader.GetInt64(0),
            OrderId = reader.GetInt64(1),
            ItemId = reader.GetInt64(2),
            QtyOrdered = reader.GetDouble(3),
            ProductionPurpose = ProductionLinePurposeMapper.FromDbValue(reader.IsDBNull(4) ? null : reader.GetString(4)),
            ProductionPalletGroup = reader.FieldCount > 5 && !reader.IsDBNull(5) ? reader.GetString(5) : null
        };
    }

    private static ImportError ReadImportError(NpgsqlDataReader reader)
    {
        return new ImportError
        {
            Id = reader.GetInt64(0),
            EventId = reader.IsDBNull(1) ? null : reader.GetString(1),
            Reason = reader.GetString(2),
            RawJson = reader.GetString(3),
            CreatedAt = FromDbDate(reader.GetString(4)) ?? DateTime.MinValue
        };
    }

    private static HuRecord ReadHuRecord(NpgsqlDataReader reader)
    {
        return new HuRecord
        {
            Id = reader.GetInt64(0),
            Code = reader.GetString(1),
            Status = reader.GetString(2),
            CreatedAt = FromDbDate(reader.GetString(3)) ?? DateTime.MinValue,
            CreatedBy = reader.IsDBNull(4) ? null : reader.GetString(4),
            ClosedAt = FromDbDate(reader.IsDBNull(5) ? null : reader.GetString(5)),
            Note = reader.IsDBNull(6) ? null : reader.GetString(6)
        };
    }

    private static void EnsureColumn(NpgsqlConnection connection, string tableName, string columnName, string definition)
    {
        if (ColumnExists(connection, tableName, columnName))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
        command.ExecuteNonQuery();
    }

    private static void EnsureNullable(NpgsqlConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT is_nullable
FROM information_schema.columns
WHERE table_schema = current_schema()
  AND table_name = @table_name
  AND column_name = @column_name
LIMIT 1;";
        command.Parameters.AddWithValue("@table_name", tableName.ToLowerInvariant());
        command.Parameters.AddWithValue("@column_name", columnName.ToLowerInvariant());
        var isNullable = command.ExecuteScalar() as string;
        if (string.Equals(isNullable, "YES", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ALTER COLUMN {columnName} DROP NOT NULL;";
        alter.ExecuteNonQuery();
    }

    private static bool ColumnExists(NpgsqlConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT 1
FROM information_schema.columns
WHERE table_schema = current_schema()
  AND table_name = @table_name
  AND column_name = @column_name
LIMIT 1;";
        command.Parameters.AddWithValue("@table_name", tableName.ToLowerInvariant());
        command.Parameters.AddWithValue("@column_name", columnName.ToLowerInvariant());
        return command.ExecuteScalar() != null;
    }

    private static bool TableExists(NpgsqlConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT 1
FROM information_schema.tables
WHERE table_schema = current_schema()
  AND table_name = @name
LIMIT 1;";
        command.Parameters.AddWithValue("@name", tableName.ToLowerInvariant());
        return command.ExecuteScalar() != null;
    }

    private static void EnsureSchemaReady(NpgsqlConnection connection)
    {
        if (!TableExists(connection, "schema_migrations"))
        {
            throw new InvalidOperationException("Database schema is not initialized. Run deploy/scripts/migrate.sh before starting FlowStock.");
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
            var appliedCount = Convert.ToInt32(command.ExecuteScalar() ?? 0);
            if (appliedCount <= 0)
            {
                throw new InvalidOperationException("Database schema has no applied migrations. Run deploy/scripts/migrate.sh before starting FlowStock.");
            }
        }

        var requiredTables = new[]
        {
            "items",
            "item_types",
            "write_off_reasons",
            "orders",
            "order_lines",
            "order_receipt_plan_lines",
            "docs",
            "doc_lines",
            "production_pallets",
            "production_pallet_lines",
            "ledger",
            "tsd_devices",
            "marking_order",
            "client_blocks"
        };

        foreach (var table in requiredTables)
        {
            if (!TableExists(connection, table))
            {
                throw new InvalidOperationException($"Database schema is incomplete. Missing table '{table}'. Run deploy/scripts/migrate.sh.");
            }
        }

        EnsureColumn(connection, "item_types", "min_stock_uses_order_binding", "BOOLEAN NOT NULL DEFAULT FALSE");
        EnsureColumn(connection, "item_types", "enable_order_reservation", "BOOLEAN NOT NULL DEFAULT FALSE");
        EnsureColumn(connection, "item_types", "enable_marking", "BOOLEAN NOT NULL DEFAULT FALSE");
        EnsureColumn(connection, "orders", "marking_status", "TEXT NOT NULL DEFAULT 'NOT_REQUIRED'");
        EnsureColumn(connection, "orders", "marking_excel_generated_at", "TEXT NULL");
        EnsureColumn(connection, "orders", "marking_printed_at", "TEXT NULL");
        EnsureColumn(connection, "order_lines", "production_purpose", "TEXT NOT NULL DEFAULT 'INTERNAL_STOCK'");
        EnsureColumn(connection, "order_lines", "production_pallet_group", "TEXT NULL");
        EnsureColumn(connection, "doc_lines", "production_purpose", "TEXT NOT NULL DEFAULT 'INTERNAL_STOCK'");
        EnsureColumn(connection, "production_pallets", "pallet_no", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "production_pallets", "pallet_count", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "production_pallets", "printed_at", "TEXT NULL");
        EnsureNullable(connection, "marking_order", "order_id");
        EnsureColumn(connection, "marking_order", "source_type", "TEXT NULL");
        EnsureColumn(connection, "marking_order", "source_order_id", "BIGINT NULL");
        EnsureColumn(connection, "marking_code", "receipt_doc_id", "BIGINT NULL");
        EnsureColumn(connection, "marking_code", "receipt_line_id", "BIGINT NULL");

        if (!ColumnExists(connection, "orders", "order_type")
            || !ColumnExists(connection, "orders", "bind_reserved_stock")
            || !ColumnExists(connection, "doc_lines", "replaces_line_id")
            || !ColumnExists(connection, "doc_lines", "pack_single_hu")
            || !ColumnExists(connection, "ledger", "hu_code")
            || !ColumnExists(connection, "client_blocks", "is_enabled")
            || !ColumnExists(connection, "items", "item_type_id")
            || !ColumnExists(connection, "items", "min_stock_qty")
            || !ColumnExists(connection, "locations", "auto_hu_distribution_enabled")
            || !ColumnExists(connection, "item_types", "is_visible_in_product_catalog")
            || !ColumnExists(connection, "item_types", "enable_min_stock_control")
            || !ColumnExists(connection, "item_types", "min_stock_uses_order_binding")
            || !ColumnExists(connection, "item_types", "enable_order_reservation")
            || !ColumnExists(connection, "item_types", "enable_hu_distribution")
            || !ColumnExists(connection, "item_types", "enable_marking")
            || !ColumnExists(connection, "orders", "marking_status")
            || !ColumnExists(connection, "orders", "marking_excel_generated_at")
            || !ColumnExists(connection, "orders", "marking_printed_at")
            || !ColumnExists(connection, "order_lines", "production_purpose")
            || !ColumnExists(connection, "order_lines", "production_pallet_group")
            || !ColumnExists(connection, "doc_lines", "production_purpose"))
        {
            throw new InvalidOperationException("Database schema is outdated. Run deploy/scripts/migrate.sh before starting FlowStock.");
        }
    }

    private static void EnsureIndex(NpgsqlConnection connection, string indexName, string indexDefinition)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE INDEX IF NOT EXISTS {indexName} ON {indexDefinition};";
        command.ExecuteNonQuery();
    }

    private static void BackfillPartnerCreatedAt(NpgsqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE partners SET created_at = @created_at WHERE created_at IS NULL OR created_at = '';";
        command.Parameters.AddWithValue("@created_at", ToDbDate(DateTime.Now));
        command.ExecuteNonQuery();
    }

    private static void BackfillOrderTypes(NpgsqlConnection connection)
    {
        if (!ColumnExists(connection, "orders", "order_type"))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE orders SET order_type = 'CUSTOMER' WHERE order_type IS NULL OR order_type = '';";
        command.ExecuteNonQuery();
    }

    private static void BackfillBaseUom(NpgsqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE items SET base_uom = COALESCE(NULLIF(base_uom, ''), NULLIF(uom, ''), 'шт') WHERE base_uom IS NULL OR base_uom = '';";
        command.ExecuteNonQuery();
    }

    private static void BackfillLedgerHuCode(NpgsqlConnection connection)
    {
        if (!ColumnExists(connection, "ledger", "hu_code"))
        {
            return;
        }

        if (!ColumnExists(connection, "ledger", "hu"))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ledger SET hu_code = hu WHERE (hu_code IS NULL OR hu_code = '') AND hu IS NOT NULL AND hu <> '';";
        command.ExecuteNonQuery();
    }

    private static void BackfillHuRegistry(NpgsqlConnection connection)
    {
        if (!TableExists(connection, "hus"))
        {
            return;
        }

        var sources = new List<string>();
        if (ColumnExists(connection, "ledger", "hu_code"))
        {
            sources.Add("SELECT hu_code AS hu_code FROM ledger WHERE hu_code IS NOT NULL AND hu_code <> ''");
        }

        if (ColumnExists(connection, "doc_lines", "from_hu"))
        {
            sources.Add("SELECT from_hu AS hu_code FROM doc_lines WHERE from_hu IS NOT NULL AND from_hu <> ''");
        }

        if (ColumnExists(connection, "doc_lines", "to_hu"))
        {
            sources.Add("SELECT to_hu AS hu_code FROM doc_lines WHERE to_hu IS NOT NULL AND to_hu <> ''");
        }

        if (sources.Count == 0)
        {
            return;
        }

        var sql = $@"
INSERT INTO hus(hu_code, status, created_at, created_by)
SELECT DISTINCT hu_code, 'OPEN', @created_at, 'backfill'
FROM (
{string.Join("\nUNION ALL\n", sources)}
)
ON CONFLICT (hu_code) DO NOTHING;";
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@created_at", ToDbDate(DateTime.Now));
        command.ExecuteNonQuery();
    }

    private static void BackfillMarkedItemsFromKmCodes(NpgsqlConnection connection)
    {
        if (!TableExists(connection, "km_code"))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE items i
SET is_marked = 1
WHERE COALESCE(i.is_marked, 0) = 0
  AND EXISTS (
      SELECT 1
      FROM km_code c
      WHERE c.sku_id = i.id
         OR (
            c.sku_id IS NULL
            AND c.gtin14 IS NOT NULL
            AND i.gtin IS NOT NULL
            AND (
                c.gtin14 = i.gtin
                OR (LENGTH(i.gtin) = 13 AND c.gtin14 = '0' || i.gtin)
            )
         )
  );";
        command.ExecuteNonQuery();
    }

    private static string BuildItemsQuery(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return "SELECT i.id, i.name, i.is_active, i.barcode, i.gtin, i.base_uom, i.default_packaging_id, i.brand, i.volume, i.shelf_life_months, i.max_qty_per_hu, i.tara_id, i.is_marked, t.name, i.item_type_id, it.name, it.is_visible_in_product_catalog, it.enable_min_stock_control, COALESCE(it.enable_marking, FALSE), i.min_stock_qty FROM items i LEFT JOIN taras t ON t.id = i.tara_id LEFT JOIN item_types it ON it.id = i.item_type_id ORDER BY i.name";
        }

        return "SELECT i.id, i.name, i.is_active, i.barcode, i.gtin, i.base_uom, i.default_packaging_id, i.brand, i.volume, i.shelf_life_months, i.max_qty_per_hu, i.tara_id, i.is_marked, t.name, i.item_type_id, it.name, it.is_visible_in_product_catalog, it.enable_min_stock_control, COALESCE(it.enable_marking, FALSE), i.min_stock_qty FROM items i LEFT JOIN taras t ON t.id = i.tara_id LEFT JOIN item_types it ON it.id = i.item_type_id WHERE i.name ILIKE @search OR i.barcode ILIKE @search OR i.gtin ILIKE @search ORDER BY i.name";
    }

    private static string BuildStockQuery(string? search)
    {
        var baseQuery = @"
SELECT i.id,
       i.name,
       i.barcode,
       l.code,
       COALESCE(led.hu_code, led.hu),
       SUM(led.qty_delta) AS qty,
       i.base_uom,
       i.item_type_id,
       it.name,
       COALESCE(it.enable_min_stock_control, FALSE),
       COALESCE(it.min_stock_uses_order_binding, FALSE),
       COALESCE(it.enable_order_reservation, FALSE),
       i.min_stock_qty
FROM ledger led
INNER JOIN items i ON i.id = led.item_id
INNER JOIN locations l ON l.id = led.location_id
LEFT JOIN item_types it ON it.id = i.item_type_id
";

        if (!string.IsNullOrWhiteSpace(search))
        {
            baseQuery += "WHERE i.name ILIKE @search OR i.barcode ILIKE @search OR l.code ILIKE @search\n";
        }

        baseQuery += "GROUP BY i.id, i.name, i.barcode, i.base_uom, i.item_type_id, it.name, it.enable_min_stock_control, it.min_stock_uses_order_binding, it.enable_order_reservation, i.min_stock_qty, l.id, COALESCE(led.hu_code, led.hu) HAVING SUM(led.qty_delta) != 0 ORDER BY i.name, l.code, COALESCE(led.hu_code, led.hu)";
        return baseQuery;
    }

    public long AddItemRequest(ItemRequest request)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"INSERT INTO item_requests(barcode, comment, device_id, login, status, created_at, resolved_at)
VALUES(@barcode, @comment, @device_id, @login, @status, @created_at, @resolved_at)
RETURNING id;");
            command.Parameters.AddWithValue("@barcode", request.Barcode);
            command.Parameters.AddWithValue("@comment", request.Comment);
            command.Parameters.AddWithValue("@device_id", string.IsNullOrWhiteSpace(request.DeviceId) ? DBNull.Value : request.DeviceId.Trim());
            command.Parameters.AddWithValue("@login", string.IsNullOrWhiteSpace(request.Login) ? DBNull.Value : request.Login.Trim());
            command.Parameters.AddWithValue("@status", string.IsNullOrWhiteSpace(request.Status) ? "NEW" : request.Status.Trim());
            command.Parameters.AddWithValue("@created_at", ToDbDate(request.CreatedAt));
            command.Parameters.AddWithValue("@resolved_at", request.ResolvedAt.HasValue ? ToDbDate(request.ResolvedAt.Value) : DBNull.Value);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public IReadOnlyList<ItemRequest> GetItemRequests(bool includeResolved)
    {
        return WithConnection(connection =>
        {
            var sql = "SELECT id, barcode, comment, device_id, login, created_at, status, resolved_at FROM item_requests";
            if (!includeResolved)
            {
                sql += " WHERE status <> 'RESOLVED'";
            }
            sql += " ORDER BY created_at DESC";
            using var command = CreateCommand(connection, sql);
            using var reader = command.ExecuteReader();
            var list = new List<ItemRequest>();
            while (reader.Read())
            {
                list.Add(new ItemRequest
                {
                    Id = reader.GetInt64(0),
                    Barcode = reader.GetString(1),
                    Comment = reader.GetString(2),
                    DeviceId = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Login = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CreatedAt = FromDbDate(reader.IsDBNull(5) ? null : reader.GetString(5)) ?? DateTime.MinValue,
                    Status = reader.IsDBNull(6) ? "NEW" : reader.GetString(6),
                    ResolvedAt = reader.IsDBNull(7) ? null : FromDbDate(reader.GetString(7))
                });
            }

            return list;
        });
    }

    public void MarkItemRequestResolved(long requestId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE item_requests SET status = 'RESOLVED', resolved_at = @resolved_at WHERE id = @id");
            command.Parameters.AddWithValue("@resolved_at", ToDbDate(DateTime.Now));
            command.Parameters.AddWithValue("@id", requestId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public long AddKmCodeBatch(KmCodeBatch batch)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO km_code_batch(order_id, file_name, file_hash, imported_at, imported_by, total_codes, error_count)
VALUES(@order_id, @file_name, @file_hash, @imported_at, @imported_by, @total_codes, @error_count)
RETURNING id;");
            command.Parameters.AddWithValue("@order_id", batch.OrderId.HasValue ? batch.OrderId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@file_name", string.IsNullOrWhiteSpace(batch.FileName) ? DBNull.Value : batch.FileName.Trim());
            command.Parameters.AddWithValue("@file_hash", batch.FileHash);
            command.Parameters.AddWithValue("@imported_at", ToDbDate(batch.ImportedAt));
            command.Parameters.AddWithValue("@imported_by", string.IsNullOrWhiteSpace(batch.ImportedBy) ? DBNull.Value : batch.ImportedBy.Trim());
            command.Parameters.AddWithValue("@total_codes", batch.TotalCodes);
            command.Parameters.AddWithValue("@error_count", batch.ErrorCount);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdateKmCodeBatchStats(long batchId, int totalCodes, int errorCount)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE km_code_batch SET total_codes = @total_codes, error_count = @error_count WHERE id = @id");
            command.Parameters.AddWithValue("@total_codes", totalCodes);
            command.Parameters.AddWithValue("@error_count", errorCount);
            command.Parameters.AddWithValue("@id", batchId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void UpdateKmCodeBatchOrder(long batchId, long? orderId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE km_code_batch SET order_id = @order_id WHERE id = @id");
            command.Parameters.AddWithValue("@order_id", orderId.HasValue ? orderId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@id", batchId);
            command.ExecuteNonQuery();

            using var codesCommand = CreateCommand(connection, @"
UPDATE km_code
SET order_id = @order_id
WHERE batch_id = @batch_id AND status = @status;");
            codesCommand.Parameters.AddWithValue("@order_id", orderId.HasValue ? orderId.Value : DBNull.Value);
            codesCommand.Parameters.AddWithValue("@batch_id", batchId);
            codesCommand.Parameters.AddWithValue("@status", (short)KmCodeStatusMapper.ToInt(KmCodeStatus.InPool));
            codesCommand.ExecuteNonQuery();
            return 0;
        });
    }

    public KmCodeBatch? GetKmCodeBatch(long batchId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildKmBatchQuery("WHERE b.id = @id"));
            command.Parameters.AddWithValue("@id", batchId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadKmCodeBatch(reader) : null;
        });
    }

    public KmCodeBatch? FindKmCodeBatchByHash(string fileHash)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildKmBatchQuery("WHERE b.file_hash = @hash"));
            command.Parameters.AddWithValue("@hash", fileHash);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadKmCodeBatch(reader) : null;
        });
    }

    public IReadOnlyList<KmCodeBatch> GetKmCodeBatches()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildKmBatchQuery(string.Empty) + " ORDER BY b.imported_at DESC, b.id DESC");
            using var reader = command.ExecuteReader();
            var list = new List<KmCodeBatch>();
            while (reader.Read())
            {
                list.Add(ReadKmCodeBatch(reader));
            }

            return list;
        });
    }

    public long AddKmCode(KmCode code)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO km_code(batch_id, code_raw, gtin14, sku_id, product_name, status, receipt_doc_id, receipt_line_id, hu_id, location_id, ship_doc_id, ship_line_id, order_id)
VALUES(@batch_id, @code_raw, @gtin14, @sku_id, @product_name, @status, @receipt_doc_id, @receipt_line_id, @hu_id, @location_id, @ship_doc_id, @ship_line_id, @order_id)
RETURNING id;");
            command.Parameters.AddWithValue("@batch_id", code.BatchId);
            command.Parameters.AddWithValue("@code_raw", code.CodeRaw);
            command.Parameters.AddWithValue("@gtin14", string.IsNullOrWhiteSpace(code.Gtin14) ? DBNull.Value : code.Gtin14.Trim());
            command.Parameters.AddWithValue("@sku_id", code.SkuId.HasValue ? code.SkuId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@product_name", string.IsNullOrWhiteSpace(code.ProductName) ? DBNull.Value : code.ProductName.Trim());
            command.Parameters.AddWithValue("@status", (short)KmCodeStatusMapper.ToInt(code.Status));
            command.Parameters.AddWithValue("@receipt_doc_id", code.ReceiptDocId.HasValue ? code.ReceiptDocId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@receipt_line_id", code.ReceiptLineId.HasValue ? code.ReceiptLineId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@hu_id", code.HuId.HasValue ? code.HuId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@location_id", code.LocationId.HasValue ? code.LocationId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@ship_doc_id", code.ShipDocId.HasValue ? code.ShipDocId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@ship_line_id", code.ShipLineId.HasValue ? code.ShipLineId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@order_id", code.OrderId.HasValue ? code.OrderId.Value : DBNull.Value);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public KmCode? FindKmCodeByRaw(string codeRaw)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildKmCodeQuery("WHERE c.code_raw = @code_raw"));
            command.Parameters.AddWithValue("@code_raw", codeRaw);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadKmCode(reader) : null;
        });
    }

    public bool ExistsKmCodeByRawIgnoreCase(string codeRaw)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT 1 FROM km_code WHERE LOWER(code_raw) = LOWER(@code_raw) LIMIT 1");
            command.Parameters.AddWithValue("@code_raw", codeRaw);
            var result = command.ExecuteScalar();
            return result != null && result != DBNull.Value;
        });
    }

    public IReadOnlyList<KmCode> GetKmCodesByBatch(long batchId, string? search, KmCodeStatus? status, int take)
    {
        return WithConnection(connection =>
        {
            var sql = BuildKmCodeQuery("WHERE c.batch_id = @batch_id");
            if (status.HasValue)
            {
                sql += " AND c.status = @status";
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                sql += " AND c.code_raw ILIKE @search";
            }
            sql += " ORDER BY c.id LIMIT @take";
            using var command = CreateCommand(connection, sql);
            command.Parameters.AddWithValue("@batch_id", batchId);
            command.Parameters.AddWithValue("@take", take);
            if (status.HasValue)
            {
                command.Parameters.AddWithValue("@status", (short)KmCodeStatusMapper.ToInt(status.Value));
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                command.Parameters.AddWithValue("@search", $"%{search.Trim()}%");
            }
            using var reader = command.ExecuteReader();
            var list = new List<KmCode>();
            while (reader.Read())
            {
                list.Add(ReadKmCode(reader));
            }

            return list;
        });
    }

    public IReadOnlyList<KmCode> GetKmCodesByReceiptLine(long receiptLineId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildKmCodeQuery("WHERE c.receipt_line_id = @line_id ORDER BY c.id"));
            command.Parameters.AddWithValue("@line_id", receiptLineId);
            using var reader = command.ExecuteReader();
            var list = new List<KmCode>();
            while (reader.Read())
            {
                list.Add(ReadKmCode(reader));
            }

            return list;
        });
    }

    public IReadOnlyList<KmCode> GetKmCodesByShipmentLine(long shipLineId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildKmCodeQuery("WHERE c.ship_line_id = @line_id ORDER BY c.id"));
            command.Parameters.AddWithValue("@line_id", shipLineId);
            using var reader = command.ExecuteReader();
            var list = new List<KmCode>();
            while (reader.Read())
            {
                list.Add(ReadKmCode(reader));
            }

            return list;
        });
    }

    public int CountKmCodesByBatch(long batchId, KmCodeStatus? status)
    {
        return WithConnection(connection =>
        {
            var sql = "SELECT COUNT(*) FROM km_code WHERE batch_id = @batch_id";
            if (status.HasValue)
            {
                sql += " AND status = @status";
            }
            using var command = CreateCommand(connection, sql);
            command.Parameters.AddWithValue("@batch_id", batchId);
            if (status.HasValue)
            {
                command.Parameters.AddWithValue("@status", (short)KmCodeStatusMapper.ToInt(status.Value));
            }
            return Convert.ToInt32(command.ExecuteScalar() ?? 0L);
        });
    }

    public int CountKmCodesWithoutSku(long batchId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT COUNT(*) FROM km_code WHERE batch_id = @batch_id AND sku_id IS NULL");
            command.Parameters.AddWithValue("@batch_id", batchId);
            return Convert.ToInt32(command.ExecuteScalar() ?? 0L);
        });
    }

    public int CountKmCodesByReceiptLine(long receiptLineId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT COUNT(*) FROM km_code WHERE receipt_line_id = @line_id AND status = @status");
            command.Parameters.AddWithValue("@line_id", receiptLineId);
            command.Parameters.AddWithValue("@status", (short)KmCodeStatusMapper.ToInt(KmCodeStatus.OnHand));
            return Convert.ToInt32(command.ExecuteScalar() ?? 0L);
        });
    }

    public int CountKmCodesByShipmentLine(long shipLineId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT COUNT(*) FROM km_code WHERE ship_line_id = @line_id AND status = @status");
            command.Parameters.AddWithValue("@line_id", shipLineId);
            command.Parameters.AddWithValue("@status", (short)KmCodeStatusMapper.ToInt(KmCodeStatus.Shipped));
            return Convert.ToInt32(command.ExecuteScalar() ?? 0L);
        });
    }

    public int CountProductionMarkingCodesByReceiptLine(long receiptLineId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT COUNT(*)
FROM marking_code
WHERE receipt_line_id = @line_id
  AND status <> @voided_status;");
            command.Parameters.AddWithValue("@line_id", receiptLineId);
            command.Parameters.AddWithValue("@voided_status", MarkingCodeStatus.Voided);
            return Convert.ToInt32(command.ExecuteScalar() ?? 0L);
        });
    }

    public int CountAvailableProductionMarkingCodesForReceipt(long? sourceOrderId, long itemId, string? gtin)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildAvailableProductionMarkingCodeSql("COUNT(*)", null));
            AddAvailableProductionMarkingCodeParameters(command, sourceOrderId, itemId, gtin, null);
            return Convert.ToInt32(command.ExecuteScalar() ?? 0L);
        });
    }

    public IReadOnlyList<Guid> GetAvailableProductionMarkingCodeIdsForReceipt(long? sourceOrderId, long itemId, string? gtin, int take)
    {
        if (take <= 0)
        {
            return Array.Empty<Guid>();
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildAvailableProductionMarkingCodeSql("c.id", @"
ORDER BY
  CASE
    WHEN @source_order_id::bigint IS NOT NULL AND mo.source_order_id = @source_order_id::bigint THEN 0
    WHEN @source_order_id::bigint IS NOT NULL AND mo.order_id = @source_order_id::bigint THEN 1
    WHEN mo.source_type = @production_need_source_type AND mo.source_order_id IS NULL THEN 2
    ELSE 3
  END,
  mo.created_at,
  c.source_row_number NULLS LAST,
  c.created_at,
  c.id
FOR UPDATE SKIP LOCKED
LIMIT @take"));
            AddAvailableProductionMarkingCodeParameters(command, sourceOrderId, itemId, gtin, take);
            using var reader = command.ExecuteReader();
            var list = new List<Guid>();
            while (reader.Read())
            {
                list.Add(reader.GetGuid(0));
            }

            return list;
        });
    }

    public int AssignProductionMarkingCodesToReceipt(IReadOnlyList<Guid> codeIds, long docId, long lineId, DateTime appliedAt)
    {
        if (codeIds.Count == 0)
        {
            return 0;
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE marking_code
SET status = @applied_status,
    receipt_doc_id = @doc_id,
    receipt_line_id = @line_id,
    applied_at = COALESCE(applied_at, @applied_at),
    updated_at = @applied_at
WHERE id = ANY(@ids::uuid[])
  AND receipt_doc_id IS NULL
  AND receipt_line_id IS NULL
  AND status IN (@reserved_status, @printed_status);");
            command.Parameters.AddWithValue("@applied_status", MarkingCodeStatus.Applied);
            command.Parameters.AddWithValue("@doc_id", docId);
            command.Parameters.AddWithValue("@line_id", lineId);
            command.Parameters.AddWithValue("@applied_at", ToDbDate(appliedAt));
            command.Parameters.AddWithValue("@ids", codeIds.ToArray());
            command.Parameters.AddWithValue("@reserved_status", MarkingCodeStatus.Reserved);
            command.Parameters.AddWithValue("@printed_status", MarkingCodeStatus.Printed);
            return command.ExecuteNonQuery();
        });
    }

    public IReadOnlyList<long> GetAvailableKmCodeIds(long? batchId, long? orderId, long skuId, string? gtin14, int take)
    {
        return WithConnection(connection =>
        {
            var sql = @"
SELECT c.id
FROM km_code c
WHERE c.status = @status
  AND (c.sku_id = @sku_id OR (c.sku_id IS NULL AND @gtin14::text IS NOT NULL AND c.gtin14 = @gtin14::text))
  AND (@batch_id::bigint IS NULL OR c.batch_id = @batch_id::bigint)
  AND (
    @order_id::bigint IS NULL
    OR c.order_id = @order_id::bigint
    OR EXISTS (
        SELECT 1
        FROM km_code_batch b
        WHERE b.id = c.batch_id AND b.order_id = @order_id::bigint
    )
  )
ORDER BY c.id
FOR UPDATE SKIP LOCKED
LIMIT @take;";
            using var command = CreateCommand(connection, sql);
            command.Parameters.AddWithValue("@status", (short)KmCodeStatusMapper.ToInt(KmCodeStatus.InPool));
            command.Parameters.AddWithValue("@sku_id", skuId);
            command.Parameters.AddWithValue("@gtin14", string.IsNullOrWhiteSpace(gtin14) ? DBNull.Value : gtin14.Trim());
            command.Parameters.AddWithValue("@batch_id", batchId.HasValue ? batchId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@order_id", orderId.HasValue ? orderId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@take", take);
            using var reader = command.ExecuteReader();
            var list = new List<long>();
            while (reader.Read())
            {
                list.Add(reader.GetInt64(0));
            }

            return list;
        });
    }

    private static string BuildAvailableProductionMarkingCodeSql(string selectExpression, string? suffix)
    {
        var sql = $@"
SELECT {selectExpression}
FROM marking_code c
INNER JOIN marking_order mo ON mo.id = c.marking_order_id
WHERE c.receipt_doc_id IS NULL
  AND c.receipt_line_id IS NULL
  AND c.status IN (@reserved_status, @printed_status)
  AND mo.status NOT IN (@marking_status_cancelled, @marking_status_failed)
  AND (
      mo.item_id = @item_id
      OR (@gtin::text IS NOT NULL AND NULLIF(BTRIM(mo.gtin), '') = @gtin::text)
      OR (@gtin::text IS NOT NULL AND NULLIF(BTRIM(c.gtin), '') = @gtin::text)
  )
  AND (
      (
          mo.source_type = @production_order_source_type
          AND @source_order_id::bigint IS NOT NULL
          AND mo.source_order_id = @source_order_id::bigint
      )
      OR (
          mo.source_type = @production_need_source_type
          AND (
              mo.source_order_id IS NULL
              OR (@source_order_id::bigint IS NOT NULL AND mo.source_order_id = @source_order_id::bigint)
          )
      )
      OR (
          @source_order_id::bigint IS NOT NULL
          AND mo.order_id = @source_order_id::bigint
      )
  )";
        if (!string.IsNullOrWhiteSpace(suffix))
        {
            sql += "\n" + suffix;
        }

        return sql + ";";
    }

    private static void AddAvailableProductionMarkingCodeParameters(
        NpgsqlCommand command,
        long? sourceOrderId,
        long itemId,
        string? gtin,
        int? take)
    {
        command.Parameters.AddWithValue("@reserved_status", MarkingCodeStatus.Reserved);
        command.Parameters.AddWithValue("@printed_status", MarkingCodeStatus.Printed);
        command.Parameters.AddWithValue("@marking_status_cancelled", MarkingOrderStatus.Cancelled);
        command.Parameters.AddWithValue("@marking_status_failed", MarkingOrderStatus.Failed);
        command.Parameters.AddWithValue("@production_need_source_type", MarkingNeedCreationService.ProductionNeedSourceType);
        command.Parameters.AddWithValue("@production_order_source_type", MarkingNeedCreationService.ProductionOrderSourceType);
        command.Parameters.AddWithValue("@source_order_id", sourceOrderId.HasValue ? sourceOrderId.Value : DBNull.Value);
        command.Parameters.AddWithValue("@item_id", itemId);
        command.Parameters.AddWithValue("@gtin", string.IsNullOrWhiteSpace(gtin) ? DBNull.Value : gtin.Trim());
        if (take.HasValue)
        {
            command.Parameters.AddWithValue("@take", take.Value);
        }
    }

    public IReadOnlyList<long> GetAvailableKmOnHandCodeIds(long? orderId, long skuId, string? gtin14, long? locationId, long? huId, int take)
    {
        return WithConnection(connection =>
        {
            var sql = @"
SELECT c.id
FROM km_code c
WHERE c.status = @status
  AND c.ship_line_id IS NULL
  AND (c.sku_id = @sku_id OR (c.sku_id IS NULL AND @gtin14::text IS NOT NULL AND c.gtin14 = @gtin14::text))
  AND (
    @order_id::bigint IS NULL
    OR c.order_id = @order_id::bigint
    OR EXISTS (
        SELECT 1
        FROM km_code_batch b
        WHERE b.id = c.batch_id AND b.order_id = @order_id::bigint
    )
  )
  AND (@location_id::bigint IS NULL OR c.location_id = @location_id::bigint)
  AND (@hu_id::bigint IS NULL OR c.hu_id = @hu_id::bigint)
ORDER BY c.id
FOR UPDATE SKIP LOCKED
LIMIT @take;";
            using var command = CreateCommand(connection, sql);
            command.Parameters.AddWithValue("@status", (short)KmCodeStatusMapper.ToInt(KmCodeStatus.OnHand));
            command.Parameters.AddWithValue("@sku_id", skuId);
            command.Parameters.AddWithValue("@gtin14", string.IsNullOrWhiteSpace(gtin14) ? DBNull.Value : gtin14.Trim());
            command.Parameters.AddWithValue("@order_id", orderId.HasValue ? orderId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@location_id", locationId.HasValue ? locationId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@hu_id", huId.HasValue ? huId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@take", take);
            using var reader = command.ExecuteReader();
            var list = new List<long>();
            while (reader.Read())
            {
                list.Add(reader.GetInt64(0));
            }

            return list;
        });
    }

    public int AssignKmCodesToReceipt(IReadOnlyList<long> codeIds, long docId, long lineId, long? huId, long? locationId)
    {
        return WithConnection(connection =>
        {
            var updated = 0;
            foreach (var id in codeIds)
            {
                using var command = CreateCommand(connection, @"
UPDATE km_code
SET status = @status,
    receipt_doc_id = @doc_id,
    receipt_line_id = @line_id,
    hu_id = @hu_id,
    location_id = @location_id
WHERE id = @id AND status = @expected_status;");
                command.Parameters.AddWithValue("@status", (short)KmCodeStatusMapper.ToInt(KmCodeStatus.OnHand));
                command.Parameters.AddWithValue("@expected_status", (short)KmCodeStatusMapper.ToInt(KmCodeStatus.InPool));
                command.Parameters.AddWithValue("@doc_id", docId);
                command.Parameters.AddWithValue("@line_id", lineId);
                command.Parameters.AddWithValue("@hu_id", huId.HasValue ? huId.Value : DBNull.Value);
                command.Parameters.AddWithValue("@location_id", locationId.HasValue ? locationId.Value : DBNull.Value);
                command.Parameters.AddWithValue("@id", id);
                updated += command.ExecuteNonQuery();
            }

            return updated;
        });
    }

    public void MarkKmCodeShipped(long codeId, long docId, long lineId, long? orderId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE km_code
SET status = @status,
    ship_doc_id = @doc_id,
    ship_line_id = @line_id,
    order_id = COALESCE(@order_id, order_id)
WHERE id = @id AND (status = @expected_status_on_hand OR status = @expected_status_in_pool);");
            command.Parameters.AddWithValue("@status", (short)KmCodeStatusMapper.ToInt(KmCodeStatus.Shipped));
            command.Parameters.AddWithValue("@expected_status_on_hand", (short)KmCodeStatusMapper.ToInt(KmCodeStatus.OnHand));
            command.Parameters.AddWithValue("@expected_status_in_pool", (short)KmCodeStatusMapper.ToInt(KmCodeStatus.InPool));
            command.Parameters.AddWithValue("@doc_id", docId);
            command.Parameters.AddWithValue("@line_id", lineId);
            command.Parameters.AddWithValue("@order_id", orderId.HasValue ? orderId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@id", codeId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void ReopenHu(string code, string? reopenedBy, string? note)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE hus
SET status = @status,
    closed_at = NULL,
    note = @note,
    created_by = COALESCE(created_by, @reopened_by)
WHERE hu_code = @code;
");
            command.Parameters.AddWithValue("@status", "ACTIVE");
            command.Parameters.AddWithValue("@note", string.IsNullOrWhiteSpace(note) ? DBNull.Value : note.Trim());
            command.Parameters.AddWithValue("@reopened_by", string.IsNullOrWhiteSpace(reopenedBy) ? DBNull.Value : reopenedBy.Trim());
            command.Parameters.AddWithValue("@code", code.Trim());
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public long AddOrderRequest(OrderRequest request)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO order_requests(request_type, payload_json, status, created_at, created_by_login, created_by_device_id, resolved_at, resolved_by, resolution_note, applied_order_id)
VALUES(@request_type, @payload_json, @status, @created_at, @created_by_login, @created_by_device_id, @resolved_at, @resolved_by, @resolution_note, @applied_order_id)
RETURNING id;");
            command.Parameters.AddWithValue("@request_type", request.RequestType);
            command.Parameters.AddWithValue("@payload_json", request.PayloadJson);
            command.Parameters.AddWithValue("@status", string.IsNullOrWhiteSpace(request.Status) ? OrderRequestStatus.Pending : request.Status.Trim());
            command.Parameters.AddWithValue("@created_at", ToDbDate(request.CreatedAt));
            command.Parameters.AddWithValue("@created_by_login", string.IsNullOrWhiteSpace(request.CreatedByLogin) ? DBNull.Value : request.CreatedByLogin.Trim());
            command.Parameters.AddWithValue("@created_by_device_id", string.IsNullOrWhiteSpace(request.CreatedByDeviceId) ? DBNull.Value : request.CreatedByDeviceId.Trim());
            command.Parameters.AddWithValue("@resolved_at", request.ResolvedAt.HasValue ? ToDbDate(request.ResolvedAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@resolved_by", string.IsNullOrWhiteSpace(request.ResolvedBy) ? DBNull.Value : request.ResolvedBy.Trim());
            command.Parameters.AddWithValue("@resolution_note", string.IsNullOrWhiteSpace(request.ResolutionNote) ? DBNull.Value : request.ResolutionNote.Trim());
            command.Parameters.AddWithValue("@applied_order_id", request.AppliedOrderId.HasValue ? request.AppliedOrderId.Value : DBNull.Value);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public IReadOnlyList<OrderRequest> GetOrderRequests(bool includeResolved)
    {
        return WithConnection(connection =>
        {
            var sql = "SELECT id, request_type, payload_json, status, created_at, created_by_login, created_by_device_id, resolved_at, resolved_by, resolution_note, applied_order_id FROM order_requests";
            if (!includeResolved)
            {
                sql += " WHERE status = 'PENDING'";
            }

            sql += " ORDER BY created_at DESC, id DESC";
            using var command = CreateCommand(connection, sql);
            using var reader = command.ExecuteReader();
            var list = new List<OrderRequest>();
            while (reader.Read())
            {
                list.Add(new OrderRequest
                {
                    Id = reader.GetInt64(0),
                    RequestType = reader.GetString(1),
                    PayloadJson = reader.GetString(2),
                    Status = reader.IsDBNull(3) ? OrderRequestStatus.Pending : reader.GetString(3),
                    CreatedAt = FromDbDate(reader.IsDBNull(4) ? null : reader.GetString(4)) ?? DateTime.MinValue,
                    CreatedByLogin = reader.IsDBNull(5) ? null : reader.GetString(5),
                    CreatedByDeviceId = reader.IsDBNull(6) ? null : reader.GetString(6),
                    ResolvedAt = reader.IsDBNull(7) ? null : FromDbDate(reader.GetString(7)),
                    ResolvedBy = reader.IsDBNull(8) ? null : reader.GetString(8),
                    ResolutionNote = reader.IsDBNull(9) ? null : reader.GetString(9),
                    AppliedOrderId = reader.IsDBNull(10) ? null : reader.GetInt64(10)
                });
            }

            return list;
        });
    }

    public void ResolveOrderRequest(long requestId, string status, string resolvedBy, string? note, long? appliedOrderId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE order_requests
SET status = @status,
    resolved_at = @resolved_at,
    resolved_by = @resolved_by,
    resolution_note = @resolution_note,
    applied_order_id = @applied_order_id
WHERE id = @id;");
            command.Parameters.AddWithValue("@status", status);
            command.Parameters.AddWithValue("@resolved_at", ToDbDate(DateTime.Now));
            command.Parameters.AddWithValue("@resolved_by", string.IsNullOrWhiteSpace(resolvedBy) ? "WPF" : resolvedBy.Trim());
            command.Parameters.AddWithValue("@resolution_note", string.IsNullOrWhiteSpace(note) ? DBNull.Value : note.Trim());
            command.Parameters.AddWithValue("@applied_order_id", appliedOrderId.HasValue ? appliedOrderId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@id", requestId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public Guid AddMarkingCodeImport(MarkingCodeImport import)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO marking_code_import(
    id,
    original_filename,
    storage_path,
    file_hash,
    source_type,
    detected_request_number,
    detected_gtin,
    detected_quantity,
    matched_marking_order_id,
    match_confidence,
    status,
    imported_rows,
    valid_code_rows,
    duplicate_code_rows,
    error_message,
    created_at,
    processed_at)
VALUES(
    @id,
    @original_filename,
    @storage_path,
    @file_hash,
    @source_type,
    @detected_request_number,
    @detected_gtin,
    @detected_quantity,
    @matched_marking_order_id,
    @match_confidence,
    @status,
    @imported_rows,
    @valid_code_rows,
    @duplicate_code_rows,
    @error_message,
    @created_at,
    @processed_at)
RETURNING id;");
            command.Parameters.AddWithValue("@id", import.Id);
            command.Parameters.AddWithValue("@original_filename", import.OriginalFilename);
            command.Parameters.AddWithValue("@storage_path", import.StoragePath);
            command.Parameters.AddWithValue("@file_hash", import.FileHash);
            command.Parameters.AddWithValue("@source_type", import.SourceType);
            command.Parameters.AddWithValue("@detected_request_number", string.IsNullOrWhiteSpace(import.DetectedRequestNumber) ? DBNull.Value : import.DetectedRequestNumber.Trim());
            command.Parameters.AddWithValue("@detected_gtin", string.IsNullOrWhiteSpace(import.DetectedGtin) ? DBNull.Value : import.DetectedGtin.Trim());
            command.Parameters.AddWithValue("@detected_quantity", import.DetectedQuantity.HasValue ? import.DetectedQuantity.Value : DBNull.Value);
            command.Parameters.AddWithValue("@matched_marking_order_id", import.MatchedMarkingOrderId.HasValue ? import.MatchedMarkingOrderId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@match_confidence", import.MatchConfidence.HasValue ? import.MatchConfidence.Value : DBNull.Value);
            command.Parameters.AddWithValue("@status", import.Status);
            command.Parameters.AddWithValue("@imported_rows", import.ImportedRows);
            command.Parameters.AddWithValue("@valid_code_rows", import.ValidCodeRows);
            command.Parameters.AddWithValue("@duplicate_code_rows", import.DuplicateCodeRows);
            command.Parameters.AddWithValue("@error_message", string.IsNullOrWhiteSpace(import.ErrorMessage) ? DBNull.Value : import.ErrorMessage.Trim());
            command.Parameters.AddWithValue("@created_at", ToDbDate(import.CreatedAt));
            command.Parameters.AddWithValue("@processed_at", import.ProcessedAt.HasValue ? ToDbDate(import.ProcessedAt.Value) : DBNull.Value);
            return (Guid)(command.ExecuteScalar() ?? Guid.Empty);
        });
    }

    public MarkingCodeImport? FindMarkingCodeImportByHash(string fileHash)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildMarkingCodeImportQuery("WHERE i.file_hash = @file_hash"));
            command.Parameters.AddWithValue("@file_hash", fileHash);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadMarkingCodeImport(reader) : null;
        });
    }

    public void UpdateMarkingCodeImport(MarkingCodeImport import)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE marking_code_import
SET original_filename = @original_filename,
    storage_path = @storage_path,
    file_hash = @file_hash,
    source_type = @source_type,
    detected_request_number = @detected_request_number,
    detected_gtin = @detected_gtin,
    detected_quantity = @detected_quantity,
    matched_marking_order_id = @matched_marking_order_id,
    match_confidence = @match_confidence,
    status = @status,
    imported_rows = @imported_rows,
    valid_code_rows = @valid_code_rows,
    duplicate_code_rows = @duplicate_code_rows,
    error_message = @error_message,
    created_at = @created_at,
    processed_at = @processed_at
WHERE id = @id;");
            command.Parameters.AddWithValue("@id", import.Id);
            command.Parameters.AddWithValue("@original_filename", import.OriginalFilename);
            command.Parameters.AddWithValue("@storage_path", import.StoragePath);
            command.Parameters.AddWithValue("@file_hash", import.FileHash);
            command.Parameters.AddWithValue("@source_type", import.SourceType);
            command.Parameters.AddWithValue("@detected_request_number", string.IsNullOrWhiteSpace(import.DetectedRequestNumber) ? DBNull.Value : import.DetectedRequestNumber.Trim());
            command.Parameters.AddWithValue("@detected_gtin", string.IsNullOrWhiteSpace(import.DetectedGtin) ? DBNull.Value : import.DetectedGtin.Trim());
            command.Parameters.AddWithValue("@detected_quantity", import.DetectedQuantity.HasValue ? import.DetectedQuantity.Value : DBNull.Value);
            command.Parameters.AddWithValue("@matched_marking_order_id", import.MatchedMarkingOrderId.HasValue ? import.MatchedMarkingOrderId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@match_confidence", import.MatchConfidence.HasValue ? import.MatchConfidence.Value : DBNull.Value);
            command.Parameters.AddWithValue("@status", import.Status);
            command.Parameters.AddWithValue("@imported_rows", import.ImportedRows);
            command.Parameters.AddWithValue("@valid_code_rows", import.ValidCodeRows);
            command.Parameters.AddWithValue("@duplicate_code_rows", import.DuplicateCodeRows);
            command.Parameters.AddWithValue("@error_message", string.IsNullOrWhiteSpace(import.ErrorMessage) ? DBNull.Value : import.ErrorMessage.Trim());
            command.Parameters.AddWithValue("@created_at", ToDbDate(import.CreatedAt));
            command.Parameters.AddWithValue("@processed_at", import.ProcessedAt.HasValue ? ToDbDate(import.ProcessedAt.Value) : DBNull.Value);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public bool ExistsMarkingCodeByRaw(string code)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT 1 FROM marking_code WHERE code = @code LIMIT 1");
            command.Parameters.AddWithValue("@code", code);
            var result = command.ExecuteScalar();
            return result != null && result != DBNull.Value;
        });
    }

    public void AddMarkingCodes(IReadOnlyList<MarkingCode> codes)
    {
        if (codes.Count == 0)
        {
            return;
        }

        WithConnection(connection =>
        {
            foreach (var code in codes)
            {
                using var command = CreateCommand(connection, @"
INSERT INTO marking_code(
    id,
    code,
    code_hash,
    gtin,
    marking_order_id,
    import_id,
    status,
    source_row_number,
    printed_at,
    applied_at,
    reported_at,
    introduced_at,
    created_at,
    updated_at)
VALUES(
    @id,
    @code,
    @code_hash,
    @gtin,
    @marking_order_id,
    @import_id,
    @status,
    @source_row_number,
    @printed_at,
    @applied_at,
    @reported_at,
    @introduced_at,
    @created_at,
    @updated_at);");
                command.Parameters.AddWithValue("@id", code.Id);
                command.Parameters.AddWithValue("@code", code.Code);
                command.Parameters.AddWithValue("@code_hash", code.CodeHash);
                command.Parameters.AddWithValue("@gtin", string.IsNullOrWhiteSpace(code.Gtin) ? DBNull.Value : code.Gtin.Trim());
                command.Parameters.AddWithValue("@marking_order_id", code.MarkingOrderId);
                command.Parameters.AddWithValue("@import_id", code.ImportId);
                command.Parameters.AddWithValue("@status", code.Status);
                command.Parameters.AddWithValue("@source_row_number", code.SourceRowNumber.HasValue ? code.SourceRowNumber.Value : DBNull.Value);
                command.Parameters.AddWithValue("@printed_at", code.PrintedAt.HasValue ? ToDbDate(code.PrintedAt.Value) : DBNull.Value);
                command.Parameters.AddWithValue("@applied_at", code.AppliedAt.HasValue ? ToDbDate(code.AppliedAt.Value) : DBNull.Value);
                command.Parameters.AddWithValue("@reported_at", code.ReportedAt.HasValue ? ToDbDate(code.ReportedAt.Value) : DBNull.Value);
                command.Parameters.AddWithValue("@introduced_at", code.IntroducedAt.HasValue ? ToDbDate(code.IntroducedAt.Value) : DBNull.Value);
                command.Parameters.AddWithValue("@created_at", ToDbDate(code.CreatedAt));
                command.Parameters.AddWithValue("@updated_at", ToDbDate(code.UpdatedAt));
                command.ExecuteNonQuery();
            }

            return 0;
        });
    }

    public int CountMarkingCodesByMarkingOrder(Guid markingOrderId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT COUNT(*)
FROM marking_code
WHERE marking_order_id = @marking_order_id
  AND status <> @voided_status;");
            command.Parameters.AddWithValue("@marking_order_id", markingOrderId);
            command.Parameters.AddWithValue("@voided_status", MarkingCodeStatus.Voided);
            return Convert.ToInt32(command.ExecuteScalar() ?? 0L);
        });
    }

    public int CountFreeProductionMarkingCodesByItem(long itemId, string? gtin)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT COUNT(*)
FROM marking_code c
INNER JOIN marking_order mo ON mo.id = c.marking_order_id
WHERE c.status IN (@reserved_status, @printed_status)
  AND c.receipt_doc_id IS NULL
  AND c.receipt_line_id IS NULL
  AND mo.status NOT IN (@marking_status_cancelled, @marking_status_failed)
  AND (mo.source_type IN (@production_need_source_type, @production_order_source_type)
       OR mo.order_id IS NOT NULL)
  AND (
      mo.item_id = @item_id
      OR (@gtin::text IS NOT NULL AND NULLIF(BTRIM(mo.gtin), '') = @gtin::text)
      OR (@gtin::text IS NOT NULL AND NULLIF(BTRIM(c.gtin), '') = @gtin::text)
  );");
            command.Parameters.AddWithValue("@reserved_status", MarkingCodeStatus.Reserved);
            command.Parameters.AddWithValue("@printed_status", MarkingCodeStatus.Printed);
            command.Parameters.AddWithValue("@marking_status_cancelled", MarkingOrderStatus.Cancelled);
            command.Parameters.AddWithValue("@marking_status_failed", MarkingOrderStatus.Failed);
            command.Parameters.AddWithValue("@production_need_source_type", MarkingNeedCreationService.ProductionNeedSourceType);
            command.Parameters.AddWithValue("@production_order_source_type", MarkingNeedCreationService.ProductionOrderSourceType);
            command.Parameters.AddWithValue("@item_id", itemId);
            command.Parameters.AddWithValue("@gtin", string.IsNullOrWhiteSpace(gtin) ? DBNull.Value : gtin.Trim());
            return Convert.ToInt32(command.ExecuteScalar() ?? 0L);
        });
    }

    public MarkingOrder? FindMarkingOrderByRequestNumber(string requestNumber)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildMarkingOrderQuery("WHERE mo.request_number = @request_number"));
            command.Parameters.AddWithValue("@request_number", requestNumber.Trim());
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadMarkingOrder(reader) : null;
        });
    }

    public void UpdateMarkingOrderStatus(Guid id, string status, DateTime? codesBoundAt, DateTime updatedAt)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE marking_order
SET status = @status,
    codes_bound_at = @codes_bound_at,
    updated_at = @updated_at
WHERE id = @id;");
            command.Parameters.AddWithValue("@status", status);
            command.Parameters.AddWithValue("@codes_bound_at", codesBoundAt.HasValue ? ToDbDate(codesBoundAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@updated_at", ToDbDate(updatedAt));
            command.Parameters.AddWithValue("@id", id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public IReadOnlyList<ClientBlockSetting> GetClientBlockSettings()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT block_key, is_enabled
FROM client_blocks
ORDER BY block_key;");
            using var reader = command.ExecuteReader();
            var list = new List<ClientBlockSetting>();
            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                {
                    continue;
                }

                list.Add(new ClientBlockSetting(
                    reader.GetString(0),
                    !reader.IsDBNull(1) && reader.GetBoolean(1)));
            }

            return (IReadOnlyList<ClientBlockSetting>)list;
        });
    }

    public void SaveClientBlockSettings(IReadOnlyList<ClientBlockSetting> settings)
    {
        if (settings.Count == 0)
        {
            return;
        }

        WithConnection(connection =>
        {
            foreach (var setting in settings)
            {
                if (!ClientBlockCatalog.IsKnownKey(setting.Key))
                {
                    continue;
                }

                using var command = CreateCommand(connection, @"
INSERT INTO client_blocks(block_key, is_enabled, updated_at)
VALUES(@block_key, @is_enabled, @updated_at)
ON CONFLICT (block_key) DO UPDATE
SET is_enabled = EXCLUDED.is_enabled,
    updated_at = EXCLUDED.updated_at;");
                command.Parameters.AddWithValue("@block_key", setting.Key);
                command.Parameters.AddWithValue("@is_enabled", setting.IsEnabled);
                command.Parameters.AddWithValue("@updated_at", ToDbDate(DateTime.Now));
                command.ExecuteNonQuery();
            }

            return 0;
        });
    }

    public int DeleteKmCodesFromBatch(long batchId, IReadOnlyList<long> codeIds)
    {
        if (codeIds.Count == 0)
        {
            return 0;
        }

        return WithConnection(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = CreateCommand(connection, @"
DELETE FROM km_code
WHERE batch_id = @batch_id
  AND id = ANY(@ids)
  AND status = @status
  AND receipt_doc_id IS NULL
  AND ship_doc_id IS NULL;");
            command.Transaction = transaction;
            command.Parameters.AddWithValue("@batch_id", batchId);
            command.Parameters.AddWithValue("@ids", codeIds.ToArray());
            command.Parameters.AddWithValue("@status", (short)KmCodeStatusMapper.ToInt(KmCodeStatus.InPool));
            var deleted = command.ExecuteNonQuery();

            using var refreshStats = CreateCommand(connection, @"
UPDATE km_code_batch
SET total_codes = (
    SELECT COUNT(*)::integer
    FROM km_code
    WHERE batch_id = @batch_id
)
WHERE id = @batch_id;");
            refreshStats.Transaction = transaction;
            refreshStats.Parameters.AddWithValue("@batch_id", batchId);
            refreshStats.ExecuteNonQuery();

            transaction.Commit();
            return deleted;
        });
    }

    public void DeleteKmBatch(long batchId)
    {
        WithConnection(connection =>
        {
            using var transaction = connection.BeginTransaction();

            using var countAll = CreateCommand(connection, "SELECT COUNT(*) FROM km_code WHERE batch_id = @batch_id");
            countAll.Transaction = transaction;
            countAll.Parameters.AddWithValue("@batch_id", batchId);
            var total = Convert.ToInt32(countAll.ExecuteScalar() ?? 0L);

            using var countDeletable = CreateCommand(connection, @"
SELECT COUNT(*)
FROM km_code
WHERE batch_id = @batch_id
  AND status = @status
  AND receipt_doc_id IS NULL
  AND ship_doc_id IS NULL;");
            countDeletable.Transaction = transaction;
            countDeletable.Parameters.AddWithValue("@batch_id", batchId);
            countDeletable.Parameters.AddWithValue("@status", (short)KmCodeStatusMapper.ToInt(KmCodeStatus.InPool));
            var deletable = Convert.ToInt32(countDeletable.ExecuteScalar() ?? 0L);

            if (deletable != total)
            {
                throw new InvalidOperationException("Пакет содержит коды вне статуса \"В пуле\" или уже участвующие в документах. Удаление запрещено.");
            }

            using var deleteCodes = CreateCommand(connection, "DELETE FROM km_code WHERE batch_id = @batch_id");
            deleteCodes.Transaction = transaction;
            deleteCodes.Parameters.AddWithValue("@batch_id", batchId);
            deleteCodes.ExecuteNonQuery();

            using var deleteBatch = CreateCommand(connection, "DELETE FROM km_code_batch WHERE id = @batch_id");
            deleteBatch.Transaction = transaction;
            deleteBatch.Parameters.AddWithValue("@batch_id", batchId);
            var affected = deleteBatch.ExecuteNonQuery();
            if (affected == 0)
            {
                throw new InvalidOperationException("Пакет КМ не найден.");
            }

            transaction.Commit();
            return 0;
        });
    }

    private static string BuildMarkingOrderQuery(string whereClause)
    {
        var sql = @"
SELECT mo.id,
       mo.order_id,
       mo.item_id,
       mo.gtin,
       mo.requested_quantity,
       mo.request_number,
       mo.status,
       mo.notes,
       mo.source_type,
       mo.source_order_id,
       mo.requested_at,
       mo.codes_bound_at,
       mo.created_at,
       mo.updated_at
FROM marking_order mo
";
        if (!string.IsNullOrWhiteSpace(whereClause))
        {
            sql += whereClause + "\n";
        }

        return sql;
    }

    private static string BuildMarkingCodeImportQuery(string whereClause)
    {
        var sql = @"
SELECT i.id,
       i.original_filename,
       i.storage_path,
       i.file_hash,
       i.source_type,
       i.detected_request_number,
       i.detected_gtin,
       i.detected_quantity,
       i.matched_marking_order_id,
       i.match_confidence,
       i.status,
       i.imported_rows,
       i.valid_code_rows,
       i.duplicate_code_rows,
       i.error_message,
       i.created_at,
       i.processed_at
FROM marking_code_import i
";
        if (!string.IsNullOrWhiteSpace(whereClause))
        {
            sql += whereClause + "\n";
        }

        return sql;
    }

    private static string BuildMarkingCodeQuery(string whereClause)
    {
        var sql = @"
SELECT c.id,
       c.code,
       c.code_hash,
       c.gtin,
       c.marking_order_id,
       c.import_id,
       c.status,
       c.receipt_doc_id,
       c.receipt_line_id,
       c.source_row_number,
       c.printed_at,
       c.applied_at,
       c.reported_at,
       c.introduced_at,
       c.created_at,
       c.updated_at
FROM marking_code c
";
        if (!string.IsNullOrWhiteSpace(whereClause))
        {
            sql += whereClause + "\n";
        }

        return sql;
    }

    private static string BuildKmBatchQuery(string whereClause)
    {
        var sql = @"
SELECT b.id,
       b.order_id,
       o.order_ref,
       b.file_name,
       b.file_hash,
       b.imported_at,
       b.imported_by,
       b.total_codes,
       b.error_count
FROM km_code_batch b
LEFT JOIN orders o ON o.id = b.order_id
";
        if (!string.IsNullOrWhiteSpace(whereClause))
        {
            sql += whereClause + "\n";
        }

        return sql;
    }

    private static string BuildKmCodeQuery(string whereClause)
    {
        var sql = @"
SELECT c.id,
       c.batch_id,
       c.code_raw,
       c.gtin14,
       c.sku_id,
       i.name,
       i.barcode,
       c.product_name,
       c.status,
       c.receipt_doc_id,
       c.receipt_line_id,
       c.hu_id,
       h.hu_code,
       c.location_id,
       l.code,
       c.ship_doc_id,
       c.ship_line_id,
       c.order_id
FROM km_code c
LEFT JOIN items i ON i.id = c.sku_id
LEFT JOIN hus h ON h.id = c.hu_id
LEFT JOIN locations l ON l.id = c.location_id
";
        if (!string.IsNullOrWhiteSpace(whereClause))
        {
            sql += whereClause + "\n";
        }

        return sql;
    }

    private static KmCodeBatch ReadKmCodeBatch(NpgsqlDataReader reader)
    {
        return new KmCodeBatch
        {
            Id = reader.GetInt64(0),
            OrderId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
            OrderRef = reader.IsDBNull(2) ? null : reader.GetString(2),
            FileName = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            FileHash = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            ImportedAt = FromDbDate(reader.IsDBNull(5) ? null : reader.GetString(5)) ?? DateTime.MinValue,
            ImportedBy = reader.IsDBNull(6) ? null : reader.GetString(6),
            TotalCodes = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
            ErrorCount = reader.IsDBNull(8) ? 0 : reader.GetInt32(8)
        };
    }

    private static MarkingOrder ReadMarkingOrder(NpgsqlDataReader reader)
    {
        return new MarkingOrder
        {
            Id = reader.GetGuid(0),
            OrderId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
            ItemId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
            Gtin = reader.IsDBNull(3) ? null : reader.GetString(3),
            RequestedQuantity = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            RequestNumber = reader.GetString(5),
            Status = reader.GetString(6),
            Notes = reader.IsDBNull(7) ? null : reader.GetString(7),
            SourceType = reader.IsDBNull(8) ? null : reader.GetString(8),
            SourceOrderId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
            RequestedAt = reader.IsDBNull(10) ? null : FromDbDate(reader.GetString(10)),
            CodesBoundAt = reader.IsDBNull(11) ? null : FromDbDate(reader.GetString(11)),
            CreatedAt = FromDbDate(reader.GetString(12)) ?? DateTime.MinValue,
            UpdatedAt = FromDbDate(reader.GetString(13)) ?? DateTime.MinValue
        };
    }

    private static MarkingCodeImport ReadMarkingCodeImport(NpgsqlDataReader reader)
    {
        return new MarkingCodeImport
        {
            Id = reader.GetGuid(0),
            OriginalFilename = reader.GetString(1),
            StoragePath = reader.GetString(2),
            FileHash = reader.GetString(3),
            SourceType = reader.GetString(4),
            DetectedRequestNumber = reader.IsDBNull(5) ? null : reader.GetString(5),
            DetectedGtin = reader.IsDBNull(6) ? null : reader.GetString(6),
            DetectedQuantity = reader.IsDBNull(7) ? null : reader.GetInt32(7),
            MatchedMarkingOrderId = reader.IsDBNull(8) ? null : reader.GetGuid(8),
            MatchConfidence = reader.IsDBNull(9) ? null : reader.GetDecimal(9),
            Status = reader.GetString(10),
            ImportedRows = reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
            ValidCodeRows = reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
            DuplicateCodeRows = reader.IsDBNull(13) ? 0 : reader.GetInt32(13),
            ErrorMessage = reader.IsDBNull(14) ? null : reader.GetString(14),
            CreatedAt = FromDbDate(reader.GetString(15)) ?? DateTime.MinValue,
            ProcessedAt = reader.IsDBNull(16) ? null : FromDbDate(reader.GetString(16))
        };
    }

    private static KmCode ReadKmCode(NpgsqlDataReader reader)
    {
        return new KmCode
        {
            Id = reader.GetInt64(0),
            BatchId = reader.GetInt64(1),
            CodeRaw = reader.GetString(2),
            Gtin14 = reader.IsDBNull(3) ? null : reader.GetString(3),
            SkuId = reader.IsDBNull(4) ? null : reader.GetInt64(4),
            SkuName = reader.IsDBNull(5) ? null : reader.GetString(5),
            SkuBarcode = reader.IsDBNull(6) ? null : reader.GetString(6),
            ProductName = reader.IsDBNull(7) ? null : reader.GetString(7),
            Status = KmCodeStatusMapper.FromInt(reader.IsDBNull(8) ? 0 : reader.GetInt16(8)),
            ReceiptDocId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
            ReceiptLineId = reader.IsDBNull(10) ? null : reader.GetInt64(10),
            HuId = reader.IsDBNull(11) ? null : reader.GetInt64(11),
            HuCode = reader.IsDBNull(12) ? null : reader.GetString(12),
            LocationId = reader.IsDBNull(13) ? null : reader.GetInt64(13),
            LocationCode = reader.IsDBNull(14) ? null : reader.GetString(14),
            ShipDocId = reader.IsDBNull(15) ? null : reader.GetInt64(15),
            ShipLineId = reader.IsDBNull(16) ? null : reader.GetInt64(16),
            OrderId = reader.IsDBNull(17) ? null : reader.GetInt64(17)
        };
    }

    private static MarkingCode ReadMarkingCode(NpgsqlDataReader reader)
    {
        return new MarkingCode
        {
            Id = reader.GetGuid(0),
            Code = reader.GetString(1),
            CodeHash = reader.GetString(2),
            Gtin = reader.IsDBNull(3) ? null : reader.GetString(3),
            MarkingOrderId = reader.GetGuid(4),
            ImportId = reader.GetGuid(5),
            Status = reader.GetString(6),
            ReceiptDocId = reader.IsDBNull(7) ? null : reader.GetInt64(7),
            ReceiptLineId = reader.IsDBNull(8) ? null : reader.GetInt64(8),
            SourceRowNumber = reader.IsDBNull(9) ? null : reader.GetInt32(9),
            PrintedAt = reader.IsDBNull(10) ? null : FromDbDate(reader.GetString(10)),
            AppliedAt = reader.IsDBNull(11) ? null : FromDbDate(reader.GetString(11)),
            ReportedAt = reader.IsDBNull(12) ? null : FromDbDate(reader.GetString(12)),
            IntroducedAt = reader.IsDBNull(13) ? null : FromDbDate(reader.GetString(13)),
            CreatedAt = FromDbDate(reader.GetString(14)) ?? DateTime.MinValue,
            UpdatedAt = FromDbDate(reader.GetString(15)) ?? DateTime.MinValue
        };
    }

    private static string BuildImportErrorsQuery(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "SELECT id, event_id, reason, raw_json, created_at FROM import_errors ORDER BY created_at DESC";
        }

        return "SELECT id, event_id, reason, raw_json, created_at FROM import_errors WHERE reason = @reason ORDER BY created_at DESC";
    }

    private static string ToDbDate(DateTime value)
    {
        return value.ToString("s", CultureInfo.InvariantCulture);
    }

    private static string ToDbDateOnly(DateTime value)
    {
        return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static DateTime? FromDbDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}

