using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

/// <summary>
/// Controlled baseline backfill для legacy PRINTED production labels. Payload и
/// fingerprint строятся тем же runtime read-model, что используется WPF-печатью.
/// Lifecycle anomalies только диагностируются и никогда не исправляются здесь.
/// </summary>
public sealed class ProductionPalletLabelBackfillService
{
    private readonly IDataStore _data;

    public ProductionPalletLabelBackfillService(IDataStore data)
    {
        _data = data;
    }

    public ProductionPalletLabelBackfillReport Run(
        bool apply,
        IReadOnlyCollection<long>? scopedOrderIds = null)
    {
        var scope = scopedOrderIds is { Count: > 0 }
            ? scopedOrderIds.Where(id => id > 0).Distinct().ToHashSet()
            : null;
        if (!apply)
        {
            return BuildReport(_data, apply: false, scope);
        }

        ProductionPalletLabelBackfillReport? report = null;
        _data.ExecuteInTransaction(store =>
        {
            var orderIds = FindCandidateOrderIds(store, scope);
            if (orderIds.Count > 0 && !store.LockOrdersForUpdate(orderIds))
            {
                throw new InvalidOperationException("Не удалось заблокировать все заказы для label fingerprint backfill.");
            }

            report = BuildReport(store, apply: true, orderIds.ToHashSet());
        });

        return report ?? new ProductionPalletLabelBackfillReport(
            "APPLY",
            0,
            0,
            0,
            Array.Empty<ProductionPalletLabelBackfillRow>());
    }

