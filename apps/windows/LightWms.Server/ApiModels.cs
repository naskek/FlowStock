using System.Text.Json.Serialization;

namespace LightWms.Server;

public sealed class CreateDocRequest
{
    public string? Op { get; set; }
}

public sealed class CreateDocResponse
{
    public string DocUid { get; init; } = string.Empty;
    public string DocRef { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
}

public sealed class AddMoveLineRequest
{
    public string? Barcode { get; set; }
    public double Qty { get; set; }
    public string? FromLocCode { get; set; }
    public string? ToLocCode { get; set; }
    public string? FromHu { get; set; }
    public string? ToHu { get; set; }
    public string? EventId { get; set; }
}

public sealed class CloseDocRequest
{
    public string? EventId { get; set; }
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
