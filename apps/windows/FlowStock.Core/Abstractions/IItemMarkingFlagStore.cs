namespace FlowStock.Core.Abstractions;

public interface IItemMarkingFlagStore
{
    void MarkItem(long itemId);
}
