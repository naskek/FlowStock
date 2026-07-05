namespace FlowStock.App;

/// <summary>
/// Four states of the order-card pallet plan button. Every enabled state opens the same
/// <see cref="ProductionPalletBuilderWindow"/>; the legacy /plan endpoint is not called
/// from this button.
/// </summary>
public static class ProductionPalletPlanButtonState
{
    public sealed record State(string Label, bool IsEnabled);

    public static State Resolve(bool hasSavedPallets, bool productionRequired)
    {
        if (hasSavedPallets)
        {
            return productionRequired
                ? new State("Дополнить план паллет", true)
                : new State("Открыть план паллет", true);
        }

        return productionRequired
            ? new State("Создать план паллет", true)
            : new State("План паллет", false);
    }
}
