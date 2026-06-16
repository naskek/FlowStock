using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services;
using Npgsql;
using NpgsqlTypes;

namespace FlowStock.Data;

public sealed class PostgresDataStore : IDataStore, IOptimizedOrderReadModelStore, IOptimizedOrderListMetricsStore, IOptimizedWarehouseProductionStateStore, IOptimizedOrderLinesStore, IOptimizedOrderLineHuFateStore, IOptimizedOperationOrderCandidatesStore, IOptimizedHuReservationCandidatesStore, IReadyHuBindingSummaryStore, IRequestsSummaryStore, IProductionPalletSummaryBatchStore, IOrderOwnedPalletSummaryBatchStore, IOptimizedTsdOutboundPickingStore, ITsdHuResolverStore, IOrderStatusDiagnosticsStore, IOverShippedOrderDiagnosticsStore, IProductionPlanConsistencyDiagnosticsStore
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
    private const string MarkingReservedFilledLedgerStockCte = @"
ledger_stock_by_hu AS (
    SELECT l.item_id,
           UPPER(BTRIM(COALESCE(l.hu_code, l.hu))) AS hu_code,
           SUM(l.qty_delta) AS qty
    FROM ledger l
    GROUP BY l.item_id, UPPER(BTRIM(COALESCE(l.hu_code, l.hu)))
    HAVING SUM(l.qty_delta) > 0.000001
),
filled_pallet_by_hu AS (
    SELECT pp.item_id,
           UPPER(BTRIM(pp.hu_code)) AS hu_code,
           MAX(pp.planned_qty) AS filled_qty
    FROM production_pallets pp
    WHERE UPPER(BTRIM(COALESCE(pp.status, ''))) = 'FILLED'
      AND pp.hu_code IS NOT NULL
      AND BTRIM(pp.hu_code) <> ''
    GROUP BY pp.item_id, UPPER(BTRIM(pp.hu_code))
)";
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
       p.cancel_reason,
       p.cancelled_at,
       p.created_at
FROM production_pallets p
INNER JOIN items i ON i.id = p.item_id
LEFT JOIN locations l ON l.id = p.to_location_id";
    public const string TsdOutboundOrderRowsSql = @"
WITH candidate_orders AS (
    SELECT o.id,
           o.order_ref,
           o.status AS persisted_status,
           COALESCE(p.name, '') AS partner_name
    FROM orders o
    LEFT JOIN partners p ON p.id = o.partner_id
    WHERE o.order_type = @customer_order_type
      AND o.status NOT IN (@draft_order_status, @shipped_order_status, @cancelled_order_status, @merged_order_status)
),
order_line_scope AS (
    SELECT ol.id AS order_line_id,
           ol.order_id,
           ol.item_id,
           GREATEST(0, ol.qty_ordered)::double precision AS qty_ordered
    FROM order_lines ol
    INNER JOIN candidate_orders co ON co.id = ol.order_id
),
active_doc_lines AS (
    SELECT dl.id,
           dl.doc_id,
           dl.order_line_id,
           dl.item_id,
           dl.qty,
           dl.from_hu,
           dl.to_hu
    FROM doc_lines dl
    WHERE dl.qty > @qty_tolerance
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
),
closed_shipped_by_line AS (
    SELECT dl.order_line_id,
           SUM(dl.qty)::double precision AS shipped_qty
    FROM active_doc_lines dl
    INNER JOIN order_line_scope ols ON ols.order_line_id = dl.order_line_id
    INNER JOIN docs d ON d.id = dl.doc_id
                AND d.order_id = ols.order_id
                AND d.type = @outbound_doc_type
                AND d.status = @closed_doc_status
    GROUP BY dl.order_line_id
),
closed_shipped_by_line_hu AS (
    SELECT dl.order_line_id,
           UPPER(BTRIM(dl.from_hu)) AS hu_code,
           SUM(dl.qty)::double precision AS shipped_qty
    FROM active_doc_lines dl
    INNER JOIN order_line_scope ols ON ols.order_line_id = dl.order_line_id
    INNER JOIN docs d ON d.id = dl.doc_id
                AND d.order_id = ols.order_id
                AND d.type = @outbound_doc_type
                AND d.status = @closed_doc_status
    WHERE NULLIF(BTRIM(dl.from_hu), '') IS NOT NULL
    GROUP BY dl.order_line_id, UPPER(BTRIM(dl.from_hu))
),
shipment_line_remaining AS (
    SELECT ols.order_line_id,
           ols.order_id,
           ols.item_id,
           ols.qty_ordered,
           COALESCE(ship.shipped_qty, 0)::double precision AS shipped_qty,
           GREATEST(0, ols.qty_ordered - COALESCE(ship.shipped_qty, 0))::double precision AS remaining_qty
    FROM order_line_scope ols
    LEFT JOIN closed_shipped_by_line ship ON ship.order_line_id = ols.order_line_id
),
shipment_progress AS (
    SELECT co.id AS order_id,
           COALESCE(SUM(slr.qty_ordered), 0)::double precision AS ordered_qty,
           COALESCE(SUM(slr.shipped_qty), 0)::double precision AS shipped_qty,
           COALESCE(SUM(slr.remaining_qty), 0)::double precision AS remaining_qty
    FROM candidate_orders co
    LEFT JOIN shipment_line_remaining slr ON slr.order_id = co.id
    GROUP BY co.id
),
warehouse_plan_lines AS (
    SELECT p.order_id,
           p.order_line_id,
           p.item_id,
           UPPER(BTRIM(p.to_hu)) AS hu_code,
           SUM(p.qty_planned)::double precision AS qty_planned
    FROM order_receipt_plan_lines p
    INNER JOIN order_line_scope ols ON ols.order_line_id = p.order_line_id
                                 AND ols.order_id = p.order_id
                                 AND ols.item_id = p.item_id
    WHERE p.qty_planned > @qty_tolerance
      AND NULLIF(BTRIM(p.to_hu), '') IS NOT NULL
    GROUP BY p.order_id, p.order_line_id, p.item_id, UPPER(BTRIM(p.to_hu))
),
filled_pallet_lines AS (
    SELECT pp.order_id,
           pll.order_line_id,
           pll.item_id,
           UPPER(BTRIM(pp.hu_code)) AS hu_code,
           SUM(pll.planned_qty)::double precision AS planned_qty,
           pp.prd_doc_id
    FROM production_pallets pp
    INNER JOIN production_pallet_lines pll ON pll.production_pallet_id = pp.id
    INNER JOIN order_line_scope ols ON ols.order_line_id = pll.order_line_id
                                 AND ols.order_id = pp.order_id
                                 AND ols.item_id = pll.item_id
    WHERE pp.status = @pallet_filled_status
      AND pp.order_id IS NOT NULL
      AND pll.order_line_id IS NOT NULL
      AND pll.planned_qty > @qty_tolerance
      AND NULLIF(BTRIM(pp.hu_code), '') IS NOT NULL
    GROUP BY pp.order_id, pll.order_line_id, pll.item_id, UPPER(BTRIM(pp.hu_code)), pp.prd_doc_id
    UNION ALL
    SELECT pp.order_id,
           pp.order_line_id,
           pp.item_id,
           UPPER(BTRIM(pp.hu_code)) AS hu_code,
           SUM(pp.planned_qty)::double precision AS planned_qty,
           pp.prd_doc_id
    FROM production_pallets pp
    INNER JOIN order_line_scope ols ON ols.order_line_id = pp.order_line_id
                                 AND ols.order_id = pp.order_id
                                 AND ols.item_id = pp.item_id
    WHERE pp.status = @pallet_filled_status
      AND pp.order_id IS NOT NULL
      AND pp.order_line_id IS NOT NULL
      AND pp.planned_qty > @qty_tolerance
      AND NULLIF(BTRIM(pp.hu_code), '') IS NOT NULL
      AND NOT EXISTS (
          SELECT 1
          FROM production_pallet_lines pll
          WHERE pll.production_pallet_id = pp.id
      )
    GROUP BY pp.order_id, pp.order_line_id, pp.item_id, UPPER(BTRIM(pp.hu_code)), pp.prd_doc_id
),
candidate_hu_items AS (
    SELECT item_id, hu_code FROM warehouse_plan_lines
    UNION
    SELECT item_id, hu_code FROM filled_pallet_lines
),
ledger_by_hu_item AS (
    SELECT chi.item_id,
           chi.hu_code,
           SUM(l.qty_delta)::double precision AS qty
    FROM candidate_hu_items chi
    INNER JOIN ledger l ON l.item_id = chi.item_id
                       AND UPPER(BTRIM(COALESCE(l.hu_code, l.hu))) = chi.hu_code
    GROUP BY chi.item_id, chi.hu_code
    HAVING SUM(l.qty_delta) > @qty_tolerance
),
positive_prd_ledger_by_doc_item_hu AS (
    SELECT l.doc_id,
           fpl.item_id,
           fpl.hu_code,
           SUM(l.qty_delta)::double precision AS qty
    FROM (
        SELECT DISTINCT prd_doc_id, item_id, hu_code
        FROM filled_pallet_lines
    ) fpl
    INNER JOIN ledger l ON l.doc_id = fpl.prd_doc_id
                       AND l.item_id = fpl.item_id
                       AND UPPER(BTRIM(COALESCE(l.hu_code, l.hu))) = fpl.hu_code
                       AND l.qty_delta > @qty_tolerance
    GROUP BY l.doc_id, fpl.item_id, fpl.hu_code
),
legacy_receipt_totals AS (
    SELECT dl.order_line_id,
           SUM(dl.qty)::double precision AS qty_received
    FROM active_doc_lines dl
    INNER JOIN order_line_scope ols ON ols.order_line_id = dl.order_line_id
    INNER JOIN docs d ON d.id = dl.doc_id
                AND d.order_id = ols.order_id
                AND d.type = @production_doc_type
                AND d.status = @closed_doc_status
    WHERE NOT EXISTS (
          SELECT 1
          FROM production_pallets pp
          WHERE pp.prd_doc_id = d.id
            AND pp.status <> @pallet_cancelled_status
      )
    GROUP BY dl.order_line_id
),
filled_pallet_receipt_totals AS (
    SELECT fpl.order_line_id,
           SUM(LEAST(fpl.planned_qty, prd.qty))::double precision AS qty_received
    FROM filled_pallet_lines fpl
    INNER JOIN positive_prd_ledger_by_doc_item_hu prd ON prd.doc_id = fpl.prd_doc_id
                                                     AND prd.item_id = fpl.item_id
                                                     AND prd.hu_code = fpl.hu_code
    GROUP BY fpl.order_line_id
),
receipt_totals AS (
    SELECT order_line_id,
           SUM(qty_received)::double precision AS qty_received
    FROM (
        SELECT order_line_id, qty_received FROM legacy_receipt_totals
        UNION ALL
        SELECT order_line_id, qty_received FROM filled_pallet_receipt_totals
    ) receipt_sources
    GROUP BY order_line_id
),
reserved_totals AS (
    SELECT wpl.order_line_id,
           SUM(LEAST(wpl.qty_planned, stock.qty))::double precision AS qty_reserved
    FROM warehouse_plan_lines wpl
    INNER JOIN ledger_by_hu_item stock ON stock.item_id = wpl.item_id
                                      AND stock.hu_code = wpl.hu_code
    GROUP BY wpl.order_line_id
),
receipt_need AS (
    SELECT co.id AS order_id,
           COALESCE(BOOL_OR(ols.qty_ordered
               - COALESCE(receipt.qty_received, 0)
               - COALESCE(reserved.qty_reserved, 0) > @qty_tolerance), FALSE) AS has_receipt_need
    FROM candidate_orders co
    LEFT JOIN order_line_scope ols ON ols.order_id = co.id
    LEFT JOIN receipt_totals receipt ON receipt.order_line_id = ols.order_line_id
    LEFT JOIN reserved_totals reserved ON reserved.order_line_id = ols.order_line_id
    GROUP BY co.id
),
expected_line_sources AS (
    SELECT wpl.order_id,
           wpl.order_line_id,
           wpl.hu_code,
           LEAST(
               GREATEST(0, wpl.qty_planned - COALESCE(shipped_hu.shipped_qty, 0)),
               stock.qty,
               slr.remaining_qty)::double precision AS qty
    FROM warehouse_plan_lines wpl
    INNER JOIN ledger_by_hu_item stock ON stock.item_id = wpl.item_id
                                      AND stock.hu_code = wpl.hu_code
    INNER JOIN shipment_line_remaining slr ON slr.order_line_id = wpl.order_line_id
                                          AND slr.remaining_qty > @qty_tolerance
    LEFT JOIN closed_shipped_by_line_hu shipped_hu ON shipped_hu.order_line_id = wpl.order_line_id
                                                  AND shipped_hu.hu_code = wpl.hu_code
    WHERE wpl.qty_planned - COALESCE(shipped_hu.shipped_qty, 0) > @qty_tolerance
    UNION ALL
    SELECT fpl.order_id,
           fpl.order_line_id,
           fpl.hu_code,
           LEAST(
               GREATEST(0, fpl.planned_qty - COALESCE(shipped_hu.shipped_qty, 0)),
               stock.qty,
               slr.remaining_qty)::double precision AS qty
    FROM filled_pallet_lines fpl
    INNER JOIN ledger_by_hu_item stock ON stock.item_id = fpl.item_id
                                      AND stock.hu_code = fpl.hu_code
    INNER JOIN shipment_line_remaining slr ON slr.order_line_id = fpl.order_line_id
                                          AND slr.remaining_qty > @qty_tolerance
    LEFT JOIN closed_shipped_by_line_hu shipped_hu ON shipped_hu.order_line_id = fpl.order_line_id
                                                  AND shipped_hu.hu_code = fpl.hu_code
    WHERE fpl.planned_qty - COALESCE(shipped_hu.shipped_qty, 0) > @qty_tolerance
),
expected_hus AS (
    SELECT DISTINCT order_id, hu_code
    FROM expected_line_sources
    WHERE qty > @qty_tolerance
),
expected_agg AS (
    SELECT order_id,
           COUNT(DISTINCT hu_code)::int AS expected_hu_count
    FROM expected_hus
    GROUP BY order_id
),
tsd_docs AS (
    SELECT d.order_id,
           TRUE AS has_tsd_doc
    FROM docs d
    INNER JOIN candidate_orders co ON co.id = d.order_id
    WHERE d.type = @outbound_doc_type
      AND UPPER(BTRIM(COALESCE(d.comment, ''))) LIKE UPPER(@tsd_picking_comment) || '%'
    GROUP BY d.order_id
),
selected_doc AS (
    SELECT order_id, id, status
    FROM (
        SELECT d.order_id,
               d.id,
               d.status,
               ROW_NUMBER() OVER (
                   PARTITION BY d.order_id
                   ORDER BY CASE WHEN d.status = @draft_doc_status THEN 0 ELSE 1 END,
                            CASE WHEN d.status = @draft_doc_status THEN d.id ELSE -d.id END
               ) AS rn
        FROM docs d
        INNER JOIN candidate_orders co ON co.id = d.order_id
        WHERE d.type = @outbound_doc_type
          AND (d.status = @draft_doc_status
               OR UPPER(BTRIM(COALESCE(d.comment, ''))) LIKE UPPER(@tsd_picking_comment) || '%')
    ) ranked
    WHERE rn = 1
),
picked_hus AS (
    SELECT sd.order_id,
           UPPER(BTRIM(dl.from_hu)) AS hu_code
    FROM selected_doc sd
    INNER JOIN active_doc_lines dl ON dl.doc_id = sd.id
    WHERE NULLIF(BTRIM(dl.from_hu), '') IS NOT NULL
    GROUP BY sd.order_id, UPPER(BTRIM(dl.from_hu))
),
picked_agg AS (
    SELECT expected.order_id,
           COUNT(DISTINCT picked.hu_code)::int AS picked_hu_count
    FROM expected_hus expected
    INNER JOIN picked_hus picked ON picked.order_id = expected.order_id
                                AND picked.hu_code = expected.hu_code
    GROUP BY expected.order_id
),
scanned_agg AS (
    SELECT sd.order_id,
           COALESCE(SUM(dl.qty), 0)::double precision AS scanned_qty
    FROM selected_doc sd
    INNER JOIN active_doc_lines dl ON dl.doc_id = sd.id
    WHERE sd.status = @draft_doc_status
    GROUP BY sd.order_id
),
fingerprint_source AS (
    SELECT expected.order_id,
           STRING_AGG(
               expected.hu_code || ':' || CASE WHEN picked.hu_code IS NULL THEN '0' ELSE '1' END,
               '|' ORDER BY expected.hu_code) AS source
    FROM expected_hus expected
    LEFT JOIN picked_hus picked ON picked.order_id = expected.order_id
                               AND picked.hu_code = expected.hu_code
    GROUP BY expected.order_id
)
SELECT co.id,
       co.order_ref,
       co.partner_name,
       CASE
           WHEN progress.shipped_qty > @qty_tolerance
                AND progress.remaining_qty > @qty_tolerance THEN @partial_status_display
           WHEN co.persisted_status = @accepted_order_status THEN @accepted_status_display
           WHEN co.persisted_status = @in_progress_order_status THEN @in_progress_status_display
           WHEN co.persisted_status = @draft_order_status THEN @draft_status_display
           WHEN co.persisted_status = @shipped_order_status THEN @shipped_status_display
           WHEN co.persisted_status = @cancelled_order_status THEN @cancelled_status_display
           WHEN co.persisted_status = @merged_order_status THEN @merged_status_display
           ELSE co.persisted_status
       END AS status_display,
       COALESCE(expected.expected_hu_count, 0)::int AS expected_hu_count,
       COALESCE(picked.picked_hu_count, 0)::int AS picked_hu_count,
       progress.ordered_qty,
       progress.shipped_qty,
       progress.remaining_qty,
       COALESCE(scanned.scanned_qty, 0)::double precision AS scanned_qty,
       COALESCE(selected.status = @closed_doc_status, FALSE) AS is_closed,
       COALESCE(fingerprint.source, '') AS fingerprint_source
FROM candidate_orders co
INNER JOIN shipment_progress progress ON progress.order_id = co.id
INNER JOIN expected_agg expected ON expected.order_id = co.id
LEFT JOIN picked_agg picked ON picked.order_id = co.id
LEFT JOIN scanned_agg scanned ON scanned.order_id = co.id
LEFT JOIN selected_doc selected ON selected.order_id = co.id
LEFT JOIN tsd_docs tsd ON tsd.order_id = co.id
LEFT JOIN receipt_need receipt_need ON receipt_need.order_id = co.id
LEFT JOIN fingerprint_source fingerprint ON fingerprint.order_id = co.id
WHERE expected.expected_hu_count > 0
  AND (
      co.persisted_status = @accepted_order_status
      OR COALESCE(tsd.has_tsd_doc, FALSE)
      OR (progress.shipped_qty > @qty_tolerance AND progress.remaining_qty > @qty_tolerance)
      OR NOT COALESCE(receipt_need.has_receipt_need, FALSE)
  )
ORDER BY LOWER(co.order_ref), co.order_ref;";
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
open_production_activity_by_order AS (
    SELECT activity.order_id,
           TRUE AS has_open_production_receipt
    FROM (
        SELECT d.order_id
        FROM docs d
        INNER JOIN order_scope os ON os.id = d.order_id
        WHERE d.type = 'PRODUCTION_RECEIPT'
          AND d.status <> 'CLOSED'
          AND d.order_id IS NOT NULL
        UNION
        SELECT ols.order_id
        FROM docs d
        INNER JOIN doc_lines dl ON dl.doc_id = d.id
        INNER JOIN order_lines_scope ols ON ols.id = dl.order_line_id
        WHERE d.type = 'PRODUCTION_RECEIPT'
          AND d.status <> 'CLOSED'
    ) activity
    GROUP BY activity.order_id
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
           COUNT(olm.order_line_id) FILTER (WHERE olm.qty_ordered > 0.000001) AS demand_line_count,
           COALESCE(BOOL_AND(olm.qty_shipped + 0.000001 >= olm.qty_ordered), FALSE) AS fully_shipped,
           COALESCE(BOOL_AND(olm.qty_customer_ready + 0.000001 >= olm.qty_ordered), FALSE) AS fully_customer_ready,
           COALESCE(BOOL_AND(olm.qty_produced_total + 0.000001 >= olm.qty_ordered), FALSE) AS fully_produced,
           COALESCE(BOOL_AND(olm.qty_produced_total + 0.000001 >= olm.qty_ordered) FILTER (WHERE olm.qty_ordered > 0.000001), FALSE) AS fully_demand_produced,
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
                              END > 0.000001), FALSE) AS has_receipt_remaining,
           COALESCE(SUM(GREATEST(0, olm.qty_ordered)), 0)::double precision AS shipment_ordered_qty,
           COALESCE(SUM(GREATEST(0, olm.qty_shipped)), 0)::double precision AS shipment_shipped_qty,
           COALESCE(SUM(GREATEST(0, olm.qty_ordered - olm.qty_shipped)), 0)::double precision AS shipment_remaining_qty
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
           COUNT(*) FILTER (WHERE ps.status IN ('PLANNED', 'PRINTED', 'FILLED'))::int AS active_pallet_count,
           COALESCE(SUM(ps.planned_qty) FILTER (WHERE ps.status <> 'CANCELLED'), 0)::double precision AS planned_qty,
           COUNT(*) FILTER (WHERE ps.status = 'FILLED')::int AS filled_pallet_count,
           COALESCE(SUM(ps.planned_qty) FILTER (WHERE ps.status = 'FILLED'), 0)::double precision AS filled_qty
    FROM pallet_source ps
    INNER JOIN order_scope os ON os.id = ps.order_id
    GROUP BY ps.order_id
),
" + MarkingReservedFilledLedgerStockCte + @",
reserved_stock_hu_by_line AS (
    SELECT p.order_line_id,
           SUM(LEAST(p.qty_planned, ls.qty)) AS reserved_stock_hu_qty
    FROM order_receipt_plan_lines p
    INNER JOIN order_lines_scope ols ON ols.id = p.order_line_id
    INNER JOIN order_base ob ON ob.id = p.order_id AND ob.order_type = 'CUSTOMER'
    INNER JOIN ledger_stock_by_hu ls ON ls.item_id = p.item_id
                                    AND ls.hu_code = UPPER(BTRIM(p.to_hu))
    WHERE p.qty_planned > 0
      AND p.to_hu IS NOT NULL
      AND BTRIM(p.to_hu) <> ''
    GROUP BY p.order_line_id
),
markable_line_need AS (
    SELECT olm.order_id,
           olm.item_id,
           olm.qty_ordered,
           NULLIF(BTRIM(i.gtin), '') AS gtin,
           CASE
               WHEN olm.order_type = 'INTERNAL' THEN GREATEST(0, olm.qty_ordered)
               ELSE GREATEST(0, olm.qty_ordered - olm.qty_shipped - COALESCE(rsh.reserved_stock_hu_qty, 0))
           END AS qty_for_marking
    FROM order_line_metrics olm
    INNER JOIN items i ON i.id = olm.item_id
    INNER JOIN item_types it ON it.id = i.item_type_id
    LEFT JOIN reserved_stock_hu_by_line rsh ON rsh.order_line_id = olm.order_line_id
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
selected_marking_orders AS (
    SELECT mo.id,
           COALESCE(mo.order_id, mo.source_order_id) AS order_id,
           mo.item_id,
           mo.gtin
    FROM marking_order mo
    INNER JOIN order_scope os ON os.id = COALESCE(mo.order_id, mo.source_order_id)
    WHERE COALESCE(mo.order_id, mo.source_order_id) IS NOT NULL
      AND mo.status NOT IN (@marking_status_cancelled, @marking_status_failed)
      AND (mo.source_type IN (@production_need_source_type, @production_order_source_type)
           OR mo.order_id IS NOT NULL)
),
free_code_stats AS (
    SELECT smo.order_id,
           COALESCE(smo.item_id, 0) AS item_id,
           COALESCE(NULLIF(BTRIM(COALESCE(smo.gtin, c.gtin)), ''), '') AS gtin,
           COUNT(*) AS codes_total
    FROM selected_marking_orders smo
    INNER JOIN marking_code c ON c.marking_order_id = smo.id
    WHERE c.status IN (@marking_code_status_reserved, @marking_code_status_printed)
      AND c.receipt_doc_id IS NULL
      AND c.receipt_line_id IS NULL
      AND EXISTS (
          SELECT 1
          FROM markable_item_need need
          WHERE need.order_id = smo.order_id
            AND (COALESCE(smo.item_id, 0) = need.item_id
             OR (need.gtin IS NOT NULL
                 AND COALESCE(NULLIF(BTRIM(COALESCE(smo.gtin, c.gtin)), ''), '') = COALESCE(need.gtin, '')))
      )
    GROUP BY smo.order_id,
             COALESCE(smo.item_id, 0),
             COALESCE(NULLIF(BTRIM(COALESCE(smo.gtin, c.gtin)), ''), '')
),
bound_code_stats AS (
    SELECT ols.order_id,
           ols.item_id,
           COALESCE(NULLIF(BTRIM(i.gtin), ''), '') AS gtin,
           COUNT(*) AS codes_total
    FROM order_lines_scope ols
    INNER JOIN doc_lines dl ON dl.order_line_id = ols.id
    INNER JOIN marking_code c ON c.receipt_line_id = dl.id
    INNER JOIN selected_marking_orders smo ON smo.id = c.marking_order_id
                                         AND smo.order_id = ols.order_id
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
           AND EXISTS (
               SELECT 1
               FROM markable_line_need mln
               WHERE mln.order_id = ob.id
                 AND mln.qty_ordered > 0
           )
           AND NOT EXISTS (
               SELECT 1
               FROM markable_item_need need
               LEFT JOIN LATERAL (
                   SELECT COALESCE(SUM(free.codes_total), 0) AS total
                   FROM free_code_stats free
                   WHERE free.order_id = need.order_id
                     AND (free.item_id = need.item_id
                          OR (need.gtin IS NOT NULL AND free.gtin = COALESCE(need.gtin, '')))
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
           WHEN ob.persisted_status = 'MERGED' THEN 'MERGED'
           WHEN ob.order_type = 'INTERNAL' THEN CASE
               WHEN COALESCE(ss.any_produced, FALSE)
                    AND COALESCE(ss.demand_line_count, 0) > 0
                    AND COALESCE(ss.fully_demand_produced, FALSE) THEN 'SHIPPED'
               WHEN ob.persisted_status = 'DRAFT'
                    AND NOT COALESCE(ss.any_produced, FALSE)
                    AND NOT COALESCE(opa.has_open_production_receipt, FALSE)
                    AND COALESCE(ps.active_pallet_count, 0) = 0
                    AND UPPER(BTRIM(COALESCE(ob.marking_status, ''))) NOT IN ('PRINTED', 'EXCEL_GENERATED') THEN 'DRAFT'
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
                AND COALESCE(ss.any_produced, FALSE)
                AND COALESCE(ss.demand_line_count, 0) > 0
                AND COALESCE(ss.fully_demand_produced, FALSE) THEN ds.production_closed_at
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
       COALESCE(ps.filled_qty, 0) AS filled_qty,
       COALESCE(olf.shipment_ordered_qty, 0) AS shipment_ordered_qty,
       COALESCE(olf.shipment_shipped_qty, 0) AS shipment_shipped_qty,
       COALESCE(olf.shipment_remaining_qty, 0) AS shipment_remaining_qty
FROM order_base ob
LEFT JOIN status_summary ss ON ss.order_id = ob.id
LEFT JOIN doc_summary ds ON ds.order_id = ob.id
LEFT JOIN open_production_activity_by_order opa ON opa.order_id = ob.id
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

    public ProductionFillingCompletion? GetProductionFillingCompletion(long orderId, string operationFingerprint)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT order_id, operation_fingerprint, completed_at, completed_by_device_id
FROM production_filling_completions
WHERE order_id = @order_id AND operation_fingerprint = @operation_fingerprint;");
            command.Parameters.AddWithValue("@order_id", orderId);
            command.Parameters.AddWithValue("@operation_fingerprint", operationFingerprint);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new ProductionFillingCompletion
                {
                    OrderId = reader.GetInt64(0),
                    OperationFingerprint = reader.GetString(1),
                    CompletedAt = reader.GetDateTime(2),
                    CompletedByDeviceId = reader.IsDBNull(3) ? null : reader.GetString(3)
                }
                : null;
        });
    }

    public IReadOnlyList<long> GetProductionFillingReadyOrderIds()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT p.order_id
FROM production_pallets p
WHERE p.order_id IS NOT NULL
GROUP BY p.order_id
HAVING COUNT(*) FILTER (WHERE UPPER(COALESCE(p.status, '')) <> 'CANCELLED') > 0
   AND COUNT(*) FILTER (WHERE UPPER(COALESCE(p.status, '')) NOT IN ('FILLED', 'CANCELLED')) = 0;");
            using var reader = command.ExecuteReader();
            var orderIds = new List<long>();
            while (reader.Read())
            {
                orderIds.Add(reader.GetInt64(0));
            }
            return orderIds;
        });
    }

    public IReadOnlyList<Order> GetOrdersByIds(IReadOnlyCollection<long> orderIds)
    {
        var ids = NormalizePositiveDistinctIds(orderIds);
        if (ids.Length == 0)
        {
            return Array.Empty<Order>();
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, $@"
{BuildOrderSelectSql("SELECT id FROM orders WHERE id = ANY(@order_ids)")}
");
            AddOrderSelectParameters(command);
            command.Parameters.Add("@order_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value = ids;
            using var reader = command.ExecuteReader();
            var orders = new List<Order>();
            while (reader.Read())
            {
                orders.Add(ReadOrder(reader));
            }

            return orders;
        });
    }

    public IReadOnlyDictionary<long, IReadOnlyList<ProductionPallet>> GetProductionPalletsByOrderIds(IReadOnlyCollection<long> orderIds)
    {
        var ids = NormalizePositiveDistinctIds(orderIds);
        if (ids.Length == 0)
        {
            return new Dictionary<long, IReadOnlyList<ProductionPallet>>();
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, $@"
{ProductionPalletSelectSql}
INNER JOIN docs d ON d.id = p.prd_doc_id
WHERE p.order_id = ANY(@order_ids)
  AND d.type = @doc_type
ORDER BY p.order_id, p.id;
");
            command.Parameters.Add("@order_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value = ids;
            command.Parameters.AddWithValue("@doc_type", DocTypeMapper.ToOpString(DocType.ProductionReceipt));
            using var reader = command.ExecuteReader();
            var pallets = new List<ProductionPallet>();
            while (reader.Read())
            {
                pallets.Add(ReadProductionPallet(reader));
            }

            reader.Close();
            AttachProductionPalletLines(connection, pallets);
            return GroupByOrderId(pallets, pallet => pallet.OrderId);
        });
    }

    public IReadOnlyDictionary<long, IReadOnlyList<OrderLine>> GetOrderLinesByOrderIds(IReadOnlyCollection<long> orderIds)
    {
        var ids = NormalizePositiveDistinctIds(orderIds);
        if (ids.Length == 0)
        {
            return new Dictionary<long, IReadOnlyList<OrderLine>>();
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT id, order_id, item_id, qty_ordered, production_purpose, production_pallet_group
FROM order_lines
WHERE order_id = ANY(@order_ids)
ORDER BY order_id, id;
");
            command.Parameters.Add("@order_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value = ids;
            using var reader = command.ExecuteReader();
            var lines = new List<OrderLine>();
            while (reader.Read())
            {
                lines.Add(ReadOrderLine(reader));
            }

            return GroupByOrderId(lines, line => line.OrderId);
        });
    }

    public IReadOnlyDictionary<long, ProductionPalletSummary> GetOrderOwnedProductionPalletSummaries(IReadOnlyCollection<long> orderIds)
    {
        var ids = NormalizePositiveDistinctIds(orderIds);
        if (ids.Length == 0)
        {
            return new Dictionary<long, ProductionPalletSummary>();
        }

        var palletsByOrderId = GetProductionPalletsByOrderIds(ids);
        var linesByOrderId = GetOrderLinesByOrderIds(ids);
        var result = new Dictionary<long, ProductionPalletSummary>();
        foreach (var orderId in ids)
        {
            palletsByOrderId.TryGetValue(orderId, out var pallets);
            linesByOrderId.TryGetValue(orderId, out var lines);
            result[orderId] = ProductionPalletService.BuildSummary(
                ProductionPalletService.BuildOrderOwnedPalletViews(
                    orderId,
                    pallets ?? Array.Empty<ProductionPallet>(),
                    (lines ?? Array.Empty<OrderLine>()).ToDictionary(line => line.Id)));
        }

        return result;
    }

    public IReadOnlyList<ProductionFillingCompletion> GetProductionFillingCompletionsByOrderIds(IReadOnlyCollection<long> orderIds)
    {
        var ids = NormalizePositiveDistinctIds(orderIds);
        if (ids.Length == 0)
        {
            return Array.Empty<ProductionFillingCompletion>();
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT order_id, operation_fingerprint, completed_at, completed_by_device_id
FROM production_filling_completions
WHERE order_id = ANY(@order_ids);
");
            command.Parameters.Add("@order_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value = ids;
            using var reader = command.ExecuteReader();
            var rows = new List<ProductionFillingCompletion>();
            while (reader.Read())
            {
                rows.Add(new ProductionFillingCompletion
                {
                    OrderId = reader.GetInt64(0),
                    OperationFingerprint = reader.GetString(1),
                    CompletedAt = reader.GetDateTime(2),
                    CompletedByDeviceId = reader.IsDBNull(3) ? null : reader.GetString(3)
                });
            }

            return rows;
        });
    }

    public IReadOnlyDictionary<long, IReadOnlyList<Doc>> GetDocsByOrderIds(IReadOnlyCollection<long> orderIds)
    {
        var ids = NormalizePositiveDistinctIds(orderIds);
        if (ids.Length == 0)
        {
            return new Dictionary<long, IReadOnlyList<Doc>>();
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, $@"
{DocSelectBase}
WHERE d.order_id = ANY(@order_ids)
ORDER BY d.order_id, d.created_at DESC, d.id DESC;
");
            command.Parameters.Add("@order_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value = ids;
            using var reader = command.ExecuteReader();
            var docs = new List<Doc>();
            while (reader.Read())
            {
                docs.Add(ReadDoc(reader));
            }

            return GroupByOrderId(docs, doc => doc.OrderId);
        });
    }

    public IReadOnlyDictionary<long, IReadOnlyList<DocLine>> GetDocLinesByDocIds(IReadOnlyCollection<long> docIds)
    {
        var ids = NormalizePositiveDistinctIds(docIds);
        if (ids.Length == 0)
        {
            return new Dictionary<long, IReadOnlyList<DocLine>>();
        }

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
INNER JOIN docs d ON d.id = dl.doc_id
WHERE dl.doc_id = ANY(@doc_ids)
  AND (
      (d.type = @inventory_correction_type AND ABS(dl.qty) > @qty_tolerance)
      OR (d.type <> @inventory_correction_type AND dl.qty > @qty_tolerance)
  )
  AND NOT EXISTS (
      SELECT 1
      FROM doc_lines newer
      WHERE newer.replaces_line_id = dl.id
  )
ORDER BY dl.doc_id, dl.id;
");
            command.Parameters.Add("@doc_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value = ids;
            command.Parameters.AddWithValue("@inventory_correction_type", DocTypeMapper.ToOpString(DocType.InventoryCorrection));
            command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
            using var reader = command.ExecuteReader();
            var lines = new List<DocLine>();
            while (reader.Read())
            {
                lines.Add(ReadDocLine(reader));
            }

            return GroupByDocId(lines, line => line.DocId);
        });
    }

    public IReadOnlyDictionary<long, IReadOnlyList<OrderReceiptPlanLine>> GetOrderReceiptPlanLinesByOrderIds(IReadOnlyCollection<long> orderIds)
    {
        var ids = NormalizePositiveDistinctIds(orderIds);
        if (ids.Length == 0)
        {
            return new Dictionary<long, IReadOnlyList<OrderReceiptPlanLine>>();
        }

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
WHERE p.order_id = ANY(@order_ids)
ORDER BY p.order_id, p.sort_order, p.id;
");
            command.Parameters.Add("@order_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value = ids;
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

            return GroupByOrderId(lines, line => line.OrderId);
        });
    }

    public IReadOnlyDictionary<long, IReadOnlyList<OrderShipmentLine>> GetOrderShipmentRemainingByOrderIds(IReadOnlyCollection<long> orderIds)
    {
        var ids = NormalizePositiveDistinctIds(orderIds);
        if (ids.Length == 0)
        {
            return new Dictionary<long, IReadOnlyList<OrderShipmentLine>>();
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT ol.id,
       ol.order_id,
       ol.item_id,
       i.name,
       ol.qty_ordered,
       COALESCE(s.sum_qty, 0) AS shipped_qty,
       GREATEST(0, ol.qty_ordered - COALESCE(s.sum_qty, 0)) AS remaining
FROM order_lines ol
INNER JOIN items i ON i.id = ol.item_id
LEFT JOIN (
    SELECT d.order_id,
           dl.order_line_id,
           SUM(dl.qty) AS sum_qty
    FROM doc_lines dl
    INNER JOIN docs d ON d.id = dl.doc_id
    WHERE d.status = @status
      AND d.type = @doc_type
      AND d.order_id = ANY(@order_ids)
      AND dl.order_line_id IS NOT NULL
      AND dl.qty > 0
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
    GROUP BY d.order_id, dl.order_line_id
) s ON s.order_line_id = ol.id AND s.order_id = ol.order_id
WHERE ol.order_id = ANY(@order_ids)
ORDER BY ol.order_id, ol.id;
");
            command.Parameters.Add("@order_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value = ids;
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

            return GroupByOrderId(lines, line => line.OrderId);
        });
    }

    public IReadOnlyDictionary<long, IReadOnlyDictionary<long, double>> GetShippedTotalsByOrderIds(IReadOnlyCollection<long> orderIds)
    {
        var ids = NormalizePositiveDistinctIds(orderIds);
        if (ids.Length == 0)
        {
            return new Dictionary<long, IReadOnlyDictionary<long, double>>();
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT d.order_id, dl.order_line_id, COALESCE(SUM(dl.qty), 0)
FROM docs d
INNER JOIN doc_lines dl ON dl.doc_id = d.id
WHERE d.type = @type
  AND d.status = @status
  AND d.order_id = ANY(@order_ids)
  AND dl.order_line_id IS NOT NULL
  AND dl.qty > 0
  AND NOT EXISTS (
      SELECT 1
      FROM doc_lines newer
      WHERE newer.replaces_line_id = dl.id
  )
GROUP BY d.order_id, dl.order_line_id;
");
            command.Parameters.AddWithValue("@type", DocTypeMapper.ToOpString(DocType.Outbound));
            command.Parameters.AddWithValue("@status", DocTypeMapper.StatusToString(DocStatus.Closed));
            command.Parameters.Add("@order_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value = ids;
            using var reader = command.ExecuteReader();
            var totalsByOrder = new Dictionary<long, Dictionary<long, double>>();
            while (reader.Read())
            {
                var orderId = reader.GetInt64(0);
                if (!totalsByOrder.TryGetValue(orderId, out var totals))
                {
                    totals = new Dictionary<long, double>();
                    totalsByOrder[orderId] = totals;
                }

                totals[reader.GetInt64(1)] = reader.GetDouble(2);
            }

            return totalsByOrder.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<long, double>)pair.Value);
        });
    }

    public void AddProductionFillingCompletion(ProductionFillingCompletion completion)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO production_filling_completions(order_id, operation_fingerprint, completed_at, completed_by_device_id)
