using System.Text.Json.Serialization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class ProductionPalletEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/orders/{orderId:long}/production-pallets/plan", HandlePlanOrder);
        app.MapPost("/api/orders/{orderId:long}/production-pallets/cancel-plan", HandleCancelPlan);
        app.MapPost("/api/orders/{targetCustomerOrderId:long}/production-pallets/adopt-from-internal/{sourceInternalOrderId:long}", HandleAdoptFromInternal);
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
        app.MapGet("/api/production-pallets/filled-without-stock", HandleFilledWithoutStock);
        app.MapPost("/api/production-pallets/backfill-filled-stock", HandleBackfillFilledStock);
        app.MapGet("/api/production-pallets/filled-stock-reverse-candidates", HandleFilledStockReverseCandidates);
        app.MapPost("/api/production-pallets/reverse-filled-stock-backfill-draft", HandleReverseFilledStockBackfillDraft);
    }

    private static IResult HandleFilledWithoutStock(IDataStore store)
    {
        var service = new ProductionPalletFilledStockBackfillService(store);
        var analyses = service.GetStockAnalyses();
        var safeGaps = service.GetFilledWithoutStock();
        return Results.Ok(new
        {
            ok = true,
            count = safeGaps.Count,
            items = safeGaps.Select(MapStockAnalysis),
            analyses = analyses.Select(MapStockAnalysis)
        });
    }

    private static async Task<IResult> HandleBackfillFilledStock(HttpRequest request, IDataStore store)
    {
        var body = await request.ReadFromJsonAsync<BackfillFilledStockRequest>();
        var dryRun = body?.DryRun ?? true;
        var service = new ProductionPalletFilledStockBackfillService(store);
        var result = service.BackfillFilledStock(dryRun);
        return Results.Ok(new
        {
            ok = true,
            dry_run = result.DryRun,
            analysis_count = result.Analyses.Count,
            gap_count = result.Applied.Count,
            ledger_rows_written = result.LedgerRowsWritten,
            analyses = result.Analyses.Select(MapStockAnalysis),
            applied = result.Applied.Select(MapStockAnalysis)
        });
    }

    private static IResult HandleFilledStockReverseCandidates(IDataStore store)
    {
        var service = new ProductionPalletFilledStockBackfillService(store);
        var candidates = service.GetReverseCandidates();
        return Results.Ok(new
        {
            ok = true,
            count = candidates.Count,
            items = candidates.Select(MapReverseCandidate)
        });
    }

    private static async Task<IResult> HandleReverseFilledStockBackfillDraft(HttpRequest request, IDataStore store)
    {
        var body = await request.ReadFromJsonAsync<ReverseFilledStockBackfillDraftRequest>();
        if (body?.PalletIds == null || body.PalletIds.Count == 0)
        {
            return Results.BadRequest(new { ok = false, error = "INVALID_PALLET_IDS", message = "Укажите pallet_ids." });
        }

        var service = new ProductionPalletFilledStockBackfillService(store);
        var result = service.CreateReverseBackfillDraft(body.PalletIds, body.Comment);
        if (!result.Success)
        {
            return Results.BadRequest(new
            {
                ok = false,
                error = result.Error,
                message = result.Message,
                warnings = result.Warnings
            });
        }

        return Results.Ok(new
        {
            ok = true,
            doc_id = result.DocId,
            doc_ref = result.DocRef,
            line_count = result.LineCount,
            message = result.Message,
            warnings = result.Warnings
        });
    }

    private static object MapStockAnalysis(FilledProductionPalletStockAnalysis analysis) =>
        new
        {
            pallet_id = analysis.PalletId,
            prd_doc_id = analysis.PrdDocId,
            prd_ref = analysis.PrdDocRef,
            order_id = analysis.OrderId,
            order_ref = analysis.OrderRef,
            order_status = analysis.OrderStatus,
            item_id = analysis.ItemId,
            item_name = analysis.ItemName,
            hu_code = analysis.HuCode,
            location_id = analysis.ToLocationId,
            location_code = analysis.ToLocationCode,
            planned_qty = analysis.PlannedQty,
            current_ledger_qty = analysis.CurrentLedgerQty,
            outbound_by_same_hu_qty = analysis.OutboundBySameHuQty,
            outbound_docs_by_same_hu = analysis.OutboundDocsBySameHu,
            outbound_by_order_item_qty = analysis.OutboundByOrderItemQty,
            outbound_docs_by_order_item = analysis.OutboundDocsByOrderItem,
            decision = analysis.Decision,
            expected_current_qty = analysis.ExpectedCurrentQty,
            missing_qty = analysis.MissingQty,
            reason = analysis.Reason,
            status = analysis.Status,
            filled_at = analysis.FilledAt
        };

    private static object MapReverseCandidate(FilledStockReverseCandidate candidate) =>
        new
        {
            pallet_id = candidate.PalletId,
            prd_doc_id = candidate.PrdDocId,
            prd_ref = candidate.PrdDocRef,
            order_id = candidate.OrderId,
            order_ref = candidate.OrderRef,
            order_status = candidate.OrderStatus,
            item_id = candidate.ItemId,
            item_name = candidate.ItemName,
            hu_code = candidate.HuCode,
            location_id = candidate.LocationId,
            location_code = candidate.LocationCode,
            planned_qty = candidate.PlannedQty,
            current_hu_stock = candidate.CurrentHuStock,
            outbound_by_same_hu_qty = candidate.OutboundBySameHuQty,
            outbound_docs_by_same_hu = candidate.OutboundDocsBySameHu,
            outbound_by_order_item_qty = candidate.OutboundByOrderItemQty,
            outbound_docs_by_order_item = candidate.OutboundDocsByOrderItem,
            reverse_qty = candidate.ReverseQty,
            reason = candidate.Reason
        };

    private sealed class BackfillFilledStockRequest
    {
        [JsonPropertyName("dry_run")]
        public bool DryRun { get; init; } = true;
    }

    private sealed class ReverseFilledStockBackfillDraftRequest
    {
        [JsonPropertyName("pallet_ids")]
        public IReadOnlyList<long>? PalletIds { get; init; }

        [JsonPropertyName("comment")]
        public string? Comment { get; init; }
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

    private static IResult HandleCancelPlan(long orderId, ProductionPalletService service)
    {
        try
        {
            var result = service.CancelOrderPlan(orderId);
            return Results.Ok(new
            {
                success = true,
                message = result.Message,
                prd_doc_id = result.PrdDocId,
                removed_pallet_count = result.RemovedPalletCount,
                removed_line_count = result.RemovedLineCount
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { ok = false, success = false, error = ex.Message, message = ex.Message });
        }
    }

    private static IResult HandleAdoptFromInternal(long targetCustomerOrderId, long sourceInternalOrderId, ProductionPalletService service)
    {
        try
        {
            var result = service.AdoptPlanFromInternal(targetCustomerOrderId, sourceInternalOrderId);
            return Results.Ok(new
            {
                success = true,
                message = result.Message,
                source_order_id = result.SourceOrderId,
                target_order_id = result.TargetOrderId,
                source_prd_doc_id = result.SourcePrdDocId,
                target_prd_doc_id = result.TargetPrdDocId,
                transferred_pallet_count = result.TransferredPalletCount,
                transferred_line_count = result.TransferredLineCount,
                transferred_hu_codes = result.TransferredHuCodes,
                source_order_status = result.SourceOrderStatus,
                source_order_comment_updated = result.SourceOrderCommentUpdated,
                warnings = result.Warnings.Select(warning => new
                {
                    code = warning.Code,
                    message = warning.Message
                })
            });
        }
        catch (ProductionPalletPlanAdoptionException ex)
        {
            return Results.BadRequest(new { ok = false, success = false, error_code = ex.Code, error = ex.Message, message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { ok = false, success = false, error_code = "INVALID_OPERATION", error = ex.Message, message = ex.Message });
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

    private static async Task<IResult> HandleMarkPrinted(
        long orderId,
        HttpRequest request,
        ProductionPalletService service)
    {
        try
        {
            IReadOnlyList<long>? palletIds = null;
            if (request.ContentLength > 0)
            {
                var body = await request.ReadFromJsonAsync<MarkPrintedRequest>();
                palletIds = body?.PalletIds;
            }

            var updated = service.MarkPrinted(orderId, palletIds, DateTime.Now);
            return Results.Ok(new { ok = true, updated_count = updated });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { ok = false, error = ex.Message, message = ex.Message });
        }
    }

    private sealed class MarkPrintedRequest
    {
        [JsonPropertyName("pallet_ids")]
        public IReadOnlyList<long>? PalletIds { get; init; }
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
            client_name = row.ClientName,
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
