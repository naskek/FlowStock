using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class OrderProducedStockReleaseEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/orders/{orderId:long}/lines/{orderLineId:long}/release-produced-stock", Handle);
    }

    private static IResult Handle(long orderId, long orderLineId, IDataStore store)
    {
        try
        {
            var result = store.ReleaseProducedCustomerStockForOrderLine(orderId, orderLineId);
            return Results.Ok(new OrderProducedStockReleaseEnvelope
            {
                Ok = true,
                OrderId = result.OrderId,
                OrderLineId = result.OrderLineId,
                ReleasedPalletCount = result.ReleasedPalletCount,
                ReleasedHuCodes = result.ReleasedHuCodes,
                ReleasedQty = result.ReleasedQty
            });
        }
        catch (OrderProducedStockReleaseException ex)
        {
            var payload = new ApiErrorResult(false, ex.ErrorCode, ex.Message);
            return ex.ErrorCode is "ORDER_NOT_FOUND" or "ORDER_LINE_NOT_FOUND"
                ? Results.NotFound(payload)
                : Results.BadRequest(payload);
        }
    }
}

public sealed class OrderProducedStockReleaseEnvelope
{
    [System.Text.Json.Serialization.JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("order_id")]
    public long OrderId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("order_line_id")]
    public long OrderLineId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("released_pallet_count")]
    public int ReleasedPalletCount { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("released_hu_codes")]
    public IReadOnlyList<string> ReleasedHuCodes { get; init; } = Array.Empty<string>();

    [System.Text.Json.Serialization.JsonPropertyName("released_qty")]
    public double ReleasedQty { get; init; }
}
