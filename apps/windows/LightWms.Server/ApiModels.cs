using System.Text.Json.Serialization;

namespace LightWms.Server;

public sealed class CreateDocRequest
{
    [JsonPropertyName("doc_uid")]
    public string? DocUid { get; set; }

    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("doc_ref")]
    public string? DocRef { get; set; }

    [JsonPropertyName("partner_id")]
    public long? PartnerId { get; set; }

    [JsonPropertyName("from_location_id")]
    public long? FromLocationId { get; set; }

    [JsonPropertyName("to_location_id")]
    public long? ToLocationId { get; set; }

    [JsonPropertyName("from_hu")]
    public string? FromHu { get; set; }

    [JsonPropertyName("to_hu")]
    public string? ToHu { get; set; }
}

public sealed class CreateDocResponse
{
    public string DocUid { get; init; } = string.Empty;
    public string DocRef { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
}

public sealed class AddDocLineRequest
{
    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("barcode")]
    public string? Barcode { get; set; }

    [JsonPropertyName("item_id")]
    public long? ItemId { get; set; }

    [JsonPropertyName("qty")]
    public double Qty { get; set; }

    [JsonPropertyName("uom_code")]
    public string? UomCode { get; set; }
}

public sealed class CloseDocRequest
{
    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }
}

public sealed class HuGenerateRequest
{
    public int Count { get; set; }

    [JsonPropertyName("created_by")]
    public string? CreatedBy { get; set; }
}

public sealed class ApiResult
{
    public ApiResult(bool ok, string? error = null)
    {
        Ok = ok;
        Error = error;
    }

    public bool Ok { get; init; }
    public string? Error { get; init; }
}

public sealed class OperationEventRequest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }

    [JsonPropertyName("ts")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("op")]
    public string? Op { get; set; }

    [JsonPropertyName("doc_ref")]
    public string? DocRef { get; set; }

    [JsonPropertyName("barcode")]
    public string? Barcode { get; set; }

    [JsonPropertyName("qty")]
    public double Qty { get; set; }

    [JsonPropertyName("from_loc")]
    public string? FromLoc { get; set; }

    [JsonPropertyName("to_loc")]
    public string? ToLoc { get; set; }

    [JsonPropertyName("from_hu")]
    public string? FromHu { get; set; }

    [JsonPropertyName("to_hu")]
    public string? ToHu { get; set; }

    [JsonPropertyName("hu_code")]
    public string? HuCode { get; set; }

    [JsonPropertyName("from_location_id")]
    public int? FromLocationId { get; set; }

    [JsonPropertyName("to_location_id")]
    public int? ToLocationId { get; set; }

    [JsonPropertyName("partner_code")]
    public string? PartnerCode { get; set; }

    [JsonPropertyName("order_ref")]
    public string? OrderRef { get; set; }

    [JsonPropertyName("reason_code")]
    public string? ReasonCode { get; set; }
}
