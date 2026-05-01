namespace FlowStock.Core.Models;

public static class MarkingStatusResolver
{
    public static MarkingStatus Resolve(MarkingStatus storedStatus, bool markingRequired, OrderStatus orderStatus)
    {
        if (storedStatus == MarkingStatus.Printed)
        {
            return MarkingStatus.Printed;
        }

        if (orderStatus == OrderStatus.Cancelled)
        {
            return MarkingStatus.NotRequired;
        }

        return markingRequired
            ? MarkingStatus.Required
            : MarkingStatus.NotRequired;
    }
}