    private static ProductionPalletLabelBackfillReport BuildReport(
        IDataStore store,
        bool apply,
        IReadOnlySet<long>? scopedOrderIds)
    {
        var rows = new List<ProductionPalletLabelBackfillRow>();
        var backfilled = 0;

        foreach (var order in store.GetOrders()
                     .Where(order => scopedOrderIds == null || scopedOrderIds.Contains(order.Id))
                     .OrderBy(order => order.Id))
        {
            var candidates = GetCandidates(store, order.Id);
            if (candidates.Count == 0)
            {
                continue;
            }

            var docsById = store.GetDocsByOrder(order.Id)
                .Where(doc => doc.Type == DocType.ProductionReceipt)
                .ToDictionary(doc => doc.Id);

            IReadOnlyDictionary<long, ProductionPalletPrintRow> printRows;
            string? payloadError = null;
            try
            {
                printRows = new ProductionPalletService(store)
                    .GetPrintRows(order.Id)
                    .Where(row => string.Equals(
                        row.SourceType,
                        ProductionPalletPrintSourceType.ProductionPallet,
                        StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(row => row.PalletId);
            }
            catch (Exception ex)
            {
                printRows = new Dictionary<long, ProductionPalletPrintRow>();
                payloadError = $"CANONICAL_LABEL_PAYLOAD_UNAVAILABLE: {ex.Message}";
            }

            foreach (var pallet in candidates.OrderBy(pallet => pallet.Id))
            {
                printRows.TryGetValue(pallet.Id, out var printRow);
                docsById.TryGetValue(pallet.PrdDocId, out var doc);
                var blocker = ResolveBlocker(order, doc, pallet, printRow, payloadError);
                if (blocker != null)
                {
                    rows.Add(ToReportRow(
                        order.Id,
                        pallet,
                        printRow?.LabelFingerprint,
                        ProductionPalletLabelBackfillAction.Blocked,
                        blocker));
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(pallet.PrintedLabelFingerprint))
                {
                    rows.Add(ToReportRow(
                        order.Id,
                        pallet,
                        printRow?.LabelFingerprint,
                        ProductionPalletLabelBackfillAction.AlreadyVerified,
                        null));
                    continue;
                }

                var action = apply
                    ? ProductionPalletLabelBackfillAction.Backfilled
                    : ProductionPalletLabelBackfillAction.WouldBackfill;
                if (apply)
                {
                    var updated = store.BackfillProductionPalletLabelFingerprint(
                        order.Id,
                        pallet.Id,
                        printRow!.LabelFingerprint!);
                    if (updated != 1)
                    {
                        throw new InvalidOperationException(
                            $"Legacy label fingerprint backfill lost pallet {pallet.Id} after the order lock.");
                    }

                    backfilled++;
                }

                rows.Add(ToReportRow(order.Id, pallet, printRow!.LabelFingerprint, action, null));
            }
        }

        return new ProductionPalletLabelBackfillReport(
            apply ? "APPLY" : "DRY_RUN",
            rows.Count,
            backfilled,
            rows.Count(row => row.Action == ProductionPalletLabelBackfillAction.Blocked),
            rows);
    }

    private static IReadOnlyList<long> FindCandidateOrderIds(
        IDataStore store,
        IReadOnlySet<long>? scopedOrderIds)
    {
        return store.GetOrders()
            .Where(order => scopedOrderIds == null || scopedOrderIds.Contains(order.Id))
            .Select(order => order.Id)
            .Where(orderId => GetCandidates(store, orderId).Count > 0)
            .Distinct()
            .OrderBy(orderId => orderId)
            .ToArray();
    }

    private static IReadOnlyList<ProductionPallet> GetCandidates(IDataStore store, long orderId)
    {
        return store.GetDocsByOrder(orderId)
            .Where(doc => doc.Type == DocType.ProductionReceipt)
            .SelectMany(doc => store.GetProductionPalletsByDoc(doc.Id))
            .Where(pallet =>
                string.Equals(pallet.Status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase)
                || (string.Equals(pallet.Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase)
                    && (pallet.PrintedAt.HasValue
                        || !string.IsNullOrWhiteSpace(pallet.PrintedLabelFingerprint)
                        || pallet.PrintedLabelFingerprintVersion.HasValue)))
            .OrderBy(pallet => pallet.Id)
            .ToArray();
    }

    private static string? ResolveBlocker(
        Order order,
        Doc? doc,
        ProductionPallet pallet,
        ProductionPalletPrintRow? printRow,
        string? payloadError)
    {
        var hasFingerprint = !string.IsNullOrWhiteSpace(pallet.PrintedLabelFingerprint);
        if (hasFingerprint != pallet.PrintedLabelFingerprintVersion.HasValue)
        {
            return "FINGERPRINT_PAIR_INCONSISTENT";
        }

        if (string.Equals(pallet.Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase))
        {
            return "PLANNED_HAS_PRINT_EVIDENCE";
        }

        if (!pallet.PrintedAt.HasValue)
        {
            return "PRINTED_AT_MISSING";
        }

        if (order.Status is not (OrderStatus.Draft or OrderStatus.InProgress or OrderStatus.Accepted))
        {
            return "ORDER_NOT_ACTIVE";
        }

        if (doc?.Status != DocStatus.Draft)
        {
            return "PRD_NOT_DRAFT";
        }

        if (hasFingerprint
            && pallet.PrintedLabelFingerprintVersion != ProductionPalletLabelContract.FingerprintVersion)
        {
            return "FINGERPRINT_VERSION_UNSUPPORTED";
        }

        if (payloadError != null)
        {
            return payloadError;
        }

        if (printRow == null || string.IsNullOrWhiteSpace(printRow.LabelFingerprint))
        {
            return "CANONICAL_LABEL_PAYLOAD_UNAVAILABLE";
        }

        return null;
    }

    private static ProductionPalletLabelBackfillRow ToReportRow(
        long orderId,
        ProductionPallet pallet,
        string? currentFingerprint,
        string action,
        string? blockerReason)
    {
        return new ProductionPalletLabelBackfillRow(
            orderId,
            pallet.Id,
            pallet.HuCode,
            pallet.Status,
            pallet.PrintedAt.HasValue,
            currentFingerprint,
            action,
            blockerReason);
    }
}
