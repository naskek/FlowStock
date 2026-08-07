using System.Text.Json;
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
        app.MapGet("/api/orders/{orderId:long}/production-pallets/pre-plan-coverage-preview", HandlePrePlanCoveragePreview);
        // Compatibility alias: старый маршрут warning из 81774cf, отдаёт тот же расширенный payload.
        app.MapGet("/api/orders/{orderId:long}/production-pallets/internal-supply-warning", HandlePrePlanCoveragePreview);
        app.MapGet("/api/orders/{orderId:long}/production-pallets/cancel-plan-options", HandleCancelPlanOptions);
        app.MapPost("/api/orders/{orderId:long}/production-pallets/cancel-plan", HandleCancelPlan);
        app.MapPost("/api/orders/{targetCustomerOrderId:long}/production-pallets/adopt-from-internal/{sourceInternalOrderId:long}", HandleAdoptFromInternal);
        app.MapGet("/api/orders/{orderId:long}/production-pallets/print-rows", HandlePrintRows);
        app.MapPost("/api/orders/{orderId:long}/production-pallets/mark-printed", HandleMarkPrinted);
        app.MapPost("/api/docs/{docId:long}/production-pallets/plan", HandlePlan);
        app.MapGet("/api/docs/{docId:long}/production-pallets", HandleGet);
        app.MapGet("/api/tsd/production/filling-orders", HandleFillingOrders);
        app.MapGet("/api/tsd/production/orders/{orderId:long}/filling-context", HandleFillingContext);
        app.MapPost("/api/tsd/production/orders/{orderId:long}/start-filling", HandleStartFilling);
        app.MapPost("/api/tsd/production/orders/{orderId:long}/complete", HandleCompleteFilling);
        app.MapGet("/api/tsd/production/filling-docs", HandleWorkItems);
        app.MapPost("/api/tsd/production/scan-pallet", HandleScan);
        app.MapPost("/api/tsd/production/fill-pallet", HandleFill);
        app.MapPost("/api/tsd/production/fill-mixed-pallet-components", HandleFillMixedComponents);
        app.MapGet("/api/production-pallets/filled-without-stock", HandleFilledWithoutStock);
        app.MapPost("/api/production-pallets/backfill-filled-stock", HandleBackfillFilledStock);
        app.MapGet("/api/production-pallets/filled-stock-reverse-candidates", HandleFilledStockReverseCandidates);
        app.MapPost("/api/production-pallets/reverse-filled-stock-backfill-draft", HandleReverseFilledStockBackfillDraft);
        app.MapGet("/api/production-pallets/filling-corrections/preview", HandleFillingCorrectionPreview);
        app.MapPost("/api/production-pallets/filling-corrections/confirm", HandleFillingCorrectionConfirm);
        app.MapGet("/api/production-pallets/filling-corrections/history", HandleFillingCorrectionHistory);
    }

    private static IResult HandleFillingCorrectionPreview(string? hu_code, IDataStore store)
    {
        var preview = new ProductionPalletFillingCorrectionService(store).Preview(hu_code);
        return Results.Ok(MapCorrectionPreview(preview));
    }

    private static async Task<IResult> HandleFillingCorrectionConfirm(HttpRequest request, IDataStore store)
    {
        FillingCorrectionConfirmBody? body;
        try
        {
            body = await request.ReadFromJsonAsync<FillingCorrectionConfirmBody>();
        }
        catch (JsonException)
        {
            return Results.BadRequest(new
            {
                ok = false,
                error = "INVALID_JSON",
                message = "Некорректное JSON-тело запроса."
            });
        }

        if (body == null)
        {
            return Results.BadRequest(new { ok = false, error = "INVALID_JSON", message = "Тело запроса обязательно." });
        }

        var result = new ProductionPalletFillingCorrectionService(store).Confirm(new ProductionPalletFillingCorrectionConfirmRequest
        {
            RequestId = body.RequestId ?? string.Empty,
            HuCode = body.HuCode ?? string.Empty,
            ExpectedAction = body.ExpectedAction ?? string.Empty,
            ReasonText = body.ReasonText ?? string.Empty,
            ActorName = body.ActorName,
            DeviceName = body.DeviceName,
            ClientName = body.ClientName,
            ClientVersion = body.ClientVersion
        });
        var payload = MapCorrectionResult(result);
        if (result.Success)
        {
            return Results.Ok(payload);
        }

        return ResolveFillingCorrectionErrorStatus(result.ErrorCode) switch
        {
            StatusCodes.Status400BadRequest => Results.BadRequest(payload),
            StatusCodes.Status403Forbidden => Results.Json(payload, statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Conflict(payload)
        };
    }

    public static int ResolveFillingCorrectionErrorStatus(string? errorCode) =>
        errorCode switch
        {
            ProductionPalletFillingCorrectionErrorCodes.BlockDisabled => StatusCodes.Status403Forbidden,
            ProductionPalletFillingCorrectionErrorCodes.InvalidRequestId
                or ProductionPalletFillingCorrectionErrorCodes.HuRequired
                or ProductionPalletFillingCorrectionErrorCodes.InvalidAction
                or ProductionPalletFillingCorrectionErrorCodes.ReasonRequired
                or ProductionPalletFillingCorrectionErrorCodes.ReasonTooLong
                => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status409Conflict
        };

    private static IResult HandleFillingCorrectionHistory(string? hu_code, IDataStore store)
    {
        var entries = new ProductionPalletFillingCorrectionService(store).History(hu_code);
        return Results.Ok(new
        {
            ok = true,
            hu_code = string.IsNullOrWhiteSpace(hu_code) ? string.Empty : hu_code.Trim().ToUpperInvariant(),
            items = entries.Select(entry => new
            {
                adjustment_id = entry.AdjustmentId,
                action = entry.Action,
                hu_code = entry.HuCode,
                source_pallet_id = entry.SourcePalletId,
                source_prd_doc_id = entry.SourcePrdDocId,
                cor_doc_id = entry.CorDocId,
                replacement_pallet_id = entry.ReplacementPalletId,
                replacement_prd_doc_id = entry.ReplacementPrdDocId,
                reason_text = entry.ReasonText,
                created_at = entry.CreatedAt
            })
        });
    }

    private static object MapCorrectionPreview(ProductionPalletFillingCorrectionPreview preview) => new
    {
        ok = true,
        hu_code = preview.HuCode,
        action = preview.Action,
        can_confirm = preview.CanConfirm,
        source_pallet_id = preview.SourcePalletId,
        source_prd_doc_id = preview.SourcePrdDocId,
        source_prd_ref = preview.SourcePrdRef,
        marking_code_count = preview.MarkingCodeCount,
        components = preview.Components.Select(component => new
        {
            component_id = component.ComponentId,
            doc_line_id = component.DocLineId,
            order_line_id = component.OrderLineId,
            item_id = component.ItemId,
            item_name = component.ItemName,
            planned_qty = component.PlannedQty,
            filled_qty = component.FilledQty
        }),
        ledger_inversion = preview.LedgerInversion.Select(line => new
        {
            source_ledger_entry_id = line.SourceLedgerEntryId,
            source_doc_line_id = line.SourceDocLineId,
            item_id = line.ItemId,
            location_id = line.LocationId,
            hu_code = line.HuCode,
            source_qty = line.SourceQty,
            correction_qty = line.CorrectionQty
        }),
        blockers = preview.Blockers.Select(blocker => new
        {
            code = blocker.Code,
            message = blocker.Message
        })
    };

    private static object MapCorrectionResult(ProductionPalletFillingCorrectionResult result) => new
    {
        ok = result.Success,
        replay = result.Replay,
        error = result.ErrorCode,
        message = result.Message,
        adjustment_id = result.AdjustmentId,
        action = result.Action,
        hu_code = result.HuCode,
        source_pallet_id = result.SourcePalletId,
        source_prd_doc_id = result.SourcePrdDocId,
        cor_doc_id = result.CorDocId,
        cor_doc_ref = result.CorDocRef,
        replacement_pallet_id = result.ReplacementPalletId,
        replacement_prd_doc_id = result.ReplacementPrdDocId
    };

    private sealed class FillingCorrectionConfirmBody
    {
        [JsonPropertyName("request_id")]
        public string? RequestId { get; init; }

        [JsonPropertyName("hu_code")]
        public string? HuCode { get; init; }

        [JsonPropertyName("expected_action")]
        public string? ExpectedAction { get; init; }

        [JsonPropertyName("reason_text")]
        public string? ReasonText { get; init; }

        [JsonPropertyName("actor_name")]
        public string? ActorName { get; init; }

        [JsonPropertyName("device_name")]
        public string? DeviceName { get; init; }

        [JsonPropertyName("client_name")]
        public string? ClientName { get; init; }

        [JsonPropertyName("client_version")]
        public string? ClientVersion { get; init; }
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

    private static async Task<IResult> HandlePlanOrder(long orderId, HttpRequest request, ProductionPalletService service)
    {
        PlanOrderRequest parsedRequest;
        ProductionPalletPlanMode mode;
        try
        {
            parsedRequest = await TryReadPlanRequestAsync(request);
            var parsedMode = TryParsePlanMode(parsedRequest.Mode);
            if (parsedMode == null)
            {
                return Results.BadRequest(new
                {
                    ok = false,
                    error = "INVALID_PLAN_MODE",
                    message = "Неизвестный режим планирования. Допустимо: full, skip_internal_supply, adopt_internal_then_plan, apply_selected_coverage_then_plan."
                });
            }

            mode = parsedMode.Value;
        }
        catch (JsonException)
        {
            return Results.BadRequest(new
            {
                ok = false,
                error = "INVALID_JSON",
                message = "Некорректное JSON-тело запроса планирования."
            });
        }

        try
        {
            var modeEcho = mode switch
            {
                ProductionPalletPlanMode.SkipInternalSupply => "skip_internal_supply",
                ProductionPalletPlanMode.AdoptInternalThenPlan => "adopt_internal_then_plan",
                ProductionPalletPlanMode.ApplySelectedCoverageThenPlan => "apply_selected_coverage_then_plan",
                _ => "full"
            };
            var result = mode == ProductionPalletPlanMode.ApplySelectedCoverageThenPlan
                ? service.PlanOrderApplySelectedCoverageThenPlan(
                    orderId,
                    new ProductionPalletSelectedCoveragePlanRequest
                    {
                        SelectedWarehouseHus = (parsedRequest.SelectedWarehouseHus ?? Array.Empty<SelectedWarehouseHuRequest>())
                            .Select(row => new ProductionPalletSelectedWarehouseHu
                            {
                                HuCode = row.HuCode ?? string.Empty,
                                ItemId = row.ItemId,
                                TargetOrderLineId = row.TargetOrderLineId
                            })
                            .ToArray(),
                        SelectedInternalProductionPalletIds =
                            parsedRequest.SelectedInternalProductionPalletIds ?? Array.Empty<long>(),
                        PlanRemainder = parsedRequest.PlanRemainder ?? true
                    })
                : service.PlanOrder(orderId, mode);
            return Results.Ok(MapOrderPlan(result, modeEcho));
        }
        catch (ProductionPalletSelectedCoverageException ex)
        {
            return Results.Conflict(new
            {
                ok = false,
                error = ex.Code,
                message = ex.Message,
                problems = ex.Problems
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { ok = false, error = ex.Message, message = ex.Message });
        }
    }

    /// <summary>
    /// Пустое тело, отсутствующий Content-Type, null-JSON, {} или отсутствующий mode означают Full;
    /// неизвестный mode возвращает null (400 INVALID_PLAN_MODE). Клиентские qty/order_line_ids не читаются.
    /// </summary>
    private static async Task<PlanOrderRequest> TryReadPlanRequestAsync(HttpRequest request)
    {
        string raw;
        using (var reader = new StreamReader(request.Body))
        {
            raw = await reader.ReadToEndAsync();
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new PlanOrderRequest();
        }

        return JsonSerializer.Deserialize<PlanOrderRequest>(raw, PlanRequestJsonOptions) ?? new PlanOrderRequest();
    }

    private static ProductionPalletPlanMode? TryParsePlanMode(string? mode)
    {
        var rawMode = mode?.Trim();
        if (string.IsNullOrEmpty(rawMode) || string.Equals(rawMode, "full", StringComparison.OrdinalIgnoreCase))
        {
            return ProductionPalletPlanMode.Full;
        }

        if (string.Equals(rawMode, "skip_internal_supply", StringComparison.OrdinalIgnoreCase))
        {
            return ProductionPalletPlanMode.SkipInternalSupply;
        }

        if (string.Equals(rawMode, "adopt_internal_then_plan", StringComparison.OrdinalIgnoreCase))
        {
            return ProductionPalletPlanMode.AdoptInternalThenPlan;
        }

        if (string.Equals(rawMode, "apply_selected_coverage_then_plan", StringComparison.OrdinalIgnoreCase))
        {
            return ProductionPalletPlanMode.ApplySelectedCoverageThenPlan;
        }

        return null;
    }

    private static readonly JsonSerializerOptions PlanRequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class PlanOrderRequest
    {
        [JsonPropertyName("mode")]
        public string? Mode { get; init; }

        [JsonPropertyName("selected_warehouse_hus")]
        public IReadOnlyList<SelectedWarehouseHuRequest>? SelectedWarehouseHus { get; init; }

        [JsonPropertyName("selected_internal_production_pallet_ids")]
        public IReadOnlyList<long>? SelectedInternalProductionPalletIds { get; init; }

        [JsonPropertyName("plan_remainder")]
        public bool? PlanRemainder { get; init; }
    }

    private sealed class SelectedWarehouseHuRequest
    {
        [JsonPropertyName("hu_code")]
        public string? HuCode { get; init; }

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("target_order_line_id")]
        public long TargetOrderLineId { get; init; }
    }

    private static IResult HandlePrePlanCoveragePreview(long orderId, ProductionPalletService service)
    {
        try
        {
            var preview = service.GetCustomerPrePlanCoveragePreview(orderId);
            return Results.Ok(new
            {
                ok = true,
                order_id = preview.OrderId,
                order_ref = preview.OrderRef,
                has_warning = preview.HasWarning,
                message = preview.Message,
                lines = preview.Lines.Select(MapInternalSupplyWarningLine),
                would_plan_line_count = preview.WouldPlanLineCount,
                safe_line_count = preview.SafeLineCount,
                warning_line_count = preview.WarningLineCount,
                warehouse_hu_candidates = preview.WarehouseHuCandidates.Select(MapWarehouseHuCandidate),
                internal_planned_hu_candidates = preview.InternalPlannedHuCandidates.Select(MapInternalPlannedHuCandidate),
                adoptable_internal_planned_hus = preview.AdoptableInternalPlannedHus.Select(MapProjectedAdoptionHu),
                adoption_skipped_candidates = preview.AdoptionSkippedCandidates.Select(MapAdoptionSkippedCandidate),
                projected_adopted_pallet_count = preview.ProjectedAdoptedPalletCount,
                projected_adopted_qty = preview.ProjectedAdoptedQty,
                projected_remaining_qty_after_adoption = preview.ProjectedRemainingQtyAfterAdoption,
                has_free_warehouse_hu = preview.HasFreeWarehouseHu,
                free_warehouse_hu = preview.FreeWarehouseHuLines.Select(line => new
                {
                    customer_order_line_id = line.OrderLineId,
                    item_id = line.ItemId,
                    item_name = line.ItemName,
                    would_plan_qty = line.WouldPlanQty,
                    free_hu_count = line.FreeHuCount,
                    free_hu_qty = line.FreeHuQty
                })
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { ok = false, error = ex.Message, message = ex.Message });
        }
    }

    private static IResult HandleCancelPlanOptions(long orderId, ProductionPalletService service)
    {
        try
        {
            var options = service.GetCancelPlanOptions(orderId);
            return Results.Ok(new
            {
                order_id = options.OrderId,
                order_ref = options.OrderRef,
                rows = options.Rows.Select(MapCancelPlanRow)
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { ok = false, success = false, error = ex.Message, message = ex.Message });
        }
    }

    private static async Task<IResult> HandleCancelPlan(long orderId, HttpRequest request, ProductionPalletService service)
    {
        try
        {
            IReadOnlyList<long>? palletIds = null;
            if (request.ContentLength is null or > 0)
            {
                var body = await request.ReadFromJsonAsync<CancelPlanRequest>();
                palletIds = body?.PalletIds;
            }

            if (palletIds == null || palletIds.Count == 0)
            {
                return Results.BadRequest(new
                {
                    ok = false,
                    success = false,
                    error = "INVALID_PALLET_IDS",
                    message = "Укажите pallet_ids для удаления выбранных паллет."
                });
            }

            var result = service.CancelOrderPlan(orderId, palletIds);
            return Results.Ok(new
            {
                success = true,
                message = result.Message,
                prd_doc_id = result.PrdDocId,
                removed_pallet_count = result.RemovedPalletCount,
                removed_line_count = result.RemovedLineCount,
                requested_pallet_ids = result.RequestedPalletIds,
                removed_pallet_ids = result.RemovedPalletIds,
                skipped_pallet_ids = result.SkippedPalletIds
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { ok = false, success = false, error = ex.Message, message = ex.Message });
        }
    }

    private static object MapCancelPlanRow(ProductionPalletCancelPlanRow row)
    {
        return new
        {
            pallet_id = row.PalletId,
            prd_doc_id = row.PrdDocId,
            prd_doc_ref = row.PrdDocRef,
            order_line_id = row.OrderLineId,
            item_id = row.ItemId,
            item_name = row.ItemName,
            hu_code = row.HuCode,
            planned_qty = row.PlannedQty,
            status = row.Status,
            is_selectable = row.IsSelectable,
            is_selected_by_default = row.IsSelectedByDefault,
            disabled_reason = row.DisabledReason,
            has_marking_warning = row.HasMarkingWarning
        };
    }

    private sealed class CancelPlanRequest
    {
        [JsonPropertyName("pallet_ids")]
        public IReadOnlyList<long>? PalletIds { get; init; }
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

    private static IResult HandleStartFilling(
        long orderId,
        ProductionPalletService service,
        IServiceProvider services)
    {
        return HandleFillingContext(orderId, service, services);
    }

    private static IResult HandleFillingContext(
        long orderId,
        ProductionPalletService service,
        IServiceProvider services)
    {
        try
        {
            return Results.Ok(MapFillingContext(
                service.GetFillingContext(orderId),
                GetProductionPresentations(services, orderId)));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { ok = false, error = ex.Message, message = ex.Message });
        }
    }

    private static IResult HandleCompleteFilling(
        long orderId,
        ProductionFillingCompleteRequest request,
        ProductionPalletService service,
        IDataStore store,
        IServiceProvider services)
    {
        try
        {
            var result = service.CompleteFilling(orderId, request.DeviceId);
            if (!result.Success)
            {
                return Results.BadRequest(new { ok = false, error = result.Error, message = result.Message });
            }

            return Results.Ok(new
            {
                ok = true,
                message = result.Message,
                is_closed = true,
                closed_at = result.ClosedAt,
                operation_id = orderId,
                context = result.Context == null
                    ? null
                    : MapFillingContext(result.Context, GetProductionPresentations(services, orderId))
            });
        }
        catch (Exception ex)
        {
            BusinessNotificationEndpoints.TryAddFinalizeFailure(store, "PRODUCTION_FILLING", orderId, null, ex);
            return Results.Json(new { ok = false, error = "FILLING_COMPLETE_FAILED", message = ex.Message }, statusCode: 500);
        }
    }

    private static IResult HandleScan(
        ProductionPalletScanRequest request,
        ProductionPalletService service,
        IServiceProvider services)
    {
        try
        {
            var result = service.Scan(request.OrderId, request.PrdDocId, request.HuCode);
            if (!result.Success)
            {
                return Results.BadRequest(new { ok = false, error = result.Error, message = result.ErrorMessage ?? result.Error });
            }

            var presentations = result.OrderId is long orderId
                ? GetProductionPresentations(services, orderId)
                : null;
            return Results.Ok(MapScanResult(result, presentations));
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = ex.Message, message = ex.Message }, statusCode: 500);
        }
    }

    private static IResult HandleFill(
        ProductionPalletFillRequest request,
        ProductionPalletService service,
        IServiceProvider services)
    {
        try
        {
            var result = service.Fill(request.HuCode, request.DeviceId, request.OrderId, request.PrdDocId);
            if (!result.Success)
            {
                return Results.BadRequest(new { ok = false, error = result.Error, message = result.ErrorMessage ?? result.Error });
            }

            var presentations = result.Pallet?.OrderId is long orderId
                ? GetProductionPresentations(services, orderId)
                : null;
            return Results.Ok(new
            {
                ok = true,
                already_filled = result.AlreadyFilled,
                prd_auto_closed = result.PrdAutoClosed,
                closed_prd_doc_id = result.ClosedPrdDocId,
                closed_prd_doc_ref = result.ClosedPrdDocRef,
                error = result.Error,
                message = result.ErrorMessage ?? result.Message,
                pallet = result.Pallet == null ? null : MapPallet(result.Pallet, FindPresentation(presentations, result.Pallet.HuCode)),
                document = result.Document == null ? null : MapDocument(result.Document, presentations)
            });
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = ex.Message, message = ex.Message }, statusCode: 500);
        }
    }

    private static IResult HandleFillMixedComponents(
        ProductionPalletMixedComponentFillRequest request,
        ProductionPalletService service,
        IServiceProvider services)
    {
        try
        {
            var result = service.FillMixedComponents(
                request.HuCode,
                request.ComponentLineIds,
                request.DeviceId,
                request.OrderId,
                request.PrdDocId);
            if (!result.Success)
            {
                return Results.BadRequest(new { ok = false, error = result.Error, message = result.ErrorMessage ?? result.Error });
            }

            var presentations = result.Pallet?.OrderId is long orderId
                ? GetProductionPresentations(services, orderId)
                : null;
            return Results.Ok(new
            {
                ok = true,
                already_filled = result.AlreadyFilled,
                effective_status = result.EffectiveStatus,
                filled_component_count = result.FilledComponentCount,
                total_component_count = result.TotalComponentCount,
                ledger_written = result.LedgerWritten,
                prd_auto_closed = result.PrdAutoClosed,
                closed_prd_doc_id = result.ClosedPrdDocId,
                closed_prd_doc_ref = result.ClosedPrdDocRef,
                error = result.Error,
                message = result.ErrorMessage ?? result.Message,
                pallet = result.Pallet == null ? null : MapPallet(result.Pallet, FindPresentation(presentations, result.Pallet.HuCode)),
                document = result.Document == null ? null : MapDocument(result.Document, presentations)
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
            summary = MapSummary(order.Summary),
            required_pallets = order.Progress.RequiredPallets,
            scanned_pallets = order.Progress.ScannedPallets,
            remaining_pallets = order.Progress.RemainingPallets,
            can_close = order.Progress.CanClose,
            is_closed = order.Progress.IsClosed,
            operation_fingerprint = order.Progress.OperationFingerprint
        };
    }

    private static object MapFillingContext(
        ProductionFillingContext context,
        IReadOnlyDictionary<string, ProductionTaskPresentation>? presentations = null)
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
            required_pallets = context.Progress.RequiredPallets,
            scanned_pallets = context.Progress.ScannedPallets,
            remaining_pallets = context.Progress.RemainingPallets,
            can_close = context.Progress.CanClose,
            is_closed = context.Progress.IsClosed,
            operation_fingerprint = context.Progress.OperationFingerprint,
            document = MapDocument(context.Document, presentations)
        };
    }

    private static object MapOrderPlan(ProductionPalletOrderPlanResult result, string mode = "full")
    {
        return new
        {
            order_id = result.OrderId,
            order_ref = result.OrderRef,
            prd_doc_id = result.PrdDocId,
            prd_ref = result.PrdDocRef,
            prd_doc_ref = result.PrdDocRef,
            was_existing = result.WasExisting,
            production_required = result.ProductionRequired,
            message = result.Message,
            mode,
            planned_pallet_count = result.Summary.PlannedPalletCount,
            planned_qty = result.Summary.PlannedQty,
            filled_pallet_count = result.Summary.FilledPalletCount,
            filled_qty = result.Summary.FilledQty,
            remaining_pallet_count = result.Summary.RemainingPalletCount,
            remaining_qty = result.Summary.RemainingQty,
            planned_order_line_ids = result.PlannedOrderLineIds,
            skipped_lines = result.SkippedLines.Select(MapPlanSkippedLine),
            adopted_internal_planned_hus = result.AdoptedInternalPlannedHus.Select(MapProjectedAdoptionHu),
            adoption_skipped_candidates = result.AdoptionSkippedCandidates.Select(MapAdoptionSkippedCandidate),
            reprint_required_hus = result.ReprintRequiredHus.Select(MapProjectedAdoptionHu),
            bound_warehouse_hus = result.BoundWarehouseHus.Select(MapWarehouseHuCandidate),
            adopted_pallet_count = result.AdoptedPalletCount,
            adopted_qty = result.AdoptedQty,
            bound_warehouse_hu_count = result.BoundWarehouseHuCount,
            bound_warehouse_qty = result.BoundWarehouseQty,
            newly_planned_pallet_count = result.NewlyPlannedPalletCount,
            newly_planned_qty = result.NewlyPlannedQty,
            summary = MapSummary(result.Summary),
            document = MapDocument(result.Document)
        };
    }

    private static object MapWarehouseHuCandidate(ProductionPalletWarehouseHuCandidate row)
    {
        return new
        {
            source_type = row.SourceType,
            hu_code = row.HuCode,
            item_id = row.ItemId,
            item_name = row.ItemName,
            target_order_line_id = row.TargetOrderLineId,
            qty = row.Qty,
            status = row.Status,
            source_ref = row.SourceRef,
            recommended = row.Recommended,
            selected_by_default = row.SelectedByDefault,
            disabled_reason = row.DisabledReason
        };
    }

    private static object MapInternalPlannedHuCandidate(ProductionPalletInternalPlannedHuCandidate row)
    {
        return new
        {
            source_type = row.SourceType,
            production_pallet_id = row.ProductionPalletId,
            hu_code = row.HuCode,
            source_order_id = row.SourceOrderId,
            source_order_ref = row.SourceOrderRef,
            source_prd_doc_id = row.SourcePrdDocId,
            source_prd_doc_ref = row.SourcePrdDocRef,
            source_status = row.SourceStatus,
            target_order_line_id = row.TargetOrderLineId,
            item_id = row.ItemId,
            item_name = row.ItemName,
            planned_qty = row.PlannedQty,
            production_pallet_group = row.ProductionPalletGroup,
            is_mixed = row.IsMixed,
            status = row.Status,
            recommended = row.Recommended,
            selected_by_default = row.SelectedByDefault,
            disabled_reason = row.DisabledReason
        };
    }

    private static object MapPlanSkippedLine(ProductionPalletPlanSkippedLine line)
    {
        return new
        {
            customer_order_line_id = line.OrderLineId,
            item_id = line.ItemId,
            item_name = line.ItemName,
            production_pallet_group = line.ProductionPalletGroup,
            skipped_reason = line.SkippedReason,
            triggered_by_order_line_id = line.TriggeredByOrderLineId,
            internal_refs = line.InternalRefs.Select(MapInternalSupplyWarningLine)
        };
    }

    private static object MapInternalSupplyWarningLine(ProductionPalletInternalSupplyWarningLine line)
    {
        return new
        {
            customer_order_line_id = line.OrderLineId,
            item_id = line.ItemId,
            item_name = line.ItemName,
            would_plan_qty = line.WouldPlanQty,
            internal_order_id = line.InternalOrderId,
            internal_order_ref = line.InternalOrderRef,
            internal_status = line.InternalOrderStatus,
            expected_qty = line.ExpectedQty
        };
    }

    private static object MapProjectedAdoptionHu(ProductionPalletProjectedAdoptionHu row)
    {
        return new
        {
            production_pallet_id = row.ProductionPalletId,
            hu_code = row.HuCode,
            source_order_id = row.SourceOrderId,
            source_order_ref = row.SourceOrderRef,
            source_prd_doc_id = row.SourcePrdDocId,
            source_prd_doc_ref = row.SourcePrdDocRef,
            source_status = row.SourceStatus,
            target_order_line_id = row.TargetOrderLineId,
            item_id = row.ItemId,
            item_name = row.ItemName,
            planned_qty = row.PlannedQty,
            production_pallet_group = row.ProductionPalletGroup,
            is_mixed = row.IsMixed,
            status = row.Status,
            will_require_reprint = row.WillRequireReprint,
            lines = row.Lines.Select(line => new
            {
                source_order_line_id = line.SourceOrderLineId,
                target_order_line_id = line.TargetOrderLineId,
                doc_line_id = line.DocLineId,
                item_id = line.ItemId,
                item_name = line.ItemName,
                planned_qty = line.PlannedQty
            })
        };
    }

    private static object MapAdoptionSkippedCandidate(ProductionPalletAdoptionSkippedCandidate row)
    {
        return new
        {
            production_pallet_id = row.ProductionPalletId,
            hu_code = row.HuCode,
            source_order_id = row.SourceOrderId,
            source_order_ref = row.SourceOrderRef,
            source_prd_doc_id = row.SourcePrdDocId,
            source_prd_doc_ref = row.SourcePrdDocRef,
            source_status = row.SourceStatus,
            target_order_line_id = row.TargetOrderLineId,
            item_id = row.ItemId,
            item_name = row.ItemName,
            planned_qty = row.PlannedQty,
            production_pallet_group = row.ProductionPalletGroup,
            is_mixed = row.IsMixed,
            status = row.Status,
            skip_reason = row.SkipReason
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
            storage_conditions = row.StorageConditions,
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
            status = row.Status,
            source_type = row.SourceType
        };
    }

    private static object MapScanResult(
        ProductionPalletScanResult result,
        IReadOnlyDictionary<string, ProductionTaskPresentation>? presentations = null)
    {
        return new
        {
            ok = true,
            error = result.Error,
            message = result.ErrorMessage,
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
                component_line_id = line.ComponentLineId,
                item_id = line.ItemId,
                item_name = line.ItemName,
                brand = line.Brand,
                qty = line.Qty,
                planned_qty = line.PlannedQty,
                filled_qty = line.FilledQty,
                filled_at = line.FilledAt,
                is_completed = line.IsCompleted,
                uom = line.Uom
            }),
            pallet_index = result.PalletIndex,
            pallet_count = result.PalletCount,
            pallet_status = result.PalletStatus,
            effective_status = result.EffectiveStatus,
            can_fill = result.CanFill,
            filled_component_count = result.Lines.Count(line => line.IsCompleted),
            total_component_count = result.Lines.Count,
            production_presentation = MapProductionPresentation(FindPresentation(presentations, result.HuCode)),
            document = result.Document == null ? null : MapDocument(result.Document, presentations)
        };
    }

    private static object MapDocument(
        ProductionPalletDocument document,
        IReadOnlyDictionary<string, ProductionTaskPresentation>? presentations = null)
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
            pallets = document.Pallets.Select(pallet =>
                MapPallet(pallet, FindPresentation(presentations, pallet.HuCode)))
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

    private static object MapPallet(
        ProductionPallet pallet,
        ProductionTaskPresentation? presentation = null)
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
            effective_status = pallet.EffectiveStatus,
            can_fill = pallet.CanFill,
            is_mixed_pallet = pallet.IsMixedPallet,
            filled_component_count = pallet.FilledComponentCount,
            total_component_count = pallet.TotalComponentCount,
            production_presentation = MapProductionPresentation(presentation),
            lines = pallet.Lines.Select(line => new
            {
                component_line_id = line.Id,
                item_id = line.ItemId,
                item_name = line.ItemName,
                brand = line.Brand,
                qty = line.PlannedQty,
                planned_qty = line.PlannedQty,
                filled_qty = line.FilledQty,
                filled_at = line.FilledAt,
                is_completed = line.IsCompleted,
                uom = line.Uom
            }),
            filled_at = pallet.FilledAt,
            filled_by_device_id = pallet.FilledByDeviceId,
            created_at = pallet.CreatedAt
        };
    }

    private static IReadOnlyDictionary<string, ProductionTaskPresentation>? GetProductionPresentations(
        IServiceProvider services,
        long orderId)
    {
        var readModel = services.GetService<HuOperatorReadModelService>();
        return readModel?.GetProductionForOrder(orderId).ToDictionary(
            row => row.HuCode,
            StringComparer.OrdinalIgnoreCase);
    }

    private static ProductionTaskPresentation? FindPresentation(
        IReadOnlyDictionary<string, ProductionTaskPresentation>? presentations,
        string? huCode)
    {
        if (presentations == null || string.IsNullOrWhiteSpace(huCode))
        {
            return null;
        }

        return presentations.TryGetValue(huCode.Trim(), out var presentation) ? presentation : null;
    }

    private static object? MapProductionPresentation(ProductionTaskPresentation? presentation) =>
        presentation == null
            ? null
            : new
            {
                state = new
                {
                    code = presentation.State.Code,
                    label = presentation.State.Label
                },
                progress = presentation.Progress == null
                    ? null
                    : new
                    {
                        completed_components = presentation.Progress.CompletedComponents,
                        total_components = presentation.Progress.TotalComponents
                    }
            };

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

    private sealed class ProductionFillingCompleteRequest
    {
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

    private sealed class ProductionPalletMixedComponentFillRequest
    {
        [JsonPropertyName("order_id")]
        public long? OrderId { get; init; }

        [JsonPropertyName("prd_doc_id")]
        public long? PrdDocId { get; init; }

        [JsonPropertyName("hu_code")]
        public string? HuCode { get; init; }

        [JsonPropertyName("device_id")]
        public string? DeviceId { get; init; }

        [JsonPropertyName("component_line_ids")]
        public IReadOnlyList<long>? ComponentLineIds { get; init; }
    }
}
