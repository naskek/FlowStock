using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace FlowStock.Server;

public static class ProductionNeedCreateOrdersEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/reports/production-need/create-orders/preview", HandlePreviewAsync);
        app.MapPost("/api/production-needs/create-orders", HandleCreateAsync);
    }

    private static Task<IResult> HandlePreviewAsync(IDataStore store)
    {
        var preview = new ProductionNeedOrderCreationService(store).PreviewDraftOrders();
        var response = new PreviewProductionNeedOrdersResponse
        {
            Ok = true,
            Message = preview.Message,
            Rows = preview.Rows.Select(row => new PreviewProductionNeedOrdersResponseLine
            {
                ItemId = row.ItemId,
                Gtin = row.Gtin,
                ItemName = row.ItemName,
                QtyToCreate = row.Qty,
                Reason = row.Reason,
                MinStockQty = row.MinStockQty,
                FreeStockQty = row.FreeStockQty,
                OpenInternalOrderQty = row.OpenInternalOrderQty,
                PlannedPalletQty = row.PlannedPalletQty,
                FilledPalletQty = row.FilledPalletQty
            }).ToArray()
        };

        return Task.FromResult<IResult>(Results.Ok(response));
    }

    private static async Task<IResult> HandleCreateAsync(HttpRequest request, IDataStore store)
    {
        try
        {
            var payload = await TryReadRequestAsync(request);
            var hasExplicitQtyPayload = payload?.Rows?.Any(row => row.ItemId > 0 && row.QtyOrdered.HasValue) == true;
            var requestedLines = payload?.Rows?
                .Where(row => row.ItemId > 0 && row.QtyOrdered.HasValue)
                .Select(row => new ProductionNeedOrderDraftRequestLine
                {
                    ItemId = row.ItemId,
                    QtyOrdered = row.QtyOrdered ?? 0
                })
                .ToArray();
            if (hasExplicitQtyPayload && (requestedLines == null || requestedLines.All(row => row.QtyOrdered <= 0)))
            {
                throw new InvalidOperationException("Нет строк с количеством больше нуля для создания внутреннего заказа.");
            }

            var result = new ProductionNeedOrderCreationService(store).CreateDraftOrders(requestedLines);
            return Results.Ok(new CreateProductionNeedOrdersResponse
            {
                Ok = true,
                Message = result.Message,
                CustomerDraftCount = result.CustomerDraftCount,
                InternalDraftCount = result.InternalDraftCount,
                CreatedLineCount = result.CreatedLineCount,
                CreatedQty = result.CreatedQty,
                DebugSummary = result.DebugSummary
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new ApiResult(false, ex.Message));
        }
    }

    private static async Task<CreateProductionNeedOrdersRequest?> TryReadRequestAsync(HttpRequest request)
    {
        if (request.ContentLength.HasValue && request.ContentLength.Value == 0)
        {
            return null;
        }

        try
        {
            return await JsonSerializer.DeserializeAsync<CreateProductionNeedOrdersRequest>(
                request.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Некорректный запрос формирования производственного заказа.");
        }
    }
}
