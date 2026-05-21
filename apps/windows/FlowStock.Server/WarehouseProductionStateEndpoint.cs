using System.Globalization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server;

public static class WarehouseProductionStateEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/reports/warehouse-production-state", HandleGet);
    }

    private static IResult HandleGet(HttpRequest request, IDataStore store)
    {
        var includeZero = string.Equals(request.Query["include_zero"], "1", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(request.Query["include_zero"], "true", StringComparison.OrdinalIgnoreCase);
        var belowMinOnly = string.Equals(request.Query["below_min_only"], "1", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(request.Query["below_min_only"], "true", StringComparison.OrdinalIgnoreCase);
        var search = request.Query["q"].ToString();

        var rows = new WarehouseProductionStateService(store)
            .GetRows(includeZero, search, belowMinOnly)
            .Select(MapRow)
            .ToList();
        return Results.Ok(rows);
    }

    private static object MapRow(WarehouseProductionStateRow row)
    {
        return new
        {
            item_id = row.ItemId,
            item_name = row.ItemName,
            barcode = row.Barcode,
            gtin = row.Gtin,
            item_type = row.ItemType,
            brand = row.Brand,
            base_uom = row.BaseUom,
            stock_qty = row.StockQty,
            free_qty = row.FreeQty,
            reserved_qty = row.ReservedQty,
            min_stock_qty = row.MinStockQty,
            below_min_qty = row.BelowMinQty,
            customer_open_demand_qty = row.CustomerOpenDemandQty,
            customer_remaining_to_ship_qty = row.CustomerRemainingToShipQty,
            internal_open_qty = row.InternalOpenQty,
            internal_remaining_qty = row.InternalRemainingQty,
            prd_planned_qty = row.PrdPlannedQty,
            prd_filled_qty = row.PrdFilledQty,
            pallet_planned_count = row.PalletPlannedCount,
            pallet_filled_count = row.PalletFilledCount,
            remaining_need_qty = row.RemainingNeedQty,
            need_reason = row.NeedReason,
            warnings = row.Warnings,
            need_breakdown = new
            {
                demand_to_close_customer_orders = row.NeedBreakdown.DemandToCloseCustomerOrders,
                demand_to_min_stock = row.NeedBreakdown.DemandToMinStock,
                already_planned_internal = row.NeedBreakdown.AlreadyPlannedInternal,
                already_planned_prd = row.NeedBreakdown.AlreadyPlannedPrd,
                remaining_to_create = row.NeedBreakdown.RemainingToCreate
            },
            hu_rows = row.HuRows.Select(hu => new
            {
                location = hu.Location,
                location_id = hu.LocationId,
                hu_code = hu.HuCode,
                qty = hu.Qty,
                origin_internal_order_id = hu.OriginInternalOrderId,
                origin_internal_order_ref = hu.OriginInternalOrderRef,
                reserved_customer_order_id = hu.ReservedCustomerOrderId,
                reserved_customer_order_ref = hu.ReservedCustomerOrderRef,
                reserved_customer_id = hu.ReservedCustomerId,
                reserved_customer_name = hu.ReservedCustomerName,
                stock_status = hu.StockStatus
            }),
            customer_orders = row.CustomerOrders.Select(order => new
            {
                order_id = order.OrderId,
                order_ref = order.OrderRef,
                partner_name = order.PartnerName,
                status = order.Status,
                qty_ordered = order.QtyOrdered,
                shipped_qty = order.ShippedQty,
                remaining_qty = order.RemainingQty
            }),
            internal_orders = row.InternalOrders.Select(order => new
            {
                order_id = order.OrderId,
                order_ref = order.OrderRef,
                status = order.Status,
                qty_ordered = order.QtyOrdered,
                produced_qty = order.ProducedQty,
                remaining_qty = order.RemainingQty
            }),
            production_receipts = row.ProductionReceipts.Select(prd => new
            {
                prd_doc_id = prd.PrdDocId,
                prd_ref = prd.PrdRef,
                pallet_id = prd.PalletId,
                hu_code = prd.HuCode,
                pallet_status = prd.PalletStatus,
                pallet_status_display = prd.PalletStatusDisplay,
                source_order_ref = prd.SourceOrderRef,
                planned_qty = prd.PlannedQty,
                filled_qty = prd.FilledQty,
                qty = prd.Qty,
                stock_effect = prd.StockEffect,
                status_note = prd.StatusNote,
                is_mixed_pallet = prd.IsMixedPallet,
                composition = prd.Composition,
                location = prd.Location
            })
        };
    }
}
