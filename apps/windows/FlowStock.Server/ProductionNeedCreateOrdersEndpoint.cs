using FlowStock.Core.Abstractions;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class ProductionNeedCreateOrdersEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/production-needs/create-orders", HandleCreate);
    }

    private static IResult HandleCreate(IDataStore store)
    {
        try
        {
            var result = new ProductionNeedOrderCreationService(store).CreateDraftOrders();
            return Results.Ok(new CreateProductionNeedOrdersResponse
            {
                Ok = true,
                Message = result.Message,
                CustomerDraftCount = result.CustomerDraftCount,
                InternalDraftCount = result.InternalDraftCount,
                CreatedLineCount = result.CreatedLineCount,
                CreatedMarkingTaskCount = result.CreatedMarkingTaskCount,
                CreatedMarkingQty = result.CreatedMarkingQty,
                DebugSummary = result.DebugSummary
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new ApiResult(false, ex.Message));
        }
    }
}
