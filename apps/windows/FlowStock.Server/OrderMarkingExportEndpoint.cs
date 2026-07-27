using System.Diagnostics;
using System.Globalization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
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
    }

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
