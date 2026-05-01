using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed record MarkingStatusBackfillOptions(
    DateTime? CreatedBefore,
    bool Apply,
    string? Confirm = null,
    DateTime? Timestamp = null);

public sealed class MarkingStatusBackfillReport
{
    public bool Applied { get; init; }
    public DateTime CreatedBefore { get; init; }
    public int TotalScanned { get; set; }
    public int ChangedToPrinted { get; set; }
    public int ChangedToNotRequired { get; set; }
    public int SkippedCancelled { get; set; }
    public int SkippedPending { get; set; }
    public int AlreadyPrinted { get; set; }
}

public sealed class MarkingStatusBackfillService
{
    private readonly IDataStore _data;

    public MarkingStatusBackfillService(IDataStore data)
    {
        _data = data;
    }

    public MarkingStatusBackfillReport Run(MarkingStatusBackfillOptions options)
    {
        if (!options.CreatedBefore.HasValue)
        {
            throw new ArgumentException("Для backfill статусов ЧЗ требуется --created-before YYYY-MM-DD.", nameof(options));
        }

        if (options.Apply && !string.Equals(options.Confirm, "APPLY", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Для применения backfill статусов ЧЗ требуется --confirm APPLY.");
        }

        var cutoff = options.CreatedBefore.Value.Date;
        var timestamp = options.Timestamp ?? DateTime.UtcNow;
        var itemsById = _data.GetItems(null).ToDictionary(item => item.Id, item => item);
        var report = new MarkingStatusBackfillReport
        {
            Applied = options.Apply,
            CreatedBefore = cutoff
        };

        foreach (var order in _data.GetOrders()
                     .Where(order => order.CreatedAt.Date < cutoff)
                     .OrderBy(order => order.Id))
        {
            report.TotalScanned++;

            if (order.Status == OrderStatus.Cancelled)
            {
                report.SkippedCancelled++;
                continue;
            }

            if (order.Status == OrderStatus.Draft)
            {
                report.SkippedPending++;
                continue;
            }

            if (order.Status is not (OrderStatus.InProgress or OrderStatus.Accepted or OrderStatus.Shipped))
            {
                report.SkippedPending++;
                continue;
            }

            if (order.IsLegacyExcelGeneratedMarkingStatus)
            {
                report.ChangedToPrinted++;
                if (options.Apply)
                {
                    _data.UpdateOrderMarkingStatusForBackfill(order.Id, MarkingStatus.Printed, timestamp);
                }

                continue;
            }

            if (order.MarkingStatus == MarkingStatus.Printed)
            {
                report.AlreadyPrinted++;
                continue;
            }

            if (HasCompletedLifecycleEvidence(order))
            {
                report.ChangedToPrinted++;
                if (options.Apply)
                {
                    _data.UpdateOrderMarkingStatusForBackfill(order.Id, MarkingStatus.Printed, timestamp);
                }

                continue;
            }

            var hasMarkableLines = _data.GetOrderLines(order.Id)
                .Any(line => itemsById.TryGetValue(line.ItemId, out var item)
                             && item.IsChestnyZnakMarkingRequired);

            if (hasMarkableLines)
            {
                report.ChangedToPrinted++;
                if (options.Apply)
                {
                    _data.UpdateOrderMarkingStatusForBackfill(order.Id, MarkingStatus.Printed, timestamp);
                }

                continue;
            }

            if (order.MarkingStatus != MarkingStatus.NotRequired)
            {
                report.ChangedToNotRequired++;
                if (options.Apply)
                {
                    _data.UpdateOrderMarkingStatusForBackfill(order.Id, MarkingStatus.NotRequired, timestamp);
                }
            }
        }

        return report;
    }

    private static bool HasCompletedLifecycleEvidence(Order order)
    {
        return order.MarkingExcelGeneratedAt.HasValue
               || order.MarkingPrintedAt.HasValue;
    }
}
