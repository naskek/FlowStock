namespace FlowStock.Core.Services;

/// <summary>
/// Feature flags for TSD-confirmed ledger flow (branch new-ledger-logic).
/// </summary>
public sealed class FlowStockLedgerFlowOptions
{
    public const string SectionName = "FlowStock";

    /// <summary>
    /// After TSD fill-pallet, isolate pallet to dedicated PRD and close immediately (ledger +).
    /// </summary>
    public bool ProductionAutoCloseOnFill { get; set; } = true;

    /// <summary>
    /// After TSD outbound picking complete (or last scan), close OUT and write ledger −.
    /// </summary>
    public bool OutboundAutoCloseOnComplete { get; set; } = true;
}
