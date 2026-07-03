using Microsoft.AspNetCore.Routing;

namespace FlowStock.Server.Discovery;

public static class FlowStockDiscoveryEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/discovery", (FlowStockDiscoveryOptions discovery) => Results.Ok(discovery.ToResponse()));
    }
}
