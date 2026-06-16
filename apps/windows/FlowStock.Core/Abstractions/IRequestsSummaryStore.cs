namespace FlowStock.Core.Abstractions;

public interface IRequestsSummaryStore
{
    int CountPendingItemRequests();

    int CountPendingOrderRequests();
}
