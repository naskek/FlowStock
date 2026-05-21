namespace FlowStock.Data;

public static class OperationOrderCandidateSql
{
    private const string CandidateOrdersCte = @"
candidate_orders AS (
    SELECT o.id,
           o.order_ref,
           o.order_type,
           o.status AS persisted_status,
           o.created_at
    FROM orders o
    LEFT JOIN partners p ON p.id = o.partner_id
    WHERE o.status NOT IN (@cancelled_status, @merged_status)
      AND (
          @require_customer_orders = FALSE
          OR o.order_type = @customer_order_type
      )
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
)";

    public const string ProductionReceiptCandidateMetricsCte = CandidateOrdersCte + @",
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
line_metrics_seed AS (
    SELECT co.id AS order_id,
           co.order_type,
           co.persisted_status,
           col.id AS order_line_id,
           col.item_id,
           col.qty_ordered,
           0::double precision AS qty_shipped,
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
order_list_flags AS (
    SELECT co.id AS order_id,
           COALESCE(BOOL_OR(olm.qty_ordered
                            - CASE
                                  WHEN olm.order_type = 'CUSTOMER' THEN olm.qty_customer_ready
                                  ELSE olm.qty_produced_total
                              END > 0.000001), FALSE) AS has_receipt_remaining
    FROM candidate_orders co
    LEFT JOIN order_line_metrics olm ON olm.order_id = co.id
    GROUP BY co.id
),
status_summary AS (
    SELECT co.id AS order_id,
           COUNT(olm.order_line_id) AS line_count,
           COUNT(olm.order_line_id) FILTER (WHERE olm.qty_ordered > 0.000001) AS demand_line_count,
           COALESCE(BOOL_AND(olm.qty_customer_ready + 0.000001 >= olm.qty_ordered), FALSE) AS fully_customer_ready,
           COALESCE(BOOL_AND(olm.qty_produced_total + 0.000001 >= olm.qty_ordered), FALSE) AS fully_produced,
           COALESCE(BOOL_AND(olm.qty_produced_total + 0.000001 >= olm.qty_ordered) FILTER (WHERE olm.qty_ordered > 0.000001), FALSE) AS fully_demand_produced,
           COALESCE(BOOL_OR(olm.qty_produced_total > 0.000001), FALSE) AS any_produced
    FROM candidate_orders co
    LEFT JOIN order_line_metrics olm ON olm.order_id = co.id
    GROUP BY co.id
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
                        AND NOT COALESCE(ss.any_produced, FALSE) THEN 'DRAFT'
                   ELSE 'IN_PROGRESS'
               END
               ELSE CASE
                   WHEN co.persisted_status = 'DRAFT' THEN 'DRAFT'
                   WHEN COALESCE(ss.line_count, 0) > 0 AND COALESCE(ss.fully_customer_ready, FALSE) THEN 'ACCEPTED'
                   ELSE 'IN_PROGRESS'
               END
           END AS effective_status
    FROM candidate_orders co
    LEFT JOIN status_summary ss ON ss.order_id = co.id
),
limited_candidate_ids AS (
    SELECT eo.id
    FROM effective_orders eo
    INNER JOIN candidate_orders co ON co.id = eo.id
    INNER JOIN order_list_flags olf ON olf.order_id = eo.id
    WHERE eo.effective_status NOT IN (@shipped_status, @cancelled_status, @merged_status)
      AND olf.has_receipt_remaining
    ORDER BY co.created_at DESC,
             co.order_ref DESC
    LIMIT @limit
)";

    public const string OutboundCandidateMetricsCte = CandidateOrdersCte + @",
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
line_metrics_seed AS (
    SELECT co.id AS order_id,
           co.order_type,
           co.persisted_status,
           col.id AS order_line_id,
           col.item_id,
           col.qty_ordered,
           COALESCE(shipped.qty_shipped, 0) AS qty_shipped,
           COALESCE(reserved.qty_reserved, 0) AS qty_reserved,
           COALESCE(direct_produced.qty_received, 0) AS qty_direct_received
    FROM candidate_orders co
    LEFT JOIN candidate_order_lines col ON col.order_id = co.id
    LEFT JOIN shipped_by_line shipped ON shipped.order_line_id = col.id
    LEFT JOIN reserved_by_line reserved ON reserved.order_line_id = col.id
    LEFT JOIN direct_produced_by_line direct_produced ON direct_produced.order_line_id = col.id
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
           qty_direct_received AS qty_produced_total,
           CASE
               WHEN order_type = 'CUSTOMER' THEN qty_direct_received + qty_reserved
               ELSE qty_direct_received
           END AS qty_customer_ready
    FROM line_metrics_seed
),
order_list_flags AS (
    SELECT co.id AS order_id,
           COALESCE(BOOL_OR(olm.order_type = 'CUSTOMER'
                            AND olm.qty_ordered - olm.qty_shipped > 0.000001), FALSE) AS has_shipment_remaining
    FROM candidate_orders co
    LEFT JOIN order_line_metrics olm ON olm.order_id = co.id
    GROUP BY co.id
),
status_summary AS (
    SELECT co.id AS order_id,
           COUNT(olm.order_line_id) AS line_count,
           COALESCE(BOOL_AND(olm.qty_shipped + 0.000001 >= olm.qty_ordered), FALSE) AS fully_shipped,
           COALESCE(BOOL_AND(olm.qty_customer_ready + 0.000001 >= olm.qty_ordered), FALSE) AS fully_customer_ready
    FROM candidate_orders co
    LEFT JOIN order_line_metrics olm ON olm.order_id = co.id
    GROUP BY co.id
),
effective_orders AS (
    SELECT co.id,
           co.order_ref,
           co.created_at,
           CASE
               WHEN co.persisted_status = 'CANCELLED' THEN 'CANCELLED'
               WHEN co.persisted_status = 'MERGED' THEN 'MERGED'
               WHEN co.persisted_status = 'DRAFT' THEN 'DRAFT'
               WHEN COALESCE(ss.line_count, 0) > 0 AND COALESCE(ss.fully_shipped, FALSE) THEN 'SHIPPED'
               WHEN COALESCE(ss.line_count, 0) > 0 AND COALESCE(ss.fully_customer_ready, FALSE) THEN 'ACCEPTED'
               ELSE 'IN_PROGRESS'
           END AS effective_status
    FROM candidate_orders co
    LEFT JOIN status_summary ss ON ss.order_id = co.id
),
limited_candidate_ids AS (
    SELECT eo.id
    FROM effective_orders eo
    INNER JOIN candidate_orders co ON co.id = eo.id
    INNER JOIN order_list_flags olf ON olf.order_id = eo.id
    WHERE eo.effective_status NOT IN (@shipped_status, @cancelled_status, @merged_status)
      AND co.order_type = @customer_order_type
      AND olf.has_shipment_remaining
    ORDER BY co.created_at DESC,
             co.order_ref DESC
    LIMIT @limit
)";

    public static string BuildOrderScopeSql(bool requireCustomerOrders, bool requireReceiptRemaining, bool requireShipmentRemaining)
    {
        var metricsCte = requireReceiptRemaining && !requireShipmentRemaining
            ? ProductionReceiptCandidateMetricsCte
            : OutboundCandidateMetricsCte;

        return $@"
WITH {metricsCte}
SELECT id
FROM limited_candidate_ids";
    }
}
