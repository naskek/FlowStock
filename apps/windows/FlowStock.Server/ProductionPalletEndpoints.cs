using System.Text.Json.Serialization;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class ProductionPalletEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/orders/{orderId:long}/production-pallets/plan", HandlePlanOrder);
        app.MapGet("/api/orders/{orderId:long}/production-pallets/print-rows", HandlePrintRows);
        app.MapPost("/api/orders/{orderId:long}/production-pallets/mark-printed", HandleMarkPrinted);
        app.MapPost("/api/docs/{docId:long}/production-pallets/plan", HandlePlan);
        app.MapGet("/api/docs/{docId:long}/production-pallets", HandleGet);
        app.MapGet("/api/tsd/production/filling-orders", HandleFillingOrders);
        app.MapGet("/api/tsd/production/orders/{orderId:long}/filling-context", HandleFillingContext);
        app.MapPost("/api/tsd/production/orders/{orderId:long}/start-filling", HandleStartFilling);
        app.MapGet("/api/tsd/production/filling-docs", HandleWorkItems);
        app.MapPost("/api/tsd/production/scan-pallet", HandleScan);
        app.MapPost("/api/tsd/production/fill-pallet", HandleFill);
    }

    private static IResult HandlePlanOrder(long orderId, ProductionPalletService service)
    {
        try
        {
            return Results.Ok(MapOrderPlan(service.PlanOrder(orderId)));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { ok = false, error = ex.Message, message = ex.Message });
        }
    }

    private static IResult HandlePlan(long docId, ProductionPalletService service)
    {
        try
        {
            return Results.Ok(MapDocument(service.Plan(docId)));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { ok = false, error = ex.Message, message = ex.Message });
        }
    }

    private static IResult HandlePrintRows(long orderId, ProductionPalletService service)
    {
        try
        {
            return Results.Ok(service.GetPrintRows(orderId).Select(MapPrintRow));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { ok = false, error = ex.Message, message = ex.Message });
        }
    }

    private static IResult HandleMarkPrinted(long orderId, ProductionPalletService service)
    {
        try
        {
            var updated = service.MarkPrinted(orderId, DateTime.Now);
            return Results.Ok(new { ok = true, updated_count = updated });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { ok = false, error = ex.Message, message = ex.Message });
        }
    }

    private static IResult HandleGet(long docId, ProductionPalletService service)
    {
        try
        {
            return Results.Ok(MapDocument(service.Get(docId)));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { ok = false, error = ex.Message, message = ex.Message });
        }
    }

    private static IResult HandleWorkItems(ProductionPalletService service)
    {
        return Results.Ok(service.GetActiveWorkItems().Select(MapWorkItem));
    }

    private static IResult HandleFillingOrders(ProductionPalletService service)
    {
        return Results.Ok(service.GetFillingOrders().Select(MapFillingOrder));
    }

    private static IResult HandleStartFilling(long orderId, ProductionPalletService service)
    {
        return HandleFillingContext(orderId, service);
    }

    private static IResult HandleFillingContext(long orderId, ProductionPalletService service)
    {
        try
        {
            return Results.Ok(MapFillingContext(service.GetFillingContext(orderId)));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { ok = false, error = ex.Message, message = ex.Message });
        }
    }

    private static IResult HandleScan(ProductionPalletScanRequest request, ProductionPalletService service)
    {
        try
        {
            var result = service.Scan(request.OrderId, request.PrdDocId, request.HuCode);
            if (!result.Success)
            {
                return Results.BadRequest(new { ok = false, error = result.Error, message = result.Error });
            }

            return Results.Ok(MapScanResult(result));
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = ex.Message, message = ex.Message }, statusCode: 500);
        }
    }

    private static IResult HandleFill(ProductionPalletFillRequest request, ProductionPalletService service)
    {
        try
        {
            var result = service.Fill(request.HuCode, request.DeviceId, request.OrderId, request.PrdDocId);
            if (!result.Success)
            {
                return Results.BadRequest(new { ok = false, error = result.Error, message = result.Error });
            }

            return Results.Ok(new
            {
                ok = true,
                already_filled = result.AlreadyFilled,
                pallet = result.Pallet == null ? null : MapPallet(result.Pallet),
                document = result.Document == null ? null : MapDocument(result.Document)
            });
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = ex.Message, message = ex.Message }, statusCode: 500);
        }
    }

    private static object MapWorkItem(ProductionPalletWorkItem item)
    {
        return new
        {
            prd_doc_id = item.PrdDocId,
            prd_doc_ref = item.PrdDocRef,
            prd_status = item.PrdStatus,
            order_id = item.OrderId,
            order_ref = item.OrderRef,
            summary = MapSummary(item.Summary)
        };
    }

    private static object MapFillingOrder(ProductionFillingOrder order)
    {
        return new
        {
            order_id = order.OrderId,
            order_ref = order.OrderRef,
            order_type = order.OrderType,
            order_type_display = order.OrderTypeDisplay,
            order_status = order.OrderStatus,
            order_status_display = order.OrderStatusDisplay,
            partner_name = order.PartnerName,
            prd_doc_id = order.PrdDocId,
            prd_doc_ref = order.PrdDocRef,
            summary = MapSummary(order.Summary)
        };
    }

    private static object MapFillingContext(ProductionFillingContext context)
    {
        return new
        {
            order_id = context.OrderId,
            order_ref = context.OrderRef,
            order_type = context.OrderType,
            order_type_display = context.OrderTypeDisplay,
            order_status = context.OrderStatus,
            order_status_display = context.OrderStatusDisplay,
            partner_name = context.PartnerName,
            prd_doc_id = context.PrdDocId,
            prd_doc_ref = context.PrdDocRef,
            document = MapDocument(context.Document)
        };
    }

    private static object MapOrderPlan(ProductionPalletOrderPlanResult result)
    {
        return new
        {
            order_id = result.OrderId,
            order_ref = result.OrderRef,
            prd_doc_id = result.PrdDocId,
            prd_ref = result.PrdDocRef,
            prd_doc_ref = result.PrdDocRef,
            was_existing = result.WasExisting,
            planned_pallet_count = result.Summary.PlannedPalletCount,
            planned_qty = result.Summary.PlannedQty,
            filled_pallet_count = result.Summary.FilledPalletCount,
            filled_qty = result.Summary.FilledQty,
            remaining_pallet_count = result.Summary.RemainingPalletCount,
            remaining_qty = result.Summary.RemainingQty,
            summary = MapSummary(result.Summary),
            document = MapDocument(result.Document)
        };
    }

    private static object MapPrintRow(ProductionPalletPrintRow row)
    {
        return new
        {
            pallet_id = row.PalletId,
            order_id = row.OrderId,
            order_ref = row.OrderRef,
            prd_doc_id = row.PrdDocId,
            prd_ref = row.PrdRef,
            hu_code = row.HuCode,
            item_id = row.ItemId,
            item_name = row.ItemName,
            brand = row.Brand,
            qty = row.Qty,
            uom = row.Uom,
            pallet_no = row.PalletNo,
            pallet_count = row.PalletCount,
            storage_place = row.StoragePlace,
            production_date = row.ProductionDate,
            comment = row.Comment,
            is_mixed_pallet = row.IsMixedPallet,
            composition = row.Composition,
            line1_item_name = row.Lines.Count > 0 ? row.Lines[0].ItemName : string.Empty,
            line1_qty = row.Lines.Count > 0 ? row.Lines[0].Qty : 0,
            line2_item_name = row.Lines.Count > 1 ? row.Lines[1].ItemName : string.Empty,
            line2_qty = row.Lines.Count > 1 ? row.Lines[1].Qty : 0,
            line3_item_name = row.Lines.Count > 2 ? row.Lines[2].ItemName : string.Empty,
            line3_qty = row.Lines.Count > 2 ? row.Lines[2].Qty : 0,
            status = row.Status
        };
    }

    private static object MapScanResult(ProductionPalletScanResult result)
    {
        return new
        {
            ok = true,
            already_filled = result.AlreadyFilled,
            order_id = result.OrderId,
            order_ref = result.OrderRef,
            prd_doc_id = result.PrdDocId,
            prd_doc_ref = result.PrdDocRef,
            pallet_id = result.PalletId,
            hu_code = result.HuCode,
            item_id = result.ItemId,
            item_name = result.ItemName,
            item_brand = result.ItemBrand,
            base_uom = result.BaseUom,
            planned_qty = result.PlannedQty,
            is_mixed_pallet = result.IsMixedPallet,
            lines = result.Lines.Select(line => new
            {
                item_id = line.ItemId,
                item_name = line.ItemName,
                brand = line.Brand,
                qty = line.Qty,
                uom = line.Uom
            }),
            pallet_index = result.PalletIndex,
            pallet_count = result.PalletCount,
            pallet_status = result.PalletStatus,
            document = result.Document == null ? null : MapDocument(result.Document)
        };
    }

    private static object MapDocument(ProductionPalletDocument document)
    {
        return new
        {
            prd_doc_id = document.PrdDocId,
            summary = MapSummary(document.Summary),
            lines = document.Lines.Select(line => new
            {
                order_line_id = line.OrderLineId,
                item_id = line.ItemId,
                item_name = line.ItemName,
                ordered_qty = line.OrderedQty,
                planned_pallet_count = line.PlannedPalletCount,
                planned_qty = line.PlannedQty,
                filled_pallet_count = line.FilledPalletCount,
                filled_qty = line.FilledQty,
                remaining_pallet_count = line.RemainingPalletCount,
                remaining_qty = line.RemainingQty
            }),
            pallets = document.Pallets.Select(MapPallet)
        };
    }

    private static object MapSummary(ProductionPalletSummary summary)
    {
        return new
        {
            planned_pallet_count = summary.PlannedPalletCount,
            planned_qty = summary.PlannedQty,
            filled_pallet_count = summary.FilledPalletCount,
            filled_qty = summary.FilledQty,
            remaining_pallet_count = summary.RemainingPalletCount,
            remaining_qty = summary.RemainingQty
        };
    }

    private static object MapPallet(ProductionPallet pallet)
    {
        return new
        {
            id = pallet.Id,
            prd_doc_id = pallet.PrdDocId,
            doc_line_id = pallet.DocLineId,
            order_id = pallet.OrderId,
            order_line_id = pallet.OrderLineId,
            item_id = pallet.ItemId,
            item_name = pallet.ItemName,
            hu_code = pallet.HuCode,
            planned_qty = pallet.PlannedQty,
            to_location_id = pallet.ToLocationId,
            to_location_code = pallet.ToLocationCode,
            status = pallet.Status,
            is_mixed_pallet = pallet.IsMixedPallet,
            lines = pallet.Lines.Select(line => new
            {
                item_id = line.ItemId,
                item_name = line.ItemName,
                brand = line.Brand,
                qty = line.PlannedQty,
                uom = line.Uom
            }),
            filled_at = pallet.FilledAt,
            filled_by_device_id = pallet.FilledByDeviceId,
            created_at = pallet.CreatedAt
        };
    }

    private sealed class ProductionPalletFillRequest
    {
        [JsonPropertyName("order_id")]
        public long? OrderId { get; init; }

        [JsonPropertyName("prd_doc_id")]
        public long? PrdDocId { get; init; }

        [JsonPropertyName("hu_code")]
        public string? HuCode { get; init; }

        [JsonPropertyName("device_id")]
        public string? DeviceId { get; init; }
    }

    private sealed class ProductionPalletScanRequest
    {
        [JsonPropertyName("order_id")]
        public long? OrderId { get; init; }

        [JsonPropertyName("prd_doc_id")]
        public long? PrdDocId { get; init; }

        [JsonPropertyName("hu_code")]
        public string? HuCode { get; init; }

        [JsonPropertyName("device_id")]
        public string? DeviceId { get; init; }
    }
}
