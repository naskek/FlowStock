using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

public interface IHuOperatorFactsStore
{
    IReadOnlyList<HuOperatorFacts> GetForOrder(long orderId);

    HuOperatorFacts? GetForHu(string huCode);
}
