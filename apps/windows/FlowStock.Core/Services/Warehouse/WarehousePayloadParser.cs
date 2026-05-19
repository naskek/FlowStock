using System.Text.Json;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services.Warehouse;

public static class WarehousePayloadParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static WarehouseMoveHuPayload ParseMoveHu(WarehouseActionLine line)
    {
        var fromJson = TryDeserialize<WarehouseMoveHuPayload>(line.PayloadJson);
        return new WarehouseMoveHuPayload
        {
            HuCode = FirstNonEmpty(fromJson?.HuCode, line.HuCode),
            ItemId = fromJson?.ItemId ?? line.ItemId,
            Qty = fromJson?.Qty ?? line.Qty,
            FromLocationId = fromJson?.FromLocationId ?? line.FromLocationId,
            ToLocationId = fromJson?.ToLocationId ?? line.ToLocationId
        };
    }

    public static WarehouseAdoptPalletPlanPayload ParseAdopt(WarehouseActionLine line)
    {
        var fromJson = TryDeserialize<WarehouseAdoptPalletPlanPayload>(line.PayloadJson);
        return new WarehouseAdoptPalletPlanPayload
        {
            SourceInternalOrderId = fromJson?.SourceInternalOrderId ?? line.SourceOrderId,
            TargetCustomerOrderId = fromJson?.TargetCustomerOrderId ?? line.TargetOrderId
        };
    }

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static T? TryDeserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
