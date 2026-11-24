using System.Globalization;
using FlowStock.Core.Abstractions;

namespace FlowStock.Core.Services;

public static class OrderReferenceAllocator
{
    public static void Lock(IDataStore store)
    {
        if (store is IOrderRefSerializationStore serialization)
            serialization.LockOrderReferences();
    }

    // Must run in the transaction that writes the order. An outer dispatcher
    // must acquire Lock before taking any request/order/item/HU locks as well.
    public static string Resolve(IDataStore store, string? requestedRef, long excludedOrderId = 0)
    {
        Lock(store);
        using var timing = OperationTimings.Measure("ref_lookup");
        var requested = string.IsNullOrWhiteSpace(requestedRef) ? null : requestedRef.Trim();
        if (requested != null)
        {
            var collision = store is IOrderRefLookupStore lookup
                ? lookup.HasOtherOrderWithRef(excludedOrderId, requested)
                : store.GetOrders().Any(order => order.Id != excludedOrderId
                    && string.Equals(order.OrderRef, requested, StringComparison.OrdinalIgnoreCase));
            if (!collision) return requested;
        }

        var refs = store is IOrderRefLookupStore optimized
            ? optimized.GetAllOrderRefsForNextNumber()
            : store.GetOrders().Select(order => order.OrderRef).ToArray();
        long max = 0;
        foreach (var raw in refs)
        {
            var value = raw?.Trim();
            if (!string.IsNullOrEmpty(value) && value.All(char.IsDigit)
                && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && number > max)
                max = number;
        }

        // Exhaustion must roll back rather than wrap into a conflicting number.
        return checked(max + 1).ToString("D3", CultureInfo.InvariantCulture);
    }
}
