namespace FlowStock.Core.Abstractions;

public interface IMarkingHuFulfillmentEligibilityStore
{
    IReadOnlySet<string> GetEligibleHuCodesForMarkingBinding(
        long orderLineId,
        IReadOnlyDictionary<string, double> huQuantities);

    void ValidateFinalMarkingHuBinding(
        long orderLineId,
        IReadOnlyDictionary<string, double> huQuantities);
}
