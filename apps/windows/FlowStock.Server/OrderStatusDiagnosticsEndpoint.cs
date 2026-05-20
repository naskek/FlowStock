using System.Text.Json;
using System.Text.Json.Serialization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class OrderStatusDiagnosticsEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/diagnostics/order-status/refresh-fully-shipped", HandleRefreshFullyShippedAsync);
    }

    private static async Task<IResult> HandleRefreshFullyShippedAsync(HttpRequest request, IDataStore store)
    {
        RefreshFullyShippedOrderStatusRequest? body = null;
        using var reader = new StreamReader(request.Body);
        var rawBody = await reader.ReadToEndAsync();
        if (!string.IsNullOrWhiteSpace(rawBody))
        {
            try
            {
                body = JsonSerializer.Deserialize<RefreshFullyShippedOrderStatusRequest>(
                    rawBody,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { ok = false, error = "INVALID_JSON" });
            }
        }

        var apply = body?.DryRun == true
            ? false
            : body?.Apply == true || body?.DryRun == false;
        var report = new OrderService(store).RefreshFullyShippedCustomerOrderStatuses(apply);
        return Results.Ok(new
        {
            ok = true,
            dry_run = report.DryRun,
            refreshed_count = report.RefreshedCount,
            changed_count = report.ChangedCount,
            orders = report.Rows.Select(row => new
            {
                order_id = row.OrderId,
                order_ref = row.OrderRef,
                old_status = OrderStatusMapper.StatusToString(row.OldStatus),
                new_status = OrderStatusMapper.StatusToString(row.NewStatus),
                total_ordered_qty = row.TotalOrderedQty,
                total_shipped_qty = row.TotalShippedQty,
                updated = row.Updated
            })
        });
    }

    private sealed class RefreshFullyShippedOrderStatusRequest
    {
        [JsonPropertyName("dry_run")]
        public bool? DryRun { get; init; }

        [JsonPropertyName("apply")]
        public bool? Apply { get; init; }
    }
}
