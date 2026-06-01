namespace FlowStock.App;

public sealed class WpfReadyHuBindingReadModel
{
    public string RequestType { get; init; } = string.Empty;
    public int HuCount { get; init; }
    public int OrderCount { get; init; }
    public int LineCount { get; init; }
    public IReadOnlyList<WpfReadyHuBindingHuRow> HuRows { get; init; } =
        Array.Empty<WpfReadyHuBindingHuRow>();
}

public sealed class WpfReadyHuBindingHuRow
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
    public IReadOnlyList<WpfReadyHuBindingCompatibleOrderRow> CompatibleOrders { get; init; } =
        Array.Empty<WpfReadyHuBindingCompatibleOrderRow>();
}

public sealed class WpfReadyHuBindingCompatibleOrderRow
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public long? PartnerId { get; init; }
    public string? PartnerName { get; init; }
    public string? PartnerCode { get; init; }
    public DateTime? DueDate { get; init; }
    public DateTime CreatedAt { get; init; }
    public string Status { get; init; } = string.Empty;
    public IReadOnlyList<WpfReadyHuBindingCompatibleLineRow> Lines { get; init; } =
        Array.Empty<WpfReadyHuBindingCompatibleLineRow>();
}

public sealed class WpfReadyHuBindingCompatibleLineRow
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
