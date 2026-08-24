namespace FlowStock.Core.Models;

public static class MarkingStatusResolver
{
    public static MarkingStatus Resolve(MarkingStatus storedStatus, bool markingRequired, OrderStatus orderStatus)
    {
        if (orderStatus == OrderStatus.Cancelled)
        {
            return MarkingStatus.NotRequired;
        }

        return markingRequired
            ? MarkingStatus.NotApplied
            : MarkingStatus.NotRequired;
    }
}
