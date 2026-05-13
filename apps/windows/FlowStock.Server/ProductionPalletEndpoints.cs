using System.Text.Json.Serialization;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class ProductionPalletEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/docs/{docId:long}/production-pallets/plan", HandlePlan);
        app.MapGet("/api/docs/{docId:long}/production-pallets", HandleGet);
        app.MapGet("/api/tsd/production/filling-docs", HandleWorkItems);
        app.MapPost("/api/tsd/production/scan-pallet", HandleScan);
        app.MapPost("/api/tsd/production/fill-pallet", HandleFill);
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

    private static IResult HandleScan(ProductionPalletScanRequest request, ProductionPalletService service)
    {
        var result = service.Scan(request.OrderId, request.PrdDocId, request.HuCode);
        if (!result.Success)
        {
            return Results.BadRequest(new { ok = false, error = result.Error, message = result.Error });
        }

        return Results.Ok(MapScanResult(result));
    }

    private static IResult HandleFill(ProductionPalletFillRequest request, ProductionPalletService service)
    {
        var result = service.Fill(request.HuCode, request.DeviceId);
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
            filled_at = pallet.FilledAt,
            filled_by_device_id = pallet.FilledByDeviceId,
            created_at = pallet.CreatedAt
        };
    }

    private sealed class ProductionPalletFillRequest
    {
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
