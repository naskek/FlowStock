using System.Diagnostics;
using System.Globalization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services;
using FlowStock.Core.Services.Marking;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FlowStock.Server;

public static class OrderMarkingExportEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/orders/{orderId:long}/marking/preview", HandlePreview);
        app.MapPost("/api/orders/{orderId:long}/marking/export", HandleExport);
        app.MapPost("/api/orders/{orderId:long}/marking/import/preview", HandleImportPreview)
            .DisableAntiforgery();
        app.MapPost("/api/orders/{orderId:long}/marking/import/confirm", HandleImportConfirm)
            .DisableAntiforgery();
    }

    private static async Task<IResult> HandleImportPreview(
        long orderId,
        HttpRequest request,
        IDataStore dataStore,
        CancellationToken cancellationToken)
    {
        try
        {
            if (dataStore is not IOrderScopedMarkingImportStore store)
            {
                return Results.Problem("Order-scoped marking import is unavailable.", statusCode: 503);
            }

            var envelope = new OrderMarkingExportService(dataStore)
                .EnsureCustomerImportEnvelope(orderId, DateTime.UtcNow);
            if (!envelope.IsSuccess)
            {
                if (envelope.Message.Contains(
                        MarkingCutoverRuntimeErrors.MaintenanceRequired,
                        StringComparison.Ordinal))
                {
                    return Results.Conflict(new
                    {
                        error = MarkingCutoverRuntimeErrors.MaintenanceRequired,
                        message = envelope.Message
                    });
                }

                return Results.BadRequest(new { error = "MARKING_IMPORT_SCOPE_UNAVAILABLE", message = envelope.Message });
            }

            var files = await ReadImportFiles(request, cancellationToken);
            var result = new OrderScopedMarkingImportService(store).Preview(orderId, files);
            return result.IsValid
                ? Results.Ok(MapImportPreview(result))
                : string.Equals(
                    result.ErrorCode,
                    MarkingCutoverRuntimeErrors.MaintenanceRequired,
                    StringComparison.Ordinal)
                    ? Results.Conflict(MapImportPreview(result))
                    : Results.BadRequest(MapImportPreview(result));
        }
        catch (InvalidDataException ex)
        {
            return Results.BadRequest(new { error = "INVALID_MULTIPART_REQUEST", message = ex.Message });
        }
    }

    private static async Task<IResult> HandleImportConfirm(
        long orderId,
        HttpRequest request,
        IDataStore dataStore,
        CancellationToken cancellationToken)
    {
        try
        {
            if (dataStore is not IOrderScopedMarkingImportStore store)
            {
                return Results.Problem("Order-scoped marking import is unavailable.", statusCode: 503);
            }

            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "MULTIPART_FORM_REQUIRED" });
            }

            var form = await request.ReadFormAsync(cancellationToken);
            if (!Guid.TryParse(form["batch_id"], out var batchId)
                || string.IsNullOrWhiteSpace(form["snapshot_hash"])
                || string.IsNullOrWhiteSpace(form["idempotency_key"]))
            {
                return Results.BadRequest(new { error = "batch_id, snapshot_hash and idempotency_key are required" });
            }

            var files = await ReadImportFiles(form.Files, cancellationToken);
            var confirmRecovery = bool.TryParse(form["confirm_recovery"], out var parsedRecovery) && parsedRecovery;
            var result = new OrderScopedMarkingImportService(store).Confirm(
                orderId,
                batchId,
                form["snapshot_hash"].ToString(),
                form["idempotency_key"].ToString(),
                confirmRecovery,
                files);
            return Results.Ok(new
            {
                batch_id = result.BatchId,
                already_confirmed = result.WasAlreadyConfirmed,
                persisted_code_count = result.PersistedCodeCount,
                activated_marking_order_ids = result.ActivatedMarkingOrderIds
            });
        }
        catch (InvalidDataException ex)
        {
            return Results.BadRequest(new { error = "INVALID_MULTIPART_REQUEST", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static async Task<IReadOnlyList<MarkingImportUploadFile>> ReadImportFiles(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            throw new InvalidDataException("multipart/form-data is required");
        }

        var form = await request.ReadFormAsync(cancellationToken);
        return await ReadImportFiles(form.Files, cancellationToken);
    }

    private static async Task<IReadOnlyList<MarkingImportUploadFile>> ReadImportFiles(
        IFormFileCollection formFiles,
        CancellationToken cancellationToken)
    {
        var result = new List<MarkingImportUploadFile>(formFiles.Count);
        foreach (var file in formFiles)
        {
            await using var stream = new MemoryStream();
            await file.CopyToAsync(stream, cancellationToken);
            result.Add(new MarkingImportUploadFile(file.FileName, stream.ToArray()));
        }

        return result;
    }

    private static object MapImportPreview(OrderScopedMarkingImportPreviewResult result) => new
    {
        ok = result.IsValid,
        error = result.ErrorCode,
        message = result.Message,
        snapshot_hash = result.SnapshotHash,
        requires_recovery_confirmation = result.RequiresRecoveryConfirmation,
        warnings = result.Warnings,
        files = result.Files.Select(file => new
        {
            filename = file.FileName,
            file_hash = file.FileHash,
            file_size_bytes = file.FileSizeBytes,
            total_rows = file.TotalRows,
            valid_rows = file.ValidRows,
            invalid_rows = file.InvalidRows,
            duplicate_rows = file.DuplicateRows,
            warnings = file.Warnings
        }),
        requests = result.Requests.Select(markingRequest => new
        {
            marking_order_id = markingRequest.MarkingOrderId,
            request_number = markingRequest.RequestNumber,
            gtin = markingRequest.Gtin,
            required_qty = markingRequest.RequiredQuantity,
            reserve_qty = markingRequest.ReserveQuantity,
            requested_qty = markingRequest.RequestedQuantity,
            imported_before = markingRequest.ImportedBefore,
            valid_in_batch = markingRequest.ValidInBatch,
            imported_after = markingRequest.ImportedAfter,
            coverage_will_activate = markingRequest.CoverageWillActivate,
            reserve_short = markingRequest.ReserveShort
        })
    };

    private static IResult HandlePreview(long orderId, IDataStore store, ILoggerFactory loggerFactory)
    {
        var stopwatch = Stopwatch.StartNew();
        var logger = loggerFactory.CreateLogger("FlowStock.Server.OrderMarkingExportEndpoint");
        try
        {
            var result = new OrderMarkingExportService(store).Preview(orderId);
            LogOperation(
                logger,
                "preview",
                result.IsSuccess ? "success" : "failure",
                stopwatch.ElapsedMilliseconds,
                orderId,
                result.LineCount,
                0,
                0);
            if (!result.IsSuccess)
            {
                return Results.BadRequest(new ApiResult(false, result.Message));
            }

            return Results.Ok(MapPreviewResponse(result));
        }
        catch (Exception ex)
        {
            LogOperation(logger, "preview", "exception", stopwatch.ElapsedMilliseconds, orderId, 0, 0, 0, ex);
            throw;
        }
    }

    private static IResult HandleExport(
        long orderId,
        HttpResponse response,
        IDataStore store,
        ILoggerFactory loggerFactory)
    {
        var stopwatch = Stopwatch.StartNew();
        var logger = loggerFactory.CreateLogger("FlowStock.Server.OrderMarkingExportEndpoint");
        try
        {
            var result = new OrderMarkingExportService(store).Export(orderId, DateTime.Now);
            LogOperation(
                logger,
                "export",
                result.IsSuccess ? "success" : "failure",
                stopwatch.ElapsedMilliseconds,
                orderId,
                result.LineCount,
                result.CreatedCodeQty,
                result.ReusedCodeQty);
            if (!result.IsSuccess)
            {
                return Results.BadRequest(new ApiResult(false, result.Message));
            }

            if (result.FileBytes == null)
            {
                return Results.Ok(MapResponse(result));
            }

            response.Headers["X-FlowStock-Marking-Line-Count"] = result.LineCount.ToString(CultureInfo.InvariantCulture);
            response.Headers["X-FlowStock-Marking-Export-Line-Count"] = result.ExportLineCount.ToString(CultureInfo.InvariantCulture);
            response.Headers["X-FlowStock-Marking-Created-Qty"] = result.CreatedCodeQty.ToString("0.###", CultureInfo.InvariantCulture);
            response.Headers["X-FlowStock-Marking-Reused-Qty"] = result.ReusedCodeQty.ToString("0.###", CultureInfo.InvariantCulture);
            return Results.File(
                result.FileBytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                result.FileName);
        }
        catch (Exception ex)
        {
            LogOperation(logger, "export", "exception", stopwatch.ElapsedMilliseconds, orderId, 0, 0, 0, ex);
            throw;
        }
    }

    private static void LogOperation(
        ILogger logger,
        string operation,
        string outcome,
        long elapsedMs,
        long orderId,
        int lineCount,
        double createdCodeQty,
        double reusedCodeQty,
        Exception? exception = null)
    {
        const string template =
            "Marking operation completed: operation={Operation}, outcome={Outcome}, elapsed_ms={ElapsedMs}, "
            + "order_id={OrderId}, line_count={LineCount}, created_code_qty={CreatedCodeQty}, reused_code_qty={ReusedCodeQty}";
        if (exception == null)
        {
            logger.LogInformation(
                template,
                operation,
                outcome,
                elapsedMs,
                orderId,
                lineCount,
                createdCodeQty,
                reusedCodeQty);
            return;
        }

        logger.LogError(
            exception,
            template,
            operation,
            outcome,
            elapsedMs,
            orderId,
            lineCount,
            createdCodeQty,
            reusedCodeQty);
    }

    private static object MapPreviewResponse(OrderMarkingExportPreviewResult result)
    {
        return new
        {
            order_id = result.OrderId,
            order_ref = result.OrderRef,
            line_count = result.LineCount,
            total_qty = result.TotalQty,
            message = result.Message,
            lines = result.Lines.Select(line => new
            {
                order_line_id = line.OrderLineId,
                item_id = line.ItemId,
                item_name = line.ItemName,
                gtin = line.Gtin,
                qty = line.Qty,
                marking_applicable = line.MarkingApplicable,
                required_qty = line.RequiredQty,
                covered_qty = line.CoveredQty,
                remaining_to_produce = line.RemainingToProduce,
                planned_qty = line.PlannedQty,
                unplanned_qty = line.UnplannedQty,
                scoped_qty = line.ScopedQty,
                requested_qty = line.RequestedQty,
                imported_qty = line.ImportedQty,
                reserve_qty = line.ReserveQty,
                hu_count = line.HuCount,
                hu_codes = line.HuCodes
            }).ToArray()
        };
    }

    private static object MapResponse(OrderMarkingExportResult result)
    {
        return new
        {
            ok = true,
            message = result.Message,
            line_count = result.LineCount,
            export_line_count = result.ExportLineCount,
            required_qty = result.RequiredQty,
            covered_qty = result.CoveredQty,
            created_code_qty = result.CreatedCodeQty,
            reused_code_qty = result.ReusedCodeQty,
            lines = result.Lines.Select(line => new
            {
                order_line_id = line.OrderLineId,
                item_id = line.ItemId,
                item_name = line.ItemName,
                gtin = line.Gtin,
                required_qty = line.RequiredQty,
                covered_qty = line.CoveredQty,
                existing_code_qty = line.ExistingCodeQty,
                export_qty = line.ExportQty
            }).ToArray()
        };
    }
}