VALUES (@order_id, @operation_fingerprint, @completed_at, @completed_by_device_id)
ON CONFLICT (order_id, operation_fingerprint) DO NOTHING;");
            command.Parameters.AddWithValue("@order_id", completion.OrderId);
            command.Parameters.AddWithValue("@operation_fingerprint", completion.OperationFingerprint);
            command.Parameters.AddWithValue("@completed_at", completion.CompletedAt);
            command.Parameters.AddWithValue("@completed_by_device_id", (object?)completion.CompletedByDeviceId ?? DBNull.Value);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public bool AddBusinessNotification(BusinessNotification notification)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO business_notifications(event_type, severity, title, message, entity_type, entity_id, entity_ref, created_at, source, dedupe_key)
VALUES (@event_type, @severity, @title, @message, @entity_type, @entity_id, @entity_ref, @created_at, @source, @dedupe_key)
ON CONFLICT (dedupe_key) DO NOTHING;");
            command.Parameters.AddWithValue("@event_type", notification.EventType);
            command.Parameters.AddWithValue("@severity", notification.Severity);
            command.Parameters.AddWithValue("@title", notification.Title);
            command.Parameters.AddWithValue("@message", notification.Message);
            command.Parameters.AddWithValue("@entity_type", (object?)notification.EntityType ?? DBNull.Value);
            command.Parameters.AddWithValue("@entity_id", (object?)notification.EntityId ?? DBNull.Value);
            command.Parameters.AddWithValue("@entity_ref", (object?)notification.EntityRef ?? DBNull.Value);
            command.Parameters.AddWithValue("@created_at", notification.CreatedAt);
            command.Parameters.AddWithValue("@source", notification.Source);
            command.Parameters.AddWithValue("@dedupe_key", notification.DedupeKey);
            return command.ExecuteNonQuery() > 0;
        });
    }

    public IReadOnlyList<BusinessNotification> GetBusinessNotifications(bool unreadOnly, int limit, string readerKey)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT n.id, n.event_type, n.severity, n.title, n.message, n.entity_type, n.entity_id, n.entity_ref,
       n.created_at, n.source, n.dedupe_key, (r.notification_id IS NOT NULL) AS is_read
FROM business_notifications n
LEFT JOIN business_notification_reads r ON r.notification_id = n.id AND r.reader_key = @reader_key
WHERE (@unread_only = FALSE OR r.notification_id IS NULL)
ORDER BY n.created_at DESC, n.id DESC
LIMIT @limit;");
            command.Parameters.AddWithValue("@reader_key", readerKey);
            command.Parameters.AddWithValue("@unread_only", unreadOnly);
            command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 500));
            using var reader = command.ExecuteReader();
            var rows = new List<BusinessNotification>();
            while (reader.Read())
            {
                rows.Add(new BusinessNotification
                {
                    Id = reader.GetInt64(0),
                    EventType = reader.GetString(1),
                    Severity = reader.GetString(2),
                    Title = reader.GetString(3),
                    Message = reader.GetString(4),
                    EntityType = reader.IsDBNull(5) ? null : reader.GetString(5),
                    EntityId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    EntityRef = reader.IsDBNull(7) ? null : reader.GetString(7),
                    CreatedAt = reader.GetDateTime(8),
                    Source = reader.GetString(9),
                    DedupeKey = reader.GetString(10),
                    IsRead = reader.GetBoolean(11)
                });
            }
            return rows;
        });
    }

    public int CountUnreadBusinessNotifications(string readerKey)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT COUNT(*)
FROM business_notifications n
LEFT JOIN business_notification_reads r ON r.notification_id = n.id AND r.reader_key = @reader_key
WHERE r.notification_id IS NULL;");
            command.Parameters.AddWithValue("@reader_key", readerKey);
            return Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        });
    }

    public void MarkBusinessNotificationRead(long notificationId, string readerKey, DateTime readAt)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO business_notification_reads(notification_id, reader_key, read_at)
SELECT id, @reader_key, @read_at FROM business_notifications WHERE id = @notification_id
ON CONFLICT (notification_id, reader_key) DO NOTHING;");
            command.Parameters.AddWithValue("@notification_id", notificationId);
            command.Parameters.AddWithValue("@reader_key", readerKey);
            command.Parameters.AddWithValue("@read_at", readAt);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void MarkAllBusinessNotificationsRead(string readerKey, DateTime readAt)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO business_notification_reads(notification_id, reader_key, read_at)
SELECT id, @reader_key, @read_at FROM business_notifications
ON CONFLICT (notification_id, reader_key) DO NOTHING;");
            command.Parameters.AddWithValue("@reader_key", readerKey);
            command.Parameters.AddWithValue("@read_at", readAt);
            command.ExecuteNonQuery();
            return 0;
        });
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
INNER JOIN docs d ON d.id = dl.doc_id
WHERE dl.doc_id = @doc_id
  AND (
      (d.type = @inventory_correction_type AND ABS(dl.qty) > @qty_tolerance)
      OR (d.type <> @inventory_correction_type AND dl.qty > @qty_tolerance)
  )
  AND NOT EXISTS (
      SELECT 1
      FROM doc_lines newer
      WHERE newer.replaces_line_id = dl.id
  )
ORDER BY dl.id");
            command.Parameters.AddWithValue("@doc_id", docId);
            command.Parameters.AddWithValue("@inventory_correction_type", DocTypeMapper.ToOpString(DocType.InventoryCorrection));
            command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
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

    public double GetLedgerQtyByDocItemHu(long docId, long itemId, string? huCode)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT COALESCE(SUM(qty_delta), 0)
