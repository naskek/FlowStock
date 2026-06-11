using System.Diagnostics;
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

    private static IResult HandleSingle(long orderId, IDataStore store, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("FlowStock.Server.OrderLinesPerformance");
        var totalStopwatch = Stopwatch.StartNew();
        var phaseStopwatch = Stopwatch.StartNew();
        var orderService = new OrderService(store);
        var order = orderService.GetOrder(orderId);
        phaseStopwatch.Stop();
        var orderLookupMs = phaseStopwatch.ElapsedMilliseconds;
        if (order == null)
        {
            totalStopwatch.Stop();
            LogSinglePerformance(
                logger,
                orderId,
                "NOT_FOUND",
                orderLookupMs,
                lineViewsMs: null,
                productionHuCodesMs: null,
                huDetailsMs: null,
                totalStopwatch.ElapsedMilliseconds,
                lineCount: 0);
            return Results.NotFound(new ApiResult(false, "ORDER_NOT_FOUND"));
        }

        phaseStopwatch.Restart();
        var lineViewsByOrder = orderService.GetOrderLineViewsByOrderIds([orderId]);
        var lineViews = lineViewsByOrder.TryGetValue(orderId, out var loadedLines)
            ? loadedLines
            : Array.Empty<OrderLineView>();
        phaseStopwatch.Stop();
        var lineViewsMs = phaseStopwatch.ElapsedMilliseconds;

        phaseStopwatch.Restart();
        var productionHusByOrderLine = BuildProductionHuCodesByOrderLineIds(
            store,
            lineViews.Select(line => line.Id).Where(id => id > 0).Distinct().ToArray(),
            [orderId]);
        phaseStopwatch.Stop();
        var productionHuCodesMs = phaseStopwatch.ElapsedMilliseconds;

        var detailsTiming = new OrderLineHuDetailsTiming();
        var fateTiming = new OrderLineHuFateTiming();
        phaseStopwatch.Restart();
        var detailsByOrderLine = OrderLineHuDetailsBuilder.BuildByOrder(
            store,
            order,
            lineViews,
            detailsTiming,
            fateTiming);
        phaseStopwatch.Stop();
        var huDetailsMs = phaseStopwatch.ElapsedMilliseconds;

        var lines = lineViews
            .Select(line => MapOrderLine(line, productionHusByOrderLine, detailsByOrderLine.GetValueOrDefault(line.Id)))
            .ToList();
        totalStopwatch.Stop();

        LogSinglePerformance(
            logger,
            orderId,
            "OK",
            orderLookupMs,
            lineViewsMs,
            productionHuCodesMs,
            huDetailsMs,
            totalStopwatch.ElapsedMilliseconds,
            lines.Count);
        LogHuDetailsPerformance(logger, orderId, detailsTiming);
        LogHuFatePerformance(logger, orderId, fateTiming);

        return Results.Ok(lines);
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
                    .Select(line => MapOrderLine(line, productionHusByOrderLine, details: null))
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
        return ProductionOrderLineHuCodes.BuildByOrder(store, orderId);
    }

    private static void LogSinglePerformance(
        ILogger logger,
        long orderId,
        string result,
        long? orderLookupMs,
        long? lineViewsMs,
        long? productionHuCodesMs,
        long? huDetailsMs,
        long totalMs,
        int lineCount)
    {
        logger.LogInformation(
            "PERF order-lines-single order_id={OrderId} result={Result} order_lookup_ms={OrderLookupMs} line_views_ms={LineViewsMs} production_hu_codes_ms={ProductionHuCodesMs} hu_details_ms={HuDetailsMs} total_ms={TotalMs} line_count={LineCount}",
            orderId,
            result,
            orderLookupMs,
            lineViewsMs,
            productionHuCodesMs,
            huDetailsMs,
            totalMs,
            lineCount);
    }

    private static void LogHuDetailsPerformance(ILogger logger, long orderId, OrderLineHuDetailsTiming timing)
    {
        logger.LogInformation(
            "PERF order-line-hu-details order_id={OrderId} get_order_lines_ms={GetOrderLinesMs} build_warehouse_rows_ms={BuildWarehouseRowsMs} hu_fate_ms={HuFateMs} build_production_rows_ms={BuildProductionRowsMs} build_shipped_rows_ms={BuildShippedRowsMs} confirmed_receipt_ledger_totals_ms={ConfirmedReceiptLedgerTotalsMs} customer_coverage_ms={CustomerCoverageMs} final_mapping_ms={FinalMappingMs} total_ms={TotalMs}",
            orderId,
            timing.GetOrderLinesMs,
            timing.BuildWarehouseRowsMs,
            timing.HuFateMs,
            timing.BuildProductionRowsMs,
            timing.BuildShippedRowsMs,
            timing.ConfirmedReceiptLedgerTotalsMs,
            timing.CustomerCoverageMs,
            timing.FinalMappingMs,
            timing.TotalMs);
    }

    private static void LogHuFatePerformance(ILogger logger, long orderId, OrderLineHuFateTiming timing)
    {
        logger.LogInformation(
            "PERF hu-fate order_id={OrderId} skipped={Skipped} get_orders_ms={GetOrdersMs} orders_count={OrdersCount} get_docs_ms={GetDocsMs} docs_count={DocsCount} get_hu_stock_rows_ms={GetHuStockRowsMs} hu_stock_rows_count={HuStockRowsCount} build_sources_ms={BuildSourcesMs} sources_count={SourcesCount} build_reservations_ms={BuildReservationsMs} reservations_count={ReservationsCount} build_shipments_ms={BuildShipmentsMs} shipments_count={ShipmentsCount} final_rows_ms={FinalRowsMs} final_rows_count={FinalRowsCount} total_ms={TotalMs}",
            orderId,
            timing.Skipped,
            timing.GetOrdersMs,
            timing.OrdersCount,
            timing.GetDocsMs,
            timing.DocsCount,
            timing.GetHuStockRowsMs,
            timing.HuStockRowsCount,
            timing.BuildSourcesMs,
            timing.SourcesCount,
            timing.BuildReservationsMs,
            timing.ReservationsCount,
            timing.BuildShipmentsMs,
            timing.ShipmentsCount,
            timing.FinalRowsMs,
            timing.FinalRowsCount,
            timing.TotalMs);
    }

    private static OrderLineResponse MapOrderLine(
        OrderLineView line,
        IReadOnlyDictionary<long, string[]> productionHusByOrderLine,
        OrderLineHuDetails? details)
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
            line.PalletFillTitle,
            details?.WarehouseHuRows.Select(MapWarehouseHuRow).ToArray(),
            details?.ProductionHuRows.Select(MapProductionHuRow).ToArray(),
            details?.ShippedHuRows.Select(MapShippedHuRow).ToArray(),
            details?.Coverage is { } coverage ? MapCoverage(coverage) : null);
    }

    private static WarehouseHuRowResponse MapWarehouseHuRow(OrderLineWarehouseHuRow row) =>
        new(row.HuCode, row.Qty, row.LocationCode, row.LocationName, row.StockStatus, row.IsBoundToOrder);

    private static ProductionHuRowResponse MapProductionHuRow(OrderLineProductionHuRow row) =>
        new(
            row.HuCode,
            row.PalletStatus,
            row.PlannedQty,
            row.FilledQty,
            row.PrdRef,
            row.FateCode,
            row.FateLabel,
            row.FateOrderRef,
            row.FateDocRef,
            row.FateQty);

    private static ShippedHuRowResponse MapShippedHuRow(OrderLineShippedHuRow row) =>
        new(row.HuCode, row.Qty);

    private static CoverageResponse MapCoverage(OrderLineCoverage coverage) =>
        new(
            coverage.OrderedQty,
            coverage.WarehouseBoundQty,
            coverage.ProductionFilledQty,
            coverage.ShippedQty,
            coverage.CoveredQty,
            coverage.MissingQty);

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
        [property: JsonPropertyName("pallet_fill_title")] string? PalletFillTitle,
        [property: JsonPropertyName("warehouse_hu_rows"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<WarehouseHuRowResponse>? WarehouseHuRows,
        [property: JsonPropertyName("production_hu_rows"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ProductionHuRowResponse>? ProductionHuRows,
        [property: JsonPropertyName("shipped_hu_rows"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ShippedHuRowResponse>? ShippedHuRows,
        [property: JsonPropertyName("coverage"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        CoverageResponse? Coverage);

    private sealed record WarehouseHuRowResponse(
        [property: JsonPropertyName("hu_code")] string HuCode,
        [property: JsonPropertyName("qty")] double Qty,
        [property: JsonPropertyName("location_code")] string? LocationCode,
        [property: JsonPropertyName("location_name")] string? LocationName,
        [property: JsonPropertyName("stock_status")] string StockStatus,
        [property: JsonPropertyName("is_bound_to_order")] bool IsBoundToOrder);

    private sealed record ProductionHuRowResponse(
        [property: JsonPropertyName("hu_code")] string HuCode,
        [property: JsonPropertyName("pallet_status")] string PalletStatus,
        [property: JsonPropertyName("planned_qty")] double PlannedQty,
        [property: JsonPropertyName("filled_qty")] double FilledQty,
        [property: JsonPropertyName("prd_ref")] string? PrdRef,
        [property: JsonPropertyName("fate_code"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? FateCode,
        [property: JsonPropertyName("fate_label"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? FateLabel,
        [property: JsonPropertyName("fate_order_ref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? FateOrderRef,
        [property: JsonPropertyName("fate_doc_ref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? FateDocRef,
        [property: JsonPropertyName("fate_qty"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? FateQty);

    private sealed record ShippedHuRowResponse(
        [property: JsonPropertyName("hu_code")] string HuCode,
        [property: JsonPropertyName("qty")] double Qty);

    private sealed record CoverageResponse(
        [property: JsonPropertyName("ordered_qty")] double OrderedQty,
        [property: JsonPropertyName("warehouse_bound_qty")] double WarehouseBoundQty,
        [property: JsonPropertyName("production_filled_qty")] double ProductionFilledQty,
        [property: JsonPropertyName("shipped_qty")] double ShippedQty,
        [property: JsonPropertyName("covered_qty")] double CoveredQty,
        [property: JsonPropertyName("missing_qty")] double MissingQty);
}
