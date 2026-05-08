using FlowStock.Core.Abstractions;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class MarkingCreateFromProductionNeedsEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/marking/create-from-production-needs", HandleCreate);
    }

    private static IResult HandleCreate(IDataStore store)
    {
        try
        {
            var result = new MarkingNeedCreationService(store).CreateFromProductionNeeds(DateTime.Now);
            return Results.Ok(new CreateMarkingFromProductionNeedsResponse
            {
                Ok = true,
                Message = result.Message,
                CreatedTaskCount = result.CreatedTaskCount,
                CreatedQty = result.CreatedQty,
                DebugSummary = result.DebugSummary
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new ApiResult(false, ex.Message));
        }
    }
}
