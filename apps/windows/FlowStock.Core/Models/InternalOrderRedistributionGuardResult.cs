namespace FlowStock.Core.Models;

public sealed class InternalOrderRedistributionGuardResult
{
    public const string BlockedCode = "AUTO_REDISTRIBUTION_BLOCKED_ACTIVE_PRODUCTION";
    public const string BlockedMessage =
        "Автоперенос запрещён: внутренний заказ имеет активный PRD/план паллет/маркировку. Требуется диагностика и ручной repair.";

    public bool IsBlocked { get; init; }
    public long SourceOrderId { get; init; }
    public string SourceOrderRef { get; init; } = string.Empty;
    public IReadOnlyList<InternalOrderRedistributionGuardPrdRow> DraftPrdDocs { get; init; } = Array.Empty<InternalOrderRedistributionGuardPrdRow>();
    public IReadOnlyList<InternalOrderRedistributionGuardPalletRow> ActivePallets { get; init; } = Array.Empty<InternalOrderRedistributionGuardPalletRow>();
    public IReadOnlyList<InternalOrderRedistributionGuardPrdRow> PrdDocsWithLedger { get; init; } = Array.Empty<InternalOrderRedistributionGuardPrdRow>();
    public IReadOnlyList<InternalOrderRedistributionGuardMarkingRow> MarkingOrders { get; init; } = Array.Empty<InternalOrderRedistributionGuardMarkingRow>();
}

public sealed class InternalOrderRedistributionGuardPrdRow
{
    public long DocId { get; init; }
    public string DocRef { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
}

public sealed class InternalOrderRedistributionGuardPalletRow
{
    public long PalletId { get; init; }
    public string HuCode { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public long PrdDocId { get; init; }
    public string? PrdDocRef { get; init; }
    public long ItemId { get; init; }
}

public sealed class InternalOrderRedistributionGuardMarkingRow
{
    public Guid MarkingOrderId { get; init; }
    public string RequestNumber { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public long? ItemId { get; init; }
    public int ReservedCodeCount { get; init; }
}
