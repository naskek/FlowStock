using FlowStock.Core.Abstractions;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace FlowStock.Server;

public static class ProductionNeedCreateOrdersEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/production-needs/create-orders", HandleCreateAsync);
    }

    private static async Task<IResult> HandleCreateAsync(HttpRequest request, IDataStore store)
    {
        try
        {
            var payload = await TryReadRequestAsync(request);
            var requestedLines = payload?.Rows?
                .Where(row => row.ItemId > 0 && row.QtyOrdered > 0)
                .Select(row => new ProductionNeedOrderDraftRequestLine
                {
                    ItemId = row.ItemId,
                    QtyOrdered = row.QtyOrdered
                })
                .ToArray();
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
        if (request.ContentLength.GetValueOrDefault() <= 0)
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
