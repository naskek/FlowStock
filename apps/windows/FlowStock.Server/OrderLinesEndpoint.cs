using System.Text.Json.Serialization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class OrderLinesEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/orders/lines", HandleBatch);
        app.MapGet("/api/orders/{orderId:long}/lines", HandleSingle);
    }

    private static IResult HandleSingle(long orderId, IDataStore store)
    {
        var orderService = new OrderService(store);
        var order = orderService.GetOrder(orderId);
        if (order == null)
        {
            return Results.NotFound(new ApiResult(false, "ORDER_NOT_FOUND"));
        }

        var linesByOrder = BuildOrderLinesByOrderIds(store, [orderId]);
        return Results.Ok(linesByOrder.TryGetValue(orderId, out var lines) ? lines : Array.Empty<OrderLineResponse>());
    }

    private static IResult HandleBatch(HttpRequest request, IDataStore store)
    {
        if (!TryParseOrderIds(request, out var orderIds, out var error))
        {
            return Results.BadRequest(new ApiResult(false, error ?? "INVALID_ORDER_IDS"));
        }

        if (orderIds.Count == 0)
        {
            return Results.Ok(Array.Empty<OrderLinesBatchResponse>());
        }

        var linesByOrder = BuildOrderLinesByOrderIds(store, orderIds);
        var response = orderIds
            .Select(orderId => new OrderLinesBatchResponse(
                orderId,
                linesByOrder.TryGetValue(orderId, out var lines) ? lines : Array.Empty<OrderLineResponse>()))
            .ToList();
        return Results.Ok(response);
    }

    private static IReadOnlyDictionary<long, IReadOnlyList<OrderLineResponse>> BuildOrderLinesByOrderIds(
        IDataStore store,
        IReadOnlyCollection<long> orderIds)
    {
        var orderService = new OrderService(store);
        var linesByOrder = orderService.GetOrderLineViewsByOrderIds(orderIds);
        var lineIds = linesByOrder.Values
            .SelectMany(lines => lines)
            .Select(line => line.Id)
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        var productionHusByOrderLine = BuildProductionHuCodesByOrderLineIds(store, lineIds, orderIds);

        return orderIds.ToDictionary(
            orderId => orderId,
            orderId =>
            {
                if (!linesByOrder.TryGetValue(orderId, out var lines))
                {
                    return (IReadOnlyList<OrderLineResponse>)Array.Empty<OrderLineResponse>();
                }

                return lines
                    .Select(line => MapOrderLine(line, productionHusByOrderLine))
                    .ToList();
            });
    }

    private static IReadOnlyDictionary<long, string[]> BuildProductionHuCodesByOrderLineIds(
        IDataStore store,
        IReadOnlyCollection<long> orderLineIds,
        IReadOnlyCollection<long> orderIds)
    {
        if (orderLineIds.Count == 0)
        {
            return new Dictionary<long, string[]>();
        }

        if (store is IOptimizedOrderLinesStore optimizedStore)
        {
            return optimizedStore.GetProductionHuCodesByOrderLineIds(orderLineIds);
        }

        var result = new Dictionary<long, string[]>();
        foreach (var orderId in orderIds)
        {
            foreach (var pair in BuildProductionHuCodesByOrderLine(store, orderId))
            {
                result[pair.Key] = pair.Value;
            }
        }

        return result;
    }

    private static Dictionary<long, string[]> BuildProductionHuCodesByOrderLine(IDataStore store, long orderId)
    {
        var result = new Dictionary<long, string[]>();
        var rows = new Dictionary<long, SortedSet<string>>();

        foreach (var reservedLine in store.GetOrderReceiptPlanLines(orderId)
                     .Where(line => line.QtyPlanned > 0))
        {
            if (reservedLine.OrderLineId <= 0 || string.IsNullOrWhiteSpace(reservedLine.ToHu))
            {
                continue;
            }

            var huCode = reservedLine.ToHu.Trim();
            if (!HasPositiveHuBalance(store, reservedLine.ItemId, huCode))
            {
                continue;
            }

            if (!rows.TryGetValue(reservedLine.OrderLineId, out var huCodes))
            {
                huCodes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                rows[reservedLine.OrderLineId] = huCodes;
            }

            huCodes.Add(huCode);
        }

        foreach (var doc in store.GetDocsByOrder(orderId).Where(doc => doc.Type == DocType.ProductionReceipt))
        {
            foreach (var pallet in store.GetProductionPalletsByDoc(doc.Id)
                         .Where(pallet => string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)))
            {
                var componentLines = pallet.Lines.Count > 0
                    ? pallet.Lines
                    : new[]
                    {
                        new ProductionPalletComponentLine
                        {
                            OrderLineId = pallet.OrderLineId
                        }
                    };
                foreach (var line in componentLines)
                {
                    if (!line.OrderLineId.HasValue || string.IsNullOrWhiteSpace(pallet.HuCode))
                    {
                        continue;
                    }

                    var itemId = line.ItemId > 0 ? line.ItemId : pallet.ItemId;
                    if (!HasPositiveHuBalance(store, itemId, pallet.HuCode))
                    {
                        continue;
                    }

                    if (!rows.TryGetValue(line.OrderLineId.Value, out var huCodes))
                    {
                        huCodes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                        rows[line.OrderLineId.Value] = huCodes;
                    }

                    huCodes.Add(pallet.HuCode);
                }
            }
        }

        foreach (var pair in rows)
        {
            result[pair.Key] = pair.Value.ToArray();
        }

        return result;
    }

    private static bool HasPositiveHuBalance(IDataStore store, long itemId, string huCode)
    {
        return store.GetHuStockRows()
            .Where(row => row.ItemId == itemId)
            .Where(row => string.Equals(row.HuCode?.Trim(), huCode, StringComparison.OrdinalIgnoreCase))
            .Sum(row => row.Qty) > StockQuantityRules.QtyTolerance;
    }

    private static OrderLineResponse MapOrderLine(
        OrderLineView line,
        IReadOnlyDictionary<long, string[]> productionHusByOrderLine)
    {
        var huCodes = productionHusByOrderLine.TryGetValue(line.Id, out var values)
            ? values
            : Array.Empty<string>();
        return new OrderLineResponse(
            line.Id,
            line.OrderId,
            line.ItemId,
            line.ItemName,
            line.Barcode,
            line.Gtin,
            line.QtyOrdered,
            ProductionLinePurposeMapper.ToDbValue(line.ProductionPurpose),
            line.ProductionPurposeDisplay,
            line.ProductionPalletGroup,
            huCodes,
            string.Join(", ", huCodes),
            line.QtyShipped,
            line.QtyProduced,
            line.QtyRemaining,
            line.QtyAvailable,
            line.CanShipNow,
            line.Shortage,
            line.PlannedPalletCount,
            line.FilledPalletCount,
            line.PlannedPalletQty,
            line.FilledPalletQty,
            line.LineFullyShipped,
            line.HidePalletFillIndicator,
            line.ShowPalletCompletedIcon,
            line.BlockingFillRequired,
            line.FulfillmentStatus,
            line.PalletFillLabel,
            line.PalletFillTone,
            line.PalletFillTitle);
    }

    private static bool TryParseOrderIds(HttpRequest request, out IReadOnlyList<long> orderIds, out string? error)
    {
        var seen = new HashSet<long>();
        var result = new List<long>();
        foreach (var value in request.Query["ids"])
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var token in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!long.TryParse(token, out var orderId) || orderId <= 0)
                {
                    orderIds = Array.Empty<long>();
                    error = "INVALID_ORDER_IDS";
                    return false;
                }

                if (seen.Add(orderId))
                {
                    result.Add(orderId);
                }
            }
        }

        orderIds = result;
        error = null;
        return true;
    }

    private sealed record OrderLinesBatchResponse(
        [property: JsonPropertyName("order_id")] long OrderId,
        [property: JsonPropertyName("lines")] IReadOnlyList<OrderLineResponse> Lines);

    private sealed record OrderLineResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("order_id")] long OrderId,
        [property: JsonPropertyName("item_id")] long ItemId,
        [property: JsonPropertyName("item_name")] string ItemName,
        [property: JsonPropertyName("barcode")] string? Barcode,
        [property: JsonPropertyName("gtin")] string? Gtin,
        [property: JsonPropertyName("qty_ordered")] double QtyOrdered,
        [property: JsonPropertyName("production_purpose")] string ProductionPurpose,
        [property: JsonPropertyName("production_purpose_display")] string ProductionPurposeDisplay,
        [property: JsonPropertyName("production_pallet_group")] string? ProductionPalletGroup,
        [property: JsonPropertyName("production_hu_codes")] string[] ProductionHuCodes,
        [property: JsonPropertyName("production_hu_codes_display")] string ProductionHuCodesDisplay,
        [property: JsonPropertyName("qty_shipped")] double QtyShipped,
        [property: JsonPropertyName("qty_produced")] double QtyProduced,
        [property: JsonPropertyName("qty_left")] double QtyLeft,
        [property: JsonPropertyName("qty_available")] double QtyAvailable,
        [property: JsonPropertyName("can_ship_now")] double CanShipNow,
        [property: JsonPropertyName("shortage")] double Shortage,
        [property: JsonPropertyName("planned_pallet_count")] int PlannedPalletCount,
        [property: JsonPropertyName("filled_pallet_count")] int FilledPalletCount,
        [property: JsonPropertyName("pallet_planned_qty")] double PalletPlannedQty,
        [property: JsonPropertyName("pallet_filled_qty")] double PalletFilledQty,
        [property: JsonPropertyName("line_fully_shipped")] bool LineFullyShipped,
        [property: JsonPropertyName("hide_pallet_fill_indicator")] bool HidePalletFillIndicator,
        [property: JsonPropertyName("show_pallet_completed_icon")] bool ShowPalletCompletedIcon,
        [property: JsonPropertyName("blocking_fill_required")] bool BlockingFillRequired,
        [property: JsonPropertyName("fulfillment_status")] string FulfillmentStatus,
        [property: JsonPropertyName("pallet_fill_label")] string? PalletFillLabel,
        [property: JsonPropertyName("pallet_fill_tone")] string PalletFillTone,
        [property: JsonPropertyName("pallet_fill_title")] string? PalletFillTitle);
}
