using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class ProductionHuAwaitingShipmentEligibility
{
    public static bool IsEligible(ProductionHuAwaitingShipmentEligibilityFacts? facts)
    {
        if (facts == null
            || !Is(facts.PersistedPalletStatus, ProductionPalletStatus.Filled)
            || !facts.OwnerOrderId.HasValue
            || facts.OwnerOrderId.Value <= 0
            || !facts.EvaluatedOrderId.HasValue
            || facts.EvaluatedOrderId.Value != facts.OwnerOrderId.Value
            || !Is(facts.OwnerOrderType, "CUSTOMER")
            || !IsAwaitingShipmentOrderStatus(facts.OwnerOrderStatus)
            || facts.Components.Count == 0)
        {
            return false;
        }

        var normalizedComponents = facts.Components
            .Select(component => new
            {
                Component = component,
                HuCode = NormalizeHu(component.HuCode)
            })
            .ToArray();
        if (normalizedComponents.Any(row =>
                !row.Component.OrderLineId.HasValue
                || row.Component.OrderLineId.Value <= 0
                || row.Component.OrderLineOrderId != facts.OwnerOrderId
                || row.Component.ItemId <= 0
                || row.HuCode == null
                || row.Component.FilledQty + StockQuantityRules.QtyTolerance < row.Component.PlannedQty))
        {
            return false;
        }

        var componentKeys = normalizedComponents
            .Select(row => new ComponentKey(row.Component.ItemId, row.HuCode!))
            .Distinct()
            .ToArray();
        var keyFacts = facts.ComponentKeys
            .Select(key => new
            {
                Fact = key,
                HuCode = NormalizeHu(key.HuCode)
            })
            .Where(row => row.Fact.ItemId > 0 && row.HuCode != null)
            .GroupBy(row => new ComponentKey(row.Fact.ItemId, row.HuCode!))
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    AllBalancesPositive = group.All(row =>
                        row.Fact.LedgerBalance > StockQuantityRules.QtyTolerance),
                    HasReservation = group.Any(row => row.Fact.HasActiveReservation),
                    HasShipment = group.Any(row => row.Fact.HasActiveShipment)
                });

        return componentKeys.All(key =>
            keyFacts.TryGetValue(key, out var keyFact)
            && keyFact.AllBalancesPositive
            && !keyFact.HasReservation
            && !keyFact.HasShipment);
    }

    public static bool IsAwaitingShipmentOrderStatus(string? status) =>
        Is(status, "IN_PROGRESS") || Is(status, "ACCEPTED");

    private static string? NormalizeHu(string? huCode) =>
        string.IsNullOrWhiteSpace(huCode) ? null : huCode.Trim().ToUpperInvariant();

    private static bool Is(string? value, string expected) =>
        string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private sealed record ComponentKey(long ItemId, string HuCode);
}
