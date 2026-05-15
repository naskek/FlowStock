namespace FlowStock.Core.Models;

public sealed class OrderListMetrics
{
    public long OrderId { get; init; }
    public bool HasShipmentRemaining { get; init; }
    public bool HasReceiptRemaining { get; init; }
    public bool HasProductionPalletPlan { get; init; }
    public ProductionPalletSummary PalletSummary { get; init; } = new();
}
