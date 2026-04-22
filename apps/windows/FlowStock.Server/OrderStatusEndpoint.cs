using Microsoft.AspNetCore.Builder;

namespace FlowStock.Server;

public static class OrderStatusEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/orders/{orderId:long}/status", () =>
            Results.BadRequest(new ApiResult(false, "ORDER_STATUS_MANUAL_DISABLED")));
    }
}
