using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using System.Text.Json.Serialization;

namespace FlowStock.Server;

public static class TsdOutboundPickingEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/tsd/outbound/orders", HandleListOrders);
        app.MapGet("/api/tsd/outbound/orders/{orderId:long}", HandleGetOrder);
        app.MapPost("/api/tsd/outbound/orders/{orderId:long}/scan", HandleScan);
        app.MapPost("/api/tsd/outbound/orders/{orderId:long}/complete", HandleComplete);
    }

    private static Ok<object> HandleListOrders(OutboundPickingService service)
    {
        return TypedResults.Ok((object)service.GetOrders().Select(MapOrderRow).ToArray());
    }

    private static Results<Ok<object>, BadRequest<object>> HandleGetOrder(long orderId, OutboundPickingService service)
    {
        try
        {
            return TypedResults.Ok((object)MapOrderDetails(service.GetDetails(orderId)));
        }
        catch (InvalidOperationException ex)
        {
            return TypedResults.BadRequest((object)new { ok = false, error = "VALIDATION_ERROR", message = ex.Message });
        }
    }

    private static Results<Ok<object>, BadRequest<object>> HandleScan(long orderId, OutboundPickingScanRequest request, OutboundPickingService service)
    {
        var result = service.Scan(orderId, request.HuCode ?? string.Empty, request.DeviceId);
        if (!result.Success)
        {
            return TypedResults.BadRequest((object)new
            {
                ok = false,
                error = result.ErrorCode,
                message = result.Message
            });
        }

        return TypedResults.Ok((object)new
        {
            ok = true,
            message = result.Message,
            already_picked = result.AlreadyPicked,
            order = result.Order == null ? null : MapOrderDetails(result.Order)
        });
    }

    private static Results<Ok<object>, BadRequest<object>> HandleComplete(long orderId, OutboundPickingCompleteRequest request, OutboundPickingService service)
    {
        var result = service.Complete(orderId);
        if (!result.Success)
        {
            return TypedResults.BadRequest((object)new
            {
                ok = false,
                error = result.ErrorCode,
                message = result.Message
            });
        }

        return TypedResults.Ok((object)new
        {
            ok = true,
            message = result.Message,
            order = result.Order == null ? null : MapOrderDetails(result.Order)
        });
    }

    private static object MapOrderRow(OutboundPickingOrderRow row)
    {
        return new
        {
            order_id = row.OrderId,
            order_ref = row.OrderRef,
            partner_name = row.PartnerName,
            status = row.Status,
            expected_hu_count = row.ExpectedHuCount,
            picked_hu_count = row.PickedHuCount,
            is_complete = row.IsComplete
        };
    }

    private static object MapOrderDetails(OutboundPickingOrderDetails details)
    {
        return new
        {
            order_id = details.OrderId,
            order_ref = details.OrderRef,
            partner_name = details.PartnerName,
            status = details.Status,
            draft_outbound_doc_id = details.DraftOutboundDocId,
            draft_outbound_doc_ref = details.DraftOutboundDocRef,
            expected_hu_count = details.ExpectedHuCount,
            picked_hu_count = details.PickedHuCount,
            is_complete = details.IsComplete,
            hus = details.Hus.Select(MapHu).ToArray()
        };
    }

    private static object MapHu(OutboundPickingHuRow hu)
    {
        return new
        {
            hu_code = hu.HuCode,
            status = hu.Status,
            qty = hu.Qty,
            item_summary = hu.ItemSummary,
            lines = hu.Lines.Select(line => new
            {
                item_id = line.ItemId,
                item_name = line.ItemName,
                order_line_id = line.OrderLineId,
                location_id = line.LocationId,
                location_code = line.LocationCode,
                qty = line.Qty
            }).ToArray()
        };
    }

    private sealed class OutboundPickingScanRequest
    {
        [JsonPropertyName("hu_code")]
        public string? HuCode { get; init; }

        [JsonPropertyName("device_id")]
        public string? DeviceId { get; init; }
    }

    private sealed class OutboundPickingCompleteRequest
    {
        [JsonPropertyName("device_id")]
        public string? DeviceId { get; init; }
    }
}