FROM ledger
WHERE doc_id = @doc_id
  AND item_id = @item_id
  AND (
      (@hu_code IS NULL AND NULLIF(BTRIM(COALESCE(hu_code, hu)), '') IS NULL)
      OR (@hu_code IS NOT NULL AND UPPER(BTRIM(COALESCE(hu_code, hu))) = @hu_code)
  );");
            command.Parameters.AddWithValue("@doc_id", docId);
            command.Parameters.AddWithValue("@item_id", itemId);
            command.Parameters.Add("@hu_code", NpgsqlDbType.Text).Value =
                string.IsNullOrWhiteSpace(huCode) ? DBNull.Value : huCode.Trim().ToUpperInvariant();
            var result = command.ExecuteScalar();
            return result == null || result == DBNull.Value ? 0 : Convert.ToDouble(result, CultureInfo.InvariantCulture);
        });
    }

    public IReadOnlyList<DocLineView> GetDocLineViews(long docId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT dl.id, dl.order_line_id, dl.production_purpose, dl.item_id, i.name, i.barcode, dl.qty, dl.qty_input, dl.uom_code, i.base_uom, lf.code, lt.code, dl.from_hu, dl.to_hu, dl.pack_single_hu
FROM doc_lines dl
INNER JOIN docs d ON d.id = dl.doc_id
INNER JOIN items i ON i.id = dl.item_id
LEFT JOIN locations lf ON lf.id = dl.from_location_id
LEFT JOIN locations lt ON lt.id = dl.to_location_id
WHERE dl.doc_id = @doc_id
  AND (
      (d.type = @inventory_correction_type AND ABS(dl.qty) > @qty_tolerance)
      OR (d.type <> @inventory_correction_type AND dl.qty > @qty_tolerance)
  )
  AND NOT EXISTS (
      SELECT 1
      FROM doc_lines newer
      WHERE newer.replaces_line_id = dl.id
  )
ORDER BY dl.id;
");
            command.Parameters.AddWithValue("@doc_id", docId);
            command.Parameters.AddWithValue("@inventory_correction_type", DocTypeMapper.ToOpString(DocType.InventoryCorrection));
            command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
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
           dl.pack_single_hu,
           UPPER(BTRIM(dl.to_hu)) AS hu_key,
           CASE
               WHEN dl.pack_single_hu THEN 'HU:' || UPPER(BTRIM(dl.to_hu))
               ELSE 'LINE:' || dl.id::text
           END AS pallet_key
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
      AND NOT EXISTS (
          SELECT 1
          FROM production_pallets pp
          WHERE pp.doc_line_id = dl.id
      )
),
grouped AS (
    SELECT prd_doc_id,
           pallet_key,
           COUNT(DISTINCT order_line_id) AS order_line_count,
           SUM(qty) AS total_qty
    FROM active_lines
    GROUP BY prd_doc_id, pallet_key
),
representative AS (
    SELECT al.*,
           ROW_NUMBER() OVER (PARTITION BY al.prd_doc_id, al.pallet_key ORDER BY al.doc_line_id) AS rn
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
INNER JOIN grouped g ON g.prd_doc_id = al.prd_doc_id AND g.pallet_key = al.pallet_key
WHERE rn = 1
  AND NOT EXISTS (
      SELECT 1
      FROM production_pallets existing
      WHERE existing.prd_doc_id = al.prd_doc_id
        AND existing.status <> @cancelled_status
        AND (
            (al.pack_single_hu AND UPPER(BTRIM(existing.hu_code)) = UPPER(BTRIM(al.hu_code)))
            OR (NOT al.pack_single_hu AND existing.doc_line_id = al.doc_line_id)
        )
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
                       AND (
                           (dl.pack_single_hu AND UPPER(BTRIM(dl.to_hu)) = UPPER(BTRIM(p.hu_code)))
                           OR (NOT dl.pack_single_hu AND dl.id = p.doc_line_id)
                       )
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

    public IReadOnlyDictionary<long, ProductionPalletSummary> GetProductionPalletSummariesByDocIds(IReadOnlyCollection<long> docIds)
    {
        var ids = NormalizePositiveDistinctIds(docIds);
        if (ids.Length == 0)
        {
            return new Dictionary<long, ProductionPalletSummary>();
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
WITH line_totals AS (
    SELECT pll.production_pallet_id,
           COUNT(*) AS line_count,
           COALESCE(SUM(pll.planned_qty), 0)::double precision AS planned_qty,
           COUNT(*) FILTER (WHERE COALESCE(pll.filled_qty, 0) + @qty_tolerance >= pll.planned_qty) AS completed_line_count
    FROM production_pallet_lines pll
    INNER JOIN production_pallets pp ON pp.id = pll.production_pallet_id
    WHERE pp.prd_doc_id = ANY(@doc_ids)
    GROUP BY pll.production_pallet_id
),
pallet_source AS (
    SELECT pp.prd_doc_id,
           UPPER(COALESCE(pp.status, '')) AS status,
           CASE
               WHEN COALESCE(lt.line_count, 0) > 0 THEN COALESCE(lt.planned_qty, 0)
               ELSE pp.planned_qty
           END AS planned_qty,
           COALESCE(lt.line_count, 0) > 0
           AND COALESCE(lt.completed_line_count, 0) = COALESCE(lt.line_count, 0) AS all_components_filled
    FROM production_pallets pp
    LEFT JOIN line_totals lt ON lt.production_pallet_id = pp.id
    WHERE pp.prd_doc_id = ANY(@doc_ids)
)
SELECT ps.prd_doc_id,
       COUNT(*) FILTER (WHERE ps.status <> @cancelled_status)::int AS planned_pallet_count,
       COALESCE(SUM(ps.planned_qty) FILTER (WHERE ps.status <> @cancelled_status), 0)::double precision AS planned_qty,
       COUNT(*) FILTER (WHERE ps.status = @filled_status)::int AS filled_pallet_count,
       COALESCE(SUM(ps.planned_qty) FILTER (WHERE ps.status = @filled_status), 0)::double precision AS filled_qty,
       COUNT(*) FILTER (
           WHERE ps.status IN (@planned_status, @printed_status)
             AND NOT ps.all_components_filled
       )::int AS remaining_pallet_count,
       COALESCE(SUM(ps.planned_qty) FILTER (
           WHERE ps.status IN (@planned_status, @printed_status)
             AND NOT ps.all_components_filled
       ), 0)::double precision AS remaining_qty
FROM pallet_source ps
GROUP BY ps.prd_doc_id;
");
            command.Parameters.Add("@doc_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value = ids;
            command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
            command.Parameters.AddWithValue("@cancelled_status", ProductionPalletStatus.Cancelled);
            command.Parameters.AddWithValue("@filled_status", ProductionPalletStatus.Filled);
            command.Parameters.AddWithValue("@planned_status", ProductionPalletStatus.Planned);
            command.Parameters.AddWithValue("@printed_status", ProductionPalletStatus.Printed);

            using var reader = command.ExecuteReader();
            var result = new Dictionary<long, ProductionPalletSummary>();
            while (reader.Read())
            {
                result[reader.GetInt64(0)] = new ProductionPalletSummary
                {
                    PlannedPalletCount = reader.GetInt32(1),
                    PlannedQty = reader.GetDouble(2),
                    FilledPalletCount = reader.GetInt32(3),
                    FilledQty = reader.GetDouble(4),
                    RemainingPalletCount = reader.GetInt32(5),
                    RemainingQty = reader.GetDouble(6)
                };
            }

            return result;
        });
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

    public ProductionPallet? GetProductionPalletByHuForUpdate(string huCode)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, $@"
{ProductionPalletSelectSql}
WHERE UPPER(BTRIM(p.hu_code)) = UPPER(BTRIM(@hu_code))
ORDER BY CASE WHEN p.status = @cancelled_status THEN 1 ELSE 0 END,
         p.id
LIMIT 1
FOR UPDATE OF p;
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
  AND (o.id IS NULL OR o.status NOT IN (@shipped_order_status, @cancelled_order_status, @merged_order_status))
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
            command.Parameters.AddWithValue("@shipped_order_status", OrderStatusMapper.StatusToString(OrderStatus.Shipped));
            command.Parameters.AddWithValue("@cancelled_order_status", OrderStatusMapper.StatusToString(OrderStatus.Cancelled));
            command.Parameters.AddWithValue("@merged_order_status", OrderStatusMapper.StatusToString(OrderStatus.Merged));
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

    public bool HasProductionPalletLinesForDoc(long docId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT 1
FROM production_pallet_lines pll
INNER JOIN production_pallets pp ON pp.id = pll.production_pallet_id
WHERE pp.prd_doc_id = @doc_id
LIMIT 1;
");
            command.Parameters.AddWithValue("@doc_id", docId);
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
  AND (
      status <> @planned_status
      OR EXISTS (
          SELECT 1
          FROM production_pallet_lines progress
          WHERE progress.production_pallet_id = production_pallets.id
            AND progress.filled_qty > @qty_tolerance
      )
  )
LIMIT 1;
"))
            {
                guard.Parameters.AddWithValue("@doc_id", docId);
                guard.Parameters.AddWithValue("@planned_status", ProductionPalletStatus.Planned);
                guard.Parameters.AddWithValue("@cancelled_status", ProductionPalletStatus.Cancelled);
                guard.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
                if (guard.ExecuteScalar() != null)
                {
                    throw new InvalidOperationException("План паллет уже напечатан или наполнен. Переназначение HU запрещено.");
                }
            }

            ClearProductionPalletPlanCore(connection, docId, deletePlanHus: false);
            return 0;
        });
    }

    public void DetachRemovableProductionPalletPlanForDraftReceiptCancel(long docId)
    {
        WithConnection(connection =>
        {
            using (var ledgerGuard = CreateCommand(connection, "SELECT 1 FROM ledger WHERE doc_id = @doc_id LIMIT 1;"))
            {
                ledgerGuard.Parameters.AddWithValue("@doc_id", docId);
                if (ledgerGuard.ExecuteScalar() != null)
                {
                    throw new InvalidOperationException("Нельзя отменить заказ: по черновику выпуска уже есть движения склада.");
                }
            }

            using (var factsGuard = CreateCommand(connection, @"
SELECT 1
FROM production_pallets pp
LEFT JOIN production_pallet_lines pll ON pll.production_pallet_id = pp.id
WHERE pp.prd_doc_id = @doc_id
  AND (
      pp.status NOT IN (@planned_status, @cancelled_status)
      OR COALESCE(pll.filled_qty, 0) > @qty_tolerance
  )
LIMIT 1;
"))
            {
                factsGuard.Parameters.AddWithValue("@doc_id", docId);
                factsGuard.Parameters.AddWithValue("@planned_status", ProductionPalletStatus.Planned);
                factsGuard.Parameters.AddWithValue("@cancelled_status", ProductionPalletStatus.Cancelled);
                factsGuard.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
                if (factsGuard.ExecuteScalar() != null)
                {
                    throw new InvalidOperationException("Нельзя отменить заказ: по черновику выпуска есть фактические паллеты.");
                }
            }

            using (var detachPlan = CreateCommand(connection, @"
DELETE FROM production_pallet_lines pll
USING production_pallets pp
WHERE pp.id = pll.production_pallet_id
  AND pp.prd_doc_id = @doc_id
  AND pp.status IN (@planned_status, @cancelled_status);

DELETE FROM production_pallets
WHERE prd_doc_id = @doc_id
  AND status IN (@planned_status, @cancelled_status);
"))
            {
                detachPlan.Parameters.AddWithValue("@doc_id", docId);
                detachPlan.Parameters.AddWithValue("@planned_status", ProductionPalletStatus.Planned);
                detachPlan.Parameters.AddWithValue("@cancelled_status", ProductionPalletStatus.Cancelled);
                detachPlan.ExecuteNonQuery();
            }

            return 0;
        });
    }

    public ProductionPalletPlanCleanupCounts ClearPlannedProductionPalletPlanForOrderLines(
        long orderId,
        IReadOnlyCollection<long> orderLineIds)
    {
        var ids = orderLineIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return new ProductionPalletPlanCleanupCounts();
        }

        return WithConnection(connection =>
        {
            int removedPalletCount;
            int removedLineCount;
            using (var count = CreateCommand(connection, @"
WITH target_pallet_lines AS (
    SELECT pll.id, pll.doc_line_id, pll.production_pallet_id
    FROM production_pallet_lines pll
    INNER JOIN production_pallets pp ON pp.id = pll.production_pallet_id
    WHERE pp.order_id = @order_id
      AND pp.status = @planned_status
      AND pll.order_line_id = ANY(@order_line_ids)
      AND NOT EXISTS (
          SELECT 1 FROM production_pallet_lines progress
          WHERE progress.production_pallet_id = pp.id
            AND progress.filled_qty > @qty_tolerance
      )
),
target_single_pallets AS (
    SELECT pp.id, pp.doc_line_id
    FROM production_pallets pp
    WHERE pp.order_id = @order_id
      AND pp.status = @planned_status
      AND pp.order_line_id = ANY(@order_line_ids)
      AND NOT EXISTS (
          SELECT 1
          FROM production_pallet_lines pll
          WHERE pll.production_pallet_id = pp.id
      )
),
deleted_component_pallets AS (
    SELECT DISTINCT pp.id
    FROM production_pallets pp
    WHERE pp.id IN (SELECT production_pallet_id FROM target_pallet_lines)
      AND NOT EXISTS (
          SELECT 1
          FROM production_pallet_lines remaining
          WHERE remaining.production_pallet_id = pp.id
            AND (
                remaining.order_line_id IS NULL
                OR remaining.order_line_id <> ALL(@order_line_ids)
            )
      )
)
SELECT
    (SELECT COUNT(*) FROM deleted_component_pallets)
    + (SELECT COUNT(*) FROM target_single_pallets),
    (SELECT COUNT(DISTINCT doc_line_id) FROM target_pallet_lines)
    + (SELECT COUNT(DISTINCT doc_line_id) FROM target_single_pallets WHERE doc_line_id IS NOT NULL);
"))
            {
                count.Parameters.AddWithValue("@order_id", orderId);
                count.Parameters.AddWithValue("@order_line_ids", ids);
                count.Parameters.AddWithValue("@planned_status", ProductionPalletStatus.Planned);
                count.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
                using var reader = count.ExecuteReader();
                if (reader.Read())
                {
                    removedPalletCount = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                    removedLineCount = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                }
                else
                {
                    removedPalletCount = 0;
                    removedLineCount = 0;
                }
            }

            using (var cleanup = CreateCommand(connection, @"
WITH target_pallet_lines AS (
    SELECT pll.id, pll.doc_line_id, pll.production_pallet_id
    FROM production_pallet_lines pll
    INNER JOIN production_pallets pp ON pp.id = pll.production_pallet_id
    WHERE pp.order_id = @order_id
      AND pp.status = @planned_status
      AND pll.order_line_id = ANY(@order_line_ids)
      AND NOT EXISTS (
          SELECT 1 FROM production_pallet_lines progress
          WHERE progress.production_pallet_id = pp.id
            AND progress.filled_qty > @qty_tolerance
      )
),
target_doc_lines AS (
    SELECT DISTINCT doc_line_id AS id FROM target_pallet_lines
    UNION
    SELECT DISTINCT pp.doc_line_id AS id
    FROM production_pallets pp
    WHERE pp.order_id = @order_id
      AND pp.status = @planned_status
      AND pp.order_line_id = ANY(@order_line_ids)
      AND pp.doc_line_id IS NOT NULL
      AND NOT EXISTS (
          SELECT 1
          FROM production_pallet_lines pll
          WHERE pll.production_pallet_id = pp.id
      )
),
deleted_pallet_lines AS (
    DELETE FROM production_pallet_lines pll
    USING target_pallet_lines target
    WHERE pll.id = target.id
    RETURNING pll.production_pallet_id
),
empty_pallets AS (
    SELECT pp.id
    FROM production_pallets pp
    WHERE pp.order_id = @order_id
      AND pp.status = @planned_status
      AND (
          pp.order_line_id = ANY(@order_line_ids)
          OR pp.id IN (SELECT production_pallet_id FROM deleted_pallet_lines)
      )
      AND NOT EXISTS (
          SELECT 1
          FROM production_pallet_lines remaining
          WHERE remaining.production_pallet_id = pp.id
      )
),
deleted_pallets AS (
    DELETE FROM production_pallets pp
    USING empty_pallets target
    WHERE pp.id = target.id
    RETURNING pp.id
),
remaining_pallets AS (
    SELECT pp.id,
           MIN(pll.doc_line_id) AS doc_line_id,
           CASE WHEN COUNT(DISTINCT pll.order_line_id) = 1 THEN MIN(pll.order_line_id) ELSE NULL END AS order_line_id,
           MIN(pll.item_id) AS item_id,
           SUM(pll.planned_qty) AS planned_qty
    FROM production_pallets pp
    INNER JOIN production_pallet_lines pll ON pll.production_pallet_id = pp.id
    WHERE pp.order_id = @order_id
      AND pp.status = @planned_status
      AND pp.id IN (SELECT production_pallet_id FROM deleted_pallet_lines)
      AND pp.id NOT IN (SELECT id FROM deleted_pallets)
    GROUP BY pp.id
),
updated_pallets AS (
    UPDATE production_pallets pp
    SET doc_line_id = remaining.doc_line_id,
        order_line_id = remaining.order_line_id,
        item_id = remaining.item_id,
        planned_qty = remaining.planned_qty
    FROM remaining_pallets remaining
    WHERE pp.id = remaining.id
    RETURNING pp.id
)
DELETE FROM doc_lines dl
USING target_doc_lines target
WHERE dl.id = target.id
  AND NOT EXISTS (
      SELECT 1
      FROM production_pallet_lines pll
      WHERE pll.doc_line_id = dl.id
  );
"))
            {
                cleanup.Parameters.AddWithValue("@order_id", orderId);
                cleanup.Parameters.AddWithValue("@order_line_ids", ids);
                cleanup.Parameters.AddWithValue("@planned_status", ProductionPalletStatus.Planned);
                cleanup.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
                cleanup.ExecuteNonQuery();
            }

            return new ProductionPalletPlanCleanupCounts
            {
                RemovedPalletCount = removedPalletCount,
                RemovedLineCount = removedLineCount
            };
        });
    }

    public ProductionPalletPlanCleanupCounts CancelProductionPalletPlan(long docId)
    {
        return WithConnection(connection =>
        {
            using (var filledGuard = CreateCommand(connection, @"
SELECT 1
FROM production_pallets
WHERE prd_doc_id = @doc_id
  AND status = @filled_status
LIMIT 1;
"))
            {
                filledGuard.Parameters.AddWithValue("@doc_id", docId);
                filledGuard.Parameters.AddWithValue("@filled_status", ProductionPalletStatus.Filled);
                if (filledGuard.ExecuteScalar() != null)
                {
                    throw new InvalidOperationException("Нельзя удалить план паллет: есть уже наполненные паллеты.");
                }
            }

            using (var ledgerGuard = CreateCommand(connection, "SELECT COUNT(*) FROM ledger WHERE doc_id = @doc_id;"))
            {
                ledgerGuard.Parameters.AddWithValue("@doc_id", docId);
                var ledgerCount = Convert.ToInt32(ledgerGuard.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
                if (ledgerCount > 0)
                {
                    throw new InvalidOperationException("Нельзя удалить план паллет: по выпуску уже есть движения склада.");
                }
            }

            return ClearProductionPalletPlanCore(connection, docId, deletePlanHus: true);
        });
    }

    public ProductionPalletPlanCleanupCounts DeleteProductionPalletPlanPallets(IReadOnlyCollection<long> productionPalletIds)
    {
        var ids = productionPalletIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return new ProductionPalletPlanCleanupCounts();
        }

        return WithConnection(connection =>
        {
            int removedPalletCount;
            int removedLineCount;
            long[] removedPalletIds;
            using (var count = CreateCommand(connection, @"
WITH target_pallets AS (
    SELECT pp.id
    FROM production_pallets pp
    INNER JOIN docs d ON d.id = pp.prd_doc_id
    WHERE pp.id = ANY(@pallet_ids)
      AND pp.status IN (@planned_status, @printed_status)
      AND d.status <> @closed_status
      AND NOT EXISTS (
          SELECT 1 FROM production_pallet_lines progress
          WHERE progress.production_pallet_id = pp.id
            AND progress.filled_qty > @qty_tolerance
      )
),
target_doc_lines AS (
    SELECT DISTINCT pll.doc_line_id AS id
    FROM production_pallet_lines pll
    WHERE pll.production_pallet_id IN (SELECT id FROM target_pallets)
    UNION
    SELECT DISTINCT pp.doc_line_id AS id
    FROM production_pallets pp
    WHERE pp.id IN (SELECT id FROM target_pallets)
      AND pp.doc_line_id IS NOT NULL
),
removable_doc_lines AS (
    SELECT target.id
    FROM target_doc_lines target
    WHERE NOT EXISTS (
          SELECT 1
          FROM production_pallets pp
          WHERE pp.id NOT IN (SELECT id FROM target_pallets)
            AND pp.doc_line_id = target.id
      )
      AND NOT EXISTS (
          SELECT 1
          FROM production_pallet_lines pll
          WHERE pll.production_pallet_id NOT IN (SELECT id FROM target_pallets)
            AND pll.doc_line_id = target.id
      )
)
SELECT
    (SELECT COUNT(*) FROM target_pallets),
    (SELECT COUNT(*) FROM removable_doc_lines),
    COALESCE((SELECT ARRAY_AGG(id ORDER BY id) FROM target_pallets), ARRAY[]::bigint[]);
"))
            {
                count.Parameters.AddWithValue("@pallet_ids", ids);
                count.Parameters.AddWithValue("@planned_status", ProductionPalletStatus.Planned);
                count.Parameters.AddWithValue("@printed_status", ProductionPalletStatus.Printed);
                count.Parameters.AddWithValue("@closed_status", DocTypeMapper.StatusToString(DocStatus.Closed));
                count.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
                using var reader = count.ExecuteReader();
                if (reader.Read())
                {
                    removedPalletCount = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                    removedLineCount = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                    removedPalletIds = reader.IsDBNull(2) ? Array.Empty<long>() : reader.GetFieldValue<long[]>(2);
                }
                else
                {
                    removedPalletCount = 0;
                    removedLineCount = 0;
                    removedPalletIds = Array.Empty<long>();
                }
            }

            using (var cleanup = CreateCommand(connection, @"
WITH target_pallets AS (
    SELECT pp.id, pp.hu_code
    FROM production_pallets pp
    INNER JOIN docs d ON d.id = pp.prd_doc_id
    WHERE pp.id = ANY(@pallet_ids)
      AND pp.status IN (@planned_status, @printed_status)
      AND d.status <> @closed_status
      AND NOT EXISTS (
          SELECT 1 FROM production_pallet_lines progress
          WHERE progress.production_pallet_id = pp.id
            AND progress.filled_qty > @qty_tolerance
      )
),
target_doc_lines AS (
    SELECT DISTINCT pll.doc_line_id AS id
    FROM production_pallet_lines pll
    WHERE pll.production_pallet_id IN (SELECT id FROM target_pallets)
    UNION
    SELECT DISTINCT pp.doc_line_id AS id
    FROM production_pallets pp
    WHERE pp.id IN (SELECT id FROM target_pallets)
      AND pp.doc_line_id IS NOT NULL
),
removable_doc_lines AS (
    SELECT target.id
    FROM target_doc_lines target
    WHERE NOT EXISTS (
          SELECT 1
          FROM production_pallets pp
          WHERE pp.id NOT IN (SELECT id FROM target_pallets)
            AND pp.doc_line_id = target.id
      )
      AND NOT EXISTS (
          SELECT 1
          FROM production_pallet_lines pll
          WHERE pll.production_pallet_id NOT IN (SELECT id FROM target_pallets)
            AND pll.doc_line_id = target.id
      )
),
deleted_hus AS (
    DELETE FROM hus h
    USING target_pallets target
    WHERE COALESCE(h.created_by, '') = @plan_created_by
      AND UPPER(BTRIM(h.hu_code)) = UPPER(BTRIM(target.hu_code))
      AND NOT EXISTS (
          SELECT 1
          FROM ledger l
          WHERE UPPER(BTRIM(l.hu_code)) = UPPER(BTRIM(h.hu_code))
      )
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines dl
          WHERE dl.id NOT IN (SELECT id FROM removable_doc_lines)
            AND (
                UPPER(BTRIM(COALESCE(dl.to_hu, ''))) = UPPER(BTRIM(h.hu_code))
                OR UPPER(BTRIM(COALESCE(dl.from_hu, ''))) = UPPER(BTRIM(h.hu_code))
            )
      )
      AND NOT EXISTS (
          SELECT 1
          FROM production_pallets pp
          WHERE pp.id NOT IN (SELECT id FROM target_pallets)
            AND UPPER(BTRIM(pp.hu_code)) = UPPER(BTRIM(h.hu_code))
            AND pp.status <> @cancelled_status
      )
    RETURNING h.hu_code
),
deleted_pallet_lines AS (
    DELETE FROM production_pallet_lines pll
    USING target_pallets target
    WHERE pll.production_pallet_id = target.id
    RETURNING pll.id
),
deleted_pallets AS (
    DELETE FROM production_pallets pp
    USING target_pallets target
    WHERE pp.id = target.id
    RETURNING pp.id
)
DELETE FROM doc_lines dl
USING removable_doc_lines target
WHERE dl.id = target.id;
"))
            {
                cleanup.Parameters.AddWithValue("@pallet_ids", ids);
                cleanup.Parameters.AddWithValue("@planned_status", ProductionPalletStatus.Planned);
                cleanup.Parameters.AddWithValue("@printed_status", ProductionPalletStatus.Printed);
                cleanup.Parameters.AddWithValue("@cancelled_status", ProductionPalletStatus.Cancelled);
                cleanup.Parameters.AddWithValue("@closed_status", DocTypeMapper.StatusToString(DocStatus.Closed));
                cleanup.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
                cleanup.Parameters.AddWithValue("@plan_created_by", "PRODUCTION-PALLET-PLAN");
                cleanup.ExecuteNonQuery();
            }

            return new ProductionPalletPlanCleanupCounts
            {
                RemovedPalletCount = removedPalletCount,
                RemovedLineCount = removedLineCount,
                RemovedPalletIds = removedPalletIds
            };
        });
    }

    public ProductionPalletPlanAdoptionResult AdoptProductionPalletPlan(
        long sourcePrdDocId,
        long targetPrdDocId,
        long sourceOrderId,
        long targetOrderId,
        IReadOnlyDictionary<long, long> targetOrderLineIdByItemId)
    {
        if (targetOrderLineIdByItemId.Count == 0)
        {
            throw new InvalidOperationException("Не заданы строки клиентского заказа для переноса плана паллет.");
        }

        return WithConnection(connection =>
        {
            var values = string.Join(", ", targetOrderLineIdByItemId.Select((_, index) => $"(@item_id_{index}, @order_line_id_{index})"));
            var targetLineCte = $"WITH target_lines(item_id, target_order_line_id) AS (VALUES {values})";

            var transferredHuCodes = new List<string>();
            int transferredPalletCount;
            int transferredLineCount;
            using (var count = CreateCommand(connection, $@"
{targetLineCte}
SELECT
    COUNT(DISTINCT pp.id),
    COUNT(DISTINCT dl.id)
FROM production_pallets pp
INNER JOIN doc_lines dl ON dl.id = pp.doc_line_id
INNER JOIN target_lines tl ON tl.item_id = pp.item_id
WHERE pp.prd_doc_id = @source_prd_doc_id
  AND pp.status <> @cancelled_status
  AND dl.doc_id = @source_prd_doc_id;
"))
            {
                AddTargetLineParameters(count, targetOrderLineIdByItemId);
                count.Parameters.AddWithValue("@source_prd_doc_id", sourcePrdDocId);
                count.Parameters.AddWithValue("@cancelled_status", ProductionPalletStatus.Cancelled);
                using var reader = count.ExecuteReader();
                if (!reader.Read())
                {
                    transferredPalletCount = 0;
                    transferredLineCount = 0;
                }
                else
                {
                    transferredPalletCount = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                    transferredLineCount = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                }
            }

            using (var huCommand = CreateCommand(connection, @"
SELECT DISTINCT pp.hu_code
FROM production_pallets pp
WHERE pp.prd_doc_id = @source_prd_doc_id
  AND pp.status <> @cancelled_status
ORDER BY pp.hu_code;
"))
            {
                huCommand.Parameters.AddWithValue("@source_prd_doc_id", sourcePrdDocId);
                huCommand.Parameters.AddWithValue("@cancelled_status", ProductionPalletStatus.Cancelled);
                using var reader = huCommand.ExecuteReader();
                while (reader.Read())
                {
                    transferredHuCodes.Add(reader.GetString(0));
                }
            }

            using (var updatePalletLines = CreateCommand(connection, $@"
{targetLineCte}
UPDATE production_pallet_lines pll
SET order_line_id = tl.target_order_line_id
FROM production_pallets pp,
     target_lines tl
WHERE pp.id = pll.production_pallet_id
  AND pp.prd_doc_id = @source_prd_doc_id
  AND pll.item_id = tl.item_id;
"))
            {
                AddTargetLineParameters(updatePalletLines, targetOrderLineIdByItemId);
                updatePalletLines.Parameters.AddWithValue("@source_prd_doc_id", sourcePrdDocId);
                updatePalletLines.ExecuteNonQuery();
            }

            using (var updatePallets = CreateCommand(connection, $@"
{targetLineCte}
UPDATE production_pallets pp
SET prd_doc_id = @target_prd_doc_id,
    order_id = @target_order_id,
    order_line_id = tl.target_order_line_id
FROM target_lines tl
WHERE pp.prd_doc_id = @source_prd_doc_id
  AND pp.item_id = tl.item_id;
"))
            {
                AddTargetLineParameters(updatePallets, targetOrderLineIdByItemId);
                updatePallets.Parameters.AddWithValue("@source_prd_doc_id", sourcePrdDocId);
                updatePallets.Parameters.AddWithValue("@target_prd_doc_id", targetPrdDocId);
                updatePallets.Parameters.AddWithValue("@target_order_id", targetOrderId);
                updatePallets.ExecuteNonQuery();
            }

            using (var updateDocLines = CreateCommand(connection, $@"
{targetLineCte}
UPDATE doc_lines dl
SET doc_id = @target_prd_doc_id,
    order_line_id = tl.target_order_line_id,
    production_purpose = @target_purpose
FROM target_lines tl
WHERE dl.doc_id = @source_prd_doc_id
  AND dl.item_id = tl.item_id;
"))
            {
                AddTargetLineParameters(updateDocLines, targetOrderLineIdByItemId);
                updateDocLines.Parameters.AddWithValue("@source_prd_doc_id", sourcePrdDocId);
                updateDocLines.Parameters.AddWithValue("@target_prd_doc_id", targetPrdDocId);
                updateDocLines.Parameters.AddWithValue("@target_purpose", ProductionLinePurposeMapper.ToDbValue(ProductionLinePurpose.CustomerOrder));
                updateDocLines.ExecuteNonQuery();
            }

            return new ProductionPalletPlanAdoptionResult
            {
                Success = true,
                Message = "План паллет перенесён на клиентский заказ.",
                SourceOrderId = sourceOrderId,
                TargetOrderId = targetOrderId,
                SourcePrdDocId = sourcePrdDocId,
                TargetPrdDocId = targetPrdDocId,
                TransferredPalletCount = transferredPalletCount,
                TransferredLineCount = transferredLineCount,
                TransferredHuCodes = transferredHuCodes
            };
        });
    }

    public void AssignProductionPalletToPrdDoc(long productionPalletId, long targetPrdDocId)
    {
        WithConnection(connection =>
        {
            using var updatePallet = CreateCommand(connection, @"
UPDATE production_pallets
SET prd_doc_id = @target_prd_doc_id
WHERE id = @production_pallet_id;
");
            updatePallet.Parameters.AddWithValue("@target_prd_doc_id", targetPrdDocId);
            updatePallet.Parameters.AddWithValue("@production_pallet_id", productionPalletId);
            if (updatePallet.ExecuteNonQuery() == 0)
            {
                throw new InvalidOperationException("Паллета не найдена для переноса в отдельный выпуск.");
            }

            using var updateDocLines = CreateCommand(connection, @"
UPDATE doc_lines dl
SET doc_id = @target_prd_doc_id
WHERE dl.id IN (
    SELECT pp.doc_line_id
    FROM production_pallets pp
    WHERE pp.id = @production_pallet_id
    UNION
    SELECT pll.doc_line_id
    FROM production_pallet_lines pll
    WHERE pll.production_pallet_id = @production_pallet_id
);
");
            updateDocLines.Parameters.AddWithValue("@target_prd_doc_id", targetPrdDocId);
            updateDocLines.Parameters.AddWithValue("@production_pallet_id", productionPalletId);
            updateDocLines.ExecuteNonQuery();
            return 0;
        });
    }

    private static void AddTargetLineParameters(NpgsqlCommand command, IReadOnlyDictionary<long, long> targetOrderLineIdByItemId)
    {
        var index = 0;
        foreach (var pair in targetOrderLineIdByItemId)
        {
            command.Parameters.AddWithValue($"@item_id_{index}", pair.Key);
            command.Parameters.AddWithValue($"@order_line_id_{index}", pair.Value);
            index++;
        }
    }

    private ProductionPalletPlanCleanupCounts ClearProductionPalletPlanCore(NpgsqlConnection connection, long docId, bool deletePlanHus)
    {
        var removedPalletCount = 0;
        var removedLineCount = 0;
        using (var countPallets = CreateCommand(connection, @"
SELECT COUNT(*)
FROM production_pallets
WHERE prd_doc_id = @doc_id
  AND status <> @cancelled_status;
"))
        {
            countPallets.Parameters.AddWithValue("@doc_id", docId);
            countPallets.Parameters.AddWithValue("@cancelled_status", ProductionPalletStatus.Cancelled);
            removedPalletCount = Convert.ToInt32(countPallets.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        using (var countLines = CreateCommand(connection, "SELECT COUNT(*) FROM doc_lines WHERE doc_id = @doc_id;"))
        {
            countLines.Parameters.AddWithValue("@doc_id", docId);
            removedLineCount = Convert.ToInt32(countLines.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        if (deletePlanHus)
        {
            using var deleteHus = CreateCommand(connection, @"
DELETE FROM hus h
WHERE COALESCE(h.created_by, '') = @plan_created_by
  AND EXISTS (
      SELECT 1
      FROM production_pallets pp
      WHERE pp.prd_doc_id = @doc_id
        AND UPPER(BTRIM(pp.hu_code)) = UPPER(BTRIM(h.hu_code))
  )
  AND NOT EXISTS (
      SELECT 1
      FROM ledger l
      WHERE UPPER(BTRIM(l.hu_code)) = UPPER(BTRIM(h.hu_code))
  )
  AND NOT EXISTS (
      SELECT 1
      FROM doc_lines dl
      WHERE dl.doc_id <> @doc_id
        AND (
            UPPER(BTRIM(COALESCE(dl.to_hu, ''))) = UPPER(BTRIM(h.hu_code))
            OR UPPER(BTRIM(COALESCE(dl.from_hu, ''))) = UPPER(BTRIM(h.hu_code))
        )
  )
  AND NOT EXISTS (
      SELECT 1
      FROM production_pallets pp
      WHERE pp.prd_doc_id <> @doc_id
        AND UPPER(BTRIM(pp.hu_code)) = UPPER(BTRIM(h.hu_code))
        AND pp.status <> @cancelled_status
  );
");
            deleteHus.Parameters.AddWithValue("@doc_id", docId);
            deleteHus.Parameters.AddWithValue("@plan_created_by", "PRODUCTION-PALLET-PLAN");
            deleteHus.Parameters.AddWithValue("@cancelled_status", ProductionPalletStatus.Cancelled);
            deleteHus.ExecuteNonQuery();
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

        return new ProductionPalletPlanCleanupCounts
        {
            RemovedPalletCount = removedPalletCount,
            RemovedLineCount = removedLineCount
        };
    }

    public double GetFilledProductionPalletQtyByOrderLine(long orderLineId, long? excludePalletId = null)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT COALESCE(SUM(
    CASE
        WHEN EXISTS (
            SELECT 1
            FROM production_pallet_lines pll
            WHERE pll.production_pallet_id = pp.id
              AND pll.order_line_id = @order_line_id
        ) THEN (
            SELECT COALESCE(SUM(pll.planned_qty), 0)
            FROM production_pallet_lines pll
            WHERE pll.production_pallet_id = pp.id
              AND pll.order_line_id = @order_line_id
        )
        WHEN pp.order_line_id = @order_line_id THEN pp.planned_qty
        ELSE 0
    END
), 0)
FROM production_pallets pp
WHERE pp.status = @filled_status
  AND (@exclude_pallet_id::bigint IS NULL OR pp.id <> @exclude_pallet_id::bigint)
  AND (
      pp.order_line_id = @order_line_id
      OR EXISTS (
          SELECT 1
          FROM production_pallet_lines pll
          WHERE pll.production_pallet_id = pp.id
            AND pll.order_line_id = @order_line_id
      )
  );
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

    public IReadOnlyList<ProductionPallet> GetFilledProductionPalletsByItemAndLocation(long itemId, long locationId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, $@"
{ProductionPalletSelectSql}
WHERE p.status = @filled_status
  AND p.to_location_id = @location_id
  AND (
      p.item_id = @item_id
      OR EXISTS (
          SELECT 1
          FROM production_pallet_lines pll
          WHERE pll.production_pallet_id = p.id
            AND pll.item_id = @item_id
      )
  )
ORDER BY p.hu_code, p.id;
");
            command.Parameters.AddWithValue("@filled_status", ProductionPalletStatus.Filled);
            command.Parameters.AddWithValue("@item_id", itemId);
            command.Parameters.AddWithValue("@location_id", locationId);
            using var reader = command.ExecuteReader();
            var pallets = new List<ProductionPallet>();
            while (reader.Read())
            {
                pallets.Add(ReadProductionPallet(reader));
            }

            reader.Close();
            AttachProductionPalletLines(connection, pallets);
            return pallets;
        });
    }

    public IReadOnlyList<FilledProductionPalletStockMetrics> GetFilledProductionPalletStockMetrics()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
WITH filled_components AS (
    SELECT p.id AS pallet_id,
           p.prd_doc_id,
           d.doc_ref AS prd_doc_ref,
           COALESCE(p.order_id, d.order_id) AS order_id,
           o.order_ref,
           o.status AS order_status,
           COALESCE(pll.item_id, p.item_id) AS item_id,
           i.name AS item_name,
           p.hu_code,
           p.to_location_id,
           l.code AS to_location_code,
           p.status,
           p.filled_at,
           COALESCE(pll.planned_qty, p.planned_qty) AS planned_qty
    FROM production_pallets p
    INNER JOIN docs d ON d.id = p.prd_doc_id
    LEFT JOIN orders o ON o.id = COALESCE(p.order_id, d.order_id)
    LEFT JOIN locations l ON l.id = p.to_location_id
    LEFT JOIN production_pallet_lines pll ON pll.production_pallet_id = p.id
    INNER JOIN items i ON i.id = COALESCE(pll.item_id, p.item_id)
    WHERE p.status = @filled_status
      AND p.to_location_id IS NOT NULL
)
SELECT fc.pallet_id,
       fc.prd_doc_id,
       fc.prd_doc_ref,
       fc.order_id,
       fc.order_ref,
       fc.order_status,
       fc.item_id,
       fc.item_name,
       fc.hu_code,
       fc.to_location_id,
       fc.to_location_code,
       fc.planned_qty,
       COALESCE(ledger.qty, 0) AS current_ledger_qty,
       COALESCE(out_hu.qty, 0) AS outbound_by_same_hu_qty,
       COALESCE(out_hu.doc_refs, '') AS outbound_docs_by_same_hu,
       COALESCE(out_order.qty, 0) AS outbound_by_order_item_qty,
       COALESCE(out_order.doc_refs, '') AS outbound_docs_by_order_item,
       fc.status,
       fc.filled_at
FROM filled_components fc
LEFT JOIN LATERAL (
    SELECT COALESCE(SUM(led.qty_delta), 0) AS qty
    FROM ledger led
    WHERE led.item_id = fc.item_id
      AND led.location_id = fc.to_location_id
      AND UPPER(BTRIM(COALESCE(led.hu_code, ''))) = UPPER(BTRIM(fc.hu_code))
) ledger ON TRUE
LEFT JOIN LATERAL (
    SELECT COALESCE(SUM(-led.qty_delta), 0) AS qty,
           STRING_AGG(DISTINCT d.doc_ref, ', ' ORDER BY d.doc_ref) AS doc_refs
    FROM ledger led
    INNER JOIN docs d ON d.id = led.doc_id
    WHERE d.type = @outbound_doc_type
      AND d.status = @closed_doc_status
      AND led.item_id = fc.item_id
      AND led.qty_delta < -@qty_tolerance
      AND UPPER(BTRIM(COALESCE(led.hu_code, ''))) = UPPER(BTRIM(fc.hu_code))
) out_hu ON TRUE
LEFT JOIN LATERAL (
    SELECT COALESCE(SUM(-led.qty_delta), 0) AS qty,
           STRING_AGG(DISTINCT d.doc_ref, ', ' ORDER BY d.doc_ref) AS doc_refs
    FROM ledger led
    INNER JOIN docs d ON d.id = led.doc_id
    WHERE d.type = @outbound_doc_type
      AND d.status = @closed_doc_status
      AND fc.order_id IS NOT NULL
      AND d.order_id = fc.order_id
      AND led.item_id = fc.item_id
      AND led.qty_delta < -@qty_tolerance
) out_order ON TRUE
ORDER BY fc.prd_doc_id, fc.hu_code, fc.item_id;
");
            command.Parameters.AddWithValue("@filled_status", ProductionPalletStatus.Filled);
            command.Parameters.AddWithValue("@outbound_doc_type", DocTypeMapper.ToOpString(DocType.Outbound));
            command.Parameters.AddWithValue("@closed_doc_status", DocTypeMapper.StatusToString(DocStatus.Closed));
            command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
            using var reader = command.ExecuteReader();
            var rows = new List<FilledProductionPalletStockMetrics>();
            while (reader.Read())
            {
                rows.Add(new FilledProductionPalletStockMetrics
                {
                    PalletId = reader.GetInt64(0),
                    PrdDocId = reader.GetInt64(1),
                    PrdDocRef = reader.GetString(2),
                    OrderId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    OrderRef = reader.IsDBNull(4) ? null : reader.GetString(4),
                    OrderStatus = reader.IsDBNull(5) ? null : reader.GetString(5),
                    ItemId = reader.GetInt64(6),
                    ItemName = reader.GetString(7),
                    HuCode = reader.GetString(8),
                    ToLocationId = reader.GetInt64(9),
                    ToLocationCode = reader.IsDBNull(10) ? null : reader.GetString(10),
                    PlannedQty = reader.GetDouble(11),
                    CurrentLedgerQty = reader.GetDouble(12),
                    OutboundBySameHuQty = reader.GetDouble(13),
                    OutboundDocsBySameHu = reader.GetString(14),
                    OutboundByOrderItemQty = reader.GetDouble(15),
                    OutboundDocsByOrderItem = reader.GetString(16),
                    Status = reader.GetString(17),
                    FilledAt = FromDbDate(reader.IsDBNull(18) ? null : reader.GetString(18))
                });
            }

            return rows;
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
SET filled_qty = planned_qty,
    filled_at = COALESCE(filled_at, @filled_at)
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

    public int MarkProductionPalletComponentsFilled(
        long palletId,
        IReadOnlyCollection<long> componentLineIds,
        DateTime filledAt)
    {
        var ids = componentLineIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE production_pallet_lines
SET filled_qty = planned_qty,
    filled_at = COALESCE(filled_at, @filled_at)
WHERE production_pallet_id = @pallet_id
  AND id = ANY(@component_line_ids)
  AND filled_qty < planned_qty;
");
            command.Parameters.AddWithValue("@pallet_id", palletId);
            command.Parameters.AddWithValue("@component_line_ids", ids);
            command.Parameters.AddWithValue("@filled_at", ToDbDate(filledAt));
            return command.ExecuteNonQuery();
        });
    }

    public int CancelProductionPallets(IReadOnlyList<long> palletIds)
    {
        if (palletIds.Count == 0)
        {
            return 0;
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE production_pallets
SET status = @cancelled_status
WHERE id = ANY(@pallet_ids)
  AND status <> @cancelled_status
  AND status <> @filled_status
  AND NOT EXISTS (
      SELECT 1 FROM production_pallet_lines progress
      WHERE progress.production_pallet_id = production_pallets.id
        AND progress.filled_qty > @qty_tolerance
  );
");
            command.Parameters.AddWithValue("@pallet_ids", palletIds.ToArray());
            command.Parameters.AddWithValue("@cancelled_status", ProductionPalletStatus.Cancelled);
            command.Parameters.AddWithValue("@filled_status", ProductionPalletStatus.Filled);
            command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
            return command.ExecuteNonQuery();
        });
    }

    public int CancelProductionPalletsForReadyHuBinding(IReadOnlyList<long> palletIds, string reason, DateTime cancelledAt)
    {
        var ids = (palletIds ?? Array.Empty<long>())
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE production_pallets
SET status = @cancelled_status,
    cancel_reason = @cancel_reason,
    cancelled_at = @cancelled_at
WHERE id = ANY(@pallet_ids)
  AND status = @planned_status
  AND printed_at IS NULL
  AND filled_at IS NULL
  AND NOT EXISTS (
      SELECT 1 FROM production_pallet_lines progress
      WHERE progress.production_pallet_id = production_pallets.id
        AND progress.filled_qty > @qty_tolerance
  );
");
            command.Parameters.AddWithValue("@pallet_ids", ids);
            command.Parameters.AddWithValue("@cancelled_status", ProductionPalletStatus.Cancelled);
            command.Parameters.AddWithValue("@planned_status", ProductionPalletStatus.Planned);
            command.Parameters.AddWithValue("@cancel_reason", string.IsNullOrWhiteSpace(reason) ? DBNull.Value : reason.Trim());
            command.Parameters.AddWithValue("@cancelled_at", ToDbDate(cancelledAt));
            command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
            return command.ExecuteNonQuery();
        });
    }

    public bool HasUnsafeMarkingForProductionPalletReplacement(
        long orderId,
        long orderLineId,
        long itemId,
        IReadOnlyCollection<long> docLineIds)
    {
        var activeDocLineIds = (docLineIds ?? Array.Empty<long>())
            .Where(id => id > 0)
            .Distinct()
            .ToArray();

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT 1
FROM orders o
WHERE o.id = @order_id
  AND (
      UPPER(BTRIM(COALESCE(o.marking_status, ''))) = @printed_order_status
      OR o.marking_excel_generated_at IS NOT NULL
      OR o.marking_printed_at IS NOT NULL
  )
LIMIT 1;
");
            command.Parameters.AddWithValue("@order_id", orderId);
            command.Parameters.AddWithValue("@printed_order_status", MarkingStatusMapper.ToString(MarkingStatus.Printed));
            if (command.ExecuteScalar() != null)
            {
                return true;
            }

            using var markingOrderCommand = CreateCommand(connection, @"
SELECT 1
FROM marking_order mo
WHERE (mo.order_id = @order_id OR mo.source_order_id = @order_id)
  AND (mo.item_id IS NULL OR mo.item_id = @item_id)
  AND COALESCE(mo.status, '') NOT IN (@cancelled_status, @failed_status)
LIMIT 1;
");
            markingOrderCommand.Parameters.AddWithValue("@order_id", orderId);
            markingOrderCommand.Parameters.AddWithValue("@item_id", itemId);
            markingOrderCommand.Parameters.AddWithValue("@cancelled_status", MarkingOrderStatus.Cancelled);
            markingOrderCommand.Parameters.AddWithValue("@failed_status", MarkingOrderStatus.Failed);
            if (markingOrderCommand.ExecuteScalar() != null)
            {
                return true;
            }

            if (activeDocLineIds.Length == 0)
            {
                return false;
            }

            using var codeCommand = CreateCommand(connection, @"
SELECT 1
FROM marking_code c
WHERE c.receipt_line_id = ANY(@doc_line_ids)
  AND c.status <> @voided_status
LIMIT 1;
");
            codeCommand.Parameters.AddWithValue("@doc_line_ids", activeDocLineIds);
            codeCommand.Parameters.AddWithValue("@voided_status", MarkingCodeStatus.Voided);
            return codeCommand.ExecuteScalar() != null;
        });
    }

    public int RemoveDocLinesForProductionPallets(IReadOnlyCollection<long> productionPalletIds)
    {
        var ids = productionPalletIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
WITH target_doc_lines AS (
    SELECT DISTINCT pll.doc_line_id AS id
    FROM production_pallet_lines pll
    WHERE pll.production_pallet_id = ANY(@pallet_ids)
    UNION
    SELECT DISTINCT pp.doc_line_id AS id
    FROM production_pallets pp
    WHERE pp.id = ANY(@pallet_ids)
      AND pp.doc_line_id IS NOT NULL
)
DELETE FROM doc_lines dl
USING target_doc_lines target
WHERE dl.id = target.id
  AND NOT EXISTS (
      SELECT 1
      FROM production_pallets pp
      WHERE pp.doc_line_id = dl.id
  )
  AND NOT EXISTS (
      SELECT 1
      FROM production_pallet_lines pll
      WHERE pll.doc_line_id = dl.id
  );
");
            command.Parameters.AddWithValue("@pallet_ids", ids);
            return command.ExecuteNonQuery();
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
  AND status = @planned_status
  AND NOT EXISTS (
      SELECT 1 FROM production_pallet_lines progress
      WHERE progress.production_pallet_id = production_pallets.id
        AND progress.filled_qty > @qty_tolerance
  );

UPDATE doc_lines dl
SET to_hu = @hu_code
FROM production_pallet_lines pll
INNER JOIN production_pallets pp ON pp.id = pll.production_pallet_id
WHERE pll.doc_line_id = dl.id
  AND pp.id = @id
  AND pp.status = @planned_status
  AND NOT EXISTS (
      SELECT 1 FROM production_pallet_lines progress
      WHERE progress.production_pallet_id = pp.id
        AND progress.filled_qty > @qty_tolerance
  );
");
            command.Parameters.AddWithValue("@id", palletId);
            command.Parameters.AddWithValue("@hu_code", huCode.Trim());
            command.Parameters.AddWithValue("@planned_status", ProductionPalletStatus.Planned);
            command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void ReassignOpenProductionPalletsByHu(
        long sourceOrderId,
        long targetOrderId,
        long targetOrderLineId,
        long itemId,
        IReadOnlyList<string> huCodes)
    {
        if (huCodes.Count == 0)
        {
            return;
        }

        var normalizedHuCodes = huCodes
            .Select(code => string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
        if (normalizedHuCodes.Length == 0)
        {
            return;
        }

        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE production_pallets pp
SET order_id = @target_order_id,
    order_line_id = @target_order_line_id
FROM docs d
WHERE d.id = pp.prd_doc_id
  AND d.order_id = @source_order_id
  AND d.type = @prd_type
  AND d.status <> @closed_status
  AND pp.item_id = @item_id
  AND pp.status IN (@planned_status, @printed_status)
  AND NOT EXISTS (
      SELECT 1 FROM production_pallet_lines progress
      WHERE progress.production_pallet_id = pp.id
        AND progress.filled_qty > @qty_tolerance
  )
  AND UPPER(BTRIM(pp.hu_code)) = ANY(@hu_codes);

UPDATE production_pallet_lines pll
SET order_line_id = @target_order_line_id
FROM production_pallets pp
INNER JOIN docs d ON d.id = pp.prd_doc_id
WHERE pll.production_pallet_id = pp.id
  AND d.order_id = @source_order_id
  AND d.type = @prd_type
  AND d.status <> @closed_status
  AND pp.item_id = @item_id
  AND pp.status IN (@planned_status, @printed_status)
  AND NOT EXISTS (
      SELECT 1 FROM production_pallet_lines progress
      WHERE progress.production_pallet_id = pp.id
        AND progress.filled_qty > @qty_tolerance
  )
  AND UPPER(BTRIM(pp.hu_code)) = ANY(@hu_codes);

UPDATE doc_lines dl
SET order_line_id = @target_order_line_id
FROM production_pallets pp
INNER JOIN docs d ON d.id = pp.prd_doc_id
WHERE dl.doc_id = pp.prd_doc_id
  AND UPPER(BTRIM(dl.to_hu)) = UPPER(BTRIM(pp.hu_code))
  AND d.order_id = @source_order_id
  AND d.type = @prd_type
  AND d.status <> @closed_status
  AND pp.item_id = @item_id
  AND pp.status IN (@planned_status, @printed_status)
  AND NOT EXISTS (
      SELECT 1 FROM production_pallet_lines progress
      WHERE progress.production_pallet_id = pp.id
        AND progress.filled_qty > @qty_tolerance
  )
  AND UPPER(BTRIM(pp.hu_code)) = ANY(@hu_codes);
");
            command.Parameters.AddWithValue("@source_order_id", sourceOrderId);
            command.Parameters.AddWithValue("@target_order_id", targetOrderId);
            command.Parameters.AddWithValue("@target_order_line_id", targetOrderLineId);
            command.Parameters.AddWithValue("@item_id", itemId);
            command.Parameters.AddWithValue("@prd_type", DocTypeMapper.ToOpString(DocType.ProductionReceipt));
            command.Parameters.AddWithValue("@closed_status", DocTypeMapper.StatusToString(DocStatus.Closed));
            command.Parameters.AddWithValue("@planned_status", ProductionPalletStatus.Planned);
            command.Parameters.AddWithValue("@printed_status", ProductionPalletStatus.Printed);
            command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
            command.Parameters.AddWithValue("@hu_codes", normalizedHuCodes);
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

    public int MarkProductionPalletsPrinted(long orderId, IReadOnlyCollection<long> palletIds, DateTime printedAt)
    {
        var ids = palletIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

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
  AND pp.id = ANY(@pallet_ids)
  AND pp.status = @planned_status;
");
            command.Parameters.AddWithValue("@order_id", orderId);
            command.Parameters.AddWithValue("@doc_type", DocTypeMapper.ToOpString(DocType.ProductionReceipt));
            command.Parameters.AddWithValue("@planned_status", ProductionPalletStatus.Planned);
            command.Parameters.AddWithValue("@printed_status", ProductionPalletStatus.Printed);
            command.Parameters.AddWithValue("@printed_at", ToDbDate(printedAt));
            command.Parameters.AddWithValue("@pallet_ids", ids);
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
            var orderBy = OrderPageSortSql.BuildEffectiveStatusOrderBy("orders_read_model.status", includeCancelledMerged: false);
            var orderRefOrderBy = OrderPageSortSql.BuildOrderRefDescendingOrderBy("orders_read_model.order_ref");
            using var command = CreateCommand(connection, $@"
SELECT *
FROM (
{BuildOrderSelectSql("SELECT o.id FROM orders o")}
) orders_read_model
ORDER BY {orderBy},
orders_read_model.created_at DESC,
{orderRefOrderBy}");
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

    public IReadOnlyList<OutboundPickingOrderRow> GetTsdOutboundOrderRows()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, TsdOutboundOrderRowsSql);
            AddTsdOutboundOrderRowsParameters(command);
            using var reader = command.ExecuteReader();
            var rows = new List<OutboundPickingOrderRow>();
            while (reader.Read())
            {
                rows.Add(new OutboundPickingOrderRow
                {
                    OrderId = reader.GetInt64(0),
                    OrderRef = reader.GetString(1),
                    PartnerName = reader.GetString(2),
                    Status = reader.GetString(3),
                    ExpectedHuCount = reader.GetInt32(4),
                    PickedHuCount = reader.GetInt32(5),
                    OrderedQty = reader.GetDouble(6),
                    ShippedQty = reader.GetDouble(7),
                    RemainingQty = reader.GetDouble(8),
                    ScannedQty = reader.GetDouble(9),
                    IsClosed = reader.GetBoolean(10),
                    OperationFingerprint = BuildTsdOutboundOperationFingerprint(reader.GetString(11))
                });
            }

            return rows;
        });
    }

    public IReadOnlyList<Order> GetOrdersPage(
        bool includeInternal,
        string? query,
        int limit,
        int offset,
        bool includeCancelledMerged = false)
    {
        return WithConnection(connection =>
        {
            var normalized = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
            var effectiveOrderBy = OrderPageSortSql.BuildEffectiveStatusOrderBy("eo.effective_status", includeCancelledMerged);
            var pagedOrderBy = OrderPageSortSql.BuildEffectiveStatusOrderBy("paged_orders.status", includeCancelledMerged);
            var effectiveOrderRefOrderBy = OrderPageSortSql.BuildOrderRefDescendingOrderBy("eo.order_ref");
            var pagedOrderRefOrderBy = OrderPageSortSql.BuildOrderRefDescendingOrderBy("paged_orders.order_ref");
            var pageOrderScopeSql = $@"
WITH candidate_orders AS (
    SELECT o.id,
           o.order_ref,
           o.order_type,
           o.status AS persisted_status,
           o.created_at,
           COALESCE(o.marking_status, 'NOT_REQUIRED') AS marking_status
    FROM orders o
    LEFT JOIN partners p ON p.id = o.partner_id
    WHERE (@include_internal OR o.order_type = @customer_order_type)
      AND (@include_cancelled_merged OR o.status NOT IN (@cancelled_status, @merged_status))
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
open_production_activity_by_order AS (
    SELECT activity.order_id,
           TRUE AS has_open_production_receipt
    FROM (
        SELECT d.order_id
        FROM docs d
        INNER JOIN candidate_orders co ON co.id = d.order_id
        WHERE d.type = 'PRODUCTION_RECEIPT'
          AND d.status <> 'CLOSED'
          AND d.order_id IS NOT NULL
        UNION
        SELECT col.order_id
        FROM docs d
        INNER JOIN doc_lines dl ON dl.doc_id = d.id
        INNER JOIN candidate_order_lines col ON col.id = dl.order_line_id
        WHERE d.type = 'PRODUCTION_RECEIPT'
          AND d.status <> 'CLOSED'
    ) activity
    GROUP BY activity.order_id
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
           COUNT(olm.order_line_id) FILTER (WHERE olm.qty_ordered > 0.000001) AS demand_line_count,
           COALESCE(BOOL_AND(olm.qty_shipped + 0.000001 >= olm.qty_ordered), FALSE) AS fully_shipped,
           COALESCE(BOOL_AND(olm.qty_customer_ready + 0.000001 >= olm.qty_ordered), FALSE) AS fully_customer_ready,
           COALESCE(BOOL_AND(olm.qty_produced_total + 0.000001 >= olm.qty_ordered), FALSE) AS fully_produced,
           COALESCE(BOOL_AND(olm.qty_produced_total + 0.000001 >= olm.qty_ordered) FILTER (WHERE olm.qty_ordered > 0.000001), FALSE) AS fully_demand_produced,
           COALESCE(BOOL_OR(olm.qty_produced_total > 0.000001), FALSE) AS any_produced,
           COALESCE(MAX(production_totals.qty_received), 0) > 0.000001 AS any_posted_production
    FROM candidate_orders co
    LEFT JOIN order_line_metrics olm ON olm.order_id = co.id
    LEFT JOIN production_totals_by_order production_totals ON production_totals.order_id = co.id
    GROUP BY co.id
),
pallet_source AS (
    SELECT COALESCE(pp.order_id, MAX(ol.order_id), d.order_id) AS order_id,
           pp.id,
           pp.status
    FROM production_pallets pp
    INNER JOIN docs d ON d.id = pp.prd_doc_id
    LEFT JOIN production_pallet_lines pll ON pll.production_pallet_id = pp.id
    LEFT JOIN order_lines ol ON ol.id = pll.order_line_id
    WHERE d.type = 'PRODUCTION_RECEIPT'
    GROUP BY pp.id,
             pp.order_id,
             d.order_id,
             pp.status
),
pallet_summary AS (
    SELECT ps.order_id,
           COUNT(*) FILTER (WHERE ps.status IN ('PLANNED', 'PRINTED', 'FILLED'))::int AS active_pallet_count
    FROM pallet_source ps
    INNER JOIN candidate_orders co ON co.id = ps.order_id
    GROUP BY ps.order_id
),
effective_orders AS (
    SELECT co.id,
           co.order_ref,
           co.created_at,
           CASE
               WHEN co.persisted_status = 'CANCELLED' THEN 'CANCELLED'
               WHEN co.persisted_status = 'MERGED' THEN 'MERGED'
               WHEN co.order_type = 'INTERNAL' THEN CASE
                   WHEN COALESCE(ss.any_produced, FALSE)
                        AND COALESCE(ss.demand_line_count, 0) > 0
                        AND COALESCE(ss.fully_demand_produced, FALSE) THEN 'SHIPPED'
                   WHEN co.persisted_status = 'DRAFT'
                        AND NOT COALESCE(ss.any_produced, FALSE)
                        AND NOT COALESCE(opa.has_open_production_receipt, FALSE)
                        AND COALESCE(ps.active_pallet_count, 0) = 0
                        AND UPPER(BTRIM(COALESCE(co.marking_status, ''))) NOT IN ('PRINTED', 'EXCEL_GENERATED') THEN 'DRAFT'
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
    LEFT JOIN open_production_activity_by_order opa ON opa.order_id = co.id
    LEFT JOIN pallet_summary ps ON ps.order_id = co.id
)
SELECT eo.id
FROM effective_orders eo
ORDER BY {effectiveOrderBy},
{effectiveOrderRefOrderBy},
eo.created_at DESC,
eo.id DESC
LIMIT @limit OFFSET @offset";
            using var command = CreateCommand(connection, $@"
SELECT *
FROM (
{BuildOrderSelectSql(pageOrderScopeSql)}
) paged_orders
ORDER BY {pagedOrderBy},
{pagedOrderRefOrderBy},
paged_orders.created_at DESC,
paged_orders.id DESC");
            AddOrderSelectParameters(command);
            command.Parameters.AddWithValue("@include_internal", includeInternal);
            command.Parameters.AddWithValue("@include_cancelled_merged", includeCancelledMerged);
            command.Parameters.AddWithValue("@cancelled_status", OrderStatusMapper.StatusToString(OrderStatus.Cancelled));
            command.Parameters.AddWithValue("@merged_status", OrderStatusMapper.StatusToString(OrderStatus.Merged));
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

    public IReadOnlyList<Order> GetOperationOrderCandidates(DocType docType, string? query, int limit)
    {
        if (docType is not (DocType.ProductionReceipt or DocType.Outbound))
        {
            return Array.Empty<Order>();
        }

        return WithConnection(connection =>
        {
            var normalized = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
            var effectiveLimit = Math.Clamp(limit, 1, 50);
            var requireCustomerOrders = docType == DocType.Outbound;
            var requireReceiptRemaining = docType == DocType.ProductionReceipt;
            var requireShipmentRemaining = docType == DocType.Outbound;
            var scopeSql = OperationOrderCandidateSql.BuildOrderScopeSql(
                requireCustomerOrders,
                requireReceiptRemaining,
                requireShipmentRemaining);
            using var command = CreateCommand(connection, $@"
SELECT *
FROM (
{BuildOrderSelectSql(scopeSql)}
) candidate_orders
ORDER BY candidate_orders.created_at DESC,
         candidate_orders.order_ref DESC");
            AddOrderSelectParameters(command);
            command.Parameters.AddWithValue("@require_customer_orders", requireCustomerOrders);
            command.Parameters.AddWithValue("@require_receipt_remaining", requireReceiptRemaining);
            command.Parameters.AddWithValue("@require_shipment_remaining", requireShipmentRemaining);
            command.Parameters.AddWithValue("@customer_order_type", OrderStatusMapper.TypeToString(OrderType.Customer));
            command.Parameters.AddWithValue("@shipped_status", OrderStatusMapper.StatusToString(OrderStatus.Shipped));
            command.Parameters.AddWithValue("@cancelled_status", OrderStatusMapper.StatusToString(OrderStatus.Cancelled));
            command.Parameters.AddWithValue("@merged_status", OrderStatusMapper.StatusToString(OrderStatus.Merged));
            command.Parameters.Add("@query", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(normalized) ? DBNull.Value : normalized;
            command.Parameters.Add("@query_pattern", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(normalized) ? DBNull.Value : $"%{normalized}%";
            command.Parameters.AddWithValue("@limit", effectiveLimit);
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
           COUNT(olm.order_line_id) FILTER (WHERE olm.qty_ordered > 0.000001) AS demand_line_count,
    FROM production_pallet_lines pll
    INNER JOIN production_pallets pp ON pp.id = pll.production_pallet_id
    INNER JOIN order_line_scope ols ON ols.id = pll.order_line_id
           COALESCE(BOOL_AND(olm.qty_produced_total + 0.000001 >= olm.qty_ordered) FILTER (WHERE olm.qty_ordered > 0.000001), FALSE) AS fully_demand_produced,
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
           COALESCE(BOOL_OR(qty_ordered - (qty_produced + qty_reserved) > 0.000001), FALSE) AS has_receipt_remaining,
           COALESCE(SUM(GREATEST(0, qty_ordered)), 0)::double precision AS shipment_ordered_qty,
           COALESCE(SUM(GREATEST(0, qty_shipped)), 0)::double precision AS shipment_shipped_qty,
           COALESCE(SUM(GREATEST(0, qty_ordered - qty_shipped)), 0)::double precision AS shipment_remaining_qty
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
       COALESCE(pallet_summary.filled_qty, 0) AS filled_qty,
       COALESCE(line_summary.shipment_ordered_qty, 0) AS shipment_ordered_qty,
       COALESCE(line_summary.shipment_shipped_qty, 0) AS shipment_shipped_qty,
       COALESCE(line_summary.shipment_remaining_qty, 0) AS shipment_remaining_qty
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
                    ShipmentOrderedQty = reader.GetDouble(7),
                    ShipmentShippedQty = reader.GetDouble(8),
                    ShipmentRemainingQty = reader.GetDouble(9),
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

    public IReadOnlyList<FullyShippedCustomerOrderStatusCandidate> GetFullyShippedCustomerOrderStatusCandidates()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
WITH candidate_orders AS (
    SELECT o.id,
           o.order_ref,
           o.status AS persisted_status
    FROM orders o
    WHERE o.order_type = @customer_order_type
      AND o.status NOT IN (@draft_order_status, @shipped_order_status, @cancelled_order_status, @merged_order_status)
),
line_totals AS (
    SELECT co.id AS order_id,
           co.order_ref,
           co.persisted_status,
           ol.id AS order_line_id,
           GREATEST(0, ol.qty_ordered) AS qty_ordered,
           COALESCE(shipped.qty_shipped, 0) AS qty_shipped
    FROM candidate_orders co
    INNER JOIN order_lines ol ON ol.order_id = co.id
    LEFT JOIN LATERAL (
        SELECT SUM(dl.qty) AS qty_shipped
        FROM doc_lines dl
        INNER JOIN docs d ON d.id = dl.doc_id
        WHERE d.order_id = co.id
          AND d.status = @closed_doc_status
          AND d.type = @outbound_doc_type
          AND dl.order_line_id = ol.id
          AND dl.qty > 0
          AND NOT EXISTS (
              SELECT 1
              FROM doc_lines newer
              WHERE newer.replaces_line_id = dl.id
          )
    ) shipped ON TRUE
)
SELECT order_id,
       order_ref,
       persisted_status,
       SUM(qty_ordered) AS total_ordered_qty,
       SUM(qty_shipped) AS total_shipped_qty
FROM line_totals
GROUP BY order_id,
         order_ref,
         persisted_status
HAVING COUNT(*) FILTER (WHERE qty_ordered > @qty_tolerance) > 0
   AND BOOL_AND(qty_shipped + @qty_tolerance >= qty_ordered)
ORDER BY order_ref,
         order_id;
");
            command.Parameters.AddWithValue("@customer_order_type", OrderStatusMapper.TypeToString(OrderType.Customer));
            command.Parameters.AddWithValue("@draft_order_status", OrderStatusMapper.StatusToString(OrderStatus.Draft));
            command.Parameters.AddWithValue("@shipped_order_status", OrderStatusMapper.StatusToString(OrderStatus.Shipped));
            command.Parameters.AddWithValue("@cancelled_order_status", OrderStatusMapper.StatusToString(OrderStatus.Cancelled));
            command.Parameters.AddWithValue("@merged_order_status", OrderStatusMapper.StatusToString(OrderStatus.Merged));
            command.Parameters.AddWithValue("@closed_doc_status", DocTypeMapper.StatusToString(DocStatus.Closed));
            command.Parameters.AddWithValue("@outbound_doc_type", DocTypeMapper.ToOpString(DocType.Outbound));
            command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);

            using var reader = command.ExecuteReader();
            var rows = new List<FullyShippedCustomerOrderStatusCandidate>();
            while (reader.Read())
            {
                rows.Add(new FullyShippedCustomerOrderStatusCandidate
                {
                    OrderId = reader.GetInt64(0),
                    OrderRef = reader.GetString(1),
                    OldStatus = OrderStatusMapper.StatusFromString(reader.GetString(2)) ?? OrderStatus.InProgress,
                    TotalOrderedQty = reader.GetDouble(3),
                    TotalShippedQty = reader.GetDouble(4)
                });
            }

            return rows;
        });
    }

    public IReadOnlyList<OverShippedOrderDiagnosticItem> GetOverShippedOrderDiagnostics()
    {
        return WithConnection<IReadOnlyList<OverShippedOrderDiagnosticItem>>(connection =>
        {
            var rows = new Dictionary<(long OrderId, long ItemId), OverShippedOrderDiagnosticItem>();
            using (var command = CreateCommand(connection, $@"
{BuildOverShippedOrderDiagnosticsCte()}
SELECT order_id,
       order_ref,
       item_id,
       item_name,
       qty_ordered,
       shipped_by_api_read_model,
       shipped_by_closed_outbound,
       shipped_by_ledger,
       GREATEST(0, GREATEST(shipped_by_api_read_model, shipped_by_closed_outbound, shipped_by_ledger) - qty_ordered) AS over_shipped_qty
FROM candidates
ORDER BY order_ref,
         item_name,
         item_id;
"))
            {
                AddOverShippedOrderDiagnosticsParameters(command);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var row = new OverShippedOrderDiagnosticItem
                    {
                        OrderId = reader.GetInt64(0),
                        OrderRef = reader.GetString(1),
                        ItemId = reader.GetInt64(2),
                        ItemName = reader.GetString(3),
                        QtyOrdered = reader.GetDouble(4),
                        ShippedByApiReadModel = reader.GetDouble(5),
                        ShippedByClosedOutbound = reader.GetDouble(6),
                        ShippedByLedger = reader.GetDouble(7),
                        OverShippedQty = reader.GetDouble(8)
                    };
                    rows[(row.OrderId, row.ItemId)] = row;
                }
            }

            if (rows.Count == 0)
            {
                return Array.Empty<OverShippedOrderDiagnosticItem>();
            }

            var outboundByKey = rows.Keys.ToDictionary(key => key, _ => new List<OverShippedOutboundDocLine>());
            using (var command = CreateCommand(connection, $@"
{BuildOverShippedOrderDiagnosticsCte()}
SELECT c.order_id,
       c.item_id,
       d.id AS doc_id,
       d.doc_ref,
       d.status,
       d.closed_at,
       dl.id AS doc_line_id,
       dl.qty,
       dl.from_hu,
       dl.order_line_id
FROM candidates c
INNER JOIN docs d ON d.order_id = c.order_id
                 AND d.type = @outbound_doc_type
                 AND d.status = @closed_doc_status
INNER JOIN active_doc_lines dl ON dl.doc_id = d.id
INNER JOIN order_lines ol ON ol.id = dl.order_line_id
                         AND ol.order_id = c.order_id
                         AND ol.item_id = c.item_id
ORDER BY c.order_ref,
         d.id,
         dl.id;
"))
            {
                AddOverShippedOrderDiagnosticsParameters(command);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var key = (reader.GetInt64(0), reader.GetInt64(1));
                    if (!outboundByKey.TryGetValue(key, out var bucket))
                    {
                        continue;
                    }

                    bucket.Add(new OverShippedOutboundDocLine
                    {
                        DocId = reader.GetInt64(2),
                        DocRef = reader.GetString(3),
                        Status = reader.GetString(4),
                        ClosedAt = reader.IsDBNull(5) ? null : FromDbDate(reader.GetString(5)),
                        DocLineId = reader.GetInt64(6),
                        Qty = reader.GetDouble(7),
                        FromHu = reader.IsDBNull(8) ? null : reader.GetString(8),
                        OrderLineId = reader.IsDBNull(9) ? null : reader.GetInt64(9)
                    });
                }
            }

            var ledgerByKey = rows.Keys.ToDictionary(key => key, _ => new List<OverShippedLedgerEntry>());
            using (var command = CreateCommand(connection, $@"
{BuildOverShippedOrderDiagnosticsCte()}
SELECT c.order_id,
       c.item_id,
       l.id AS ledger_id,
       l.doc_id,
       l.item_id AS ledger_item_id,
       l.hu_code,
       l.qty_delta
FROM candidates c
INNER JOIN docs d ON d.order_id = c.order_id
                 AND d.type = @outbound_doc_type
                 AND d.status = @closed_doc_status
INNER JOIN ledger l ON l.doc_id = d.id
                   AND l.item_id = c.item_id
ORDER BY c.order_ref,
         l.doc_id,
         l.id;
"))
            {
                AddOverShippedOrderDiagnosticsParameters(command);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var key = (reader.GetInt64(0), reader.GetInt64(1));
                    if (!ledgerByKey.TryGetValue(key, out var bucket))
                    {
                        continue;
                    }

                    bucket.Add(new OverShippedLedgerEntry
                    {
                        LedgerId = reader.GetInt64(2),
                        DocId = reader.GetInt64(3),
                        ItemId = reader.GetInt64(4),
                        HuCode = reader.IsDBNull(5) ? null : reader.GetString(5),
                        QtyDelta = reader.GetDouble(6)
                    });
                }
            }

            return rows
                .Select(pair =>
                {
                    var key = pair.Key;
                    var row = pair.Value;
                    return new OverShippedOrderDiagnosticItem
                    {
                        OrderId = row.OrderId,
                        OrderRef = row.OrderRef,
                        ItemId = row.ItemId,
                        ItemName = row.ItemName,
                        QtyOrdered = row.QtyOrdered,
                        ShippedByApiReadModel = row.ShippedByApiReadModel,
                        ShippedByClosedOutbound = row.ShippedByClosedOutbound,
                        ShippedByLedger = row.ShippedByLedger,
                        OverShippedQty = row.OverShippedQty,
                        OutboundDocs = outboundByKey.TryGetValue(key, out var outboundDocs) ? outboundDocs : Array.Empty<OverShippedOutboundDocLine>(),
                        LedgerEntries = ledgerByKey.TryGetValue(key, out var ledgerEntries) ? ledgerEntries : Array.Empty<OverShippedLedgerEntry>(),
                        Recommendation = BuildOverShippedRecommendation(row)
                    };
                })
                .ToList();
        });
    }

    public IReadOnlyList<ProductionPlanConsistencyDiagnosticItem> GetProductionPlanConsistencyDiagnostics()
    {
        return WithConnection<IReadOnlyList<ProductionPlanConsistencyDiagnosticItem>>(connection =>
        {
            var rows = new Dictionary<(long OrderId, long ItemId), ProductionPlanConsistencyDiagnosticItem>();
            using (var command = CreateCommand(connection, $@"
{BuildProductionPlanConsistencyDiagnosticsCte()}
SELECT order_id,
       order_ref,
       order_type,
       order_status,
       item_id,
       item_name,
       order_qty,
       open_prd_doc_qty,
       closed_prd_doc_qty,
       prd_doc_qty,
       open_pallet_planned_qty,
       pallet_planned_qty,
       pallet_filled_qty,
       ledger_closed_prd_qty,
       ledger_open_prd_qty,
       ledger_prd_qty,
       severity,
       problem_code
FROM candidates
ORDER BY order_ref,
         item_name,
         item_id;
"))
            {
                AddProductionPlanConsistencyDiagnosticsParameters(command);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var row = new ProductionPlanConsistencyDiagnosticItem
                    {
                        OrderId = reader.GetInt64(0),
                        OrderRef = reader.GetString(1),
                        OrderType = reader.GetString(2),
                        OrderStatus = reader.GetString(3),
                        ItemId = reader.GetInt64(4),
                        ItemName = reader.GetString(5),
                        OrderQty = reader.GetDouble(6),
                        OpenPrdDocQty = reader.GetDouble(7),
                        ClosedPrdDocQty = reader.GetDouble(8),
                        PrdDocQty = reader.GetDouble(9),
                        OpenPalletPlannedQty = reader.GetDouble(10),
                        PalletPlannedQty = reader.GetDouble(11),
                        PalletFilledQty = reader.GetDouble(12),
                        LedgerClosedPrdQty = reader.GetDouble(13),
                        LedgerOpenPrdQty = reader.GetDouble(14),
                        LedgerPrdQty = reader.GetDouble(15),
                        Severity = reader.GetString(16),
                        ProblemCode = reader.GetString(17)
                    };
                    rows[(row.OrderId, row.ItemId)] = row;
                }
            }

            if (rows.Count == 0)
            {
                return Array.Empty<ProductionPlanConsistencyDiagnosticItem>();
            }

            var palletsByKey = rows.Keys.ToDictionary(key => key, _ => new List<ProductionPlanConsistencyPalletRow>());
            using (var command = CreateCommand(connection, $@"
{BuildProductionPlanConsistencyDiagnosticsCte()}
SELECT c.order_id,
       c.item_id,
       p.pallet_id,
       p.prd_doc_id,
       p.prd_doc_ref,
       p.doc_line_id,
       p.order_line_id,
       p.line_item_id,
       p.hu_code,
       p.status,
       p.planned_qty,
       p.filled_qty
FROM candidates c
INNER JOIN pallet_rows p ON p.order_id = c.order_id
                         AND p.line_item_id = c.item_id
ORDER BY c.order_ref,
         p.prd_doc_id,
         p.pallet_id,
         p.doc_line_id;
"))
            {
                AddProductionPlanConsistencyDiagnosticsParameters(command);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var key = (reader.GetInt64(0), reader.GetInt64(1));
                    if (!palletsByKey.TryGetValue(key, out var bucket))
                    {
                        continue;
                    }

                    bucket.Add(new ProductionPlanConsistencyPalletRow
                    {
                        PalletId = reader.GetInt64(2),
                        PrdDocId = reader.GetInt64(3),
                        PrdDocRef = reader.IsDBNull(4) ? null : reader.GetString(4),
                        DocLineId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                        OrderLineId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                        ItemId = reader.GetInt64(7),
                        HuCode = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                        Status = reader.GetString(9),
                        PlannedQty = reader.GetDouble(10),
                        FilledQty = reader.GetDouble(11)
                    });
                }
            }

            var prdDocsByKey = rows.Keys.ToDictionary(key => key, _ => new List<ProductionPlanConsistencyPrdDocRow>());
            using (var command = CreateCommand(connection, $@"
{BuildProductionPlanConsistencyDiagnosticsCte()}
SELECT c.order_id,
       c.item_id,
       d.id,
       d.doc_ref,
       d.status,
       d.closed_at,
       dl.id AS doc_line_id,
       dl.order_line_id,
       dl.item_id AS line_item_id,
       dl.qty
FROM candidates c
INNER JOIN docs d ON d.type = @prd_doc_type
INNER JOIN active_doc_lines dl ON dl.doc_id = d.id
                              AND dl.item_id = c.item_id
LEFT JOIN order_lines ol ON ol.id = dl.order_line_id
WHERE COALESCE(ol.order_id, d.order_id) = c.order_id
ORDER BY c.order_ref,
         d.id,
         dl.id;
"))
            {
                AddProductionPlanConsistencyDiagnosticsParameters(command);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var key = (reader.GetInt64(0), reader.GetInt64(1));
                    if (!prdDocsByKey.TryGetValue(key, out var bucket))
                    {
                        continue;
                    }

                    bucket.Add(new ProductionPlanConsistencyPrdDocRow
                    {
                        DocId = reader.GetInt64(2),
                        DocRef = reader.GetString(3),
                        Status = reader.GetString(4),
                        ClosedAt = reader.IsDBNull(5) ? null : FromDbDate(reader.GetString(5)),
                        DocLineId = reader.GetInt64(6),
                        OrderLineId = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                        ItemId = reader.GetInt64(8),
                        Qty = reader.GetDouble(9)
                    });
                }
            }

            return rows
                .Select(pair =>
                {
                    var key = pair.Key;
                    var row = pair.Value;
                    return new ProductionPlanConsistencyDiagnosticItem
                    {
                        OrderId = row.OrderId,
                        OrderRef = row.OrderRef,
                        OrderType = row.OrderType,
                        OrderStatus = row.OrderStatus,
                        ItemId = row.ItemId,
                        ItemName = row.ItemName,
                        OrderQty = row.OrderQty,
                        OpenPrdDocQty = row.OpenPrdDocQty,
                        ClosedPrdDocQty = row.ClosedPrdDocQty,
                        PrdDocQty = row.PrdDocQty,
                        OpenPalletPlannedQty = row.OpenPalletPlannedQty,
                        PalletPlannedQty = row.PalletPlannedQty,
                        PalletFilledQty = row.PalletFilledQty,
                        LedgerClosedPrdQty = row.LedgerClosedPrdQty,
                        LedgerOpenPrdQty = row.LedgerOpenPrdQty,
                        LedgerPrdQty = row.LedgerPrdQty,
                        Severity = row.Severity,
                        ProblemCode = row.ProblemCode,
                        Recommendation = BuildProductionPlanConsistencyRecommendation(row.ProblemCode),
                        Pallets = palletsByKey.TryGetValue(key, out var pallets) ? pallets : Array.Empty<ProductionPlanConsistencyPalletRow>(),
                        PrdDocs = prdDocsByKey.TryGetValue(key, out var prdDocs) ? prdDocs : Array.Empty<ProductionPlanConsistencyPrdDocRow>()
                    };
                })
                .ToList();
        });
    }

    private static string BuildProductionPlanConsistencyDiagnosticsCte()
    {
        return @"
WITH active_doc_lines AS (
    SELECT dl.id,
           dl.doc_id,
           dl.order_line_id,
           dl.item_id,
           dl.qty
    FROM doc_lines dl
    WHERE dl.qty > @qty_tolerance
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
),
reserved_customer_hu AS (
    SELECT DISTINCT p.item_id,
           UPPER(BTRIM(p.to_hu)) AS hu_key
    FROM order_receipt_plan_lines p
    INNER JOIN orders o ON o.id = p.order_id
    WHERE o.order_type = @customer_order_type
      AND p.qty_planned > @qty_tolerance
      AND p.to_hu IS NOT NULL
      AND BTRIM(p.to_hu) <> ''
),
order_qty AS (
    SELECT ol.order_id,
           ol.item_id,
           SUM(GREATEST(0, ol.qty_ordered)) AS order_qty
    FROM order_lines ol
    GROUP BY ol.order_id,
             ol.item_id
),
prd_doc_qty AS (
    SELECT COALESCE(ol.order_id, d.order_id) AS order_id,
           dl.item_id,
           SUM(CASE WHEN d.status = @closed_doc_status THEN dl.qty ELSE 0 END) AS closed_prd_doc_qty,
           SUM(CASE WHEN d.status <> @closed_doc_status THEN dl.qty ELSE 0 END) AS open_prd_doc_qty,
           SUM(dl.qty) AS prd_doc_qty,
           BOOL_OR(d.status <> @closed_doc_status) AS has_open_prd,
           BOOL_OR(d.status = @closed_doc_status) AS has_closed_prd
    FROM docs d
    INNER JOIN active_doc_lines dl ON dl.doc_id = d.id
    LEFT JOIN order_lines ol ON ol.id = dl.order_line_id
    WHERE d.type = @prd_doc_type
      AND d.order_id IS NOT NULL
      AND COALESCE(ol.order_id, d.order_id) IS NOT NULL
    GROUP BY COALESCE(ol.order_id, d.order_id),
             dl.item_id
),
pallet_rows AS (
    SELECT COALESCE(pp.order_id, d.order_id) AS order_id,
           pp.id AS pallet_id,
           pp.prd_doc_id,
           d.status AS prd_doc_status,
           d.doc_ref AS prd_doc_ref,
           COALESCE(pll.doc_line_id, pp.doc_line_id) AS doc_line_id,
           COALESCE(pll.order_line_id, pp.order_line_id) AS order_line_id,
           COALESCE(pll.item_id, pp.item_id) AS line_item_id,
           pp.hu_code,
           pp.status,
           COALESCE(pll.planned_qty, pp.planned_qty) AS planned_qty,
           COALESCE(
               pll.filled_qty,
               CASE WHEN pp.status = @filled_pallet_status THEN pp.planned_qty ELSE 0 END
           ) AS filled_qty,
           EXISTS (
               SELECT 1
               FROM ledger hu_ledger
               WHERE hu_ledger.item_id = COALESCE(pll.item_id, pp.item_id)
                 AND UPPER(BTRIM(COALESCE(hu_ledger.hu_code, hu_ledger.hu))) = UPPER(BTRIM(pp.hu_code))
                 AND hu_ledger.qty_delta > @qty_tolerance
           ) AS has_positive_hu_ledger,
           EXISTS (
               SELECT 1 FROM production_pallet_lines progress
               WHERE progress.production_pallet_id = pp.id
                 AND progress.filled_qty > @qty_tolerance
           )
           AND EXISTS (
               SELECT 1 FROM production_pallet_lines remaining
               WHERE remaining.production_pallet_id = pp.id
                 AND remaining.filled_qty < remaining.planned_qty - @qty_tolerance
           ) AS is_partial_pallet
    FROM production_pallets pp
    INNER JOIN docs d ON d.id = pp.prd_doc_id
    LEFT JOIN production_pallet_lines pll ON pll.production_pallet_id = pp.id
    WHERE pp.status <> @cancelled_pallet_status
      AND COALESCE(pp.order_id, d.order_id) IS NOT NULL
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = COALESCE(pll.doc_line_id, pp.doc_line_id)
      )
      AND NOT EXISTS (
          SELECT 1
          FROM reserved_customer_hu reserved
          WHERE reserved.item_id = pp.item_id
            AND reserved.hu_key = UPPER(BTRIM(pp.hu_code))
      )
),
pallet_qty AS (
    SELECT order_id,
           line_item_id AS item_id,
           SUM(planned_qty) AS pallet_planned_qty,
           SUM(filled_qty) AS pallet_filled_qty,
           SUM(CASE WHEN status = @filled_pallet_status THEN filled_qty ELSE 0 END) AS persisted_filled_pallet_qty,
           BOOL_OR(
               is_partial_pallet
               AND has_positive_hu_ledger
           ) AS has_partial_pallet_with_ledger,
           BOOL_OR(
               is_partial_pallet
               AND status NOT IN (@planned_pallet_status, @printed_pallet_status)
           ) AS has_partial_pallet_invalid_status
    FROM pallet_rows
    GROUP BY order_id,
             line_item_id
),
open_pallet_qty AS (
    SELECT order_id,
           line_item_id AS item_id,
           SUM(planned_qty) AS open_pallet_planned_qty,
           SUM(filled_qty) AS open_pallet_filled_qty
    FROM pallet_rows
    WHERE prd_doc_status <> @closed_doc_status
    GROUP BY order_id,
             line_item_id
),
ledger_prd_qty AS (
    SELECT d.order_id,
           l.item_id,
           SUM(CASE WHEN d.status = @closed_doc_status AND l.qty_delta > 0 THEN l.qty_delta ELSE 0 END) AS ledger_closed_prd_qty,
           SUM(CASE WHEN d.status <> @closed_doc_status AND l.qty_delta > 0 THEN l.qty_delta ELSE 0 END) AS ledger_open_prd_qty,
           SUM(CASE WHEN l.qty_delta > 0 THEN l.qty_delta ELSE 0 END) AS ledger_prd_qty
    FROM ledger l
    INNER JOIN docs d ON d.id = l.doc_id
    WHERE d.type = @prd_doc_type
      AND d.order_id IS NOT NULL
    GROUP BY d.order_id,
             l.item_id
),
scope AS (
    SELECT order_id, item_id FROM order_qty
    UNION
    SELECT order_id, item_id FROM prd_doc_qty
    UNION
    SELECT order_id, item_id FROM pallet_qty
    UNION
    SELECT order_id, item_id FROM ledger_prd_qty
),
rollup AS (
    SELECT s.order_id,
           o.order_ref,
           o.order_type,
           o.status AS order_status,
           s.item_id,
           COALESCE(i.name, '') AS item_name,
           COALESCE(oq.order_qty, 0) AS order_qty,
           COALESCE(pdq.open_prd_doc_qty, 0) AS open_prd_doc_qty,
           COALESCE(pdq.closed_prd_doc_qty, 0) AS closed_prd_doc_qty,
           COALESCE(pdq.prd_doc_qty, 0) AS prd_doc_qty,
           COALESCE(opq.open_pallet_planned_qty, 0) AS open_pallet_planned_qty,
           COALESCE(pq.pallet_planned_qty, 0) AS pallet_planned_qty,
           COALESCE(pq.pallet_filled_qty, 0) AS pallet_filled_qty,
           COALESCE(pq.persisted_filled_pallet_qty, 0) AS persisted_filled_pallet_qty,
           COALESCE(pq.has_partial_pallet_with_ledger, FALSE) AS has_partial_pallet_with_ledger,
           COALESCE(pq.has_partial_pallet_invalid_status, FALSE) AS has_partial_pallet_invalid_status,
           COALESCE(lq.ledger_closed_prd_qty, 0) AS ledger_closed_prd_qty,
           COALESCE(lq.ledger_open_prd_qty, 0) AS ledger_open_prd_qty,
           COALESCE(lq.ledger_prd_qty, 0) AS ledger_prd_qty,
           COALESCE(pdq.has_open_prd, FALSE) AS has_open_prd,
           COALESCE(pdq.has_closed_prd, FALSE) AS has_closed_prd,
           COALESCE(pdq.open_prd_doc_qty, 0) <= @qty_tolerance
               OR ABS(COALESCE(pdq.open_prd_doc_qty, 0) - COALESCE(opq.open_pallet_planned_qty, 0)) <= @qty_tolerance AS open_prd_matches_open_pallets,
           COALESCE(opq.open_pallet_planned_qty, 0) <= @qty_tolerance
               OR ABS(COALESCE(opq.open_pallet_planned_qty, 0) - COALESCE(opq.open_pallet_filled_qty, 0)) <= @qty_tolerance AS open_pallets_match_fill
    FROM scope s
    INNER JOIN orders o ON o.id = s.order_id
    LEFT JOIN items i ON i.id = s.item_id
    LEFT JOIN order_qty oq ON oq.order_id = s.order_id AND oq.item_id = s.item_id
    LEFT JOIN prd_doc_qty pdq ON pdq.order_id = s.order_id AND pdq.item_id = s.item_id
    LEFT JOIN pallet_qty pq ON pq.order_id = s.order_id AND pq.item_id = s.item_id
    LEFT JOIN open_pallet_qty opq ON opq.order_id = s.order_id AND opq.item_id = s.item_id
    LEFT JOIN ledger_prd_qty lq ON lq.order_id = s.order_id AND lq.item_id = s.item_id
),
all_candidates AS (
    SELECT *,
           CASE
               WHEN order_status = @merged_order_status AND open_pallet_planned_qty > @qty_tolerance THEN @problem_merged_order_with_pallet_plan
               WHEN order_type = @customer_order_type AND order_status = @shipped_order_status AND has_open_prd THEN @problem_shipped_customer_with_open_prd
               WHEN order_qty <= @qty_tolerance AND open_pallet_planned_qty > @qty_tolerance THEN @problem_order_zero_but_pallets_exist
               WHEN has_open_prd
                    AND NOT (order_type = @customer_order_type AND order_status = @shipped_order_status)
                    AND open_pallet_planned_qty - order_qty > @qty_tolerance THEN @problem_pallets_exceed_order_qty
               WHEN has_open_prd
                    AND NOT (order_type = @customer_order_type AND order_status = @shipped_order_status)
                    AND open_prd_doc_qty - order_qty > @qty_tolerance THEN @problem_prd_lines_exceed_order_qty
               WHEN has_closed_prd
                    AND ABS(closed_prd_doc_qty - ledger_closed_prd_qty) > @qty_tolerance THEN @problem_closed_prd_ledger_mismatch
               WHEN persisted_filled_pallet_qty > @qty_tolerance
                    AND has_open_prd
                    AND ledger_open_prd_qty <= @qty_tolerance THEN @problem_filled_pallet_missing_ledger
               WHEN has_partial_pallet_invalid_status THEN @problem_partial_pallet_invalid_status
               WHEN has_partial_pallet_with_ledger THEN @problem_partial_pallet_has_ledger
               WHEN persisted_filled_pallet_qty > @qty_tolerance AND has_open_prd THEN @problem_filled_pallets_with_draft_prd
               ELSE NULL
           END AS problem_code,
           CASE
               WHEN order_type = @customer_order_type
                    AND order_status = @shipped_order_status
                    AND has_open_prd
                    AND open_pallet_planned_qty <= @qty_tolerance
                    AND pallet_filled_qty <= @qty_tolerance
                    AND ledger_open_prd_qty <= @qty_tolerance THEN @severity_warning
               WHEN order_type = @customer_order_type
                    AND order_status = @shipped_order_status
                    AND has_open_prd
                    AND (open_pallet_planned_qty > @qty_tolerance
                         OR pallet_filled_qty > @qty_tolerance
                         OR ledger_open_prd_qty > @qty_tolerance)
                    AND (NOT open_prd_matches_open_pallets OR NOT open_pallets_match_fill) THEN @severity_error
               WHEN order_type = @customer_order_type
                    AND order_status = @shipped_order_status
                    AND has_open_prd THEN @severity_warning
               WHEN pallet_filled_qty > @qty_tolerance AND has_open_prd THEN @severity_warning
               ELSE @severity_error
           END AS severity
    FROM rollup
),
candidates AS (
    SELECT *
    FROM all_candidates
    WHERE problem_code IS NOT NULL
)
";
    }

    private static void AddProductionPlanConsistencyDiagnosticsParameters(NpgsqlCommand command)
    {
        command.Parameters.AddWithValue("@prd_doc_type", DocTypeMapper.ToOpString(DocType.ProductionReceipt));
        command.Parameters.AddWithValue("@closed_doc_status", DocTypeMapper.StatusToString(DocStatus.Closed));
        command.Parameters.AddWithValue("@filled_pallet_status", ProductionPalletStatus.Filled);
        command.Parameters.AddWithValue("@planned_pallet_status", ProductionPalletStatus.Planned);
        command.Parameters.AddWithValue("@printed_pallet_status", ProductionPalletStatus.Printed);
        command.Parameters.AddWithValue("@cancelled_pallet_status", ProductionPalletStatus.Cancelled);
        command.Parameters.AddWithValue("@customer_order_type", OrderStatusMapper.TypeToString(OrderType.Customer));
        command.Parameters.AddWithValue("@shipped_order_status", OrderStatusMapper.StatusToString(OrderStatus.Shipped));
        command.Parameters.AddWithValue("@merged_order_status", OrderStatusMapper.StatusToString(OrderStatus.Merged));
        command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
        command.Parameters.AddWithValue("@severity_error", ProductionPlanConsistencySeverity.Error);
        command.Parameters.AddWithValue("@severity_warning", ProductionPlanConsistencySeverity.Warning);
        command.Parameters.AddWithValue("@problem_order_zero_but_pallets_exist", ProductionPlanConsistencyProblemCode.OrderZeroButPalletsExist);
        command.Parameters.AddWithValue("@problem_pallets_exceed_order_qty", ProductionPlanConsistencyProblemCode.PalletsExceedOrderQty);
        command.Parameters.AddWithValue("@problem_prd_lines_exceed_order_qty", ProductionPlanConsistencyProblemCode.PrdLinesExceedOrderQty);
        command.Parameters.AddWithValue("@problem_filled_pallets_with_draft_prd", ProductionPlanConsistencyProblemCode.FilledPalletsWithDraftPrd);
        command.Parameters.AddWithValue("@problem_filled_pallet_missing_ledger", ProductionPlanConsistencyProblemCode.FilledPalletMissingLedger);
        command.Parameters.AddWithValue("@problem_partial_pallet_has_ledger", ProductionPlanConsistencyProblemCode.PartialPalletHasLedger);
        command.Parameters.AddWithValue("@problem_partial_pallet_invalid_status", ProductionPlanConsistencyProblemCode.PartialPalletInvalidStatus);
        command.Parameters.AddWithValue("@problem_shipped_customer_with_open_prd", ProductionPlanConsistencyProblemCode.ShippedCustomerWithOpenPrd);
        command.Parameters.AddWithValue("@problem_merged_order_with_pallet_plan", ProductionPlanConsistencyProblemCode.MergedOrderWithPalletPlan);
        command.Parameters.AddWithValue("@problem_closed_prd_ledger_mismatch", ProductionPlanConsistencyProblemCode.ClosedPrdLedgerMismatch);
    }

    private static string BuildProductionPlanConsistencyRecommendation(string problemCode)
    {
        return problemCode switch
        {
            ProductionPlanConsistencyProblemCode.OrderZeroButPalletsExist =>
                "Order line quantity is zero but active pallet plan remains. Review merge/redistribution history and create a manual repair plan before closing PRD.",
            ProductionPlanConsistencyProblemCode.PalletsExceedOrderQty =>
                "Active pallet plan exceeds current order quantity. Do not close PRD until pallet plan is cancelled, transferred, or manually repaired.",
            ProductionPlanConsistencyProblemCode.PrdLinesExceedOrderQty =>
                "Active PRD document lines exceed current order quantity. Review draft PRD lines and order redistribution before closing.",
            ProductionPlanConsistencyProblemCode.FilledPalletsWithDraftPrd =>
                "Filled pallet ledger exists while PRD is still open. If quantities are aligned, close the PRD; otherwise review diagnostics before closing.",
            ProductionPlanConsistencyProblemCode.FilledPalletMissingLedger =>
                "Filled production pallet has an open PRD and no positive receipt ledger. Use controlled maintenance repair; do not edit ledger manually.",
            ProductionPlanConsistencyProblemCode.PartialPalletHasLedger =>
                "Partially filled mixed pallet already has receipt ledger. Stop filling and review the HU before any correction.",
            ProductionPlanConsistencyProblemCode.PartialPalletInvalidStatus =>
                "Partial component progress is stored under an invalid pallet status. Review the HU before continuing.",
            ProductionPlanConsistencyProblemCode.ShippedCustomerWithOpenPrd =>
                "Customer order is already shipped but has an open PRD/pallet plan. Review and cancel or repair the open production plan.",
            ProductionPlanConsistencyProblemCode.MergedOrderWithPalletPlan =>
                "Merged order still has active pallet plan. Manual review is required; do not silently edit production pallets.",
            ProductionPlanConsistencyProblemCode.ClosedPrdLedgerMismatch =>
                "Closed PRD ledger does not match PRD/pallet quantities. Do not edit ledger manually; create an explicit correction document if confirmed.",
            _ => "Review production plan consistency diagnostics."
        };
    }

    private static string BuildOverShippedOrderDiagnosticsCte()
    {
        return @"
WITH customer_order_lines AS (
    SELECT o.id AS order_id,
           o.order_ref,
           ol.id AS order_line_id,
           ol.item_id,
           i.name AS item_name,
           GREATEST(0, ol.qty_ordered) AS qty_ordered
    FROM orders o
    INNER JOIN order_lines ol ON ol.order_id = o.id
    INNER JOIN items i ON i.id = ol.item_id
    WHERE o.order_type = @customer_order_type
      AND o.status NOT IN (@cancelled_order_status, @merged_order_status)
      AND ol.qty_ordered > @qty_tolerance
),
active_doc_lines AS (
    SELECT dl.id,
           dl.doc_id,
           dl.order_line_id,
           dl.item_id,
           dl.qty,
           dl.from_hu
    FROM doc_lines dl
    WHERE dl.qty > 0
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
),
line_shipments AS (
    SELECT dl.order_line_id,
           SUM(dl.qty) AS qty_shipped
    FROM active_doc_lines dl
    INNER JOIN docs d ON d.id = dl.doc_id
    INNER JOIN customer_order_lines col ON col.order_line_id = dl.order_line_id
                                      AND col.item_id = dl.item_id
                                      AND col.order_id = d.order_id
    WHERE d.type = @outbound_doc_type
      AND d.status = @closed_doc_status
    GROUP BY dl.order_line_id
),
line_rollup AS (
    SELECT col.order_id,
           col.order_ref,
           col.item_id,
           col.item_name,
           SUM(col.qty_ordered) AS qty_ordered,
           SUM(COALESCE(ship.qty_shipped, 0)) AS shipped_by_api_read_model,
           SUM(COALESCE(ship.qty_shipped, 0)) AS shipped_by_closed_outbound
    FROM customer_order_lines col
    LEFT JOIN line_shipments ship ON ship.order_line_id = col.order_line_id
    GROUP BY col.order_id,
             col.order_ref,
             col.item_id,
             col.item_name
),
ledger_shipments AS (
    SELECT d.order_id,
           l.item_id,
           SUM(CASE WHEN l.qty_delta < 0 THEN -l.qty_delta ELSE 0 END) AS shipped_by_ledger
    FROM ledger l
    INNER JOIN docs d ON d.id = l.doc_id
    INNER JOIN orders o ON o.id = d.order_id
    WHERE o.order_type = @customer_order_type
      AND o.status NOT IN (@cancelled_order_status, @merged_order_status)
      AND d.type = @outbound_doc_type
      AND d.status = @closed_doc_status
    GROUP BY d.order_id,
             l.item_id
),
candidates AS (
    SELECT lr.order_id,
           lr.order_ref,
           lr.item_id,
           lr.item_name,
           lr.qty_ordered,
           lr.shipped_by_api_read_model,
           lr.shipped_by_closed_outbound,
           COALESCE(ledger.shipped_by_ledger, 0) AS shipped_by_ledger
    FROM line_rollup lr
    LEFT JOIN ledger_shipments ledger ON ledger.order_id = lr.order_id
                                     AND ledger.item_id = lr.item_id
    WHERE GREATEST(lr.shipped_by_api_read_model, lr.shipped_by_closed_outbound, COALESCE(ledger.shipped_by_ledger, 0))
          - lr.qty_ordered > @qty_tolerance
)
";
    }

    private static void AddOverShippedOrderDiagnosticsParameters(NpgsqlCommand command)
    {
        command.Parameters.AddWithValue("@customer_order_type", OrderStatusMapper.TypeToString(OrderType.Customer));
        command.Parameters.AddWithValue("@cancelled_order_status", OrderStatusMapper.StatusToString(OrderStatus.Cancelled));
        command.Parameters.AddWithValue("@merged_order_status", OrderStatusMapper.StatusToString(OrderStatus.Merged));
        command.Parameters.AddWithValue("@closed_doc_status", DocTypeMapper.StatusToString(DocStatus.Closed));
        command.Parameters.AddWithValue("@outbound_doc_type", DocTypeMapper.ToOpString(DocType.Outbound));
        command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
    }

    private static string BuildOverShippedRecommendation(OverShippedOrderDiagnosticItem row)
    {
        var activeOver = row.ShippedByClosedOutbound - row.QtyOrdered > StockQuantityRules.QtyTolerance;
        var ledgerOver = row.ShippedByLedger - row.QtyOrdered > StockQuantityRules.QtyTolerance;
        if (activeOver && ledgerOver)
        {
            return "REAL_OVER_SHIPMENT_REVIEW_REQUIRED";
        }

        if (activeOver)
        {
            return "DOC_LINES_OVER_ORDERED_LEDGER_NOT_OVER_SHIPPED_REVIEW_DOC_LINES";
        }

        if (ledgerOver)
        {
            return "LEDGER_OVER_ORDERED_REVIEW_LEDGER_AND_CREATE_CORRECTION_DRAFT_IF_CONFIRMED";
        }

        return "NO_ACTION";
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
" + MarkingReservedFilledLedgerStockCte + @",
reserved_filled AS (
    SELECT p.order_line_id,
           SUM(LEAST(p.qty_planned, ls.qty, fp.filled_qty)) AS qty_reserved_filled
    FROM order_receipt_plan_lines p
    INNER JOIN orders o ON o.id = p.order_id AND o.order_type = 'CUSTOMER'
    INNER JOIN ledger_stock_by_hu ls ON ls.item_id = p.item_id
                                    AND ls.hu_code = UPPER(BTRIM(p.to_hu))
    INNER JOIN filled_pallet_by_hu fp ON fp.item_id = p.item_id
                                     AND fp.hu_code = UPPER(BTRIM(p.to_hu))
    WHERE p.qty_planned > 0
      AND p.to_hu IS NOT NULL
      AND BTRIM(p.to_hu) <> ''
    GROUP BY p.order_line_id
),
line_need AS (
    SELECT ol.order_id,
           ol.id AS order_line_id,
           GREATEST(
               0,
               ol.qty_ordered
               - COALESCE(shipped.qty_shipped, 0)
               - CASE
                     WHEN o.order_type = 'CUSTOMER' THEN COALESCE(reserved_filled.qty_reserved_filled, 0)
                     ELSE 0
                 END) AS qty_for_marking
    FROM order_lines ol
    INNER JOIN orders o ON o.id = ol.order_id
    INNER JOIN items i ON i.id = ol.item_id
    INNER JOIN item_types it ON it.id = i.item_type_id
    LEFT JOIN shipped ON shipped.order_line_id = ol.id
    LEFT JOIN reserved_filled ON reserved_filled.order_line_id = ol.id
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
" + MarkingReservedFilledLedgerStockCte + @",
reserved_filled AS (
    SELECT p.order_line_id,
           SUM(LEAST(p.qty_planned, ls.qty, fp.filled_qty)) AS qty_reserved_filled
    FROM order_receipt_plan_lines p
    INNER JOIN orders o ON o.id = p.order_id AND o.order_type = 'CUSTOMER'
    INNER JOIN ledger_stock_by_hu ls ON ls.item_id = p.item_id
                                    AND ls.hu_code = UPPER(BTRIM(p.to_hu))
    INNER JOIN filled_pallet_by_hu fp ON fp.item_id = p.item_id
                                     AND fp.hu_code = UPPER(BTRIM(p.to_hu))
    WHERE p.qty_planned > 0
      AND p.to_hu IS NOT NULL
      AND BTRIM(p.to_hu) <> ''
    GROUP BY p.order_line_id
)
SELECT ol.order_id,
       ol.id,
       i.name,
       BTRIM(i.gtin) AS gtin,
       ol.qty_ordered,
       COALESCE(shipped.qty_shipped, 0) AS qty_shipped,
       CASE
           WHEN o.order_type = 'CUSTOMER' THEN COALESCE(reserved_filled.qty_reserved_filled, 0)
           ELSE 0
       END AS qty_reserved,
       GREATEST(
           0,
           ol.qty_ordered
           - COALESCE(shipped.qty_shipped, 0)
           - CASE
                 WHEN o.order_type = 'CUSTOMER' THEN COALESCE(reserved_filled.qty_reserved_filled, 0)
                 ELSE 0
             END) AS qty_for_marking
FROM order_lines ol
INNER JOIN selected_orders so ON so.order_id = ol.order_id
INNER JOIN orders o ON o.id = ol.order_id
INNER JOIN items i ON i.id = ol.item_id
INNER JOIN item_types it ON it.id = i.item_type_id
LEFT JOIN shipped ON shipped.order_line_id = ol.id
LEFT JOIN reserved_filled ON reserved_filled.order_line_id = ol.id
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

    public IReadOnlyDictionary<long, long> GetOrderIdsByOrderLineIds(IReadOnlyCollection<long> orderLineIds)
    {
        var ids = orderLineIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<long, long>();
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT id, order_id
FROM order_lines
WHERE id = ANY(@ids)
ORDER BY id;
");
            command.Parameters.Add("@ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value = ids;
            using var reader = command.ExecuteReader();
            var result = new Dictionary<long, long>();
            while (reader.Read())
            {
                result[reader.GetInt64(0)] = reader.GetInt64(1);
            }

            return result;
        });
    }

    public IReadOnlyList<OrderLineView> GetOrderLineViews(long orderId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
WITH pallet_scope AS (
    SELECT pp.id AS pallet_id,
           pp.status,
           COALESCE(pll_agg.order_line_id, pp.order_line_id) AS order_line_id,
           CASE
               WHEN pll_agg.line_qty > 0 THEN pll_agg.line_qty
               WHEN pp.order_line_id IS NOT NULL THEN pp.planned_qty
               ELSE 0
           END AS pallet_qty,
           CASE
               WHEN pll_agg.line_qty > 0 THEN pll_agg.filled_qty
               WHEN pp.status = @pallet_filled_status AND pp.order_line_id IS NOT NULL THEN pp.planned_qty
               ELSE 0
           END AS pallet_filled_qty
    FROM production_pallets pp
    LEFT JOIN LATERAL (
        SELECT pll.order_line_id,
               SUM(pll.planned_qty) AS line_qty,
               SUM(pll.filled_qty) AS filled_qty
        FROM production_pallet_lines pll
        WHERE pll.production_pallet_id = pp.id
          AND pll.order_line_id IS NOT NULL
        GROUP BY pll.order_line_id
    ) pll_agg ON TRUE
    WHERE pp.order_id = @order_id
      AND pp.status <> @pallet_cancelled_status
      AND COALESCE(pll_agg.order_line_id, pp.order_line_id) IS NOT NULL
),
pallet_metrics AS (
    SELECT order_line_id,
           COUNT(DISTINCT pallet_id)::int AS planned_pallet_count,
           COUNT(DISTINCT pallet_id) FILTER (
               WHERE status = @pallet_filled_status
                  OR (pallet_qty > @qty_tolerance AND pallet_filled_qty + @qty_tolerance >= pallet_qty)
           )::int AS filled_pallet_count,
           COALESCE(SUM(pallet_qty), 0)::double precision AS planned_pallet_qty,
           COALESCE(SUM(pallet_filled_qty), 0)::double precision AS filled_pallet_qty
    FROM pallet_scope
    GROUP BY order_line_id
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
            command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
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

    public IReadOnlyDictionary<long, IReadOnlyList<OrderLineView>> GetOrderLineViewsByOrderIds(IReadOnlyCollection<long> orderIds)
    {
        var ids = orderIds.Where(id => id > 0).Distinct().ToArray();
        var result = ids.ToDictionary(id => id, _ => (IReadOnlyList<OrderLineView>)new List<OrderLineView>());
        if (ids.Length == 0)
        {
            return result;
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
WITH line_scope AS (
    SELECT ol.id,
           ol.order_id,
           ol.item_id,
           i.name AS item_name,
           i.barcode,
           i.gtin,
           ol.qty_ordered,
           ol.production_purpose,
           ol.production_pallet_group,
           o.order_type,
           COALESCE(o.bind_reserved_stock, FALSE) AS bind_reserved_stock,
           COALESCE(it.enable_order_reservation, FALSE) AS enable_order_reservation
    FROM order_lines ol
    INNER JOIN orders o ON o.id = ol.order_id
    INNER JOIN items i ON i.id = ol.item_id
    LEFT JOIN item_types it ON it.id = i.item_type_id
    WHERE ol.order_id = ANY(@order_ids)
),
ledger_by_hu_item AS (
    SELECT l.item_id,
           UPPER(BTRIM(COALESCE(l.hu_code, l.hu))) AS hu_code,
           COALESCE(SUM(l.qty_delta), 0)::double precision AS qty
    FROM ledger l
    INNER JOIN (SELECT DISTINCT item_id FROM line_scope) items ON items.item_id = l.item_id
    WHERE NULLIF(BTRIM(COALESCE(l.hu_code, l.hu)), '') IS NOT NULL
    GROUP BY l.item_id, UPPER(BTRIM(COALESCE(l.hu_code, l.hu)))
    HAVING COALESCE(SUM(l.qty_delta), 0) > @qty_tolerance
),
positive_prd_ledger_by_doc_item_hu AS (
    SELECT l.doc_id,
           l.item_id,
           UPPER(BTRIM(COALESCE(l.hu_code, l.hu))) AS hu_code,
           COALESCE(SUM(l.qty_delta), 0)::double precision AS qty
    FROM ledger l
    INNER JOIN docs d ON d.id = l.doc_id
    INNER JOIN (SELECT DISTINCT item_id FROM line_scope) items ON items.item_id = l.item_id
    WHERE d.type = @production_doc_type
      AND l.qty_delta > @qty_tolerance
      AND NULLIF(BTRIM(COALESCE(l.hu_code, l.hu)), '') IS NOT NULL
    GROUP BY l.doc_id, l.item_id, UPPER(BTRIM(COALESCE(l.hu_code, l.hu)))
),
pallet_scope AS (
    SELECT pp.id AS pallet_id,
           pp.status,
           COALESCE(pll_agg.order_line_id, pp.order_line_id) AS order_line_id,
           CASE
               WHEN pll_agg.line_qty > 0 THEN pll_agg.line_qty
               WHEN pp.order_line_id IS NOT NULL THEN pp.planned_qty
               ELSE 0
           END AS pallet_qty,
           CASE
               WHEN pll_agg.line_qty > 0 THEN pll_agg.filled_qty
               WHEN pp.status = @pallet_filled_status AND pp.order_line_id IS NOT NULL THEN pp.planned_qty
               ELSE 0
           END AS pallet_filled_qty
    FROM production_pallets pp
    INNER JOIN docs d ON d.id = pp.prd_doc_id
    LEFT JOIN LATERAL (
        SELECT pll.order_line_id,
               SUM(pll.planned_qty) AS line_qty,
               SUM(pll.filled_qty) AS filled_qty
        FROM production_pallet_lines pll
        WHERE pll.production_pallet_id = pp.id
          AND pll.order_line_id IS NOT NULL
        GROUP BY pll.order_line_id
    ) pll_agg ON TRUE
    INNER JOIN line_scope ls ON ls.id = COALESCE(pll_agg.order_line_id, pp.order_line_id)
    WHERE pp.status <> @pallet_cancelled_status
      AND COALESCE(pll_agg.order_line_id, pp.order_line_id) IS NOT NULL
),
pallet_metrics AS (
    SELECT order_line_id,
           COUNT(DISTINCT pallet_id)::int AS planned_pallet_count,
           COUNT(DISTINCT pallet_id) FILTER (
               WHERE status = @pallet_filled_status
                  OR (pallet_qty > @qty_tolerance AND pallet_filled_qty + @qty_tolerance >= pallet_qty)
           )::int AS filled_pallet_count,
           COALESCE(SUM(pallet_qty), 0)::double precision AS planned_pallet_qty,
           COALESCE(SUM(pallet_filled_qty), 0)::double precision AS filled_pallet_qty
    FROM pallet_scope
    GROUP BY order_line_id
),
available_by_item AS (
    SELECT l.item_id,
           COALESCE(SUM(l.qty_delta), 0)::double precision AS qty_available
    FROM ledger l
    INNER JOIN (SELECT DISTINCT item_id FROM line_scope) items ON items.item_id = l.item_id
    GROUP BY l.item_id
),
shipped_totals AS (
    SELECT dl.order_line_id,
           COALESCE(SUM(dl.qty), 0)::double precision AS qty_shipped
    FROM doc_lines dl
    INNER JOIN docs d ON d.id = dl.doc_id
    INNER JOIN line_scope ls ON ls.id = dl.order_line_id
    WHERE d.type = @outbound_doc_type
      AND d.status = @closed_doc_status
      AND d.order_id = ls.order_id
      AND dl.order_line_id IS NOT NULL
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
           COALESCE(SUM(dl.qty), 0)::double precision AS qty_received
    FROM doc_lines dl
    INNER JOIN docs d ON d.id = dl.doc_id
    INNER JOIN line_scope ls ON ls.id = dl.order_line_id
    WHERE d.status = @closed_doc_status
      AND d.type = @production_doc_type
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
filled_pallet_receipt_sources AS (
    SELECT pll.order_line_id,
           LEAST(pll.planned_qty, prd_ledger.qty)::double precision AS qty_received
    FROM production_pallet_lines pll
    INNER JOIN production_pallets pp ON pp.id = pll.production_pallet_id
    INNER JOIN line_scope ls ON ls.id = pll.order_line_id
    INNER JOIN positive_prd_ledger_by_doc_item_hu prd_ledger ON prd_ledger.doc_id = pp.prd_doc_id
                                                             AND prd_ledger.item_id = pll.item_id
                                                             AND prd_ledger.hu_code = UPPER(BTRIM(pp.hu_code))
    WHERE pp.status = @pallet_filled_status
      AND pll.planned_qty > 0
    UNION ALL
    SELECT pp.order_line_id,
           LEAST(pp.planned_qty, prd_ledger.qty)::double precision AS qty_received
    FROM production_pallets pp
    INNER JOIN line_scope ls ON ls.id = pp.order_line_id
    INNER JOIN positive_prd_ledger_by_doc_item_hu prd_ledger ON prd_ledger.doc_id = pp.prd_doc_id
                                                             AND prd_ledger.item_id = pp.item_id
                                                             AND prd_ledger.hu_code = UPPER(BTRIM(pp.hu_code))
    WHERE pp.status = @pallet_filled_status
      AND pp.order_line_id IS NOT NULL
      AND pp.planned_qty > 0
      AND NOT EXISTS (
          SELECT 1
          FROM production_pallet_lines pll
          WHERE pll.production_pallet_id = pp.id
      )
),
filled_pallet_receipt_totals AS (
    SELECT order_line_id,
           COALESCE(SUM(qty_received), 0)::double precision AS qty_received
    FROM filled_pallet_receipt_sources
    GROUP BY order_line_id
),
receipt_totals AS (
    SELECT order_line_id,
           COALESCE(SUM(qty_received), 0)::double precision AS qty_received
    FROM (
        SELECT order_line_id, qty_received
        FROM legacy_receipt_totals
        UNION ALL
        SELECT order_line_id, qty_received
        FROM filled_pallet_receipt_totals
    ) receipt_sources
    GROUP BY order_line_id
),
reserved_totals AS (
    SELECT p.order_line_id,
           COALESCE(SUM(LEAST(p.qty_planned, lb.qty)), 0)::double precision AS qty_reserved
    FROM order_receipt_plan_lines p
    INNER JOIN line_scope ls ON ls.id = p.order_line_id
    INNER JOIN ledger_by_hu_item lb ON lb.item_id = p.item_id
                                    AND lb.hu_code = UPPER(BTRIM(p.to_hu))
    WHERE p.qty_planned > 0
      AND p.to_hu IS NOT NULL
      AND p.to_hu <> ''
    GROUP BY p.order_line_id
),
line_totals AS (
    SELECT ls.*,
           COALESCE(pm.planned_pallet_count, 0) AS planned_pallet_count,
           COALESCE(pm.filled_pallet_count, 0) AS filled_pallet_count,
           COALESCE(pm.planned_pallet_qty, 0)::double precision AS planned_pallet_qty,
           COALESCE(pm.filled_pallet_qty, 0)::double precision AS filled_pallet_qty,
           COALESCE(available.qty_available, 0)::double precision AS qty_available,
           COALESCE(shipped.qty_shipped, 0)::double precision AS qty_shipped,
           (COALESCE(receipt.qty_received, 0)
            + CASE
                  WHEN ls.order_type = @customer_order_type THEN COALESCE(reserved.qty_reserved, 0)
                  ELSE 0
              END)::double precision AS qty_produced
    FROM line_scope ls
    LEFT JOIN pallet_metrics pm ON pm.order_line_id = ls.id
    LEFT JOIN available_by_item available ON available.item_id = ls.item_id
    LEFT JOIN shipped_totals shipped ON shipped.order_line_id = ls.id
    LEFT JOIN receipt_totals receipt ON receipt.order_line_id = ls.id
    LEFT JOIN reserved_totals reserved ON reserved.order_line_id = ls.id
)
SELECT id,
       order_id,
       item_id,
       item_name,
       barcode,
       gtin,
       qty_ordered,
       production_purpose,
       production_pallet_group,
       planned_pallet_count,
       filled_pallet_count,
       planned_pallet_qty,
       filled_pallet_qty,
       CASE
           WHEN order_type = @internal_order_type THEN qty_produced
           ELSE qty_shipped
       END AS qty_shipped,
       CASE
           WHEN order_type = @internal_order_type THEN LEAST(qty_ordered, qty_produced)
           ELSE qty_produced
       END AS qty_produced,
       qty_available,
       CASE
           WHEN order_type = @internal_order_type THEN GREATEST(0, qty_ordered - qty_produced)
           ELSE GREATEST(0, qty_ordered - qty_shipped)
       END AS qty_remaining,
       CASE
           WHEN order_type = @internal_order_type THEN 0
           ELSE LEAST(
               GREATEST(0, qty_ordered - qty_shipped),
               CASE
                   WHEN bind_reserved_stock AND enable_order_reservation THEN GREATEST(0, qty_produced - qty_shipped)
                   ELSE GREATEST(0, qty_available)
               END)
       END AS can_ship_now,
       CASE
           WHEN order_type = @internal_order_type THEN 0
           ELSE GREATEST(
               0,
               GREATEST(0, qty_ordered - qty_shipped)
               - CASE
                     WHEN bind_reserved_stock AND enable_order_reservation THEN GREATEST(0, qty_produced - qty_shipped)
                     ELSE GREATEST(0, qty_available)
                 END)
       END AS shortage,
       order_type
FROM line_totals
ORDER BY order_id, item_name, id;
");
            command.Parameters.Add("@order_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value = ids;
            command.Parameters.AddWithValue("@closed_doc_status", DocTypeMapper.StatusToString(DocStatus.Closed));
            command.Parameters.AddWithValue("@outbound_doc_type", DocTypeMapper.ToOpString(DocType.Outbound));
            command.Parameters.AddWithValue("@production_doc_type", DocTypeMapper.ToOpString(DocType.ProductionReceipt));
            command.Parameters.AddWithValue("@customer_order_type", OrderStatusMapper.TypeToString(OrderType.Customer));
            command.Parameters.AddWithValue("@internal_order_type", OrderStatusMapper.TypeToString(OrderType.Internal));
            command.Parameters.AddWithValue("@pallet_filled_status", ProductionPalletStatus.Filled);
            command.Parameters.AddWithValue("@pallet_cancelled_status", ProductionPalletStatus.Cancelled);
            command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var orderId = reader.GetInt64(1);
                if (!result.TryGetValue(orderId, out var existing))
                {
                    continue;
                }

                var lines = (List<OrderLineView>)existing;
                var line = new OrderLineView
                {
                    Id = reader.GetInt64(0),
                    OrderId = orderId,
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
                    FilledPalletQty = reader.GetDouble(12),
                    QtyShipped = reader.GetDouble(13),
                    QtyProduced = reader.GetDouble(14),
                    QtyAvailable = reader.GetDouble(15),
                    QtyRemaining = reader.GetDouble(16),
                    CanShipNow = reader.GetDouble(17),
                    Shortage = reader.GetDouble(18)
                };
                var orderType = OrderStatusMapper.TypeFromString(reader.GetString(19)) ?? OrderType.Customer;
                OrderLinePalletFillPresentationService.Apply(new Order
                {
                    Id = orderId,
                    Type = orderType
                }, line);
                lines.Add(line);
            }

            return result;
        });
    }

    public IReadOnlyDictionary<long, string[]> GetProductionHuCodesByOrderLineIds(IReadOnlyCollection<long> orderLineIds)
    {
        var ids = orderLineIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<long, string[]>();
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
WITH line_scope AS (
    SELECT id, item_id
    FROM order_lines
    WHERE id = ANY(@order_line_ids)
),
ledger_by_hu_item AS (
    SELECT l.item_id,
           UPPER(BTRIM(COALESCE(l.hu_code, l.hu))) AS hu_code,
           COALESCE(SUM(l.qty_delta), 0)::double precision AS qty
    FROM ledger l
    INNER JOIN (SELECT DISTINCT item_id FROM line_scope) items ON items.item_id = l.item_id
    WHERE NULLIF(BTRIM(COALESCE(l.hu_code, l.hu)), '') IS NOT NULL
    GROUP BY l.item_id, UPPER(BTRIM(COALESCE(l.hu_code, l.hu)))
    HAVING COALESCE(SUM(l.qty_delta), 0) > @qty_tolerance
),
hu_sources AS (
    SELECT p.order_line_id,
           BTRIM(p.to_hu) AS hu_code
    FROM order_receipt_plan_lines p
    INNER JOIN line_scope ls ON ls.id = p.order_line_id
    INNER JOIN ledger_by_hu_item lb ON lb.item_id = p.item_id
                                    AND lb.hu_code = UPPER(BTRIM(p.to_hu))
    WHERE p.qty_planned > 0
      AND p.to_hu IS NOT NULL
      AND BTRIM(p.to_hu) <> ''
    UNION
    SELECT pll.order_line_id,
           BTRIM(pp.hu_code) AS hu_code
    FROM production_pallet_lines pll
    INNER JOIN production_pallets pp ON pp.id = pll.production_pallet_id
    INNER JOIN docs d ON d.id = pp.prd_doc_id
    INNER JOIN line_scope ls ON ls.id = pll.order_line_id
    INNER JOIN ledger_by_hu_item lb ON lb.item_id = pll.item_id
                                    AND lb.hu_code = UPPER(BTRIM(pp.hu_code))
    WHERE pp.status = @pallet_filled_status
      AND d.type = @production_doc_type
      AND pp.hu_code IS NOT NULL
      AND BTRIM(pp.hu_code) <> ''
    UNION
    SELECT pp.order_line_id,
           BTRIM(pp.hu_code) AS hu_code
    FROM production_pallets pp
    INNER JOIN docs d ON d.id = pp.prd_doc_id
    INNER JOIN line_scope ls ON ls.id = pp.order_line_id
    INNER JOIN ledger_by_hu_item lb ON lb.item_id = pp.item_id
                                    AND lb.hu_code = UPPER(BTRIM(pp.hu_code))
    WHERE pp.status = @pallet_filled_status
      AND d.type = @production_doc_type
      AND pp.order_line_id IS NOT NULL
      AND pp.hu_code IS NOT NULL
      AND BTRIM(pp.hu_code) <> ''
      AND NOT EXISTS (
          SELECT 1
          FROM production_pallet_lines pll
          WHERE pll.production_pallet_id = pp.id
      )
    UNION
    SELECT pll.order_line_id,
           BTRIM(pp.hu_code) AS hu_code
    FROM production_pallet_lines pll
    INNER JOIN production_pallets pp ON pp.id = pll.production_pallet_id
    INNER JOIN docs d ON d.id = pp.prd_doc_id
    INNER JOIN line_scope ls ON ls.id = pll.order_line_id
    WHERE pp.status IN (@pallet_planned_status, @pallet_printed_status)
      AND pp.status <> @pallet_cancelled_status
      AND d.type = @production_doc_type
      AND pp.hu_code IS NOT NULL
      AND BTRIM(pp.hu_code) <> ''
    UNION
    SELECT pp.order_line_id,
           BTRIM(pp.hu_code) AS hu_code
    FROM production_pallets pp
    INNER JOIN docs d ON d.id = pp.prd_doc_id
    INNER JOIN line_scope ls ON ls.id = pp.order_line_id
    WHERE pp.status IN (@pallet_planned_status, @pallet_printed_status)
      AND pp.status <> @pallet_cancelled_status
      AND d.type = @production_doc_type
      AND pp.order_line_id IS NOT NULL
      AND pp.hu_code IS NOT NULL
      AND BTRIM(pp.hu_code) <> ''
      AND NOT EXISTS (
          SELECT 1
          FROM production_pallet_lines pll
          WHERE pll.production_pallet_id = pp.id
      )
)
SELECT order_line_id, hu_code
FROM hu_sources
ORDER BY order_line_id, LOWER(hu_code), hu_code;
");
            command.Parameters.Add("@order_line_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value = ids;
            command.Parameters.AddWithValue("@production_doc_type", DocTypeMapper.ToOpString(DocType.ProductionReceipt));
            command.Parameters.AddWithValue("@pallet_filled_status", ProductionPalletStatus.Filled);
            command.Parameters.AddWithValue("@pallet_planned_status", ProductionPalletStatus.Planned);
            command.Parameters.AddWithValue("@pallet_printed_status", ProductionPalletStatus.Printed);
            command.Parameters.AddWithValue("@pallet_cancelled_status", ProductionPalletStatus.Cancelled);
            command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
            using var reader = command.ExecuteReader();
            var rows = new Dictionary<long, SortedSet<string>>();
            while (reader.Read())
            {
                var orderLineId = reader.GetInt64(0);
                var huCode = reader.GetString(1);
                if (!rows.TryGetValue(orderLineId, out var huCodes))
                {
                    huCodes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    rows[orderLineId] = huCodes;
                }

                huCodes.Add(huCode);
            }

            return rows.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
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
ledger_by_hu_item AS (
    SELECT l.item_id,
           UPPER(BTRIM(COALESCE(l.hu_code, l.hu))) AS hu_code,
           COALESCE(SUM(l.qty_delta), 0)::double precision AS qty
    FROM ledger l
    INNER JOIN (SELECT DISTINCT item_id FROM order_line_scope) items ON items.item_id = l.item_id
    WHERE NULLIF(BTRIM(COALESCE(l.hu_code, l.hu)), '') IS NOT NULL
    GROUP BY l.item_id, UPPER(BTRIM(COALESCE(l.hu_code, l.hu)))
    HAVING COALESCE(SUM(l.qty_delta), 0) > @qty_tolerance
),
positive_prd_ledger_by_doc_item_hu AS (
    SELECT l.doc_id,
           l.item_id,
           UPPER(BTRIM(COALESCE(l.hu_code, l.hu))) AS hu_code,
           COALESCE(SUM(l.qty_delta), 0)::double precision AS qty
    FROM ledger l
    INNER JOIN docs d ON d.id = l.doc_id
    INNER JOIN (SELECT DISTINCT item_id FROM order_line_scope) items ON items.item_id = l.item_id
    WHERE d.type = @doc_type
      AND l.qty_delta > @qty_tolerance
      AND NULLIF(BTRIM(COALESCE(l.hu_code, l.hu)), '') IS NOT NULL
    GROUP BY l.doc_id, l.item_id, UPPER(BTRIM(COALESCE(l.hu_code, l.hu)))
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
filled_pallet_sources AS (
    SELECT pll.order_line_id,
           LEAST(pll.planned_qty, prd_ledger.qty) AS sum_qty
    FROM production_pallet_lines pll
    INNER JOIN production_pallets pp ON pp.id = pll.production_pallet_id
    INNER JOIN order_line_scope ols ON ols.id = pll.order_line_id
    INNER JOIN positive_prd_ledger_by_doc_item_hu prd_ledger ON prd_ledger.doc_id = pp.prd_doc_id
                                                             AND prd_ledger.item_id = pll.item_id
                                                             AND prd_ledger.hu_code = UPPER(BTRIM(pp.hu_code))
    WHERE pp.status = @pallet_filled_status
      AND pll.planned_qty > 0
    UNION ALL
    SELECT pp.order_line_id,
           LEAST(pp.planned_qty, prd_ledger.qty) AS sum_qty
    FROM production_pallets pp
    INNER JOIN order_line_scope ols ON ols.id = pp.order_line_id
    INNER JOIN positive_prd_ledger_by_doc_item_hu prd_ledger ON prd_ledger.doc_id = pp.prd_doc_id
                                                             AND prd_ledger.item_id = pp.item_id
                                                             AND prd_ledger.hu_code = UPPER(BTRIM(pp.hu_code))
    WHERE pp.status = @pallet_filled_status
      AND pp.order_line_id IS NOT NULL
      AND pp.planned_qty > 0
      AND NOT EXISTS (
          SELECT 1
          FROM production_pallet_lines pll
          WHERE pll.production_pallet_id = pp.id
      )
),
filled_pallet_totals AS (
    SELECT order_line_id,
           SUM(sum_qty) AS sum_qty
    FROM filled_pallet_sources
    GROUP BY order_line_id
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
           SUM(LEAST(p.qty_planned, lb.qty)) AS sum_qty
    FROM order_receipt_plan_lines p
    INNER JOIN order_line_scope ols ON ols.id = p.order_line_id
    INNER JOIN ledger_by_hu_item lb ON lb.item_id = p.item_id
                                    AND lb.hu_code = UPPER(BTRIM(p.to_hu))
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
            command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
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
           i.min_stock_qty AS min_stock_qty
    FROM items i
    LEFT JOIN item_types it ON it.id = i.item_type_id
),
stock_by_item AS (
    SELECT l.item_id,
           SUM(l.qty_delta) AS physical_stock_qty
    FROM ledger l
    GROUP BY l.item_id
),
reserved_hu_candidates AS (
    SELECT pp.item_id,
           UPPER(BTRIM(pp.hu_code)) AS hu_code,
           0 AS source_priority,
           o.created_at AS source_order_created_at,
           pp.id AS source_id
    FROM production_pallets pp
    INNER JOIN orders o ON o.id = pp.order_id
    WHERE pp.order_id IS NOT NULL
      AND pp.status = @filled_pallet_status
      AND o.order_type = @customer_order_type
      AND o.status NOT IN (@shipped_order_status, @cancelled_order_status, @merged_order_status)
      AND pp.hu_code IS NOT NULL
      AND BTRIM(pp.hu_code) <> ''

    UNION ALL

    SELECT p.item_id,
           UPPER(BTRIM(p.to_hu)) AS hu_code,
           1 AS source_priority,
           o.created_at AS source_order_created_at,
           p.id AS source_id
    FROM order_receipt_plan_lines p
    INNER JOIN orders o ON o.id = p.order_id
    WHERE o.order_type = @customer_order_type
      AND o.status NOT IN (@shipped_order_status, @cancelled_order_status, @merged_order_status)
      AND p.qty_planned > 0
      AND p.to_hu IS NOT NULL
      AND BTRIM(p.to_hu) <> ''

    UNION ALL

    SELECT dl.item_id,
           UPPER(BTRIM(dl.to_hu)) AS hu_code,
           2 AS source_priority,
           o.created_at AS source_order_created_at,
           dl.id AS source_id
    FROM doc_lines dl
    INNER JOIN docs d ON d.id = dl.doc_id
    INNER JOIN orders o ON o.id = d.order_id
    WHERE d.type = @production_doc_type
      AND d.status = @closed_doc_status
      AND d.order_id IS NOT NULL
      AND o.order_type = @customer_order_type
      AND o.status NOT IN (@shipped_order_status, @cancelled_order_status, @merged_order_status)
      AND dl.qty > 0
      AND dl.to_hu IS NOT NULL
      AND BTRIM(dl.to_hu) <> ''
      AND NOT EXISTS (
          SELECT 1
          FROM doc_lines newer
          WHERE newer.replaces_line_id = dl.id
      )
),
reserved_hu_ranked AS (
    SELECT item_id,
           hu_code,
           ROW_NUMBER() OVER (
               PARTITION BY item_id, hu_code
               ORDER BY source_priority, source_order_created_at, source_id
           ) AS rn
    FROM reserved_hu_candidates
),
reserved_hu AS (
    SELECT item_id,
           hu_code
    FROM reserved_hu_ranked
    WHERE rn = 1
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
      AND o.status NOT IN (@draft_order_status, @shipped_order_status, @cancelled_order_status, @merged_order_status)
),
customer_shipped_by_line AS (
    SELECT dl.order_line_id,
           SUM(dl.qty) AS qty_shipped
    FROM active_doc_lines dl
    INNER JOIN docs d ON d.id = dl.doc_id
    INNER JOIN customer_order_lines col ON col.id = dl.order_line_id
    WHERE d.status = @closed_doc_status
      AND d.type = @outbound_doc_type
      AND d.order_id = col.order_id
    GROUP BY dl.order_line_id
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
           SUM(LEAST(
               GREATEST(0, col.qty_ordered - COALESCE(receipt.qty_received, 0) - COALESCE(reserved.qty_reserved, 0)),
               GREATEST(0, col.qty_ordered - COALESCE(shipped.qty_shipped, 0))
           )) AS order_qty
    FROM customer_order_lines col
    LEFT JOIN customer_receipt_by_line receipt ON receipt.order_line_id = col.id
    LEFT JOIN customer_reserved_by_line reserved ON reserved.order_line_id = col.id
    LEFT JOIN customer_shipped_by_line shipped ON shipped.order_line_id = col.id
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
      AND o.status NOT IN (@shipped_order_status, @cancelled_order_status, @merged_order_status)
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
      AND o.status NOT IN (@shipped_order_status, @cancelled_order_status, @merged_order_status)
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
      AND (o.id IS NULL OR o.status NOT IN (@shipped_order_status, @cancelled_order_status, @merged_order_status))
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
           WHEN COALESCE(snapshot.enable_min_stock_control, FALSE)
                AND COALESCE(snapshot.min_stock_qty, 0) > @qty_tolerance
               THEN COALESCE(snapshot.min_stock_qty, 0)
           ELSE 0
       END AS min_stock_qty,
       GREATEST(0, COALESCE(need.order_qty, 0)) AS to_close_orders_qty,
       GREATEST(0, CASE
           WHEN COALESCE(snapshot.enable_min_stock_control, FALSE)
                AND COALESCE(snapshot.min_stock_qty, 0) > @qty_tolerance
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
               AND COALESCE(snapshot.min_stock_qty, 0) > @qty_tolerance
              THEN COALESCE(snapshot.min_stock_qty, 0) - (COALESCE(stock.physical_stock_qty, 0) - COALESCE(reserved_stock.reserved_customer_order_qty, 0))
          ELSE 0
      END - COALESCE(planned.planned_internal_stock_qty, 0)) > @qty_tolerance
   OR COALESCE(planned.planned_internal_stock_qty, 0) > 0
   OR COALESCE(filled.filled_pallet_qty, 0) > 0
ORDER BY
    (GREATEST(0, COALESCE(need.order_qty, 0))
     + GREATEST(0, CASE
         WHEN COALESCE(snapshot.enable_min_stock_control, FALSE)
              AND COALESCE(snapshot.min_stock_qty, 0) > @qty_tolerance
             THEN COALESCE(snapshot.min_stock_qty, 0) - (COALESCE(stock.physical_stock_qty, 0) - COALESCE(reserved_stock.reserved_customer_order_qty, 0))
         ELSE 0
     END - COALESCE(planned.planned_internal_stock_qty, 0))) DESC,
    COALESCE(snapshot.item_type_name, 'Без типа'),
    COALESCE(snapshot.item_name, '#' || ids.item_id::text),
    ids.item_id;
");
            command.Parameters.AddWithValue("@include_zero", includeZeroNeed);
            command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
            command.Parameters.AddWithValue("@customer_order_type", OrderStatusMapper.TypeToString(OrderType.Customer));
            command.Parameters.AddWithValue("@internal_order_type", OrderStatusMapper.TypeToString(OrderType.Internal));
            command.Parameters.AddWithValue("@draft_order_status", OrderStatusMapper.StatusToString(OrderStatus.Draft));
            command.Parameters.AddWithValue("@shipped_order_status", OrderStatusMapper.StatusToString(OrderStatus.Shipped));
            command.Parameters.AddWithValue("@cancelled_order_status", OrderStatusMapper.StatusToString(OrderStatus.Cancelled));
            command.Parameters.AddWithValue("@merged_order_status", OrderStatusMapper.StatusToString(OrderStatus.Merged));
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

    public IReadOnlyDictionary<long, IReadOnlyList<WarehouseProductionStateCustomerOrderRow>> GetWarehouseProductionStateCustomerOrdersByItem()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
WITH active_doc_lines AS (
    SELECT dl.doc_id,
           dl.order_line_id,
           dl.qty
    FROM doc_lines dl
    WHERE dl.qty > 0
      AND dl.order_line_id IS NOT NULL
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
           ol.qty_ordered,
           o.order_ref,
           o.status,
           p.name AS partner_name
    FROM order_lines ol
    INNER JOIN orders o ON o.id = ol.order_id
    LEFT JOIN partners p ON p.id = o.partner_id
    WHERE o.order_type = @customer_order_type
      AND o.status NOT IN (@draft_order_status, @shipped_order_status, @cancelled_order_status, @merged_order_status)
),
shipped_by_line AS (
    SELECT dl.order_line_id,
           SUM(dl.qty) AS qty_shipped
    FROM active_doc_lines dl
    INNER JOIN docs d ON d.id = dl.doc_id
    INNER JOIN customer_order_lines col ON col.id = dl.order_line_id
    WHERE d.type = @outbound_doc_type
      AND d.status = @closed_doc_status
      AND d.order_id = col.order_id
    GROUP BY dl.order_line_id
)
SELECT col.item_id,
       col.order_id,
       col.order_ref,
       col.partner_name,
       col.status,
       SUM(col.qty_ordered) AS qty_ordered,
       SUM(COALESCE(shipped.qty_shipped, 0)) AS shipped_qty,
       SUM(GREATEST(0, col.qty_ordered - COALESCE(shipped.qty_shipped, 0))) AS remaining_qty
FROM customer_order_lines col
LEFT JOIN shipped_by_line shipped ON shipped.order_line_id = col.id
GROUP BY col.item_id,
         col.order_id,
         col.order_ref,
         col.partner_name,
         col.status
HAVING SUM(GREATEST(0, col.qty_ordered - COALESCE(shipped.qty_shipped, 0))) > @qty_tolerance
ORDER BY col.item_id,
         col.order_ref;
");
            AddWarehouseProductionStateParameters(command);
            using var reader = command.ExecuteReader();
            var result = new Dictionary<long, List<WarehouseProductionStateCustomerOrderRow>>();
            while (reader.Read())
            {
                var itemId = reader.GetInt64(0);
                if (!result.TryGetValue(itemId, out var bucket))
                {
                    bucket = new List<WarehouseProductionStateCustomerOrderRow>();
                    result[itemId] = bucket;
                }

                var status = OrderStatusMapper.StatusFromString(reader.GetString(4)) ?? OrderStatus.InProgress;
                bucket.Add(new WarehouseProductionStateCustomerOrderRow
                {
                    OrderId = reader.GetInt64(1),
                    OrderRef = reader.GetString(2),
                    PartnerName = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Status = OrderStatusMapper.StatusToDisplayName(status, OrderType.Customer),
                    QtyOrdered = reader.GetDouble(5),
                    ShippedQty = reader.GetDouble(6),
                    RemainingQty = reader.GetDouble(7)
                });
            }

            return result.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<WarehouseProductionStateCustomerOrderRow>)pair.Value
                    .OrderBy(row => row.OrderRef, StringComparer.OrdinalIgnoreCase)
                    .ToList());
        });
    }

    public IReadOnlyDictionary<long, IReadOnlyList<WarehouseProductionStateInternalOrderRow>> GetWarehouseProductionStateInternalOrdersByItem()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
WITH active_doc_lines AS (
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
internal_order_lines AS (
    SELECT ol.id,
           ol.order_id,
           ol.item_id,
           ol.qty_ordered,
           o.order_ref,
           o.status
    FROM order_lines ol
    INNER JOIN orders o ON o.id = ol.order_id
    WHERE o.order_type = @internal_order_type
      AND o.status NOT IN (@shipped_order_status, @cancelled_order_status, @merged_order_status)
),
linked_receipt_by_line AS (
    SELECT dl.order_line_id,
           SUM(dl.qty) AS qty_received
    FROM active_doc_lines dl
    INNER JOIN docs d ON d.id = dl.doc_id
    INNER JOIN internal_order_lines iol ON iol.id = dl.order_line_id
    WHERE d.status = @closed_doc_status
      AND d.type = @production_doc_type
      AND dl.order_line_id IS NOT NULL
      AND NOT EXISTS (
          SELECT 1
          FROM production_pallets pp
          WHERE pp.prd_doc_id = d.id
            AND pp.status <> @cancelled_pallet_status
      )
    GROUP BY dl.order_line_id
),
filled_pallet_by_line AS (
    SELECT pll.order_line_id,
           SUM(pll.planned_qty) AS qty_received
    FROM production_pallet_lines pll
    INNER JOIN production_pallets pp ON pp.id = pll.production_pallet_id
    INNER JOIN internal_order_lines iol ON iol.id = pll.order_line_id
    WHERE pp.status = @filled_pallet_status
      AND pll.planned_qty > 0
    GROUP BY pll.order_line_id
),
receipt_by_line AS (
    SELECT order_line_id,
           SUM(qty_received) AS qty_received
    FROM (
        SELECT order_line_id, qty_received
        FROM linked_receipt_by_line
        UNION ALL
        SELECT order_line_id, qty_received
        FROM filled_pallet_by_line
    ) receipt_sources
    GROUP BY order_line_id
),
unlinked_by_item AS (
    SELECT d.order_id,
           dl.item_id,
           SUM(dl.qty) AS qty_received
    FROM docs d
    INNER JOIN active_doc_lines dl ON dl.doc_id = d.id
    INNER JOIN orders o ON o.id = d.order_id
    WHERE o.order_type = @internal_order_type
      AND o.status NOT IN (@shipped_order_status, @cancelled_order_status, @merged_order_status)
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
line_seed AS (
    SELECT iol.order_id,
           iol.order_ref,
           iol.status,
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
    LEFT JOIN receipt_by_line receipt ON receipt.order_line_id = iol.id
    LEFT JOIN unlinked_by_item unlinked ON unlinked.order_id = iol.order_id
                                      AND unlinked.item_id = iol.item_id
),
line_remaining AS (
    SELECT order_id,
           order_ref,
           status,
           item_id,
           qty_ordered,
           GREATEST(0, qty_ordered - (
               qty_direct_received
               + CASE
                     WHEN qty_unlinked_item_received <= 0 THEN 0
                     WHEN item_line_desc_rank = 1 THEN GREATEST(0, qty_unlinked_item_received - qty_direct_unfilled_before)
                     ELSE GREATEST(0, LEAST(qty_unlinked_item_received - qty_direct_unfilled_before, qty_direct_unfilled))
                 END
           )) AS qty_remaining
    FROM line_seed
)
SELECT item_id,
       order_id,
       order_ref,
       status,
       SUM(qty_ordered) AS qty_ordered,
       SUM(GREATEST(0, qty_ordered - qty_remaining)) AS produced_qty,
       SUM(qty_remaining) AS remaining_qty
FROM line_remaining
GROUP BY item_id,
         order_id,
         order_ref,
         status
HAVING SUM(qty_remaining) > @qty_tolerance
ORDER BY item_id,
         order_ref;
");
            AddWarehouseProductionStateParameters(command);
            using var reader = command.ExecuteReader();
            var result = new Dictionary<long, List<WarehouseProductionStateInternalOrderRow>>();
            while (reader.Read())
            {
                var itemId = reader.GetInt64(0);
                if (!result.TryGetValue(itemId, out var bucket))
                {
                    bucket = new List<WarehouseProductionStateInternalOrderRow>();
                    result[itemId] = bucket;
                }

                var status = OrderStatusMapper.StatusFromString(reader.GetString(3)) ?? OrderStatus.InProgress;
                bucket.Add(new WarehouseProductionStateInternalOrderRow
                {
                    OrderId = reader.GetInt64(1),
                    OrderRef = reader.GetString(2),
                    Status = OrderStatusMapper.StatusToDisplayName(status, OrderType.Internal),
                    QtyOrdered = reader.GetDouble(4),
                    ProducedQty = reader.GetDouble(5),
                    RemainingQty = reader.GetDouble(6)
                });
            }

            return result.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<WarehouseProductionStateInternalOrderRow>)pair.Value
                    .OrderBy(row => row.OrderRef, StringComparer.OrdinalIgnoreCase)
                    .ToList());
        });
    }

    public IReadOnlyDictionary<long, WarehouseProductionStatePalletAggregate> GetWarehouseProductionStatePalletsByItem()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
WITH work_docs AS (
    SELECT d.id,
           d.doc_ref,
           d.order_id,
           COALESCE(o.order_ref, d.order_ref) AS source_order_ref,
           o.order_type,
           o.status AS order_status,
           d.status AS prd_status
    FROM production_pallets pp
    INNER JOIN docs d ON d.id = pp.prd_doc_id
    LEFT JOIN orders o ON o.id = COALESCE(pp.order_id, d.order_id)
    WHERE d.type = @production_doc_type
      AND d.status = @draft_doc_status
      AND pp.status <> @cancelled_pallet_status
      AND pp.status IN (@planned_pallet_status, @printed_pallet_status, @filled_pallet_status)
      AND (
          o.id IS NULL
          OR (
              o.order_type = @internal_order_type
              AND o.status IN (@draft_order_status, @in_progress_order_status)
          )
      )
    GROUP BY d.id,
             d.doc_ref,
             d.order_id,
             COALESCE(o.order_ref, d.order_ref),
             o.order_type,
             o.status,
             d.status
),
pallet_composition AS (
    SELECT pp.id AS pallet_id,
           STRING_AGG(i.name || ' ' || TO_CHAR(pll.planned_qty, 'FM999999999990.999'), ', ' ORDER BY i.name, pll.id) AS composition
    FROM production_pallets pp
    INNER JOIN production_pallet_lines pll ON pll.production_pallet_id = pp.id
    INNER JOIN items i ON i.id = pll.item_id
    GROUP BY pp.id
),
pallet_component_counts AS (
    SELECT production_pallet_id AS pallet_id,
           COUNT(*) AS line_count
    FROM production_pallet_lines
    GROUP BY production_pallet_id
),
ledger_balance AS (
    SELECT item_id,
           location_id,
           UPPER(BTRIM(COALESCE(hu_code, hu))) AS hu_code,
           SUM(qty_delta) AS qty
    FROM ledger
    WHERE COALESCE(hu_code, hu) IS NOT NULL
      AND BTRIM(COALESCE(hu_code, hu)) <> ''
    GROUP BY item_id,
             location_id,
             UPPER(BTRIM(COALESCE(hu_code, hu)))
),
pallet_rows AS (
    SELECT wd.id AS prd_doc_id,
           wd.doc_ref AS prd_ref,
           wd.source_order_ref,
           wd.prd_status,
           wd.order_type,
           wd.order_status,
           pp.id AS pallet_id,
           pp.hu_code,
           pp.status AS pallet_status,
           COALESCE(pll.item_id, pp.item_id) AS item_id,
           COALESCE(line_item.name, header_item.name, '') AS item_name,
           CASE WHEN pll.id IS NULL THEN pp.planned_qty ELSE pll.planned_qty END AS planned_qty,
           CASE
               WHEN pp.status <> @filled_pallet_status THEN GREATEST(0, COALESCE(pll.filled_qty, 0))
               WHEN pll.id IS NULL THEN GREATEST(0, pp.planned_qty)
               WHEN COALESCE(pll.filled_qty, 0) > @qty_tolerance THEN GREATEST(0, pll.filled_qty)
               ELSE GREATEST(0, pll.planned_qty)
           END AS filled_qty,
           pp.to_location_id,
           l.code AS location_code,
           pp.status = @filled_pallet_status AS is_filled,
           COALESCE(pcc.line_count, 0) > 1 AS is_mixed_pallet,
           CASE
               WHEN COALESCE(pcc.line_count, 0) > 1 THEN COALESCE(pc.composition, COALESCE(line_item.name, header_item.name, ''))
               ELSE COALESCE(line_item.name, header_item.name, '')
           END AS composition,
           COALESCE(lb.qty, 0) > @qty_tolerance AS in_ledger
    FROM work_docs wd
    INNER JOIN production_pallets pp ON pp.prd_doc_id = wd.id
    LEFT JOIN production_pallet_lines pll ON pll.production_pallet_id = pp.id
    LEFT JOIN items line_item ON line_item.id = pll.item_id
    LEFT JOIN items header_item ON header_item.id = pp.item_id
    LEFT JOIN locations l ON l.id = pp.to_location_id
    LEFT JOIN pallet_composition pc ON pc.pallet_id = pp.id
    LEFT JOIN pallet_component_counts pcc ON pcc.pallet_id = pp.id
    LEFT JOIN ledger_balance lb ON lb.item_id = COALESCE(pll.item_id, pp.item_id)
                               AND lb.location_id = pp.to_location_id
                               AND lb.hu_code = UPPER(BTRIM(pp.hu_code))
    WHERE pp.status <> @cancelled_pallet_status
      AND pp.status IN (@planned_pallet_status, @printed_pallet_status, @filled_pallet_status)
)
SELECT prd_doc_id,
       prd_ref,
       source_order_ref,
       prd_status,
       order_type,
       order_status,
       pallet_id,
       hu_code,
       pallet_status,
       item_id,
       item_name,
       planned_qty,
       filled_qty,
       location_code,
       is_filled,
       is_mixed_pallet,
       composition,
       in_ledger
FROM pallet_rows
WHERE NOT in_ledger
ORDER BY item_id,
         prd_ref,
         hu_code;
");
            AddWarehouseProductionStateParameters(command);
            using var reader = command.ExecuteReader();
            var aggregates = new Dictionary<long, WarehouseProductionStatePalletAggregateBuilder>();
            while (reader.Read())
            {
                var itemId = reader.GetInt64(9);
                if (!aggregates.TryGetValue(itemId, out var aggregate))
                {
                    aggregate = new WarehouseProductionStatePalletAggregateBuilder();
                    aggregates[itemId] = aggregate;
                }

                var palletId = reader.GetInt64(6);
                var palletStatus = reader.GetString(8);
                var plannedQty = reader.GetDouble(11);
                var filledQty = reader.GetDouble(12);
                var isFilled = reader.GetBoolean(14);
                var isMixed = reader.GetBoolean(15);
                var orderType = reader.IsDBNull(4) ? null : OrderStatusMapper.TypeFromString(reader.GetString(4));
                var orderStatus = reader.IsDBNull(5) ? null : OrderStatusMapper.StatusFromString(reader.GetString(5));
                var prdStatus = reader.GetString(3);
                var prdIsOpen = !string.Equals(
                    prdStatus,
                    DocTypeMapper.StatusToString(DocStatus.Closed),
                    StringComparison.OrdinalIgnoreCase);
                var displayQty = WarehouseProductionStatePresentation.ResolvePalletDisplayQty(
                    palletStatus,
                    plannedQty,
                    filledQty);

                aggregate.Rows.Add(new WarehouseProductionStatePalletRow
                {
                    PrdDocId = reader.GetInt64(0),
                    PrdRef = reader.GetString(1),
                    PalletId = palletId,
                    HuCode = reader.GetString(7),
                    PalletStatus = palletStatus,
                    PalletStatusDisplay = WarehouseProductionStatePresentation.MapPalletStatusDisplay(palletStatus),
                    SourceOrderRef = reader.IsDBNull(2) ? null : reader.GetString(2),
                    PlannedQty = Math.Max(0d, plannedQty),
                    FilledQty = Math.Max(0d, filledQty),
                    Qty = displayQty,
                    StockEffect = "план / производство",
                    StatusNote = WarehouseProductionStatePresentation.BuildPalletStatusNote(palletStatus, prdIsOpen, inLedger: false),
                    IsMixedPallet = isMixed,
                    Composition = reader.GetString(16),
                    Location = reader.IsDBNull(13) ? null : reader.GetString(13)
                });
                aggregate.PlannedQty += Math.Max(0d, plannedQty);
                aggregate.FilledQty += Math.Max(0d, filledQty);
                aggregate.PlannedPalletIds.Add(palletId);
                if (isFilled)
                {
                    aggregate.FilledPalletIds.Add(palletId);
                }

                aggregate.HasFilledWithoutLedger |= isFilled;
                aggregate.HasStalePalletAfterFullShipment |= orderType == OrderType.Customer && orderStatus == OrderStatus.Shipped;
            }

            return aggregates.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToAggregate());
        });
    }

    private static void AddWarehouseProductionStateParameters(NpgsqlCommand command)
    {
        command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
        command.Parameters.AddWithValue("@customer_order_type", OrderStatusMapper.TypeToString(OrderType.Customer));
        command.Parameters.AddWithValue("@internal_order_type", OrderStatusMapper.TypeToString(OrderType.Internal));
        command.Parameters.AddWithValue("@draft_order_status", OrderStatusMapper.StatusToString(OrderStatus.Draft));
        command.Parameters.AddWithValue("@shipped_order_status", OrderStatusMapper.StatusToString(OrderStatus.Shipped));
        command.Parameters.AddWithValue("@cancelled_order_status", OrderStatusMapper.StatusToString(OrderStatus.Cancelled));
        command.Parameters.AddWithValue("@merged_order_status", OrderStatusMapper.StatusToString(OrderStatus.Merged));
        command.Parameters.AddWithValue("@closed_doc_status", DocTypeMapper.StatusToString(DocStatus.Closed));
        command.Parameters.AddWithValue("@outbound_doc_type", DocTypeMapper.ToOpString(DocType.Outbound));
        command.Parameters.AddWithValue("@production_doc_type", DocTypeMapper.ToOpString(DocType.ProductionReceipt));
        command.Parameters.AddWithValue("@filled_pallet_status", ProductionPalletStatus.Filled);
        command.Parameters.AddWithValue("@planned_pallet_status", ProductionPalletStatus.Planned);
        command.Parameters.AddWithValue("@printed_pallet_status", ProductionPalletStatus.Printed);
        command.Parameters.AddWithValue("@cancelled_pallet_status", ProductionPalletStatus.Cancelled);
        command.Parameters.AddWithValue("@draft_doc_status", DocTypeMapper.StatusToString(DocStatus.Draft));
        command.Parameters.AddWithValue("@in_progress_order_status", OrderStatusMapper.StatusToString(OrderStatus.InProgress));
    }

    private sealed class WarehouseProductionStatePalletAggregateBuilder
    {
        public List<WarehouseProductionStatePalletRow> Rows { get; } = new();
        public double PlannedQty { get; set; }
        public double FilledQty { get; set; }
        public HashSet<long> PlannedPalletIds { get; } = new();
        public HashSet<long> FilledPalletIds { get; } = new();
        public bool HasFilledWithoutLedger { get; set; }
        public bool HasStalePalletAfterFullShipment { get; set; }

        public WarehouseProductionStatePalletAggregate ToAggregate()
        {
            return new WarehouseProductionStatePalletAggregate
            {
                Rows = Rows
                    .OrderBy(row => row.PrdRef, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.HuCode, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                PlannedQty = PlannedQty,
                FilledQty = FilledQty,
                PlannedCount = PlannedPalletIds.Count,
                FilledCount = FilledPalletIds.Count,
                HasFilledWithoutLedger = HasFilledWithoutLedger,
                HasStalePalletAfterFullShipment = HasStalePalletAfterFullShipment
            };
        }
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
  AND o.order_type = @customer_order_type
  AND o.status <> @shipped_status
  AND o.status <> @cancelled_status
  AND o.status <> @merged_status
  AND (@exclude_order_id::bigint IS NULL OR p.order_id <> @exclude_order_id::bigint)

UNION

SELECT DISTINCT p.hu_code
FROM production_pallets p
INNER JOIN orders o ON o.id = p.order_id
WHERE p.hu_code IS NOT NULL
  AND p.hu_code <> ''
  AND p.status = @filled_status
  AND o.order_type = @customer_order_type
  AND o.status <> @shipped_status
  AND o.status <> @cancelled_status
  AND o.status <> @merged_status
  AND (@exclude_order_id::bigint IS NULL OR p.order_id <> @exclude_order_id::bigint);
");
            command.Parameters.AddWithValue("@customer_order_type", OrderStatusMapper.TypeToString(OrderType.Customer));
            command.Parameters.AddWithValue("@shipped_status", OrderStatusMapper.StatusToString(OrderStatus.Shipped));
            command.Parameters.AddWithValue("@cancelled_status", OrderStatusMapper.StatusToString(OrderStatus.Cancelled));
            command.Parameters.AddWithValue("@merged_status", OrderStatusMapper.StatusToString(OrderStatus.Merged));
            command.Parameters.AddWithValue("@filled_status", ProductionPalletStatus.Filled);
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

    public bool HasPendingReadyHuBinding()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
WITH active_orders AS (
    SELECT id
    FROM orders
    WHERE order_type = @customer_order_type
      AND status IN (@in_progress_status, @accepted_status)
),
active_order_lines AS (
    SELECT ol.id AS order_line_id,
           ol.order_id,
           ol.item_id,
           GREATEST(0, ol.qty_ordered)::double precision AS qty_ordered
    FROM order_lines ol
    INNER JOIN active_orders ao ON ao.id = ol.order_id
    WHERE ol.item_id > 0
      AND ol.qty_ordered > @qty_tolerance
),
ledger_stock AS (
    SELECT led.item_id,
           UPPER(BTRIM(COALESCE(led.hu_code, led.hu))) AS hu_code,
           SUM(led.qty_delta)::double precision AS qty
    FROM ledger led
    INNER JOIN (SELECT DISTINCT item_id FROM active_order_lines) items ON items.item_id = led.item_id
    WHERE NULLIF(BTRIM(COALESCE(led.hu_code, led.hu)), '') IS NOT NULL
    GROUP BY led.item_id, UPPER(BTRIM(COALESCE(led.hu_code, led.hu)))
    HAVING SUM(led.qty_delta) > @qty_tolerance
),
reserved_hu AS (
    SELECT p.item_id,
           UPPER(BTRIM(p.to_hu)) AS hu_code
    FROM order_receipt_plan_lines p
    INNER JOIN orders o ON o.id = p.order_id
    WHERE p.qty_planned > @qty_tolerance
      AND NULLIF(BTRIM(p.to_hu), '') IS NOT NULL
      AND o.order_type = @customer_order_type
      AND o.status NOT IN (@shipped_status, @cancelled_status, @merged_status)

    UNION

    SELECT p.item_id,
           UPPER(BTRIM(p.hu_code)) AS hu_code
    FROM production_pallets p
    INNER JOIN orders o ON o.id = p.order_id
    WHERE p.order_id IS NOT NULL
      AND p.status = @filled_pallet_status
      AND NULLIF(BTRIM(p.hu_code), '') IS NOT NULL
      AND o.order_type = @customer_order_type
      AND o.status NOT IN (@shipped_status, @cancelled_status, @merged_status)
),
free_ledger_hu AS (
    SELECT stock.item_id,
           stock.hu_code,
           stock.qty
    FROM ledger_stock stock
    WHERE NOT EXISTS (
        SELECT 1
        FROM reserved_hu reserved
        WHERE reserved.item_id = stock.item_id
          AND reserved.hu_code = stock.hu_code
    )
),
current_bound_by_line AS (
    SELECT p.order_line_id,
           SUM(GREATEST(0, p.qty_planned))::double precision AS qty
    FROM order_receipt_plan_lines p
    INNER JOIN active_order_lines ol ON ol.order_line_id = p.order_line_id
                                   AND ol.order_id = p.order_id
                                   AND ol.item_id = p.item_id
    WHERE p.qty_planned > @qty_tolerance
      AND NULLIF(BTRIM(p.to_hu), '') IS NOT NULL
    GROUP BY p.order_line_id
),
open_pallet_qty_by_line AS (
    SELECT source.order_line_id,
           SUM(source.qty)::double precision AS qty
    FROM (
        SELECT pll.order_line_id,
               GREATEST(0, pll.planned_qty)::double precision AS qty
        FROM production_pallets pp
        INNER JOIN docs d ON d.id = pp.prd_doc_id
        INNER JOIN production_pallet_lines pll ON pll.production_pallet_id = pp.id
        INNER JOIN active_order_lines ol ON ol.order_line_id = pll.order_line_id
                                       AND ol.order_id = pp.order_id
                                       AND ol.item_id = pll.item_id
        WHERE d.type = @production_doc_type
          AND d.status <> @closed_doc_status
          AND pp.status IN (@planned_pallet_status, @printed_pallet_status)
          AND pll.order_line_id IS NOT NULL
          AND pll.planned_qty > @qty_tolerance

        UNION ALL

        SELECT pp.order_line_id,
               GREATEST(0, pp.planned_qty)::double precision AS qty
        FROM production_pallets pp
        INNER JOIN docs d ON d.id = pp.prd_doc_id
        INNER JOIN active_order_lines ol ON ol.order_line_id = pp.order_line_id
                                       AND ol.order_id = pp.order_id
                                       AND ol.item_id = pp.item_id
        WHERE d.type = @production_doc_type
          AND d.status <> @closed_doc_status
          AND pp.status IN (@planned_pallet_status, @printed_pallet_status)
          AND pp.order_line_id IS NOT NULL
          AND pp.planned_qty > @qty_tolerance
          AND NOT EXISTS (
              SELECT 1
              FROM production_pallet_lines pll
              WHERE pll.production_pallet_id = pp.id
          )
    ) source
    GROUP BY source.order_line_id
),
ledger_doc_item_hu AS (
    SELECT led.doc_id,
           led.item_id,
           NULLIF(UPPER(BTRIM(COALESCE(led.hu_code, led.hu))), '') AS hu_code,
           SUM(led.qty_delta)::double precision AS qty
    FROM ledger led
    WHERE led.doc_id IS NOT NULL
      AND led.qty_delta > @qty_tolerance
    GROUP BY led.doc_id, led.item_id, NULLIF(UPPER(BTRIM(COALESCE(led.hu_code, led.hu))), '')
),
produced_by_line AS (
    SELECT source.order_line_id,
           SUM(source.qty)::double precision AS qty
    FROM (
        SELECT pll.order_line_id,
               LEAST(
                   CASE WHEN pll.filled_qty > @qty_tolerance THEN pll.filled_qty ELSE pll.planned_qty END,
                   GREATEST(0, COALESCE(ledger.qty, 0)))::double precision AS qty
        FROM production_pallets pp
        INNER JOIN production_pallet_lines pll ON pll.production_pallet_id = pp.id
        INNER JOIN active_order_lines ol ON ol.order_line_id = pll.order_line_id
                                       AND ol.order_id = pp.order_id
                                       AND ol.item_id = pll.item_id
        LEFT JOIN ledger_doc_item_hu ledger ON ledger.doc_id = pp.prd_doc_id
                                           AND ledger.item_id = pll.item_id
                                           AND ledger.hu_code IS NOT DISTINCT FROM NULLIF(UPPER(BTRIM(pp.hu_code)), '')
        WHERE pp.status = @filled_pallet_status
          AND pll.order_line_id IS NOT NULL
          AND (pll.planned_qty > @qty_tolerance OR pll.filled_qty > @qty_tolerance)

        UNION ALL

        SELECT pp.order_line_id,
               LEAST(
                   pp.planned_qty,
                   GREATEST(0, COALESCE(ledger.qty, 0)))::double precision AS qty
        FROM production_pallets pp
        INNER JOIN active_order_lines ol ON ol.order_line_id = pp.order_line_id
                                       AND ol.order_id = pp.order_id
                                       AND ol.item_id = pp.item_id
        LEFT JOIN ledger_doc_item_hu ledger ON ledger.doc_id = pp.prd_doc_id
                                           AND ledger.item_id = pp.item_id
                                           AND ledger.hu_code IS NOT DISTINCT FROM NULLIF(UPPER(BTRIM(pp.hu_code)), '')
        WHERE pp.status = @filled_pallet_status
          AND pp.order_line_id IS NOT NULL
          AND pp.planned_qty > @qty_tolerance
          AND NOT EXISTS (
              SELECT 1
              FROM production_pallet_lines pll
              WHERE pll.production_pallet_id = pp.id
          )

        UNION ALL

        SELECT dl.order_line_id,
               LEAST(
                   dl.qty,
                   GREATEST(0, COALESCE(ledger.qty, 0)))::double precision AS qty
        FROM doc_lines dl
        INNER JOIN docs d ON d.id = dl.doc_id
        INNER JOIN active_order_lines ol ON ol.order_line_id = dl.order_line_id
                                       AND ol.order_id = d.order_id
                                       AND ol.item_id = dl.item_id
        LEFT JOIN ledger_doc_item_hu ledger ON ledger.doc_id = dl.doc_id
                                           AND ledger.item_id = dl.item_id
                                           AND ledger.hu_code IS NOT DISTINCT FROM NULLIF(UPPER(BTRIM(dl.to_hu)), '')
        WHERE d.type = @production_doc_type
          AND d.status = @closed_doc_status
          AND dl.order_line_id IS NOT NULL
          AND dl.qty > @qty_tolerance
          AND NOT EXISTS (
              SELECT 1
              FROM production_pallets pp
              WHERE pp.prd_doc_id = d.id
                AND pp.status <> @cancelled_pallet_status
          )
    ) source
    WHERE source.qty > @qty_tolerance
    GROUP BY source.order_line_id
),
compatible_lines AS (
    SELECT ol.order_line_id,
           ol.item_id,
           GREATEST(
               0,
               ol.qty_ordered
               - COALESCE(produced.qty, 0)
               - COALESCE(open_pallet.qty, 0)
               - COALESCE(current_bound.qty, 0))::double precision AS max_additional_qty
    FROM active_order_lines ol
    LEFT JOIN produced_by_line produced ON produced.order_line_id = ol.order_line_id
    LEFT JOIN open_pallet_qty_by_line open_pallet ON open_pallet.order_line_id = ol.order_line_id
    LEFT JOIN current_bound_by_line current_bound ON current_bound.order_line_id = ol.order_line_id
)
SELECT EXISTS (
    SELECT 1
    FROM compatible_lines line
    INNER JOIN free_ledger_hu hu ON hu.item_id = line.item_id
    WHERE line.max_additional_qty > @qty_tolerance
      AND hu.qty <= line.max_additional_qty + @qty_tolerance
    LIMIT 1
);");
            command.Parameters.AddWithValue("@customer_order_type", OrderStatusMapper.TypeToString(OrderType.Customer));
            command.Parameters.AddWithValue("@in_progress_status", OrderStatusMapper.StatusToString(OrderStatus.InProgress));
            command.Parameters.AddWithValue("@accepted_status", OrderStatusMapper.StatusToString(OrderStatus.Accepted));
            command.Parameters.AddWithValue("@shipped_status", OrderStatusMapper.StatusToString(OrderStatus.Shipped));
            command.Parameters.AddWithValue("@cancelled_status", OrderStatusMapper.StatusToString(OrderStatus.Cancelled));
            command.Parameters.AddWithValue("@merged_status", OrderStatusMapper.StatusToString(OrderStatus.Merged));
            command.Parameters.AddWithValue("@production_doc_type", DocTypeMapper.ToOpString(DocType.ProductionReceipt));
            command.Parameters.AddWithValue("@closed_doc_status", DocTypeMapper.StatusToString(DocStatus.Closed));
            command.Parameters.AddWithValue("@planned_pallet_status", ProductionPalletStatus.Planned);
            command.Parameters.AddWithValue("@printed_pallet_status", ProductionPalletStatus.Printed);
            command.Parameters.AddWithValue("@filled_pallet_status", ProductionPalletStatus.Filled);
            command.Parameters.AddWithValue("@cancelled_pallet_status", ProductionPalletStatus.Cancelled);
            command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
            return Convert.ToBoolean(command.ExecuteScalar() ?? false, CultureInfo.InvariantCulture);
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

    public void ReplaceOrderReceiptPlanLinesForOrderLines(
        long orderId,
        IReadOnlyCollection<long> orderLineIds,
        IReadOnlyList<OrderReceiptPlanLine> replacementLines)
    {
        var affectedLineIds = (orderLineIds ?? Array.Empty<long>())
            .Where(lineId => lineId > 0)
            .Distinct()
            .ToArray();
        var lines = replacementLines ?? Array.Empty<OrderReceiptPlanLine>();

        WithConnection(connection =>
        {
            var ownsTransaction = _transaction == null;
            if (ownsTransaction)
            {
                using var begin = connection.CreateCommand();
                begin.CommandText = "BEGIN;";
                begin.ExecuteNonQuery();
            }

            try
            {
                var normalizedHuCodes = lines
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

                if (affectedLineIds.Length > 0)
                {
                    using var deleteCommand = CreateCommand(connection, @"
DELETE FROM order_receipt_plan_lines
WHERE order_id = @order_id
  AND order_line_id = ANY(@order_line_ids);
");
                    deleteCommand.Parameters.AddWithValue("@order_id", orderId);
                    deleteCommand.Parameters.AddWithValue("@order_line_ids", affectedLineIds);
                    deleteCommand.ExecuteNonQuery();
                }

                if (lines.Count > 0)
                {
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
                }

                if (ownsTransaction)
                {
                    using var commit = connection.CreateCommand();
                    commit.CommandText = "COMMIT;";
                    commit.ExecuteNonQuery();
                }
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
       GREATEST(0, ol.qty_ordered - COALESCE(s.sum_qty, 0)) AS remaining
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
            var orderId = GetOrderIdByOrderLineId(connection, orderLineId);
            if (orderId.HasValue)
            {
                DeleteOrderLinesCore(connection, orderId.Value, [orderLineId]);
            }
            return 0;
        });
    }

    public void DeleteOrderLines(long orderId)
    {
        WithConnection(connection =>
        {
            DeleteOrderLinesCore(connection, orderId, GetOrderLineIds(connection, orderId));
            return 0;
        });
    }

    public void DeleteOrder(long orderId)
    {
        WithConnection(connection =>
        {
            DeleteOrderLinesCore(connection, orderId, GetOrderLineIds(connection, orderId));

            using (var deleteOrderCommand = CreateCommand(connection, "DELETE FROM orders WHERE id = @id"))
            {
                deleteOrderCommand.Parameters.AddWithValue("@id", orderId);
                deleteOrderCommand.ExecuteNonQuery();
            }
            return 0;
        });
    }

    private void DeleteOrderLinesCore(NpgsqlConnection connection, long orderId, IReadOnlyCollection<long> orderLineIds)
    {
        var ids = orderLineIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        EnsureOrderLinesCanBeDeleted(connection, orderId, ids);
        ClearRemovableProductionPalletPlanForOrderLines(connection, orderId, ids);

        using (var deletePlanCommand = CreateCommand(connection, "DELETE FROM order_receipt_plan_lines WHERE order_line_id = ANY(@order_line_ids)"))
        {
            deletePlanCommand.Parameters.AddWithValue("@order_line_ids", ids);
            deletePlanCommand.ExecuteNonQuery();
        }

        using (var deleteLinesCommand = CreateCommand(connection, "DELETE FROM order_lines WHERE id = ANY(@order_line_ids)"))
        {
            deleteLinesCommand.Parameters.AddWithValue("@order_line_ids", ids);
            deleteLinesCommand.ExecuteNonQuery();
        }
    }

    private void EnsureOrderLinesCanBeDeleted(NpgsqlConnection connection, long orderId, IReadOnlyCollection<long> orderLineIds)
    {
        var ids = orderLineIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        using var command = CreateCommand(connection, @"
WITH target_lines AS (
    SELECT ol.id,
           ol.item_id,
           COALESCE(NULLIF(BTRIM(i.name), ''), 'Строка заказа') AS item_name
    FROM order_lines ol
    LEFT JOIN items i ON i.id = ol.item_id
    WHERE ol.order_id = @order_id
      AND ol.id = ANY(@order_line_ids)
),
active_pallets AS (
    SELECT DISTINCT
           tl.id AS order_line_id,
           tl.item_name,
           pp.id AS pallet_id,
           pp.hu_code,
           COALESCE(NULLIF(BTRIM(pp.status), ''), @planned_status) AS status
    FROM target_lines tl
    INNER JOIN production_pallets pp ON pp.order_id = @order_id
    LEFT JOIN production_pallet_lines pll ON pll.production_pallet_id = pp.id
                                         AND pll.order_line_id = tl.id
    WHERE pp.order_line_id = tl.id
       OR pll.id IS NOT NULL
)
SELECT order_line_id,
       item_name,
       pallet_id,
       hu_code,
       status
FROM active_pallets
ORDER BY order_line_id, pallet_id;
");
        command.Parameters.AddWithValue("@order_id", orderId);
        command.Parameters.AddWithValue("@order_line_ids", ids);
        command.Parameters.AddWithValue("@planned_status", ProductionPalletStatus.Planned);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var itemName = reader.GetString(1);
            var status = reader.GetString(4);
            if (string.Equals(status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{itemName}: нельзя удалить строку, есть заполненные паллеты/HU.");
            }

            throw new InvalidOperationException($"{itemName}: нельзя удалить строку, паллетный план уже напечатан или находится в фактическом состоянии.");
        }
    }

    private void ClearRemovableProductionPalletPlanForOrderLines(
        NpgsqlConnection connection,
        long orderId,
        IReadOnlyCollection<long> orderLineIds)
    {
        var ids = orderLineIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        using (var cleanupPlannedPallets = CreateCommand(connection, @"
WITH planned_pallets AS (
    SELECT pp.id, pp.doc_line_id, pp.order_line_id
    FROM production_pallets pp
    INNER JOIN docs d ON d.id = pp.prd_doc_id
    WHERE pp.order_id = @order_id
      AND pp.status = @planned_status
      AND d.type = @production_receipt_type
      AND d.status = @draft_status
),
target_pallet_lines AS (
    SELECT pll.id, pll.doc_line_id, pll.production_pallet_id
    FROM production_pallet_lines pll
    INNER JOIN planned_pallets pp ON pp.id = pll.production_pallet_id
    WHERE pll.order_line_id = ANY(@order_line_ids)
),
target_header_pallets AS (
    SELECT pp.id, pp.doc_line_id
    FROM planned_pallets pp
    WHERE pp.order_line_id = ANY(@order_line_ids)
),
affected_pallets AS (
    SELECT production_pallet_id AS id FROM target_pallet_lines
    UNION
    SELECT id FROM target_header_pallets
),
target_doc_lines AS (
    SELECT DISTINCT doc_line_id AS id FROM target_pallet_lines
    UNION
    SELECT DISTINCT doc_line_id AS id FROM target_header_pallets WHERE doc_line_id IS NOT NULL
),
deleted_pallet_lines AS (
    DELETE FROM production_pallet_lines pll
    USING target_pallet_lines target
    WHERE pll.id = target.id
    RETURNING pll.production_pallet_id
),
empty_pallets AS (
    SELECT pp.id
    FROM production_pallets pp
    WHERE pp.id IN (SELECT id FROM affected_pallets)
      AND NOT EXISTS (
          SELECT 1
          FROM production_pallet_lines remaining
          WHERE remaining.production_pallet_id = pp.id
      )
),
deleted_pallets AS (
    DELETE FROM production_pallets pp
    USING empty_pallets target
    WHERE pp.id = target.id
    RETURNING pp.id
),
remaining_pallets AS (
    SELECT pp.id,
           MIN(pll.doc_line_id) AS doc_line_id,
           CASE WHEN COUNT(DISTINCT pll.order_line_id) = 1 THEN MIN(pll.order_line_id) ELSE NULL END AS order_line_id,
           MIN(pll.item_id) AS item_id,
           SUM(pll.planned_qty) AS planned_qty
    FROM production_pallets pp
    INNER JOIN production_pallet_lines pll ON pll.production_pallet_id = pp.id
    WHERE pp.id IN (SELECT id FROM affected_pallets)
      AND pp.id NOT IN (SELECT id FROM deleted_pallets)
    GROUP BY pp.id
),
updated_pallets AS (
    UPDATE production_pallets pp
    SET doc_line_id = remaining.doc_line_id,
        order_line_id = remaining.order_line_id,
        item_id = remaining.item_id,
        planned_qty = remaining.planned_qty
    FROM remaining_pallets remaining
    WHERE pp.id = remaining.id
    RETURNING pp.id
)
DELETE FROM doc_lines dl
USING target_doc_lines target
WHERE dl.id = target.id
  AND NOT EXISTS (
      SELECT 1
      FROM production_pallet_lines pll
      WHERE pll.doc_line_id = dl.id
  );
"))
        {
            cleanupPlannedPallets.Parameters.AddWithValue("@order_id", orderId);
            cleanupPlannedPallets.Parameters.AddWithValue("@order_line_ids", ids);
            cleanupPlannedPallets.Parameters.AddWithValue("@planned_status", ProductionPalletStatus.Planned);
            cleanupPlannedPallets.Parameters.AddWithValue("@production_receipt_type", DocTypeMapper.ToOpString(DocType.ProductionReceipt));
            cleanupPlannedPallets.Parameters.AddWithValue("@draft_status", DocTypeMapper.StatusToString(DocStatus.Draft));
            cleanupPlannedPallets.ExecuteNonQuery();
        }

        using (var cleanupCancelledPalletLines = CreateCommand(connection, @"
UPDATE production_pallet_lines pll
SET order_line_id = NULL
FROM production_pallets pp
INNER JOIN docs d ON d.id = pp.prd_doc_id
WHERE pp.id = pll.production_pallet_id
  AND pp.order_id = @order_id
  AND pp.status = @cancelled_status
  AND d.type = @production_receipt_type
  AND d.status = @draft_status
  AND pll.order_line_id = ANY(@order_line_ids);
"))
        {
            cleanupCancelledPalletLines.Parameters.AddWithValue("@order_id", orderId);
            cleanupCancelledPalletLines.Parameters.AddWithValue("@order_line_ids", ids);
            cleanupCancelledPalletLines.Parameters.AddWithValue("@cancelled_status", ProductionPalletStatus.Cancelled);
            cleanupCancelledPalletLines.Parameters.AddWithValue("@production_receipt_type", DocTypeMapper.ToOpString(DocType.ProductionReceipt));
            cleanupCancelledPalletLines.Parameters.AddWithValue("@draft_status", DocTypeMapper.StatusToString(DocStatus.Draft));
            cleanupCancelledPalletLines.ExecuteNonQuery();
        }

        using (var cleanupCancelledPallets = CreateCommand(connection, @"
UPDATE production_pallets pp
SET order_line_id = NULL
FROM docs d
WHERE d.id = pp.prd_doc_id
  AND pp.order_id = @order_id
  AND pp.status = @cancelled_status
  AND d.type = @production_receipt_type
  AND d.status = @draft_status
  AND pp.order_line_id = ANY(@order_line_ids);
"))
        {
            cleanupCancelledPallets.Parameters.AddWithValue("@order_id", orderId);
            cleanupCancelledPallets.Parameters.AddWithValue("@order_line_ids", ids);
            cleanupCancelledPallets.Parameters.AddWithValue("@cancelled_status", ProductionPalletStatus.Cancelled);
            cleanupCancelledPallets.Parameters.AddWithValue("@production_receipt_type", DocTypeMapper.ToOpString(DocType.ProductionReceipt));
            cleanupCancelledPallets.Parameters.AddWithValue("@draft_status", DocTypeMapper.StatusToString(DocStatus.Draft));
            cleanupCancelledPallets.ExecuteNonQuery();
        }

        using (var cleanupDocLines = CreateCommand(connection, @"
UPDATE doc_lines dl
SET order_line_id = NULL
FROM docs d
WHERE d.id = dl.doc_id
  AND d.order_id = @order_id
  AND d.type = @production_receipt_type
  AND d.status = @draft_status
  AND dl.order_line_id = ANY(@order_line_ids);
"))
        {
            cleanupDocLines.Parameters.AddWithValue("@order_id", orderId);
            cleanupDocLines.Parameters.AddWithValue("@order_line_ids", ids);
            cleanupDocLines.Parameters.AddWithValue("@production_receipt_type", DocTypeMapper.ToOpString(DocType.ProductionReceipt));
            cleanupDocLines.Parameters.AddWithValue("@draft_status", DocTypeMapper.StatusToString(DocStatus.Draft));
            cleanupDocLines.ExecuteNonQuery();
        }
    }

    public OrderProducedStockReleaseResult ReleaseProducedCustomerStockForOrderLine(long orderId, long orderLineId)
    {
        if (_connection != null && _transaction != null)
        {
            return ReleaseProducedCustomerStockForOrderLineCore(_connection, orderId, orderLineId);
        }

        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var scoped = new PostgresDataStore(connection, transaction);
        var result = scoped.ReleaseProducedCustomerStockForOrderLineCore(connection, orderId, orderLineId);
        transaction.Commit();
        return result;
    }

    private OrderProducedStockReleaseResult ReleaseProducedCustomerStockForOrderLineCore(
        NpgsqlConnection connection,
        long orderId,
        long orderLineId)
    {
        using (var orderCommand = CreateCommand(connection, @"
SELECT order_type, status
FROM orders
WHERE id = @order_id
FOR UPDATE;
"))
        {
            orderCommand.Parameters.AddWithValue("@order_id", orderId);
            using var reader = orderCommand.ExecuteReader();
            if (!reader.Read())
            {
                throw new OrderProducedStockReleaseException("ORDER_NOT_FOUND", "Заказ не найден.");
            }

            var orderType = reader.GetString(0);
            var orderStatus = reader.GetString(1);
            if (!string.Equals(orderType, OrderStatusMapper.TypeToString(OrderType.Customer), StringComparison.OrdinalIgnoreCase))
            {
                throw new OrderProducedStockReleaseException("ORDER_RELEASE_CUSTOMER_ONLY", "Операция доступна только для клиентского заказа.");
            }

            if (string.Equals(orderStatus, OrderStatusMapper.StatusToString(OrderStatus.Shipped), StringComparison.OrdinalIgnoreCase)
                || string.Equals(orderStatus, OrderStatusMapper.StatusToString(OrderStatus.Cancelled), StringComparison.OrdinalIgnoreCase)
                || string.Equals(orderStatus, OrderStatusMapper.StatusToString(OrderStatus.Merged), StringComparison.OrdinalIgnoreCase))
            {
                throw new OrderProducedStockReleaseException("ORDER_NOT_EDITABLE", "Заказ нельзя редактировать.");
            }
        }

        using (var lineCommand = CreateCommand(connection, @"
SELECT COUNT(*) FILTER (WHERE id = @order_line_id) AS target_count,
       COUNT(*) AS line_count
FROM order_lines
WHERE order_id = @order_id;
"))
        {
            lineCommand.Parameters.AddWithValue("@order_id", orderId);
            lineCommand.Parameters.AddWithValue("@order_line_id", orderLineId);
            using var reader = lineCommand.ExecuteReader();
            reader.Read();
            var targetCount = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            var lineCount = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
            if (targetCount == 0)
            {
                throw new OrderProducedStockReleaseException("ORDER_LINE_NOT_FOUND", "Строка заказа не найдена.");
            }

            if (lineCount <= 1)
            {
                throw new OrderProducedStockReleaseException(
                    "ORDER_RELEASE_LAST_LINE_FORBIDDEN",
                    "Нельзя удалить последнюю строку активного клиентского заказа.");
            }
        }

        using (var shippedCommand = CreateCommand(connection, @"
SELECT COALESCE(SUM(dl.qty), 0)
FROM docs d
INNER JOIN doc_lines dl ON dl.doc_id = d.id
WHERE d.type = @outbound_type
  AND d.status = @closed_status
  AND d.order_id = @order_id
  AND dl.order_line_id = @order_line_id
  AND dl.qty > 0
  AND NOT EXISTS (
      SELECT 1
      FROM doc_lines newer
      WHERE newer.replaces_line_id = dl.id
  );
"))
        {
            shippedCommand.Parameters.AddWithValue("@outbound_type", DocTypeMapper.ToOpString(DocType.Outbound));
            shippedCommand.Parameters.AddWithValue("@closed_status", DocTypeMapper.StatusToString(DocStatus.Closed));
            shippedCommand.Parameters.AddWithValue("@order_id", orderId);
            shippedCommand.Parameters.AddWithValue("@order_line_id", orderLineId);
            var shippedQty = Convert.ToDouble(shippedCommand.ExecuteScalar() ?? 0d, CultureInfo.InvariantCulture);
            if (shippedQty > StockQuantityRules.QtyTolerance)
            {
                throw new OrderProducedStockReleaseException(
                    "ORDER_LINE_HAS_SHIPPED_QTY",
                    "По строке уже есть отгруженное количество.");
            }
        }

        var targetPallets = new List<ReleaseTargetPallet>();
        using (var palletCommand = CreateCommand(connection, @"
WITH target_pallets AS (
    SELECT DISTINCT pp.id
    FROM production_pallets pp
    LEFT JOIN production_pallet_lines pll ON pll.production_pallet_id = pp.id
    WHERE pp.order_id = @order_id
      AND COALESCE(NULLIF(BTRIM(pp.status), ''), @planned_status) <> @cancelled_status
      AND (pp.order_line_id = @order_line_id OR pll.order_line_id = @order_line_id)
),
qty_by_pallet AS (
    SELECT pp.id,
           COALESCE(
               SUM(
                   CASE WHEN pll.order_line_id = @order_line_id
                        THEN CASE WHEN pll.filled_qty > @qty_tolerance THEN pll.filled_qty ELSE pll.planned_qty END
                        ELSE 0
                   END),
               0) AS component_qty
    FROM production_pallets pp
    LEFT JOIN production_pallet_lines pll ON pll.production_pallet_id = pp.id
    WHERE pp.id IN (SELECT id FROM target_pallets)
    GROUP BY pp.id
)
SELECT pp.id,
       pp.prd_doc_id,
       pp.doc_line_id,
       pp.hu_code,
       pp.status,
       CASE WHEN q.component_qty > @qty_tolerance THEN q.component_qty ELSE pp.planned_qty END AS release_qty
FROM production_pallets pp
INNER JOIN target_pallets target ON target.id = pp.id
INNER JOIN qty_by_pallet q ON q.id = pp.id
ORDER BY pp.id;
"))
        {
            palletCommand.Parameters.AddWithValue("@order_id", orderId);
            palletCommand.Parameters.AddWithValue("@order_line_id", orderLineId);
            palletCommand.Parameters.AddWithValue("@planned_status", ProductionPalletStatus.Planned);
            palletCommand.Parameters.AddWithValue("@cancelled_status", ProductionPalletStatus.Cancelled);
            palletCommand.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
            using var reader = palletCommand.ExecuteReader();
            while (reader.Read())
            {
                targetPallets.Add(new ReleaseTargetPallet(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    reader.IsDBNull(4) ? ProductionPalletStatus.Planned : reader.GetString(4),
                    Convert.ToDouble(reader.GetValue(5), CultureInfo.InvariantCulture)));
            }
        }

        if (targetPallets.Count == 0)
        {
            throw new OrderProducedStockReleaseException(
                "NO_FILLED_PALLETS_TO_RELEASE",
                "По строке нет выпущенных паллет для освобождения.");
        }

        if (targetPallets.Any(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)))
        {
            throw new OrderProducedStockReleaseException(
                "ORDER_LINE_RELEASE_REQUIRES_FILLED_PALLETS",
                "Освобождение доступно только когда все паллеты строки находятся в статусе FILLED.");
        }

        var palletIds = targetPallets.Select(pallet => pallet.Id).ToArray();
        using (var mixedCommand = CreateCommand(connection, @"
SELECT 1
FROM production_pallet_lines
WHERE production_pallet_id = ANY(@pallet_ids)
  AND order_line_id IS DISTINCT FROM @order_line_id
LIMIT 1;
"))
        {
            mixedCommand.Parameters.AddWithValue("@pallet_ids", palletIds);
            mixedCommand.Parameters.AddWithValue("@order_line_id", orderLineId);
            if (mixedCommand.ExecuteScalar() != null)
            {
                throw new OrderProducedStockReleaseException(
                    "MIXED_PALLET_RELEASE_NOT_SUPPORTED",
                    "Освобождение mixed/shared паллет пока не поддерживается.");
            }
        }

        var affectedDocIds = targetPallets.Select(pallet => pallet.PrdDocId).Distinct().ToArray();
        using (var deletePlan = CreateCommand(connection, @"
DELETE FROM order_receipt_plan_lines
WHERE order_id = @order_id
  AND order_line_id = @order_line_id;
"))
        {
            deletePlan.Parameters.AddWithValue("@order_id", orderId);
            deletePlan.Parameters.AddWithValue("@order_line_id", orderLineId);
            deletePlan.ExecuteNonQuery();
        }

        using (var updatePallets = CreateCommand(connection, @"
UPDATE production_pallets
SET order_id = NULL,
    order_line_id = NULL
WHERE id = ANY(@pallet_ids);
"))
        {
            updatePallets.Parameters.AddWithValue("@pallet_ids", palletIds);
            updatePallets.ExecuteNonQuery();
        }

        using (var updatePalletLines = CreateCommand(connection, @"
UPDATE production_pallet_lines
SET order_line_id = NULL
WHERE production_pallet_id = ANY(@pallet_ids)
  AND order_line_id = @order_line_id;
"))
        {
            updatePalletLines.Parameters.AddWithValue("@pallet_ids", palletIds);
            updatePalletLines.Parameters.AddWithValue("@order_line_id", orderLineId);
            updatePalletLines.ExecuteNonQuery();
        }

        using (var updateDocLines = CreateCommand(connection, @"
UPDATE doc_lines dl
SET order_line_id = NULL
WHERE dl.order_line_id = @order_line_id
  AND (
      dl.id IN (
          SELECT pp.doc_line_id
          FROM production_pallets pp
          WHERE pp.id = ANY(@pallet_ids)
      )
      OR dl.id IN (
          SELECT pll.doc_line_id
          FROM production_pallet_lines pll
          WHERE pll.production_pallet_id = ANY(@pallet_ids)
      )
  );
"))
        {
            updateDocLines.Parameters.AddWithValue("@order_line_id", orderLineId);
            updateDocLines.Parameters.AddWithValue("@pallet_ids", palletIds);
            updateDocLines.ExecuteNonQuery();
        }

        using (var updateDocs = CreateCommand(connection, @"
UPDATE docs d
SET order_id = NULL,
    order_ref = NULL
WHERE d.id = ANY(@doc_ids)
  AND d.type = @production_receipt_type
  AND NOT EXISTS (
      SELECT 1
      FROM doc_lines dl
      WHERE dl.doc_id = d.id
        AND dl.order_line_id IS NOT NULL
        AND dl.qty > 0
        AND NOT EXISTS (
            SELECT 1
            FROM doc_lines newer
            WHERE newer.replaces_line_id = dl.id
        )
  );
"))
        {
            updateDocs.Parameters.AddWithValue("@doc_ids", affectedDocIds);
            updateDocs.Parameters.AddWithValue("@production_receipt_type", DocTypeMapper.ToOpString(DocType.ProductionReceipt));
            updateDocs.ExecuteNonQuery();
        }

        using (var deleteLine = CreateCommand(connection, @"
DELETE FROM order_lines
WHERE id = @order_line_id
  AND order_id = @order_id;
"))
        {
            deleteLine.Parameters.AddWithValue("@order_id", orderId);
            deleteLine.Parameters.AddWithValue("@order_line_id", orderLineId);
            deleteLine.ExecuteNonQuery();
        }

        return new OrderProducedStockReleaseResult
        {
            OrderId = orderId,
            OrderLineId = orderLineId,
            ReleasedPalletCount = targetPallets.Count,
            ReleasedHuCodes = targetPallets
                .Select(pallet => pallet.HuCode)
                .Where(hu => !string.IsNullOrWhiteSpace(hu))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(hu => hu, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ReleasedQty = targetPallets.Sum(pallet => Math.Max(0, pallet.ReleaseQty))
        };
    }

    private long[] GetOrderLineIds(NpgsqlConnection connection, long orderId)
    {
        using var command = CreateCommand(connection, @"
SELECT id
FROM order_lines
WHERE order_id = @order_id
ORDER BY id;
");
        command.Parameters.AddWithValue("@order_id", orderId);
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids.ToArray();
    }

    private sealed record ReleaseTargetPallet(
        long Id,
        long PrdDocId,
        long DocLineId,
        string HuCode,
        string Status,
        double ReleaseQty);

    private long? GetOrderIdByOrderLineId(NpgsqlConnection connection, long orderLineId)
    {
        using var command = CreateCommand(connection, @"
SELECT order_id
FROM order_lines
WHERE id = @id;
");
        command.Parameters.AddWithValue("@id", orderLineId);
        var value = command.ExecuteScalar();
        return value == null || value is DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
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

    public IReadOnlyDictionary<long, double> GetReservedFilledHuQtyByOrderLine(long customerOrderId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, $@"
WITH {MarkingReservedFilledLedgerStockCte},
reserved_filled_hu_by_line AS (
    SELECT p.order_line_id,
           SUM(LEAST(p.qty_planned, ls.qty, fp.filled_qty)) AS reserved_filled_hu_qty
    FROM order_receipt_plan_lines p
    INNER JOIN ledger_stock_by_hu ls ON ls.item_id = p.item_id
                                    AND ls.hu_code = UPPER(BTRIM(p.to_hu))
    INNER JOIN filled_pallet_by_hu fp ON fp.item_id = p.item_id
                                     AND fp.hu_code = UPPER(BTRIM(p.to_hu))
    WHERE p.order_id = @order_id
      AND p.qty_planned > 0
      AND p.to_hu IS NOT NULL
      AND BTRIM(p.to_hu) <> ''
    GROUP BY p.order_line_id
)
SELECT order_line_id, reserved_filled_hu_qty
FROM reserved_filled_hu_by_line;
");
            command.Parameters.AddWithValue("@order_id", customerOrderId);
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
                command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
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

    public IReadOnlyList<HuReservationCandidateSourceRow> GetHuReservationCandidateSources(
        long? customerOrderId,
        IReadOnlyCollection<long> itemIds,
        IReadOnlyCollection<string> excludeHuCodes)
    {
        if (itemIds == null || itemIds.Count == 0)
        {
            return Array.Empty<HuReservationCandidateSourceRow>();
        }

        var normalizedItemIds = itemIds
            .Where(itemId => itemId > 0)
            .Distinct()
            .ToArray();
        if (normalizedItemIds.Length == 0)
        {
            return Array.Empty<HuReservationCandidateSourceRow>();
        }

        var normalizedExcludeHuCodes = (excludeHuCodes ?? Array.Empty<string>())
            .Select(code => string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, HuReservationCandidateSql.SelectSources);
            command.Parameters.AddWithValue("@item_ids", normalizedItemIds);
            command.Parameters.AddWithValue("@exclude_hu_codes", normalizedExcludeHuCodes);
            command.Parameters.AddWithValue("@customer_order_id", customerOrderId.HasValue ? customerOrderId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
            command.Parameters.AddWithValue("@customer_order_type", OrderStatusMapper.TypeToString(OrderType.Customer));
            command.Parameters.AddWithValue("@internal_order_type", OrderStatusMapper.TypeToString(OrderType.Internal));
            command.Parameters.AddWithValue("@filled_status", ProductionPalletStatus.Filled);
            command.Parameters.AddWithValue("@closed_status", DocTypeMapper.StatusToString(DocStatus.Closed));
            command.Parameters.AddWithValue("@draft_doc_status", DocTypeMapper.StatusToString(DocStatus.Draft));
            command.Parameters.AddWithValue("@draft_order_status", OrderStatusMapper.StatusToString(OrderStatus.Draft));
            command.Parameters.AddWithValue("@in_progress_order_status", OrderStatusMapper.StatusToString(OrderStatus.InProgress));
            command.Parameters.AddWithValue("@shipped_status", OrderStatusMapper.StatusToString(OrderStatus.Shipped));
            command.Parameters.AddWithValue("@cancelled_status", OrderStatusMapper.StatusToString(OrderStatus.Cancelled));
            command.Parameters.AddWithValue("@merged_status", OrderStatusMapper.StatusToString(OrderStatus.Merged));

            using var reader = command.ExecuteReader();
            var rows = new List<HuReservationCandidateSourceRow>();
            while (reader.Read())
            {
                rows.Add(new HuReservationCandidateSourceRow
                {
                    Source = reader.GetString(0),
                    HuCode = reader.GetString(1),
                    ItemId = reader.GetInt64(2),
                    Qty = reader.GetDouble(3),
                    SourceOrderId = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    SourceOrderRef = reader.IsDBNull(5) ? null : reader.GetString(5),
                    SourcePrdDocId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    SourcePrdRef = reader.IsDBNull(7) ? null : reader.GetString(7),
                    FirstReceiptAt = reader.IsDBNull(8) ? null : LedgerTimestampParser.TryParse(reader.GetString(8)),
                    FirstReceiptDocId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                    ShipReady = reader.GetBoolean(10),
                    ReservedByOrderId = reader.IsDBNull(11) ? null : reader.GetInt64(11),
                    ReservedByOrderRef = reader.IsDBNull(12) ? null : reader.GetString(12),
                    Note = reader.IsDBNull(13) ? string.Empty : reader.GetString(13)
                });
            }

            return rows;
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
HAVING ABS(COALESCE(SUM(qty_delta), 0)) > @qty_tolerance;
");
            command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
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

    public TsdHuFacts GetTsdHuFacts(string huCode)
    {
        var normalizedHu = (huCode ?? string.Empty).Trim().ToUpperInvariant();
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT status, created_at, closed_at
FROM hus
WHERE UPPER(BTRIM(hu_code)) = @hu_code
LIMIT 1;

SELECT l.item_id,
       i.name,
       COALESCE(i.base_uom, 'шт'),
       l.location_id,
       loc.code,
       SUM(l.qty_delta)::double precision
FROM ledger l
INNER JOIN items i ON i.id = l.item_id
INNER JOIN locations loc ON loc.id = l.location_id
WHERE UPPER(BTRIM(COALESCE(l.hu_code, l.hu))) = @hu_code
GROUP BY l.item_id, i.name, i.base_uom, l.location_id, loc.code
HAVING SUM(l.qty_delta) > @qty_tolerance
ORDER BY i.name, loc.code;

SELECT pp.id,
       pp.status,
       pp.prd_doc_id,
       d.doc_ref,
       d.status,
       pp.order_id,
       COALESCE(o.order_ref, d.order_ref),
       o.order_type,
       o.status,
       COALESCE(pp.pallet_no, 0),
       COALESCE(pp.pallet_count, 0),
       pp.filled_at,
       COALESCE(pll.item_id, pp.item_id),
       COALESCE(component.name, fallback_item.name),
       COALESCE(component.base_uom, fallback_item.base_uom, 'шт'),
       COALESCE(pll.planned_qty, pp.planned_qty)::double precision,
       COALESCE(pll.filled_qty, CASE WHEN UPPER(BTRIM(pp.status)) = 'FILLED' THEN pp.planned_qty ELSE 0 END)::double precision
FROM production_pallets pp
INNER JOIN docs d ON d.id = pp.prd_doc_id
LEFT JOIN orders o ON o.id = pp.order_id
LEFT JOIN production_pallet_lines pll ON pll.production_pallet_id = pp.id
LEFT JOIN items component ON component.id = pll.item_id
INNER JOIN items fallback_item ON fallback_item.id = pp.item_id
WHERE UPPER(BTRIM(pp.hu_code)) = @hu_code
ORDER BY pp.id, pll.id NULLS FIRST;

SELECT p.order_id,
       o.order_ref,
       o.order_type,
       o.status,
       p.item_id,
       i.name,
       SUM(p.qty_planned)::double precision
FROM order_receipt_plan_lines p
INNER JOIN orders o ON o.id = p.order_id
INNER JOIN items i ON i.id = p.item_id
WHERE UPPER(BTRIM(p.to_hu)) = @hu_code
  AND p.qty_planned > @qty_tolerance
GROUP BY p.order_id, o.order_ref, o.order_type, o.status, p.item_id, i.name
ORDER BY p.order_id, p.item_id;

SELECT d.id,
       d.doc_ref,
       d.type,
       d.status,
       d.order_id,
       COALESCE(o.order_ref, d.order_ref),
       o.order_type,
       o.status,
       CASE WHEN UPPER(BTRIM(dl.from_hu)) = @hu_code THEN 'FROM' ELSE 'TO' END,
       dl.item_id,
       i.name,
       COALESCE(i.base_uom, 'шт'),
       dl.qty::double precision,
       d.created_at,
       d.closed_at
FROM doc_lines dl
INNER JOIN docs d ON d.id = dl.doc_id
INNER JOIN items i ON i.id = dl.item_id
LEFT JOIN orders o ON o.id = d.order_id
WHERE dl.qty > @qty_tolerance
  AND (UPPER(BTRIM(dl.from_hu)) = @hu_code OR UPPER(BTRIM(dl.to_hu)) = @hu_code)
  AND NOT EXISTS (
      SELECT 1
      FROM doc_lines newer
      WHERE newer.replaces_line_id = dl.id
  )
ORDER BY d.id DESC, dl.id;

SELECT l.id,
       l.doc_id,
       d.doc_ref,
       d.type,
       l.item_id,
       i.name,
       loc.code,
       l.qty_delta::double precision,
       l.ts
FROM ledger l
INNER JOIN docs d ON d.id = l.doc_id
INNER JOIN items i ON i.id = l.item_id
INNER JOIN locations loc ON loc.id = l.location_id
WHERE UPPER(BTRIM(COALESCE(l.hu_code, l.hu))) = @hu_code
ORDER BY l.id DESC
LIMIT 1;
");
            command.Parameters.AddWithValue("@hu_code", normalizedHu);
            command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);

            using var reader = command.ExecuteReader();
            TsdHuRegistryFact? registry = null;
            if (reader.Read())
            {
                registry = new TsdHuRegistryFact
                {
                    Status = reader.GetString(0),
                    CreatedAt = FromDbDate(reader.GetString(1)) ?? DateTime.MinValue,
                    ClosedAt = FromDbDate(reader.IsDBNull(2) ? null : reader.GetString(2))
                };
            }

            reader.NextResult();
            var stock = new List<TsdHuStockFact>();
            while (reader.Read())
            {
                stock.Add(new TsdHuStockFact
                {
                    ItemId = reader.GetInt64(0),
                    ItemName = reader.GetString(1),
                    Uom = reader.GetString(2),
                    LocationId = reader.GetInt64(3),
                    LocationCode = reader.GetString(4),
                    Qty = reader.GetDouble(5)
                });
            }

            reader.NextResult();
            var palletRows = new List<TsdHuProductionPalletFact>();
            while (reader.Read())
            {
                palletRows.Add(new TsdHuProductionPalletFact
                {
                    PalletId = reader.GetInt64(0),
                    Status = reader.GetString(1),
                    PrdDocId = reader.GetInt64(2),
                    PrdDocRef = reader.GetString(3),
                    PrdDocStatus = reader.GetString(4),
                    OrderId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    OrderRef = reader.IsDBNull(6) ? null : reader.GetString(6),
                    OrderType = reader.IsDBNull(7) ? null : reader.GetString(7),
                    OrderStatus = reader.IsDBNull(8) ? null : reader.GetString(8),
                    PalletNo = reader.GetInt32(9),
                    PalletCount = reader.GetInt32(10),
                    FilledAt = FromDbDate(reader.IsDBNull(11) ? null : reader.GetString(11)),
                    Components = new[]
                    {
                        new TsdHuComponentFact
                        {
                            ItemId = reader.GetInt64(12),
                            ItemName = reader.GetString(13),
                            Uom = reader.GetString(14),
                            PlannedQty = reader.GetDouble(15),
                            FilledQty = reader.GetDouble(16)
                        }
                    }
                });
            }

            var pallets = palletRows
                .GroupBy(row => row.PalletId)
                .Select(group =>
                {
                    var row = group.First();
                    return new TsdHuProductionPalletFact
                    {
                        PalletId = row.PalletId,
                        Status = row.Status,
                        PrdDocId = row.PrdDocId,
                        PrdDocRef = row.PrdDocRef,
                        PrdDocStatus = row.PrdDocStatus,
                        OrderId = row.OrderId,
                        OrderRef = row.OrderRef,
                        OrderType = row.OrderType,
                        OrderStatus = row.OrderStatus,
                        PalletNo = row.PalletNo,
                        PalletCount = row.PalletCount,
                        FilledAt = row.FilledAt,
                        Components = group.SelectMany(item => item.Components).ToArray()
                    };
                })
                .ToArray();

            reader.NextResult();
            var reservations = new List<TsdHuReservationFact>();
            while (reader.Read())
            {
                reservations.Add(new TsdHuReservationFact
                {
                    OrderId = reader.GetInt64(0),
                    OrderRef = reader.GetString(1),
                    OrderType = reader.GetString(2),
                    OrderStatus = reader.GetString(3),
                    ItemId = reader.GetInt64(4),
                    ItemName = reader.GetString(5),
                    Qty = reader.GetDouble(6)
                });
            }

            reader.NextResult();
            var documents = new List<TsdHuDocumentFact>();
            while (reader.Read())
            {
                documents.Add(new TsdHuDocumentFact
                {
                    DocId = reader.GetInt64(0),
                    DocRef = reader.GetString(1),
                    DocType = reader.GetString(2),
                    DocStatus = reader.GetString(3),
                    OrderId = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    OrderRef = reader.IsDBNull(5) ? null : reader.GetString(5),
                    OrderType = reader.IsDBNull(6) ? null : reader.GetString(6),
                    OrderStatus = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Direction = reader.GetString(8),
                    ItemId = reader.GetInt64(9),
                    ItemName = reader.GetString(10),
                    Uom = reader.GetString(11),
                    Qty = reader.GetDouble(12),
                    CreatedAt = FromDbDate(reader.GetString(13)) ?? DateTime.MinValue,
                    ClosedAt = FromDbDate(reader.IsDBNull(14) ? null : reader.GetString(14))
                });
            }

            reader.NextResult();
            TsdHuMovementFact? latestMovement = null;
            if (reader.Read())
            {
                latestMovement = new TsdHuMovementFact
                {
                    LedgerId = reader.GetInt64(0),
                    DocId = reader.GetInt64(1),
                    DocRef = reader.GetString(2),
                    DocType = reader.GetString(3),
                    ItemId = reader.GetInt64(4),
                    ItemName = reader.GetString(5),
                    LocationCode = reader.GetString(6),
                    QtyDelta = reader.GetDouble(7),
                    Timestamp = LedgerTimestampParser.TryParse(reader.GetString(8)) ?? DateTime.MinValue
                };
            }

            return new TsdHuFacts
            {
                HuCode = normalizedHu,
                Registry = registry,
                Stock = stock,
                ProductionPallets = pallets,
                Reservations = reservations,
                Documents = documents,
                LatestMovement = latestMovement
            };
        });
    }

    public IReadOnlyList<ScopedOrderLineHuFateCandidate> GetScopedOrderLineHuFateCandidates(
        IReadOnlyCollection<ScopedOrderLineHuFateKey> keys)
    {
        var normalizedKeys = (keys ?? Array.Empty<ScopedOrderLineHuFateKey>())
            .Select(key => new ScopedOrderLineHuFateKey(
                key.ItemId,
                string.IsNullOrWhiteSpace(key.HuCode) ? string.Empty : key.HuCode.Trim().ToUpperInvariant()))
            .Where(key => key.ItemId > 0 && key.HuCode.Length > 0)
            .Distinct()
            .ToArray();
        if (normalizedKeys.Length == 0)
        {
            return Array.Empty<ScopedOrderLineHuFateCandidate>();
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
WITH requested_keys AS (
    SELECT requested.item_id,
           requested.hu_code
    FROM UNNEST(@item_ids::bigint[], @hu_codes::text[]) AS requested(item_id, hu_code)
),
stock_rows AS (
    SELECT 'STOCK'::text AS kind,
           requested.item_id,
           requested.hu_code,
           COALESCE(SUM(l.qty_delta), 0)::double precision AS qty,
           NULL::bigint AS target_order_id,
           NULL::bigint AS target_order_line_id,
           NULL::text AS target_order_ref,
           NULL::bigint AS doc_id,
           NULL::text AS doc_ref,
           NULL::text AS closed_at,
           NULL::text AS created_at
    FROM requested_keys requested
    INNER JOIN ledger l ON l.item_id = requested.item_id
                       AND UPPER(BTRIM(COALESCE(l.hu_code, l.hu))) = requested.hu_code
    GROUP BY requested.item_id, requested.hu_code
    HAVING COALESCE(SUM(l.qty_delta), 0) > @qty_tolerance
),
reservation_rows AS (
    SELECT 'RESERVATION'::text AS kind,
           requested.item_id,
           requested.hu_code,
           COALESCE(SUM(p.qty_planned), 0)::double precision AS qty,
           o.id AS target_order_id,
           p.order_line_id AS target_order_line_id,
           o.order_ref AS target_order_ref,
           NULL::bigint AS doc_id,
           NULL::text AS doc_ref,
           NULL::text AS closed_at,
           NULL::text AS created_at
    FROM requested_keys requested
    INNER JOIN order_receipt_plan_lines p ON p.item_id = requested.item_id
                                          AND UPPER(BTRIM(p.to_hu)) = requested.hu_code
    INNER JOIN orders o ON o.id = p.order_id
    WHERE o.order_type = @customer_order_type
      AND o.status NOT IN (@cancelled_order_status, @shipped_order_status, @merged_order_status)
      AND p.order_line_id > 0
      AND p.qty_planned > @qty_tolerance
    GROUP BY requested.item_id, requested.hu_code, o.id, p.order_line_id, o.order_ref
),
shipment_rows AS (
    SELECT 'SHIPMENT'::text AS kind,
           requested.item_id,
           requested.hu_code,
           COALESCE(SUM(dl.qty), 0)::double precision AS qty,
           o.id AS target_order_id,
           dl.order_line_id AS target_order_line_id,
           o.order_ref AS target_order_ref,
           d.id AS doc_id,
           d.doc_ref,
           d.closed_at,
           d.created_at
    FROM requested_keys requested
    INNER JOIN doc_lines dl ON dl.item_id = requested.item_id
                           AND UPPER(BTRIM(dl.from_hu)) = requested.hu_code
    INNER JOIN docs d ON d.id = dl.doc_id
    INNER JOIN orders o ON o.id = d.order_id
    WHERE d.type = @outbound_doc_type
      AND d.status = @closed_doc_status
      AND o.order_type = @customer_order_type
      AND dl.order_line_id IS NOT NULL
      AND dl.qty > @qty_tolerance
    GROUP BY requested.item_id,
             requested.hu_code,
             o.id,
             dl.order_line_id,
             o.order_ref,
             d.id,
             d.doc_ref,
             d.closed_at,
             d.created_at
)
SELECT kind,
       item_id,
       hu_code,
       qty,
       target_order_id,
       target_order_line_id,
       target_order_ref,
       doc_id,
       doc_ref,
       closed_at,
       created_at
FROM stock_rows
UNION ALL
SELECT * FROM reservation_rows
UNION ALL
SELECT * FROM shipment_rows;
");
            command.Parameters.Add("@item_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value =
                normalizedKeys.Select(key => key.ItemId).ToArray();
            command.Parameters.Add("@hu_codes", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                normalizedKeys.Select(key => key.HuCode).ToArray();
            command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
            command.Parameters.AddWithValue("@customer_order_type", OrderStatusMapper.TypeToString(OrderType.Customer));
            command.Parameters.AddWithValue("@cancelled_order_status", OrderStatusMapper.StatusToString(OrderStatus.Cancelled));
            command.Parameters.AddWithValue("@shipped_order_status", OrderStatusMapper.StatusToString(OrderStatus.Shipped));
            command.Parameters.AddWithValue("@merged_order_status", OrderStatusMapper.StatusToString(OrderStatus.Merged));
            command.Parameters.AddWithValue("@outbound_doc_type", DocTypeMapper.ToOpString(DocType.Outbound));
            command.Parameters.AddWithValue("@closed_doc_status", DocTypeMapper.StatusToString(DocStatus.Closed));

            using var reader = command.ExecuteReader();
            var rows = new List<ScopedOrderLineHuFateCandidate>();
            while (reader.Read())
            {
                rows.Add(new ScopedOrderLineHuFateCandidate
                {
                    Kind = reader.GetString(0),
                    ItemId = reader.GetInt64(1),
                    HuCode = reader.GetString(2),
                    Qty = reader.GetDouble(3),
                    TargetOrderId = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    TargetOrderLineId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    TargetOrderRef = reader.IsDBNull(6) ? null : reader.GetString(6),
                    DocId = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    DocRef = reader.IsDBNull(8) ? null : reader.GetString(8),
                    ClosedAt = reader.IsDBNull(9) ? null : LedgerTimestampParser.TryParse(reader.GetString(9)),
                    CreatedAt = reader.IsDBNull(10) ? null : LedgerTimestampParser.TryParse(reader.GetString(10))
                });
            }

            return rows;
        });
    }

    public IReadOnlyList<NegativeStockBalanceRow> GetNegativeStockBalances()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
WITH balances AS (
    SELECT led.item_id,
           led.location_id,
           NULLIF(BTRIM(COALESCE(led.hu_code, led.hu)), '') AS hu_code,
           SUM(led.qty_delta) AS qty
    FROM ledger led
    GROUP BY led.item_id, led.location_id, NULLIF(BTRIM(COALESCE(led.hu_code, led.hu)), '')
    HAVING SUM(led.qty_delta) < -@qty_tolerance
),
last_movement AS (
    SELECT DISTINCT ON (led.item_id, led.location_id, NULLIF(BTRIM(COALESCE(led.hu_code, led.hu)), ''))
           led.item_id,
           led.location_id,
           NULLIF(BTRIM(COALESCE(led.hu_code, led.hu)), '') AS hu_code,
           led.id AS last_ledger_entry_id,
           led.doc_id AS last_doc_id,
           led.ts AS last_movement_at
    FROM ledger led
    INNER JOIN balances b ON b.item_id = led.item_id
                        AND b.location_id = led.location_id
                        AND (
                            (b.hu_code IS NULL AND NULLIF(BTRIM(COALESCE(led.hu_code, led.hu)), '') IS NULL)
                            OR b.hu_code = NULLIF(BTRIM(COALESCE(led.hu_code, led.hu)), '')
                        )
    ORDER BY led.item_id,
             led.location_id,
             NULLIF(BTRIM(COALESCE(led.hu_code, led.hu)), ''),
             led.ts DESC,
             led.id DESC
)
SELECT b.item_id,
       i.name AS item_name,
       b.location_id,
       l.code AS location_code,
       b.hu_code,
       b.qty,
       lm.last_ledger_entry_id,
       lm.last_doc_id,
       d.doc_ref AS last_doc_ref,
       d.type AS last_doc_type,
       d.order_id,
       COALESCE(d.order_ref, o.order_ref) AS order_ref,
       lm.last_movement_at
FROM balances b
INNER JOIN items i ON i.id = b.item_id
INNER JOIN locations l ON l.id = b.location_id
LEFT JOIN last_movement lm ON lm.item_id = b.item_id
                            AND lm.location_id = b.location_id
                            AND (
                                (b.hu_code IS NULL AND lm.hu_code IS NULL)
                                OR b.hu_code = lm.hu_code
                            )
LEFT JOIN docs d ON d.id = lm.last_doc_id
LEFT JOIN orders o ON o.id = d.order_id
ORDER BY b.qty, i.name, l.code, b.hu_code;
");
            command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
            using var reader = command.ExecuteReader();
            var rows = new List<NegativeStockBalanceRow>();
            while (reader.Read())
            {
                var lastDocTypeText = reader.IsDBNull(9) ? null : reader.GetString(9);
                rows.Add(new NegativeStockBalanceRow
                {
                    ItemId = reader.GetInt64(0),
                    ItemName = reader.GetString(1),
                    LocationId = reader.GetInt64(2),
                    LocationCode = reader.GetString(3),
                    HuCode = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Qty = reader.GetDouble(5),
                    LastLedgerEntryId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    LastDocId = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    LastDocRef = reader.IsDBNull(8) ? null : reader.GetString(8),
                    LastDocType = string.IsNullOrWhiteSpace(lastDocTypeText)
                        ? null
                        : DocTypeMapper.FromOpString(lastDocTypeText),
                    OrderId = reader.IsDBNull(10) ? null : reader.GetInt64(10),
                    OrderRef = reader.IsDBNull(11) ? null : reader.GetString(11),
                    LastMovementAt = reader.IsDBNull(12)
                        ? null
                        : LedgerTimestampParser.TryParse(reader.GetString(12))
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
    HAVING ABS(COALESCE(SUM(qty_delta), 0)) > @qty_tolerance
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
           1 AS source_priority,
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
      AND o.status <> @merged_status

    UNION ALL

    SELECT p.item_id,
           UPPER(TRIM(p.hu_code)) AS hu_code,
           p.order_id AS reserved_customer_order_id,
           o.order_ref AS reserved_customer_order_ref,
           o.partner_id AS reserved_customer_id,
           partner.name AS reserved_customer_name,
           0 AS source_priority,
           o.created_at AS source_order_created_at,
           p.id AS source_id
    FROM production_pallets p
    INNER JOIN orders o ON o.id = p.order_id
    LEFT JOIN partners partner ON partner.id = o.partner_id
    WHERE p.order_id IS NOT NULL
      AND p.status = @filled_status
      AND o.order_type = @customer_order_type
      AND o.status <> @shipped_status
      AND o.status <> @cancelled_status
      AND o.status <> @merged_status
      AND p.hu_code IS NOT NULL
      AND p.hu_code <> ''

    UNION ALL

    SELECT dl.item_id,
           UPPER(TRIM(dl.to_hu)) AS hu_code,
           d.order_id AS reserved_customer_order_id,
           COALESCE(d.order_ref, o.order_ref) AS reserved_customer_order_ref,
           o.partner_id AS reserved_customer_id,
           partner.name AS reserved_customer_name,
           2 AS source_priority,
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
      AND o.status <> @merged_status
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
        command.Parameters.AddWithValue("@merged_status", OrderStatusMapper.StatusToString(OrderStatus.Merged));
        command.Parameters.AddWithValue("@filled_status", ProductionPalletStatus.Filled);
        command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);

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

    private static void AddTsdOutboundOrderRowsParameters(NpgsqlCommand command)
    {
        command.Parameters.AddWithValue("@customer_order_type", OrderStatusMapper.TypeToString(OrderType.Customer));
        command.Parameters.AddWithValue("@draft_order_status", OrderStatusMapper.StatusToString(OrderStatus.Draft));
        command.Parameters.AddWithValue("@accepted_order_status", OrderStatusMapper.StatusToString(OrderStatus.Accepted));
        command.Parameters.AddWithValue("@in_progress_order_status", OrderStatusMapper.StatusToString(OrderStatus.InProgress));
        command.Parameters.AddWithValue("@shipped_order_status", OrderStatusMapper.StatusToString(OrderStatus.Shipped));
        command.Parameters.AddWithValue("@cancelled_order_status", OrderStatusMapper.StatusToString(OrderStatus.Cancelled));
        command.Parameters.AddWithValue("@merged_order_status", OrderStatusMapper.StatusToString(OrderStatus.Merged));
        command.Parameters.AddWithValue("@draft_doc_status", DocTypeMapper.StatusToString(DocStatus.Draft));
        command.Parameters.AddWithValue("@closed_doc_status", DocTypeMapper.StatusToString(DocStatus.Closed));
        command.Parameters.AddWithValue("@outbound_doc_type", DocTypeMapper.ToOpString(DocType.Outbound));
        command.Parameters.AddWithValue("@production_doc_type", DocTypeMapper.ToOpString(DocType.ProductionReceipt));
        command.Parameters.AddWithValue("@pallet_filled_status", ProductionPalletStatus.Filled);
        command.Parameters.AddWithValue("@pallet_cancelled_status", ProductionPalletStatus.Cancelled);
        command.Parameters.AddWithValue("@qty_tolerance", StockQuantityRules.QtyTolerance);
        command.Parameters.AddWithValue("@tsd_picking_comment", "TSD OUTBOUND PICKING");
        command.Parameters.AddWithValue("@partial_status_display", "Частично отгружено");
        command.Parameters.AddWithValue("@draft_status_display", OrderStatusMapper.StatusToDisplayName(OrderStatus.Draft, OrderType.Customer));
        command.Parameters.AddWithValue("@accepted_status_display", OrderStatusMapper.StatusToDisplayName(OrderStatus.Accepted, OrderType.Customer));
        command.Parameters.AddWithValue("@in_progress_status_display", OrderStatusMapper.StatusToDisplayName(OrderStatus.InProgress, OrderType.Customer));
        command.Parameters.AddWithValue("@shipped_status_display", OrderStatusMapper.StatusToDisplayName(OrderStatus.Shipped, OrderType.Customer));
        command.Parameters.AddWithValue("@cancelled_status_display", OrderStatusMapper.StatusToDisplayName(OrderStatus.Cancelled, OrderType.Customer));
        command.Parameters.AddWithValue("@merged_status_display", OrderStatusMapper.StatusToDisplayName(OrderStatus.Merged, OrderType.Customer));
    }

    private static string BuildTsdOutboundOperationFingerprint(string source)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source ?? string.Empty))).ToLowerInvariant();
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
       pll.filled_at,
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
                FilledAt = FromDbDate(reader.IsDBNull(10) ? null : reader.GetString(10)),
                CreatedAt = FromDbDate(reader.GetString(11)) ?? DateTime.MinValue
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
                        FilledAt = pallet.FilledAt,
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
            CancelReason = reader.IsDBNull(17) ? null : reader.GetString(17),
            CancelledAt = FromDbDate(reader.IsDBNull(18) ? null : reader.GetString(18)),
            CreatedAt = FromDbDate(reader.GetString(19)) ?? DateTime.MinValue
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
        var shipmentOrderedQty = reader.FieldCount > 25 && !reader.IsDBNull(25) ? reader.GetDouble(25) : 0d;
        var shipmentShippedQty = reader.FieldCount > 26 && !reader.IsDBNull(26) ? reader.GetDouble(26) : 0d;
        var shipmentRemainingQty = reader.FieldCount > 27 && !reader.IsDBNull(27) ? reader.GetDouble(27) : 0d;

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
            FilledQty = filledQty,
            ShipmentOrderedQty = shipmentOrderedQty,
            ShipmentShippedQty = shipmentShippedQty,
            ShipmentRemainingQty = shipmentRemainingQty,
            IsPartiallyShipped = shipmentShippedQty > StockQuantityRules.QtyTolerance
                                 && shipmentRemainingQty > StockQuantityRules.QtyTolerance
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
            "client_blocks",
            "warehouse_action_bundles",
            "warehouse_action_lines",
            "warehouse_tasks",
            "warehouse_task_lines",
            "warehouse_task_events"
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
        EnsureColumn(connection, "production_pallets", "cancel_reason", "TEXT NULL");
        EnsureColumn(connection, "production_pallets", "cancelled_at", "TEXT NULL");
        EnsureColumn(connection, "production_pallet_lines", "filled_at", "TEXT NULL");
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
            || !ColumnExists(connection, "doc_lines", "production_purpose")
            || !ColumnExists(connection, "production_pallets", "cancel_reason")
            || !ColumnExists(connection, "production_pallets", "cancelled_at")
            || !ColumnExists(connection, "production_pallet_lines", "filled_at"))
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

        baseQuery += "GROUP BY i.id, i.name, i.barcode, i.base_uom, i.item_type_id, it.name, it.enable_min_stock_control, it.min_stock_uses_order_binding, it.enable_order_reservation, i.min_stock_qty, l.id, COALESCE(led.hu_code, led.hu) HAVING ABS(SUM(led.qty_delta)) > @qty_tolerance ORDER BY i.name, l.code, COALESCE(led.hu_code, led.hu)";
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

    public int CountPendingItemRequests()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT COUNT(*) FROM item_requests WHERE status <> 'RESOLVED';");
            return Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
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

    public int CountPendingOrderRequests()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT COUNT(*) FROM order_requests WHERE status = 'PENDING';");
            return Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
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

    public WarehouseActionBundle? GetWarehouseActionBundle(long id)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT id, bundle_ref, source, status, created_at, created_by, approved_at, approved_by,
       executed_at, completed_at, rejected_at, rejected_by, comment, error_code, error_message
FROM warehouse_action_bundles
WHERE id = @id;");
            command.Parameters.AddWithValue("@id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadWarehouseActionBundle(reader) : null;
        });
    }

    public WarehouseActionBundle? FindWarehouseBundleByRef(string bundleRef)
    {
        if (string.IsNullOrWhiteSpace(bundleRef))
        {
            return null;
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT id, bundle_ref, source, status, created_at, created_by, approved_at, approved_by,
       executed_at, completed_at, rejected_at, rejected_by, comment, error_code, error_message
FROM warehouse_action_bundles
WHERE UPPER(BTRIM(bundle_ref)) = UPPER(BTRIM(@bundle_ref));");
            command.Parameters.AddWithValue("@bundle_ref", bundleRef.Trim());
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadWarehouseActionBundle(reader) : null;
        });
    }

    public IReadOnlyList<WarehouseActionBundle> GetWarehouseActionBundles(string? status)
    {
        return WithConnection(connection =>
        {
            var sql = @"
SELECT id, bundle_ref, source, status, created_at, created_by, approved_at, approved_by,
       executed_at, completed_at, rejected_at, rejected_by, comment, error_code, error_message
FROM warehouse_action_bundles";
            if (!string.IsNullOrWhiteSpace(status))
            {
                sql += " WHERE status = @status";
            }

            sql += " ORDER BY created_at DESC, id DESC";
            using var command = CreateCommand(connection, sql);
            if (!string.IsNullOrWhiteSpace(status))
            {
                command.Parameters.AddWithValue("@status", status.Trim());
            }

            using var reader = command.ExecuteReader();
            var list = new List<WarehouseActionBundle>();
            while (reader.Read())
            {
                list.Add(ReadWarehouseActionBundle(reader));
            }

            return list;
        });
    }

    public int GetMaxWarehouseBundleRefSequenceByYear(int year)
    {
        return GetMaxRefSequenceByYear("SELECT bundle_ref FROM warehouse_action_bundles WHERE bundle_ref LIKE @pattern", year);
    }

    public long AddWarehouseActionBundle(WarehouseActionBundle bundle)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO warehouse_action_bundles(
    bundle_ref, source, status, created_at, created_by, approved_at, approved_by,
    executed_at, completed_at, rejected_at, rejected_by, comment, error_code, error_message)
VALUES(
    @bundle_ref, @source, @status, @created_at, @created_by, @approved_at, @approved_by,
    @executed_at, @completed_at, @rejected_at, @rejected_by, @comment, @error_code, @error_message)
RETURNING id;");
            command.Parameters.AddWithValue("@bundle_ref", bundle.BundleRef.Trim());
            command.Parameters.AddWithValue("@source", bundle.Source.Trim());
            command.Parameters.AddWithValue("@status", bundle.Status.Trim());
            command.Parameters.AddWithValue("@created_at", ToDbDate(bundle.CreatedAt));
            command.Parameters.AddWithValue("@created_by", ToDbNullable(bundle.CreatedBy));
            command.Parameters.AddWithValue("@approved_at", bundle.ApprovedAt.HasValue ? ToDbDate(bundle.ApprovedAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@approved_by", ToDbNullable(bundle.ApprovedBy));
            command.Parameters.AddWithValue("@executed_at", bundle.ExecutedAt.HasValue ? ToDbDate(bundle.ExecutedAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@completed_at", bundle.CompletedAt.HasValue ? ToDbDate(bundle.CompletedAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@rejected_at", bundle.RejectedAt.HasValue ? ToDbDate(bundle.RejectedAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@rejected_by", ToDbNullable(bundle.RejectedBy));
            command.Parameters.AddWithValue("@comment", ToDbNullable(bundle.Comment));
            command.Parameters.AddWithValue("@error_code", ToDbNullable(bundle.ErrorCode));
            command.Parameters.AddWithValue("@error_message", ToDbNullable(bundle.ErrorMessage));
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdateWarehouseActionBundleStatus(
        long bundleId,
        string status,
        DateTime? approvedAt,
        string? approvedBy,
        DateTime? executedAt,
        DateTime? completedAt,
        DateTime? rejectedAt,
        string? rejectedBy,
        string? errorCode,
        string? errorMessage)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE warehouse_action_bundles
SET status = @status,
    approved_at = COALESCE(@approved_at, approved_at),
    approved_by = COALESCE(@approved_by, approved_by),
    executed_at = COALESCE(@executed_at, executed_at),
    completed_at = COALESCE(@completed_at, completed_at),
    rejected_at = COALESCE(@rejected_at, rejected_at),
    rejected_by = COALESCE(@rejected_by, rejected_by),
    error_code = COALESCE(@error_code, error_code),
    error_message = COALESCE(@error_message, error_message)
WHERE id = @id;");
            command.Parameters.AddWithValue("@status", status.Trim());
            command.Parameters.AddWithValue("@approved_at", approvedAt.HasValue ? ToDbDate(approvedAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@approved_by", ToDbNullable(approvedBy));
            command.Parameters.AddWithValue("@executed_at", executedAt.HasValue ? ToDbDate(executedAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@completed_at", completedAt.HasValue ? ToDbDate(completedAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@rejected_at", rejectedAt.HasValue ? ToDbDate(rejectedAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@rejected_by", ToDbNullable(rejectedBy));
            command.Parameters.AddWithValue("@error_code", ToDbNullable(errorCode));
            command.Parameters.AddWithValue("@error_message", ToDbNullable(errorMessage));
            command.Parameters.AddWithValue("@id", bundleId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public WarehouseActionLine? GetWarehouseActionLine(long lineId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildWarehouseActionLineSelectSql("WHERE l.id = @id"));
            command.Parameters.AddWithValue("@id", lineId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadWarehouseActionLine(reader) : null;
        });
    }

    public IReadOnlyList<WarehouseActionLine> GetWarehouseActionLines(long bundleId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildWarehouseActionLineSelectSql("WHERE l.bundle_id = @bundle_id ORDER BY l.line_no, l.id"));
            command.Parameters.AddWithValue("@bundle_id", bundleId);
            using var reader = command.ExecuteReader();
            var list = new List<WarehouseActionLine>();
            while (reader.Read())
            {
                list.Add(ReadWarehouseActionLine(reader));
            }

            return list;
        });
    }

    public int GetNextWarehouseActionLineNo(long bundleId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT COALESCE(MAX(line_no), 0) + 1 FROM warehouse_action_lines WHERE bundle_id = @bundle_id;");
            command.Parameters.AddWithValue("@bundle_id", bundleId);
            return Convert.ToInt32(command.ExecuteScalar() ?? 1);
        });
    }

    public long AddWarehouseActionLine(WarehouseActionLine line)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO warehouse_action_lines(
    bundle_id, line_no, action_type, status, source_order_id, target_order_id, source_doc_id, target_doc_id,
    item_id, hu_code, from_location_id, to_location_id, qty, payload_json, result_json,
    error_code, error_message, created_at, updated_at)
VALUES(
    @bundle_id, @line_no, @action_type, @status, @source_order_id, @target_order_id, @source_doc_id, @target_doc_id,
    @item_id, @hu_code, @from_location_id, @to_location_id, @qty, @payload_json::jsonb, @result_json::jsonb,
    @error_code, @error_message, @created_at, @updated_at)
RETURNING id;");
            AddWarehouseActionLineParameters(command, line);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdateWarehouseActionLine(
        long lineId,
        string status,
        long? targetDocId,
        string? resultJson,
        string? errorCode,
        string? errorMessage,
        DateTime updatedAt)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE warehouse_action_lines
SET status = @status,
    target_doc_id = COALESCE(@target_doc_id, target_doc_id),
    result_json = COALESCE(@result_json::jsonb, result_json),
    error_code = @error_code,
    error_message = @error_message,
    updated_at = @updated_at
WHERE id = @id;");
            command.Parameters.AddWithValue("@status", status.Trim());
            command.Parameters.AddWithValue("@target_doc_id", targetDocId.HasValue ? targetDocId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@result_json", string.IsNullOrWhiteSpace(resultJson) ? DBNull.Value : resultJson);
            command.Parameters.AddWithValue("@error_code", ToDbNullable(errorCode));
            command.Parameters.AddWithValue("@error_message", ToDbNullable(errorMessage));
            command.Parameters.AddWithValue("@updated_at", ToDbDate(updatedAt));
            command.Parameters.AddWithValue("@id", lineId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public WarehouseTask? GetWarehouseTask(long taskId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildWarehouseTaskSelectSql("WHERE t.id = @id"));
            command.Parameters.AddWithValue("@id", taskId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadWarehouseTask(reader) : null;
        });
    }

    public WarehouseTask? FindWarehouseTaskByRef(string taskRef)
    {
        if (string.IsNullOrWhiteSpace(taskRef))
        {
            return null;
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildWarehouseTaskSelectSql("WHERE UPPER(BTRIM(t.task_ref)) = UPPER(BTRIM(@task_ref))"));
            command.Parameters.AddWithValue("@task_ref", taskRef.Trim());
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadWarehouseTask(reader) : null;
        });
    }

    public IReadOnlyList<WarehouseTask> GetWarehouseTasksByBundle(long bundleId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildWarehouseTaskSelectSql("WHERE t.bundle_id = @bundle_id ORDER BY t.id"));
            command.Parameters.AddWithValue("@bundle_id", bundleId);
            using var reader = command.ExecuteReader();
            var list = new List<WarehouseTask>();
            while (reader.Read())
            {
                list.Add(ReadWarehouseTask(reader));
            }

            return list;
        });
    }

    public IReadOnlyList<WarehouseTask> GetActiveWarehouseTasks(string? deviceId)
    {
        return WithConnection(connection =>
        {
            var sql = BuildWarehouseTaskSelectSql(@"
WHERE t.status IN ('NEW', 'ASSIGNED', 'IN_EXECUTION')
  AND EXISTS (
      SELECT 1
      FROM warehouse_action_bundles b
      WHERE b.id = t.bundle_id
        AND b.status IN ('APPROVED', 'IN_EXECUTION', 'EXECUTED'))");
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                sql += " AND (t.assigned_to_device_id IS NULL OR UPPER(BTRIM(t.assigned_to_device_id)) = UPPER(BTRIM(@device_id)))";
            }

            sql += " ORDER BY t.created_at, t.id";
            using var command = CreateCommand(connection, sql);
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                command.Parameters.AddWithValue("@device_id", deviceId.Trim());
            }

            using var reader = command.ExecuteReader();
            var list = new List<WarehouseTask>();
            while (reader.Read())
            {
                list.Add(ReadWarehouseTask(reader));
            }

            return list;
        });
    }

    public int GetMaxWarehouseTaskRefSequenceByYear(int year)
    {
        return GetMaxRefSequenceByYear("SELECT task_ref FROM warehouse_tasks WHERE task_ref LIKE @pattern", year);
    }

    public long AddWarehouseTask(WarehouseTask task)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO warehouse_tasks(
    task_ref, bundle_id, action_line_id, task_type, status, assigned_to_device_id, assigned_to_user,
    created_at, started_at, executed_at, confirmed_at, cancelled_at, comment)
VALUES(
    @task_ref, @bundle_id, @action_line_id, @task_type, @status, @assigned_to_device_id, @assigned_to_user,
    @created_at, @started_at, @executed_at, @confirmed_at, @cancelled_at, @comment)
RETURNING id;");
            command.Parameters.AddWithValue("@task_ref", task.TaskRef.Trim());
            command.Parameters.AddWithValue("@bundle_id", task.BundleId);
            command.Parameters.AddWithValue("@action_line_id", task.ActionLineId);
            command.Parameters.AddWithValue("@task_type", task.TaskType.Trim());
            command.Parameters.AddWithValue("@status", task.Status.Trim());
            command.Parameters.AddWithValue("@assigned_to_device_id", ToDbNullable(task.AssignedToDeviceId));
            command.Parameters.AddWithValue("@assigned_to_user", ToDbNullable(task.AssignedToUser));
            command.Parameters.AddWithValue("@created_at", ToDbDate(task.CreatedAt));
            command.Parameters.AddWithValue("@started_at", task.StartedAt.HasValue ? ToDbDate(task.StartedAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@executed_at", task.ExecutedAt.HasValue ? ToDbDate(task.ExecutedAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@confirmed_at", task.ConfirmedAt.HasValue ? ToDbDate(task.ConfirmedAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@cancelled_at", task.CancelledAt.HasValue ? ToDbDate(task.CancelledAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@comment", ToDbNullable(task.Comment));
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdateWarehouseTaskStatus(
        long taskId,
        string status,
        DateTime? startedAt,
        DateTime? executedAt,
        DateTime? confirmedAt,
        DateTime? cancelledAt,
        string? assignedToDeviceId,
        string? assignedToUser)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE warehouse_tasks
SET status = @status,
    started_at = COALESCE(@started_at, started_at),
    executed_at = COALESCE(@executed_at, executed_at),
    confirmed_at = COALESCE(@confirmed_at, confirmed_at),
    cancelled_at = COALESCE(@cancelled_at, cancelled_at),
    assigned_to_device_id = COALESCE(@assigned_to_device_id, assigned_to_device_id),
    assigned_to_user = COALESCE(@assigned_to_user, assigned_to_user)
WHERE id = @id;");
            command.Parameters.AddWithValue("@status", status.Trim());
            command.Parameters.AddWithValue("@started_at", startedAt.HasValue ? ToDbDate(startedAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@executed_at", executedAt.HasValue ? ToDbDate(executedAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@confirmed_at", confirmedAt.HasValue ? ToDbDate(confirmedAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@cancelled_at", cancelledAt.HasValue ? ToDbDate(cancelledAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@assigned_to_device_id", ToDbNullable(assignedToDeviceId));
            command.Parameters.AddWithValue("@assigned_to_user", ToDbNullable(assignedToUser));
            command.Parameters.AddWithValue("@id", taskId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public WarehouseTaskLine? GetWarehouseTaskLine(long lineId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildWarehouseTaskLineSelectSql("WHERE tl.id = @id"));
            command.Parameters.AddWithValue("@id", lineId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadWarehouseTaskLine(reader) : null;
        });
    }

    public IReadOnlyList<WarehouseTaskLine> GetWarehouseTaskLines(long taskId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildWarehouseTaskLineSelectSql("WHERE tl.task_id = @task_id ORDER BY tl.line_no, tl.id"));
            command.Parameters.AddWithValue("@task_id", taskId);
            using var reader = command.ExecuteReader();
            var list = new List<WarehouseTaskLine>();
            while (reader.Read())
            {
                list.Add(ReadWarehouseTaskLine(reader));
            }

            return list;
        });
    }

    public long AddWarehouseTaskLine(WarehouseTaskLine line)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO warehouse_task_lines(
    task_id, line_no, expected_hu_code, expected_item_id, expected_qty, from_location_id, to_location_id,
    order_id, doc_id, status, scanned_hu_code, scanned_location_id, scanned_at, device_id, operator_id,
    error_code, error_message)
VALUES(
    @task_id, @line_no, @expected_hu_code, @expected_item_id, @expected_qty, @from_location_id, @to_location_id,
    @order_id, @doc_id, @status, @scanned_hu_code, @scanned_location_id, @scanned_at, @device_id, @operator_id,
    @error_code, @error_message)
RETURNING id;");
            command.Parameters.AddWithValue("@task_id", line.TaskId);
            command.Parameters.AddWithValue("@line_no", line.LineNo);
            command.Parameters.AddWithValue("@expected_hu_code", ToDbNullable(line.ExpectedHuCode));
            command.Parameters.AddWithValue("@expected_item_id", line.ExpectedItemId.HasValue ? line.ExpectedItemId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@expected_qty", line.ExpectedQty.HasValue ? line.ExpectedQty.Value : DBNull.Value);
            command.Parameters.AddWithValue("@from_location_id", line.FromLocationId.HasValue ? line.FromLocationId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@to_location_id", line.ToLocationId.HasValue ? line.ToLocationId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@order_id", line.OrderId.HasValue ? line.OrderId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@doc_id", line.DocId.HasValue ? line.DocId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@status", line.Status.Trim());
            command.Parameters.AddWithValue("@scanned_hu_code", ToDbNullable(line.ScannedHuCode));
            command.Parameters.AddWithValue("@scanned_location_id", line.ScannedLocationId.HasValue ? line.ScannedLocationId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@scanned_at", line.ScannedAt.HasValue ? ToDbDate(line.ScannedAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@device_id", ToDbNullable(line.DeviceId));
            command.Parameters.AddWithValue("@operator_id", ToDbNullable(line.OperatorId));
            command.Parameters.AddWithValue("@error_code", ToDbNullable(line.ErrorCode));
            command.Parameters.AddWithValue("@error_message", ToDbNullable(line.ErrorMessage));
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdateWarehouseTaskLineScan(
        long lineId,
        string status,
        string? scannedHuCode,
        long? scannedLocationId,
        DateTime? scannedAt,
        string? deviceId,
        string? operatorId,
        string? errorCode,
        string? errorMessage)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE warehouse_task_lines
SET status = @status,
    scanned_hu_code = COALESCE(@scanned_hu_code, scanned_hu_code),
    scanned_location_id = COALESCE(@scanned_location_id, scanned_location_id),
    scanned_at = COALESCE(@scanned_at, scanned_at),
    device_id = COALESCE(@device_id, device_id),
    operator_id = COALESCE(@operator_id, operator_id),
    error_code = @error_code,
    error_message = @error_message
WHERE id = @id;");
            command.Parameters.AddWithValue("@status", status.Trim());
            command.Parameters.AddWithValue("@scanned_hu_code", ToDbNullable(scannedHuCode));
            command.Parameters.AddWithValue("@scanned_location_id", scannedLocationId.HasValue ? scannedLocationId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@scanned_at", scannedAt.HasValue ? ToDbDate(scannedAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@device_id", ToDbNullable(deviceId));
            command.Parameters.AddWithValue("@operator_id", ToDbNullable(operatorId));
            command.Parameters.AddWithValue("@error_code", ToDbNullable(errorCode));
            command.Parameters.AddWithValue("@error_message", ToDbNullable(errorMessage));
            command.Parameters.AddWithValue("@id", lineId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public long AddWarehouseTaskEvent(WarehouseTaskEvent warehouseEvent)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO warehouse_task_events(
    task_id, task_line_id, event_type, event_at, device_id, operator_id, hu_code, location_id, payload_json, message)
VALUES(
    @task_id, @task_line_id, @event_type, @event_at, @device_id, @operator_id, @hu_code, @location_id, @payload_json::jsonb, @message)
RETURNING id;");
            command.Parameters.AddWithValue("@task_id", warehouseEvent.TaskId);
            command.Parameters.AddWithValue("@task_line_id", warehouseEvent.TaskLineId.HasValue ? warehouseEvent.TaskLineId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@event_type", warehouseEvent.EventType.Trim());
            command.Parameters.AddWithValue("@event_at", ToDbDate(warehouseEvent.EventAt));
            command.Parameters.AddWithValue("@device_id", ToDbNullable(warehouseEvent.DeviceId));
            command.Parameters.AddWithValue("@operator_id", ToDbNullable(warehouseEvent.OperatorId));
            command.Parameters.AddWithValue("@hu_code", ToDbNullable(warehouseEvent.HuCode));
            command.Parameters.AddWithValue("@location_id", warehouseEvent.LocationId.HasValue ? warehouseEvent.LocationId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@payload_json", string.IsNullOrWhiteSpace(warehouseEvent.PayloadJson) ? "{}" : warehouseEvent.PayloadJson);
            command.Parameters.AddWithValue("@message", ToDbNullable(warehouseEvent.Message));
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public IReadOnlyList<WarehouseTaskEvent> GetWarehouseTaskEvents(long taskId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT id, task_id, task_line_id, event_type, event_at, device_id, operator_id, hu_code, location_id, payload_json::text, message
FROM warehouse_task_events
WHERE task_id = @task_id
ORDER BY event_at, id;");
            command.Parameters.AddWithValue("@task_id", taskId);
            using var reader = command.ExecuteReader();
            var list = new List<WarehouseTaskEvent>();
            while (reader.Read())
            {
                list.Add(ReadWarehouseTaskEvent(reader));
            }

            return list;
        });
    }

    public bool IsHuLockedByActiveWarehouseTask(string huCode, long? excludeBundleId)
    {
        if (string.IsNullOrWhiteSpace(huCode))
        {
            return false;
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT 1
FROM warehouse_action_bundles b
JOIN warehouse_action_lines l ON l.bundle_id = b.id
WHERE b.status IN ('SUBMITTED', 'APPROVED', 'IN_EXECUTION', 'EXECUTED')
  AND UPPER(BTRIM(l.hu_code)) = UPPER(BTRIM(@hu_code))
  AND (@exclude_bundle_id IS NULL OR b.id <> @exclude_bundle_id)
UNION ALL
SELECT 1
FROM warehouse_action_bundles b
JOIN warehouse_tasks t ON t.bundle_id = b.id
JOIN warehouse_task_lines tl ON tl.task_id = t.id
WHERE b.status IN ('SUBMITTED', 'APPROVED', 'IN_EXECUTION', 'EXECUTED')
  AND UPPER(BTRIM(COALESCE(tl.scanned_hu_code, tl.expected_hu_code))) = UPPER(BTRIM(@hu_code))
  AND (@exclude_bundle_id IS NULL OR b.id <> @exclude_bundle_id)
LIMIT 1;");
            command.Parameters.AddWithValue("@hu_code", huCode.Trim());
            command.Parameters.AddWithValue("@exclude_bundle_id", excludeBundleId.HasValue ? excludeBundleId.Value : DBNull.Value);
            return command.ExecuteScalar() != null;
        });
    }

    private static string BuildWarehouseActionLineSelectSql(string whereClause)
    {
        return @"
SELECT l.id, l.bundle_id, l.line_no, l.action_type, l.status, l.source_order_id, l.target_order_id,
       l.source_doc_id, l.target_doc_id, l.item_id, l.hu_code, l.from_location_id, l.to_location_id,
       l.qty, l.payload_json::text, l.result_json::text, l.error_code, l.error_message, l.created_at, l.updated_at
FROM warehouse_action_lines l " + whereClause;
    }

    private static string BuildWarehouseTaskSelectSql(string whereClause)
    {
        return @"
SELECT t.id, t.task_ref, t.bundle_id, t.action_line_id, t.task_type, t.status, t.assigned_to_device_id,
       t.assigned_to_user, t.created_at, t.started_at, t.executed_at, t.confirmed_at, t.cancelled_at, t.comment
FROM warehouse_tasks t " + whereClause;
    }

    private static string BuildWarehouseTaskLineSelectSql(string whereClause)
    {
        return @"
SELECT tl.id, tl.task_id, tl.line_no, tl.expected_hu_code, tl.expected_item_id, tl.expected_qty,
       tl.from_location_id, tl.to_location_id, tl.order_id, tl.doc_id, tl.status, tl.scanned_hu_code,
       tl.scanned_location_id, tl.scanned_at, tl.device_id, tl.operator_id, tl.error_code, tl.error_message
FROM warehouse_task_lines tl " + whereClause;
    }

    private static void AddWarehouseActionLineParameters(NpgsqlCommand command, WarehouseActionLine line)
    {
        command.Parameters.AddWithValue("@bundle_id", line.BundleId);
        command.Parameters.AddWithValue("@line_no", line.LineNo);
        command.Parameters.AddWithValue("@action_type", line.ActionType.Trim());
        command.Parameters.AddWithValue("@status", line.Status.Trim());
        command.Parameters.AddWithValue("@source_order_id", line.SourceOrderId.HasValue ? line.SourceOrderId.Value : DBNull.Value);
        command.Parameters.AddWithValue("@target_order_id", line.TargetOrderId.HasValue ? line.TargetOrderId.Value : DBNull.Value);
        command.Parameters.AddWithValue("@source_doc_id", line.SourceDocId.HasValue ? line.SourceDocId.Value : DBNull.Value);
        command.Parameters.AddWithValue("@target_doc_id", line.TargetDocId.HasValue ? line.TargetDocId.Value : DBNull.Value);
        command.Parameters.AddWithValue("@item_id", line.ItemId.HasValue ? line.ItemId.Value : DBNull.Value);
        command.Parameters.AddWithValue("@hu_code", ToDbNullable(line.HuCode));
        command.Parameters.AddWithValue("@from_location_id", line.FromLocationId.HasValue ? line.FromLocationId.Value : DBNull.Value);
        command.Parameters.AddWithValue("@to_location_id", line.ToLocationId.HasValue ? line.ToLocationId.Value : DBNull.Value);
        command.Parameters.AddWithValue("@qty", line.Qty.HasValue ? line.Qty.Value : DBNull.Value);
        command.Parameters.AddWithValue("@payload_json", string.IsNullOrWhiteSpace(line.PayloadJson) ? "{}" : line.PayloadJson);
        command.Parameters.AddWithValue("@result_json", string.IsNullOrWhiteSpace(line.ResultJson) ? DBNull.Value : line.ResultJson);
        command.Parameters.AddWithValue("@error_code", ToDbNullable(line.ErrorCode));
        command.Parameters.AddWithValue("@error_message", ToDbNullable(line.ErrorMessage));
        command.Parameters.AddWithValue("@created_at", ToDbDate(line.CreatedAt));
        command.Parameters.AddWithValue("@updated_at", ToDbDate(line.UpdatedAt));
    }

    private static WarehouseActionBundle ReadWarehouseActionBundle(NpgsqlDataReader reader)
    {
        return new WarehouseActionBundle
        {
            Id = reader.GetInt64(0),
            BundleRef = reader.GetString(1),
            Source = reader.GetString(2),
            Status = reader.GetString(3),
            CreatedAt = FromDbDate(reader.GetString(4)) ?? DateTime.MinValue,
            CreatedBy = reader.IsDBNull(5) ? null : reader.GetString(5),
            ApprovedAt = reader.IsDBNull(6) ? null : FromDbDate(reader.GetString(6)),
            ApprovedBy = reader.IsDBNull(7) ? null : reader.GetString(7),
            ExecutedAt = reader.IsDBNull(8) ? null : FromDbDate(reader.GetString(8)),
            CompletedAt = reader.IsDBNull(9) ? null : FromDbDate(reader.GetString(9)),
            RejectedAt = reader.IsDBNull(10) ? null : FromDbDate(reader.GetString(10)),
            RejectedBy = reader.IsDBNull(11) ? null : reader.GetString(11),
            Comment = reader.IsDBNull(12) ? null : reader.GetString(12),
            ErrorCode = reader.IsDBNull(13) ? null : reader.GetString(13),
            ErrorMessage = reader.IsDBNull(14) ? null : reader.GetString(14)
        };
    }

    private static WarehouseActionLine ReadWarehouseActionLine(NpgsqlDataReader reader)
    {
        return new WarehouseActionLine
        {
            Id = reader.GetInt64(0),
            BundleId = reader.GetInt64(1),
            LineNo = reader.GetInt32(2),
            ActionType = reader.GetString(3),
            Status = reader.GetString(4),
            SourceOrderId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
            TargetOrderId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
            SourceDocId = reader.IsDBNull(7) ? null : reader.GetInt64(7),
            TargetDocId = reader.IsDBNull(8) ? null : reader.GetInt64(8),
            ItemId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
            HuCode = reader.IsDBNull(10) ? null : reader.GetString(10),
            FromLocationId = reader.IsDBNull(11) ? null : reader.GetInt64(11),
            ToLocationId = reader.IsDBNull(12) ? null : reader.GetInt64(12),
            Qty = reader.IsDBNull(13) ? null : reader.GetDouble(13),
            PayloadJson = reader.IsDBNull(14) ? "{}" : reader.GetString(14),
            ResultJson = reader.IsDBNull(15) ? null : reader.GetString(15),
            ErrorCode = reader.IsDBNull(16) ? null : reader.GetString(16),
            ErrorMessage = reader.IsDBNull(17) ? null : reader.GetString(17),
            CreatedAt = FromDbDate(reader.GetString(18)) ?? DateTime.MinValue,
            UpdatedAt = FromDbDate(reader.GetString(19)) ?? DateTime.MinValue
        };
    }

    private static WarehouseTask ReadWarehouseTask(NpgsqlDataReader reader)
    {
        return new WarehouseTask
        {
            Id = reader.GetInt64(0),
            TaskRef = reader.GetString(1),
            BundleId = reader.GetInt64(2),
            ActionLineId = reader.GetInt64(3),
            TaskType = reader.GetString(4),
            Status = reader.GetString(5),
            AssignedToDeviceId = reader.IsDBNull(6) ? null : reader.GetString(6),
            AssignedToUser = reader.IsDBNull(7) ? null : reader.GetString(7),
            CreatedAt = FromDbDate(reader.GetString(8)) ?? DateTime.MinValue,
            StartedAt = reader.IsDBNull(9) ? null : FromDbDate(reader.GetString(9)),
            ExecutedAt = reader.IsDBNull(10) ? null : FromDbDate(reader.GetString(10)),
            ConfirmedAt = reader.IsDBNull(11) ? null : FromDbDate(reader.GetString(11)),
            CancelledAt = reader.IsDBNull(12) ? null : FromDbDate(reader.GetString(12)),
            Comment = reader.IsDBNull(13) ? null : reader.GetString(13)
        };
    }

    private static WarehouseTaskLine ReadWarehouseTaskLine(NpgsqlDataReader reader)
    {
        return new WarehouseTaskLine
        {
            Id = reader.GetInt64(0),
            TaskId = reader.GetInt64(1),
            LineNo = reader.GetInt32(2),
            ExpectedHuCode = reader.IsDBNull(3) ? null : reader.GetString(3),
            ExpectedItemId = reader.IsDBNull(4) ? null : reader.GetInt64(4),
            ExpectedQty = reader.IsDBNull(5) ? null : reader.GetDouble(5),
            FromLocationId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
            ToLocationId = reader.IsDBNull(7) ? null : reader.GetInt64(7),
            OrderId = reader.IsDBNull(8) ? null : reader.GetInt64(8),
            DocId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
            Status = reader.GetString(10),
            ScannedHuCode = reader.IsDBNull(11) ? null : reader.GetString(11),
            ScannedLocationId = reader.IsDBNull(12) ? null : reader.GetInt64(12),
            ScannedAt = reader.IsDBNull(13) ? null : FromDbDate(reader.GetString(13)),
            DeviceId = reader.IsDBNull(14) ? null : reader.GetString(14),
            OperatorId = reader.IsDBNull(15) ? null : reader.GetString(15),
            ErrorCode = reader.IsDBNull(16) ? null : reader.GetString(16),
            ErrorMessage = reader.IsDBNull(17) ? null : reader.GetString(17)
        };
    }

    private static WarehouseTaskEvent ReadWarehouseTaskEvent(NpgsqlDataReader reader)
    {
        return new WarehouseTaskEvent
        {
            Id = reader.GetInt64(0),
            TaskId = reader.GetInt64(1),
            TaskLineId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
            EventType = reader.GetString(3),
            EventAt = FromDbDate(reader.GetString(4)) ?? DateTime.MinValue,
            DeviceId = reader.IsDBNull(5) ? null : reader.GetString(5),
            OperatorId = reader.IsDBNull(6) ? null : reader.GetString(6),
            HuCode = reader.IsDBNull(7) ? null : reader.GetString(7),
            LocationId = reader.IsDBNull(8) ? null : reader.GetInt64(8),
            PayloadJson = reader.IsDBNull(9) ? "{}" : reader.GetString(9),
            Message = reader.IsDBNull(10) ? null : reader.GetString(10)
        };
    }

    private int GetMaxRefSequenceByYear(string sql, int year)
    {
        if (year <= 0)
        {
            return 0;
        }

        return WithConnection(connection =>
        {
            var yearToken = year.ToString(CultureInfo.InvariantCulture);
            using var command = CreateCommand(connection, sql);
            command.Parameters.AddWithValue("@pattern", $"%-{yearToken}-%");
            using var reader = command.ExecuteReader();
            var max = 0;
            while (reader.Read())
            {
                var value = reader.GetString(0);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var parts = value.Split('-', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                {
                    continue;
                }

                if (!string.Equals(parts[1], yearToken, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence))
                {
                    max = Math.Max(max, sequence);
                }
            }

            return max;
        });
    }

    private static object ToDbNullable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
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

    private static long[] NormalizePositiveDistinctIds(IReadOnlyCollection<long> ids)
    {
        return ids?.Where(id => id > 0).Distinct().ToArray() ?? Array.Empty<long>();
    }

    private static Dictionary<long, IReadOnlyList<T>> GroupByOrderId<T>(IEnumerable<T> rows, Func<T, long> orderIdSelector)
    {
        return rows
            .GroupBy(orderIdSelector)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<T>)group.ToList());
    }

    private static Dictionary<long, IReadOnlyList<T>> GroupByOrderId<T>(IEnumerable<T> rows, Func<T, long?> orderIdSelector)
    {
        return rows
            .Where(row => orderIdSelector(row).HasValue)
            .GroupBy(row => orderIdSelector(row)!.Value)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<T>)group.ToList());
    }

    private static Dictionary<long, IReadOnlyList<T>> GroupByDocId<T>(IEnumerable<T> rows, Func<T, long> docIdSelector)
    {
        return rows
            .GroupBy(docIdSelector)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<T>)group.ToList());
    }
}
