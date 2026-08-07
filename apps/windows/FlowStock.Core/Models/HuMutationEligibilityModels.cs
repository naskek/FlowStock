namespace FlowStock.Core.Models;

public enum HuMutationOperation
{
    Offer,
    ReserveOrBind,
    OutboundDraft,
    OutboundClose,
    ReleaseProducedStock
}

public static class HuMutationEligibilityReasonCode
{
    public const string HuNotInPhysicalStock = "HU_NOT_IN_PHYSICAL_STOCK";
    public const string HuInconsistent = "HU_INCONSISTENT";
    public const string HuMixedNotSupported = "HU_MIXED_NOT_SUPPORTED";
    public const string HuPartialQuantityNotAllowed = "HU_PARTIAL_QUANTITY_NOT_ALLOWED";
    public const string HuCompositionMismatch = "HU_COMPOSITION_MISMATCH";
    public const string HuMultipleLocations = "HU_MULTIPLE_LOCATIONS";
    public const string HuReservedByOtherOrder = "HU_RESERVED_BY_OTHER_ORDER";
    public const string HuInOtherOutbound = "HU_IN_OTHER_OUTBOUND";
    public const string HuExplicitRequiredForHuStock = "HU_EXPLICIT_REQUIRED_FOR_HU_STOCK";
    public const string HuStateChanged = "HU_STATE_CHANGED";
}

public sealed record HuMutationRequestedComponent(long ItemId, double Qty, long? LocationId = null);

public sealed class HuMutationEligibilityContext
{
    public HuMutationOperation Operation { get; init; }
    public long? TargetOrderId { get; init; }
    public long? SourceOrderId { get; init; }
    public long? CurrentOutboundDocumentId { get; init; }
    public IReadOnlySet<long> PermittedReservationOrderIds { get; init; } = new HashSet<long>();
    public IReadOnlyList<HuMutationRequestedComponent> RequestedComponents { get; init; } =
        Array.Empty<HuMutationRequestedComponent>();
}

public sealed record HuMutationEligibilityReason(string Code, string HuCode, string Details);

public sealed class HuMutationEligibilityDecision
{
    public bool Allowed => Reasons.Count == 0;
    public IReadOnlyList<HuMutationEligibilityReason> Reasons { get; init; } =
        Array.Empty<HuMutationEligibilityReason>();

    public static HuMutationEligibilityDecision Allow() => new();

    public static HuMutationEligibilityDecision Reject(params HuMutationEligibilityReason[] reasons) =>
        new() { Reasons = reasons };
}
