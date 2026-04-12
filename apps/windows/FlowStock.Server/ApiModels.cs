using System.Text.Json.Serialization;

namespace FlowStock.Server;

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

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("reason_code")]
    public string? ReasonCode { get; set; }

    [JsonPropertyName("partner_id")]
    public long? PartnerId { get; set; }

    [JsonPropertyName("order_id")]
    public long? OrderId { get; set; }

    [JsonPropertyName("order_ref")]
    public string? OrderRef { get; set; }

    [JsonPropertyName("from_location_id")]
    public long? FromLocationId { get; set; }

    [JsonPropertyName("to_location_id")]
    public long? ToLocationId { get; set; }

    [JsonPropertyName("from_hu")]
    public string? FromHu { get; set; }

    [JsonPropertyName("to_hu")]
    public string? ToHu { get; set; }

    [JsonPropertyName("draft_only")]
    public bool DraftOnly { get; set; }
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

    [JsonPropertyName("order_line_id")]
    public long? OrderLineId { get; set; }

    [JsonPropertyName("qty")]
    public double Qty { get; set; }

    [JsonPropertyName("uom_code")]
    public string? UomCode { get; set; }

    [JsonPropertyName("from_location_id")]
    public long? FromLocationId { get; set; }

    [JsonPropertyName("to_location_id")]
    public long? ToLocationId { get; set; }

    [JsonPropertyName("from_hu")]
    public string? FromHu { get; set; }

    [JsonPropertyName("to_hu")]
    public string? ToHu { get; set; }
}

public sealed class UpdateDocLineRequest
{
    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("line_id")]
    public long? LineId { get; set; }

    [JsonPropertyName("qty")]
    public double Qty { get; set; }

    [JsonPropertyName("uom_code")]
    public string? UomCode { get; set; }

    [JsonPropertyName("from_location_id")]
    public long? FromLocationId { get; set; }

    [JsonPropertyName("to_location_id")]
    public long? ToLocationId { get; set; }

    [JsonPropertyName("from_hu")]
    public string? FromHu { get; set; }

    [JsonPropertyName("to_hu")]
    public string? ToHu { get; set; }
}

public sealed class DeleteDocLineRequest
{
    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("line_id")]
    public long? LineId { get; set; }
}

public sealed class CloseDocRequest
{
    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }
}

public sealed class CloseDocResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("closed")]
    public bool Closed { get; init; }

    [JsonPropertyName("doc_uid")]
    public string? DocUid { get; init; }

    [JsonPropertyName("doc_ref")]
    public string? DocRef { get; init; }

    [JsonPropertyName("doc_status")]
    public string? DocStatus { get; init; }

    [JsonPropertyName("result")]
    public string Result { get; init; } = string.Empty;

    [JsonPropertyName("errors")]
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    [JsonPropertyName("idempotent_replay")]
    public bool IdempotentReplay { get; init; }

    [JsonPropertyName("already_closed")]
    public bool AlreadyClosed { get; init; }
}

public sealed class HuGenerateRequest
{
    public int Count { get; set; }

    [JsonPropertyName("created_by")]
    public string? CreatedBy { get; set; }
}

public sealed class CreateHuRequest
{
    [JsonPropertyName("hu_code")]
    public string? HuCode { get; set; }

    [JsonPropertyName("created_by")]
    public string? CreatedBy { get; set; }
}

public sealed class CloseHuRequest
{
    [JsonPropertyName("closed_by")]
    public string? ClosedBy { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

public sealed class TsdLoginRequest
{
    [JsonPropertyName("login")]
    public string? Login { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }
}

public sealed class ItemRequestCreateRequest
{
    [JsonPropertyName("barcode")]
    public string? Barcode { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("login")]
    public string? Login { get; set; }
}

public sealed class CreateOrderLineRequest
{
    [JsonPropertyName("item_id")]
    public long? ItemId { get; set; }

    [JsonPropertyName("qty_ordered")]
    public double QtyOrdered { get; set; }
}

public sealed class CreateOrderRequest
{
    [JsonPropertyName("order_ref")]
    public string? OrderRef { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("partner_id")]
    public long? PartnerId { get; set; }

    [JsonPropertyName("due_date")]
    public string? DueDate { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("lines")]
    public List<CreateOrderLineRequest>? Lines { get; set; }
}

public sealed class CreateOrderEnvelope
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("result")]
    public string Result { get; init; } = string.Empty;

