using System.Globalization;
using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

// Read-only suggestion. Canonical create still allocates under the writer lock.
public static class OrderReferencePreview
{
    public static string GetNext(IDataStore store)
    {
        IReadOnlyList<string> refs;
        using (OperationTimings.Measure("order_refs"))
            refs = store is IOrderRefLookupStore lookup
                ? lookup.GetAllOrderRefsForNextNumber()
                : store.GetOrders().Select(order => order.OrderRef).ToArray();
        long max = 0;
        foreach (var value in refs) Include(value);
        using (OperationTimings.Measure("pending_refs"))
        {
            foreach (var request in store.GetOrderRequests(includeResolved: false))
            {
                if (!string.Equals(request.RequestType, OrderRequestType.CreateOrder, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    using var json = JsonDocument.Parse(request.PayloadJson);
                    if (json.RootElement.ValueKind == JsonValueKind.Object
                        && json.RootElement.TryGetProperty("order_ref", out var value))
                        Include(value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString());
                }
                catch (JsonException) { /* Malformed legacy request has no numeric suggestion. */ }
            }
        }
        return checked(max + 1).ToString("D3", CultureInfo.InvariantCulture);

        void Include(string? raw)
        {
            var value = raw?.Trim();
            if (!string.IsNullOrEmpty(value) && value.All(char.IsDigit)
                && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && number > max) max = number;
        }
    }
}
