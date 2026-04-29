using FlowStock.Core.Models;
using System.Text.Json.Serialization;

namespace FlowStock.Server;

public sealed class HuStockApiRow
{
    [JsonPropertyName("hu")]
    public string Hu { get; init; } = string.Empty;
    [JsonPropertyName("item_id")]
    public long ItemId { get; init; }
    [JsonPropertyName("location_id")]
    public long LocationId { get; init; }
    [JsonPropertyName("qty")]
    public double Qty { get; init; }

    [JsonPropertyName("origin_internal_order_id")]
    public long? OriginInternalOrderId { get; init; }
    [JsonPropertyName("origin_internal_order_ref")]
    public string? OriginInternalOrderRef { get; init; }

    [JsonPropertyName("reserved_customer_order_id")]
    public long? ReservedCustomerOrderId { get; init; }
    [JsonPropertyName("reserved_customer_order_ref")]
    public string? ReservedCustomerOrderRef { get; init; }
    [JsonPropertyName("reserved_customer_id")]
    public long? ReservedCustomerId { get; init; }
    [JsonPropertyName("reserved_customer_name")]
    public string? ReservedCustomerName { get; init; }
}

public static class HuStockReadModelMapper
{
    public static string BuildHuItemKey(long itemId, string huCode)
    {
        return $"{itemId}|{huCode.Trim().ToUpperInvariant()}";
    }

    public static IReadOnlyDictionary<string, HuOrderContextRow> BuildContextMap(IReadOnlyList<HuOrderContextRow> contexts)
    {
        return (contexts ?? Array.Empty<HuOrderContextRow>())
            .GroupBy(row => BuildHuItemKey(row.ItemId, row.HuCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    public static HuStockApiRow Map(long itemId, long locationId, string huCode, double qty, IReadOnlyDictionary<string, HuOrderContextRow> contextByKey)
    {
        HuOrderContextRow? context = null;
        if (!string.IsNullOrWhiteSpace(huCode))
        {
            contextByKey.TryGetValue(BuildHuItemKey(itemId, huCode), out context);
        }

        return new HuStockApiRow
        {
            Hu = huCode,
            ItemId = itemId,
            LocationId = locationId,
            Qty = qty,
            OriginInternalOrderId = context?.OriginInternalOrderId,
            OriginInternalOrderRef = context?.OriginInternalOrderRef,
            ReservedCustomerOrderId = context?.ReservedCustomerOrderId,
            ReservedCustomerOrderRef = context?.ReservedCustomerOrderRef,
            ReservedCustomerId = context?.ReservedCustomerId,
            ReservedCustomerName = context?.ReservedCustomerName
        };
    }
}
