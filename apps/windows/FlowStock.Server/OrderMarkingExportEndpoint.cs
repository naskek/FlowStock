using System.Globalization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class OrderMarkingExportEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/orders/{orderId:long}/marking/preview", HandlePreview);
        app.MapPost("/api/orders/{orderId:long}/marking/export", HandleExport);
    }

    private static IResult HandlePreview(long orderId, IDataStore store)
    {
        var result = new OrderMarkingExportService(store, new MarkingExcelService(store)).Preview(orderId);
        if (!result.IsSuccess)
        {
            return Results.BadRequest(new ApiResult(false, result.Message));
        }

        return Results.Ok(MapPreviewResponse(result));
    }

    private static IResult HandleExport(long orderId, HttpResponse response, IDataStore store, MarkingExcelService markingExcel)
    {
        var result = new OrderMarkingExportService(store, markingExcel).Export(orderId, DateTime.Now);
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
