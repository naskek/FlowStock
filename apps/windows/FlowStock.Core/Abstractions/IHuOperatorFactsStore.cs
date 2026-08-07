using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

public interface IHuOperatorFactsStore
{
    IReadOnlyList<HuOperatorFacts> GetForOrder(long orderId);

    HuOperatorFacts? GetForHu(string huCode);

    IReadOnlyList<HuOperatorFacts> GetForHus(IReadOnlyCollection<string> huCodes)
    {
        if (huCodes == null || huCodes.Count == 0)
        {
            return Array.Empty<HuOperatorFacts>();
        }

        return huCodes
            .Select(code => string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(code => GetForHu(code!))
            .Where(facts => facts != null)
            .Cast<HuOperatorFacts>()
            .ToArray();
    }
}
