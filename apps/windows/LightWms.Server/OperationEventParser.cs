using System.Globalization;
using System.Text.Json;

namespace LightWms.Server;

internal static class OperationEventParser
{
    public sealed record OperationEventData(
        string? EventId,
        string? DeviceId,
        string? Op,
        string? DocRef,
        string? Barcode,
        double Qty,
        string? FromLoc,
        string? ToLoc,
        string? FromHu,
        string? ToHu,
        string? HuCode,
        int? FromLocationId,
        int? ToLocationId,
        string? PartnerCode,
        string? OrderRef,
        string? ReasonCode,
        int? SchemaVersion,
        string? Timestamp);

    public static bool TryParse(string json, out OperationEventData? result, out string? error)
    {
        result = null;
        error = null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            error = "INVALID_JSON";
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var qty = GetDouble(root, "qty");
            var schemaVersion = GetInt(root, "schema_version", "schemaVersion");
            result = new OperationEventData(
                EventId: GetString(root, "event_id", "eventId"),
                DeviceId: GetString(root, "device_id", "deviceId"),
                Op: GetString(root, "op"),
                DocRef: GetString(root, "doc_ref", "docRef"),
                Barcode: GetString(root, "barcode"),
                Qty: qty,
                FromLoc: GetString(root, "from_loc", "fromLoc"),
                ToLoc: GetString(root, "to_loc", "toLoc"),
                FromHu: GetString(root, "from_hu", "fromHu"),
                ToHu: GetString(root, "to_hu", "toHu"),
                HuCode: GetString(root, "hu_code", "huCode"),
                FromLocationId: GetInt(root, "from_location_id", "fromLocationId"),
                ToLocationId: GetInt(root, "to_location_id", "toLocationId"),
                PartnerCode: GetString(root, "partner_code", "partnerCode"),
                OrderRef: GetString(root, "order_ref", "orderRef"),
                ReasonCode: GetString(root, "reason_code", "reasonCode"),
                SchemaVersion: schemaVersion,
                Timestamp: GetString(root, "ts"));
            return true;
        }
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (element.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    return null;
                }

                return prop.GetString()?.Trim();
            }
        }

        return null;
    }

    private static double GetDouble(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (element.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var value))
                {
                    return value;
                }

                if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value))
                {
                    return value;
                }
            }
        }

        return 0;
    }

    private static int? GetInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (element.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var value))
                {
                    return value;
                }

                if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                {
                    return value;
                }
            }
        }

        return null;
    }
}
