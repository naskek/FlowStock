using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class NewLedgerTransitionService
{
    private const double QtyTolerance = StockQuantityRules.QtyTolerance;
    private readonly IDataStore _store;

    public NewLedgerTransitionService(IDataStore store)
    {
        _store = store;
    }

    public NewLedgerTransitionReport DryRun() => BuildReport(_store, applied: false);

    public NewLedgerTransitionReport Apply()
    {
        NewLedgerTransitionReport? report = null;
        _store.ExecuteInTransaction(store =>
        {
            var before = store.CountLedgerEntries();
            var dryRun = BuildReport(store, applied: false, ledgerRowsBefore: before);
            RemoveStaleReservations(store, dryRun.StaleReservations);
            var after = store.CountLedgerEntries();
            if (after != before)
            {
                throw new InvalidOperationException("NEW_LEDGER_TRANSITION_LEDGER_CHANGED");
            }

            report = BuildReport(store, applied: true, ledgerRowsBefore: before, ledgerRowsAfter: after);
        });

        return report ?? throw new InvalidOperationException("NEW_LEDGER_TRANSITION_FAILED");
    }

    private static NewLedgerTransitionReport BuildReport(
        IDataStore store,
        bool applied,
        long? ledgerRowsBefore = null,
        long? ledgerRowsAfter = null)
    {
        var before = ledgerRowsBefore ?? store.CountLedgerEntries();
        var staleReservations = FindStaleReservations(store);
        var filledWithoutLedger = FindFilledPalletsWithoutLedger(store);
        var draftPrdsWithLedger = FindDraftPrdsWithLedger(store);
        var actions = BuildActions(staleReservations, filledWithoutLedger, draftPrdsWithLedger);
        var after = ledgerRowsAfter ?? store.CountLedgerEntries();
        if (after != before)
        {
            throw new InvalidOperationException("NEW_LEDGER_TRANSITION_DRY_RUN_MUTATED_LEDGER");
        }

        return new NewLedgerTransitionReport
        {
            Applied = applied,
            LedgerRowsBefore = before,
            LedgerRowsAfter = after,
            StaleReservationCount = staleReservations.Count,
            StaleReservationQty = staleReservations.Sum(row => row.Qty),
            StaleReservations = staleReservations,
            FilledPalletsWithoutLedger = filledWithoutLedger,
            DraftPrdsWithLedger = draftPrdsWithLedger,
            PlannedActions = actions
        };
    }

    private static IReadOnlyList<NewLedgerStaleReservation> FindStaleReservations(IDataStore store)
    {
        var activeCustomerOrders = store.GetOrders()
            .Where(order => order.Type == OrderType.Customer && IsActiveCustomerStatus(order.Status))
            .OrderBy(order => order.Id)
            .ToArray();
        if (activeCustomerOrders.Length == 0)
        {
            return Array.Empty<NewLedgerStaleReservation>();
        }

        var balanceByItemHu = BuildHuBalanceByItemHu(store);
        var result = new List<NewLedgerStaleReservation>();
        foreach (var order in activeCustomerOrders)
        {
            foreach (var line in store.GetOrderReceiptPlanLines(order.Id))
            {
                var hu = NormalizeHu(line.ToHu);
                if (line.QtyPlanned <= QtyTolerance || string.IsNullOrWhiteSpace(hu))
                {
                    continue;
                }

                var balance = balanceByItemHu.TryGetValue((line.ItemId, hu), out var current) ? current : 0d;
                if (balance > QtyTolerance)
                {
                    continue;
                }

                result.Add(new NewLedgerStaleReservation
                {
                    PlanLineId = line.Id,
                    OrderId = order.Id,
                    OrderRef = order.OrderRef,
                    OrderLineId = line.OrderLineId,
                    ItemId = line.ItemId,
                    ToHu = hu,
                    Qty = Math.Max(0, line.QtyPlanned),
                    CurrentBalance = balance
                });
            }
        }

        return result;
    }

    private static IReadOnlyList<NewLedgerFilledPalletDiagnostic> FindFilledPalletsWithoutLedger(IDataStore store)
    {
        var result = new List<NewLedgerFilledPalletDiagnostic>();
        foreach (var doc in store.GetDocs().Where(doc => doc.Type == DocType.ProductionReceipt))
        {
            foreach (var pallet in store.GetProductionPalletsByDoc(doc.Id)
                         .Where(pallet => string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)))
            {
                var hu = NormalizeHu(pallet.HuCode);
                var balance = !string.IsNullOrWhiteSpace(hu)
                    ? GetHuBalance(store, pallet.ItemId, hu)
                    : 0d;
                var receiptQty = !string.IsNullOrWhiteSpace(hu)
                    ? store.GetLedgerQtyByDocItemHu(doc.Id, pallet.ItemId, hu)
                    : 0d;
                if (receiptQty > QtyTolerance)
                {
                    continue;
                }

                result.Add(new NewLedgerFilledPalletDiagnostic
                {
                    ProductionPalletId = pallet.Id,
                    PrdDocId = doc.Id,
                    PrdDocRef = doc.DocRef,
                    OrderId = pallet.OrderId,
                    OrderLineId = pallet.OrderLineId,
                    ItemId = pallet.ItemId,
                    HuCode = hu ?? string.Empty,
                    PlannedQty = pallet.PlannedQty,
                    CurrentBalance = balance
                });
            }
        }

        return result;
    }

    private static IReadOnlyList<NewLedgerDraftPrdLedgerDiagnostic> FindDraftPrdsWithLedger(IDataStore store)
    {
        return store.GetDocs()
            .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status == DocStatus.Draft)
            .Select(doc => new { Doc = doc, LedgerRowCount = store.CountLedgerEntriesByDocId(doc.Id) })
            .Where(row => row.LedgerRowCount > 0)
            .OrderBy(row => row.Doc.Id)
            .Select(row => new NewLedgerDraftPrdLedgerDiagnostic
            {
                PrdDocId = row.Doc.Id,
                PrdDocRef = row.Doc.DocRef,
                OrderId = row.Doc.OrderId,
                LedgerRowCount = row.LedgerRowCount
            })
            .ToArray();
    }

    private static IReadOnlyList<NewLedgerTransitionAction> BuildActions(
        IReadOnlyList<NewLedgerStaleReservation> staleReservations,
        IReadOnlyList<NewLedgerFilledPalletDiagnostic> filledWithoutLedger,
        IReadOnlyList<NewLedgerDraftPrdLedgerDiagnostic> draftPrdsWithLedger)
    {
        var actions = new List<NewLedgerTransitionAction>();
        actions.AddRange(staleReservations.Select(row => new NewLedgerTransitionAction
        {
            ActionCode = NewLedgerTransitionActionCodes.RemoveStaleReservation,
            OrderId = row.OrderId,
            OrderRef = row.OrderRef,
            OrderLineId = row.OrderLineId,
            ItemId = row.ItemId,
            HuCode = row.ToHu,
            Details = $"Remove stale reservation row {row.PlanLineId} because ledger balance is {row.CurrentBalance:0.###}."
        }));
        actions.AddRange(staleReservations
            .GroupBy(row => new { row.OrderId, row.OrderRef })
            .Select(group => new NewLedgerTransitionAction
            {
                ActionCode = NewLedgerTransitionActionCodes.RebuildActiveCustomerReservation,
                OrderId = group.Key.OrderId,
                OrderRef = group.Key.OrderRef,
                Details = "Customer receipt plan will be recalculated by normal reservation flow after stale rows are removed."
            }));
        actions.AddRange(filledWithoutLedger.Select(row => new NewLedgerTransitionAction
        {
            ActionCode = NewLedgerTransitionActionCodes.ReportFilledWithoutLedger,
            OrderId = row.OrderId,
            OrderLineId = row.OrderLineId,
            ItemId = row.ItemId,
            HuCode = row.HuCode,
            Details = $"Filled pallet {row.ProductionPalletId} on PRD {row.PrdDocRef} has no matching receipt ledger."
        }));
        actions.AddRange(draftPrdsWithLedger.Select(row => new NewLedgerTransitionAction
        {
            ActionCode = NewLedgerTransitionActionCodes.ReportDraftPrdWithLedger,
            OrderId = row.OrderId,
            Details = $"Draft PRD {row.PrdDocRef} has {row.LedgerRowCount} ledger rows."
        }));
        return actions;
    }

    private static void RemoveStaleReservations(IDataStore store, IReadOnlyList<NewLedgerStaleReservation> staleReservations)
    {
        foreach (var group in staleReservations.GroupBy(row => row.OrderId))
        {
            var staleById = group.Where(row => row.PlanLineId > 0).Select(row => row.PlanLineId).ToHashSet();
            var staleKeys = group
                .Select(row => (row.OrderLineId, row.ItemId, Hu: row.ToHu))
                .ToHashSet();
            var kept = store.GetOrderReceiptPlanLines(group.Key)
                .Where(line =>
                {
                    var hu = NormalizeHu(line.ToHu);
                    if (line.Id > 0 && staleById.Contains(line.Id))
                    {
                        return false;
                    }

                    return string.IsNullOrWhiteSpace(hu)
                           || !staleKeys.Contains((line.OrderLineId, line.ItemId, hu));
                })
                .ToArray();
            store.ReplaceOrderReceiptPlanLines(group.Key, kept);
        }
    }

    private static Dictionary<(long ItemId, string Hu), double> BuildHuBalanceByItemHu(IDataStore store)
    {
        return store.GetHuStockRows()
            .Where(row => !string.IsNullOrWhiteSpace(row.HuCode))
            .GroupBy(row => (row.ItemId, Hu: NormalizeHu(row.HuCode)!))
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Qty));
    }

    private static double GetHuBalance(IDataStore store, long itemId, string hu)
    {
        return store.GetHuStockRows()
            .Where(row => row.ItemId == itemId && string.Equals(NormalizeHu(row.HuCode), hu, StringComparison.OrdinalIgnoreCase))
            .Sum(row => row.Qty);
    }

    private static bool IsActiveCustomerStatus(OrderStatus status) =>
        status is not OrderStatus.Shipped and not OrderStatus.Cancelled and not OrderStatus.Merged;

    private static string? NormalizeHu(string? huCode) =>
        string.IsNullOrWhiteSpace(huCode) ? null : huCode.Trim().ToUpperInvariant();
}
