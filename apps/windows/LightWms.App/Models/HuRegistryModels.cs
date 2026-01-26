using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LightWms.App;

public static class HuRegistryStates
{
    public const string InStock = "IN_STOCK";
    public const string Issued = "ISSUED";
    public const string Consumed = "CONSUMED";
    public const string Unknown = "UNKNOWN";
}

public static class HuRegistryOps
{
    public const string Inbound = "INBOUND";
    public const string Move = "MOVE";
    public const string Outbound = "OUTBOUND";
    public const string Adjust = "ADJUST";
    public const string Inventory = "INVENTORY";
    public const string Unknown = "UNKNOWN";
}

public sealed class HuRegistrySnapshot
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "v1";

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = string.Empty;

    [JsonPropertyName("items")]
    public List<HuRegistryItem> Items { get; set; } = new();
}

public sealed class HuRegistryItem
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = HuRegistryStates.Unknown;

    [JsonPropertyName("item_id")]
    public long? ItemId { get; set; }

    [JsonPropertyName("item_name")]
    public string? ItemName { get; set; }

    [JsonPropertyName("location_id")]
    public long? LocationId { get; set; }

    [JsonPropertyName("location_code")]
    public string? LocationCode { get; set; }

    [JsonPropertyName("qty_base")]
    public double QtyBase { get; set; }

    [JsonPropertyName("base_uom")]
    public string? BaseUom { get; set; }

    [JsonPropertyName("last_doc_id")]
    public long? LastDocId { get; set; }

    [JsonPropertyName("last_doc_ref")]
    public string? LastDocRef { get; set; }

    [JsonPropertyName("last_op")]
    public string? LastOp { get; set; }

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = string.Empty;
}
