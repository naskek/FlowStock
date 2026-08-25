using FlowStock.Core.Abstractions;

namespace FlowStock.Core.Services.Marking;

/// <summary>
/// Server-owned candidate/write policy. Binding reserves no legacy quantity;
/// authoritative consumption happens only at OUTBOUND close.
/// </summary>
public sealed class MarkingHuFulfillmentEligibilityPolicy
{
    private readonly IMarkingHuFulfillmentEligibilityStore _store;

    public MarkingHuFulfillmentEligibilityPolicy(IMarkingHuFulfillmentEligibilityStore store)
    {
        _store = store;
    }

    public IReadOnlyList<T> FilterCandidates<T>(
        long orderLineId,
        IReadOnlyList<T> candidates,
        Func<T, string> huCode,
        Func<T, double> quantity)
    {
        var quantities = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(huCode(candidate)))
            .GroupBy(candidate => huCode(candidate).Trim().ToUpperInvariant(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => quantity(group.First()), StringComparer.Ordinal);
        var eligible = _store.GetEligibleHuCodesForMarkingBinding(orderLineId, quantities);
        return candidates.Where(candidate => eligible.Contains(huCode(candidate).Trim().ToUpperInvariant())).ToArray();
    }

    public void ValidateFinal(long orderLineId, IReadOnlyDictionary<string, double> huQuantities) =>
        _store.ValidateFinalMarkingHuBinding(orderLineId, huQuantities);
}
