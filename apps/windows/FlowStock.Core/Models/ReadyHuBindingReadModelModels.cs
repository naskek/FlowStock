namespace FlowStock.Core.Models;

public sealed class ReadyHuBindingReadModel
{
    public const string RequestType = "READY_HU_BINDING_AVAILABLE";

    public string RequestTypeCode { get; init; } = RequestType;
    public IReadOnlyList<ReadyHuBindingHuRow> HuRows { get; init; } = Array.Empty<ReadyHuBindingHuRow>();
    public int HuCount => HuRows.Count;
    public int OrderCount => HuRows
        .SelectMany(row => row.CompatibleOrders)
        .Select(order => order.OrderId)
        .Distinct()
        .Count();
    public int LineCount => HuRows
        .SelectMany(row => row.CompatibleOrders)
        .SelectMany(order => order.Lines)
        .Select(line => line.OrderLineId)
        .Distinct()
        .Count();
}

public sealed class ReadyHuBindingHuRow
{
    public string HuCode { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public double Qty { get; init; }
    public string Source { get; init; } = string.Empty;
    public string LocationDisplay { get; init; } = string.Empty;
    public long? OriginInternalOrderId { get; init; }
    public string? OriginInternalOrderRef { get; init; }
    public DateTime? FirstReceiptAt { get; init; }
    public long? FirstReceiptDocId { get; init; }
    public IReadOnlyList<ReadyHuBindingCompatibleOrderRow> CompatibleOrders { get; init; } =
        Array.Empty<ReadyHuBindingCompatibleOrderRow>();
}

public sealed class ReadyHuBindingCompatibleOrderRow
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public long? PartnerId { get; init; }
    public string? PartnerName { get; init; }
    public string? PartnerCode { get; init; }
    public DateTime? DueDate { get; init; }
    public DateTime CreatedAt { get; init; }
    public string Status { get; init; } = string.Empty;
    public IReadOnlyList<ReadyHuBindingCompatibleLineRow> Lines { get; init; } =
        Array.Empty<ReadyHuBindingCompatibleLineRow>();
}

public sealed class ReadyHuBindingCompatibleLineRow
{
    public long OrderLineId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public double QtyOrdered { get; init; }
    public double QtyShipped { get; init; }
    public double ShipmentRemainingQty { get; init; }
    public IReadOnlyList<string> CurrentBoundHuCodes { get; init; } = Array.Empty<string>();
    public double CurrentBoundQty { get; init; }
    public double MaxAdditionalBindQty { get; init; }
}