    [JsonPropertyName("order_id")]
    public long OrderId { get; init; }

    [JsonPropertyName("order_ref")]
    public string OrderRef { get; init; } = string.Empty;

    [JsonPropertyName("order_ref_changed")]
    public bool OrderRefChanged { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("line_count")]
    public int LineCount { get; init; }
}

public sealed class UpdateOrderLineRequest
{
    [JsonPropertyName("item_id")]
    public long? ItemId { get; set; }

    [JsonPropertyName("qty_ordered")]
    public double QtyOrdered { get; set; }
}

public sealed class UpdateOrderRequest
{
    [JsonPropertyName("order_ref")]
    public string? OrderRef { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("partner_id")]
    public long? PartnerId { get; set; }

    [JsonPropertyName("due_date")]
    public string? DueDate { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("lines")]
    public List<UpdateOrderLineRequest>? Lines { get; set; }
}

public sealed class UpdateOrderEnvelope
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("result")]
    public string Result { get; init; } = string.Empty;

    [JsonPropertyName("order_id")]
    public long OrderId { get; init; }

    [JsonPropertyName("order_ref")]
    public string OrderRef { get; init; } = string.Empty;

    [JsonPropertyName("order_ref_changed")]
    public bool OrderRefChanged { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("line_count")]
    public int LineCount { get; init; }
}

public sealed class DeleteOrderEnvelope
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("result")]
    public string Result { get; init; } = string.Empty;

    [JsonPropertyName("order_id")]
    public long OrderId { get; init; }

    [JsonPropertyName("order_ref")]
    public string OrderRef { get; init; } = string.Empty;
}

public sealed class SetOrderStatusRequest
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

public sealed class SetOrderStatusEnvelope
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("result")]
    public string Result { get; init; } = string.Empty;

    [JsonPropertyName("order_id")]
    public long OrderId { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;
}

public sealed class OrderRequestLineCreateRequest
{
    [JsonPropertyName("item_id")]
    public long? ItemId { get; set; }

    [JsonPropertyName("qty_ordered")]
    public double QtyOrdered { get; set; }
}

public sealed class OrderCreateRequestCreateRequest
{
    [JsonPropertyName("order_ref")]
    public string? OrderRef { get; set; }

    [JsonPropertyName("partner_id")]
    public long? PartnerId { get; set; }

    [JsonPropertyName("due_date")]
    public string? DueDate { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("lines")]
    public List<OrderRequestLineCreateRequest>? Lines { get; set; }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("login")]
    public string? Login { get; set; }
}

public sealed class OrderStatusChangeRequestCreateRequest
{
    [JsonPropertyName("order_id")]
    public long? OrderId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("login")]
    public string? Login { get; set; }
}

public sealed class ResolveOrderRequestRequest
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("resolved_by")]
    public string? ResolvedBy { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("applied_order_id")]
    public long? AppliedOrderId { get; set; }
}

public sealed class ClientBlockSettingRequest
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("is_enabled")]
    public bool IsEnabled { get; set; }
}

public sealed class SaveClientBlocksRequest
{
    [JsonPropertyName("blocks")]
    public List<ClientBlockSettingRequest>? Blocks { get; set; }
}

public sealed class UpsertTsdDeviceRequest
{
    [JsonPropertyName("login")]
    public string? Login { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; }

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }
}

public sealed class UpsertItemRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("barcode")]
    public string? Barcode { get; set; }

    [JsonPropertyName("gtin")]
    public string? Gtin { get; set; }

    [JsonPropertyName("base_uom")]
    public string? BaseUom { get; set; }

    [JsonPropertyName("brand")]
    public string? Brand { get; set; }

    [JsonPropertyName("volume")]
    public string? Volume { get; set; }

    [JsonPropertyName("shelf_life_months")]
    public int? ShelfLifeMonths { get; set; }

    [JsonPropertyName("tara_id")]
    public long? TaraId { get; set; }

    [JsonPropertyName("is_marked")]
    public bool IsMarked { get; set; }

    [JsonPropertyName("max_qty_per_hu")]
    public double? MaxQtyPerHu { get; set; }
}

public sealed class UpsertLocationRequest
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class CreateNamedEntityRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class UpsertPartnerRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
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

