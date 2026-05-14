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
    public MarkingStatus MarkingStatus { get; init; } = MarkingStatus.NotRequired;
    public bool IsLegacyExcelGeneratedMarkingStatus { get; init; }
    public bool MarkingRequired { get; init; }
    public bool MarkingApplies { get; init; }
    public bool MarkingCodeCovered { get; init; }
    public DateTime? MarkingExcelGeneratedAt { get; init; }
    public DateTime? MarkingPrintedAt { get; init; }
    public bool HasProductionPalletPlan { get; init; }
    public bool NeedsProductionPalletPlan { get; init; }

    public string TypeDisplay => OrderStatusMapper.TypeToDisplayName(Type);
    public string StatusDisplay => OrderStatusMapper.StatusToDisplayName(Status, Type);
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
    public string ProductionPalletPlanShortDisplay => HasProductionPalletPlan
        ? "План сформирован"
        : NeedsProductionPalletPlan
            ? "План не сформирован"
            : string.Empty;

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

