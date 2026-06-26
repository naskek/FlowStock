namespace FlowStock.Core.Models;

public sealed class Order
{
    public long Id { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public OrderType Type { get; init; } = OrderType.Customer;
    public long? PartnerId { get; init; }
    public DateTime? DueDate { get; init; }
    public OrderStatus Status { get; init; }
    public string? Comment { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ShippedAt { get; init; }
    public string? PartnerName { get; init; }
    public string? PartnerCode { get; init; }
    public bool UseReservedStock { get; init; }
    public string MarkingResponsibility { get; init; } = FlowStock.Core.Models.Marking.MarkingResponsibility.FlowStock;
    public MarkingStatus MarkingStatus { get; init; } = MarkingStatus.NotRequired;
    public bool IsLegacyExcelGeneratedMarkingStatus { get; init; }
    public bool MarkingRequired { get; init; }
    public bool MarkingApplies { get; init; }
    public bool MarkingCodeCovered { get; init; }
    public DateTime? MarkingExcelGeneratedAt { get; init; }
    public DateTime? MarkingPrintedAt { get; init; }
    public bool ListMetricsLoaded { get; init; }
    public bool HasShipmentRemaining { get; init; }
    public bool HasProductionPalletPlan { get; init; }
    public bool NeedsProductionPalletPlan { get; init; }
    public int PlannedPalletCount { get; init; }
    public int FilledPalletCount { get; init; }
    public double PlannedQty { get; init; }
    public double FilledQty { get; init; }
    public string PalletPlanStatus { get; init; } = string.Empty;
    public double ShipmentOrderedQty { get; init; }
    public double ShipmentShippedQty { get; init; }
    public double ShipmentRemainingQty { get; init; }
    public bool IsPartiallyShipped { get; init; }
    public string? ActiveOrderControlRef { get; init; }

    public string OrderControlDisplay => string.IsNullOrWhiteSpace(ActiveOrderControlRef)
        ? "нет"
        : ActiveOrderControlRef;

    public string TypeDisplay => OrderStatusMapper.TypeToDisplayName(Type);
    public string StatusDisplay => IsPartiallyShipped
        ? "Частично отгружено"
        : OrderStatusMapper.StatusToDisplayName(Status, Type);
    public MarkingStatus EffectiveMarkingStatus
    {
        get
        {
            if (Status == OrderStatus.Cancelled)
            {
                return MarkingCodeCovered || (!MarkingApplies && MarkingStatus == MarkingStatus.Printed)
                    ? MarkingStatus.Printed
                    : MarkingStatus.NotRequired;
            }

            if (MarkingCodeCovered)
            {
                return MarkingStatus.Printed;
            }

            if (MarkingApplies)
            {
                return MarkingStatus.Required;
            }

            return MarkingStatusResolver.Resolve(MarkingStatus, MarkingRequired, Status);
        }
    }

    public bool MarkingCompleted => MarkingCodeCovered
                                    || (!MarkingApplies && EffectiveMarkingStatus == MarkingStatus.Printed);
    public string MarkingLabel => MarkingCompleted
        ? "Маркировка проведена"
        : Status != OrderStatus.Cancelled && (MarkingRequired || MarkingApplies)
            ? "Маркировка не проведена"
            : string.Empty;
    public string MarkingStatusDisplay => MarkingStatusMapper.ToDisplayName(EffectiveMarkingStatus);
    public string MarkingStatusShortDisplay => MarkingStatusMapper.ToShortDisplayName(EffectiveMarkingStatus);
    public string ProductionPalletPlanShortDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(PalletPlanStatus))
            {
                return PalletPlanStatus;
            }

            if (HasProductionPalletPlan)
            {
                return "План сформирован";
            }

            return NeedsProductionPalletPlan
                ? "План не сформирован"
                : string.Empty;
        }
    }

    public bool ProductionPalletFillCompleted =>
        Type == OrderType.Customer && Status == OrderStatus.Shipped
            ? false
            : PlannedPalletCount > 0
              && FilledPalletCount >= PlannedPalletCount
              && ProductionPalletPlanShortDisplay.StartsWith("Наполнено", StringComparison.CurrentCultureIgnoreCase);

    public bool ProductionPalletFillInProgress =>
        Type == OrderType.Customer
        && Status == OrderStatus.Shipped
            ? false
            : FilledPalletCount > 0
              && PlannedPalletCount > 0
              && !ProductionPalletFillCompleted;

    public string PartnerDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(PartnerCode) && !string.IsNullOrWhiteSpace(PartnerName))
            {
                return $"{PartnerCode} - {PartnerName}";
            }

            if (!string.IsNullOrWhiteSpace(PartnerCode))
            {
                return PartnerCode;
            }

            return PartnerName ?? string.Empty;
        }
    }
}
