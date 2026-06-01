using System.Text.Json.Serialization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class ReadyHuBindingEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/orders/hu-bindings/ready", (IDataStore store) =>
        {
            var readModel = new ReadyHuBindingReadModelService(store).Build();
            return Results.Ok(MapResponse(readModel));
        });
    }

    public static int CountPendingNotifications(IDataStore store) =>
        new ReadyHuBindingReadModelService(store).Build().HuCount > 0 ? 1 : 0;

    public static ReadyHuBindingResponse MapResponse(ReadyHuBindingReadModel readModel)
    {
        return new ReadyHuBindingResponse
        {
            Ok = true,
            RequestType = readModel.RequestTypeCode,
            HuCount = readModel.HuCount,
            OrderCount = readModel.OrderCount,
            LineCount = readModel.LineCount,
            HuRows = readModel.HuRows.Select(MapHuRow).ToArray()
        };
    }

    private static ReadyHuBindingHuResponse MapHuRow(ReadyHuBindingHuRow row)
    {
        return new ReadyHuBindingHuResponse
        {
            HuCode = row.HuCode,
            ItemId = row.ItemId,
            ItemName = row.ItemName,
            Qty = row.Qty,
            Source = row.Source,
            LocationDisplay = row.LocationDisplay,
            OriginInternalOrderId = row.OriginInternalOrderId,
            OriginInternalOrderRef = row.OriginInternalOrderRef,
            FirstReceiptAt = row.FirstReceiptAt,
            FirstReceiptDocId = row.FirstReceiptDocId,
            CompatibleOrders = row.CompatibleOrders.Select(MapOrderRow).ToArray()
        };
    }

    private static ReadyHuBindingCompatibleOrderResponse MapOrderRow(ReadyHuBindingCompatibleOrderRow row)
    {
        return new ReadyHuBindingCompatibleOrderResponse
        {
            OrderId = row.OrderId,
            OrderRef = row.OrderRef,
            PartnerId = row.PartnerId,
            PartnerName = row.PartnerName,
            PartnerCode = row.PartnerCode,
            DueDate = row.DueDate,
            CreatedAt = row.CreatedAt,
            Status = row.Status,
            Lines = row.Lines.Select(MapLineRow).ToArray()
        };
    }

    private static ReadyHuBindingCompatibleLineResponse MapLineRow(ReadyHuBindingCompatibleLineRow row)
    {
        return new ReadyHuBindingCompatibleLineResponse
        {
            OrderLineId = row.OrderLineId,
            ItemId = row.ItemId,
            ItemName = row.ItemName,
            QtyOrdered = row.QtyOrdered,
            QtyShipped = row.QtyShipped,
            ShipmentRemainingQty = row.ShipmentRemainingQty,
            CurrentBoundHuCodes = row.CurrentBoundHuCodes,
            CurrentBoundQty = row.CurrentBoundQty,
            MaxAdditionalBindQty = row.MaxAdditionalBindQty
        };
    }

    public sealed class ReadyHuBindingResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; init; }

        [JsonPropertyName("request_type")]
        public string RequestType { get; init; } = ReadyHuBindingReadModel.RequestType;

        [JsonPropertyName("hu_count")]
        public int HuCount { get; init; }

        [JsonPropertyName("order_count")]
        public int OrderCount { get; init; }

        [JsonPropertyName("line_count")]
        public int LineCount { get; init; }

        [JsonPropertyName("hu_rows")]
        public IReadOnlyList<ReadyHuBindingHuResponse> HuRows { get; init; } =
            Array.Empty<ReadyHuBindingHuResponse>();
    }

    public sealed class ReadyHuBindingHuResponse
    {
        [JsonPropertyName("hu_code")]
        public string HuCode { get; init; } = string.Empty;

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("item_name")]
        public string ItemName { get; init; } = string.Empty;

        [JsonPropertyName("qty")]
        public double Qty { get; init; }

        [JsonPropertyName("source")]
        public string Source { get; init; } = string.Empty;

        [JsonPropertyName("location_display")]
        public string LocationDisplay { get; init; } = string.Empty;

        [JsonPropertyName("origin_internal_order_id")]
        public long? OriginInternalOrderId { get; init; }

        [JsonPropertyName("origin_internal_order_ref")]
        public string? OriginInternalOrderRef { get; init; }

        [JsonPropertyName("first_receipt_at")]
        public DateTime? FirstReceiptAt { get; init; }

        [JsonPropertyName("first_receipt_doc_id")]
        public long? FirstReceiptDocId { get; init; }

        [JsonPropertyName("compatible_orders")]
        public IReadOnlyList<ReadyHuBindingCompatibleOrderResponse> CompatibleOrders { get; init; } =
            Array.Empty<ReadyHuBindingCompatibleOrderResponse>();
    }

    public sealed class ReadyHuBindingCompatibleOrderResponse
    {
        [JsonPropertyName("order_id")]
        public long OrderId { get; init; }

        [JsonPropertyName("order_ref")]
        public string OrderRef { get; init; } = string.Empty;

        [JsonPropertyName("partner_id")]
        public long? PartnerId { get; init; }

        [JsonPropertyName("partner_name")]
        public string? PartnerName { get; init; }

        [JsonPropertyName("partner_code")]
        public string? PartnerCode { get; init; }

        [JsonPropertyName("due_date")]
        public DateTime? DueDate { get; init; }

        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; init; }

        [JsonPropertyName("status")]
        public string Status { get; init; } = string.Empty;

        [JsonPropertyName("lines")]
        public IReadOnlyList<ReadyHuBindingCompatibleLineResponse> Lines { get; init; } =
            Array.Empty<ReadyHuBindingCompatibleLineResponse>();
    }

    public sealed class ReadyHuBindingCompatibleLineResponse
    {
        [JsonPropertyName("order_line_id")]
        public long OrderLineId { get; init; }

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("item_name")]
        public string ItemName { get; init; } = string.Empty;

        [JsonPropertyName("qty_ordered")]
        public double QtyOrdered { get; init; }

        [JsonPropertyName("qty_shipped")]
        public double QtyShipped { get; init; }

        [JsonPropertyName("shipment_remaining_qty")]
        public double ShipmentRemainingQty { get; init; }

        [JsonPropertyName("current_bound_hu_codes")]
        public IReadOnlyList<string> CurrentBoundHuCodes { get; init; } = Array.Empty<string>();

        [JsonPropertyName("current_bound_qty")]
        public double CurrentBoundQty { get; init; }

        [JsonPropertyName("max_additional_bind_qty")]
        public double MaxAdditionalBindQty { get; init; }
    }
}
