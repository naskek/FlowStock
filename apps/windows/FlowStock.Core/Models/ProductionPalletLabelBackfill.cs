namespace FlowStock.Core.Models;

public static class ProductionPalletLabelBackfillAction
{
    public const string WouldBackfill = "WOULD_BACKFILL";
    public const string Backfilled = "BACKFILLED";
    public const string AlreadyVerified = "NO_ACTION_ALREADY_VERIFIED";
    public const string Blocked = "BLOCKED";
}

public sealed record ProductionPalletLabelBackfillRow(
    long OrderId,
    long PalletId,
    string HuCode,
    string Status,
    bool HasPrintedAt,
    string? CurrentLabelFingerprint,
    string Action,
    string? BlockerReason);

public sealed record ProductionPalletLabelBackfillReport(
    string Mode,
    int CandidateCount,
    int BackfilledCount,
    int BlockerCount,
    IReadOnlyList<ProductionPalletLabelBackfillRow> Rows);
