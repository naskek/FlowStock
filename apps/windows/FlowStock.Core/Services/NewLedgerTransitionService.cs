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

    public FilledLedgerRepairReport DryRunFilledLedgerRepair(FilledLedgerRepairRequest request) =>
        BuildFilledLedgerRepairReport(_store, NormalizeRepairRequest(request), dryRun: true);

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

    public FilledLedgerRepairReport ApplyFilledLedgerRepair(FilledLedgerRepairRequest request)
    {
        FilledLedgerRepairReport? report = null;
        var normalized = NormalizeRepairRequest(request);
        _store.ExecuteInTransaction(store =>
        {
            var before = BuildFilledLedgerRepairReport(store, normalized, dryRun: true);
            var appliedPalletIds = new List<long>();
            var warnings = new List<string>();

            foreach (var candidate in before.Candidates.Where(row => row.Decision == FilledLedgerRepairDecisions.SafeToBackfill))
            {
                var currentReceiptQty = store.GetLedgerQtyByDocItemHu(candidate.PrdDocId, candidate.ItemId, candidate.HuCode);
                if (currentReceiptQty > QtyTolerance)
                {
                    warnings.Add($"Pallet {candidate.PalletId} skipped: receipt ledger already exists.");
                    continue;
                }

                if (!candidate.LocationId.HasValue)
                {
                    warnings.Add($"Pallet {candidate.PalletId} skipped: no location.");
                    continue;
                }

                store.AddLedgerEntry(new LedgerEntry
                {
                    Timestamp = DateTime.UtcNow,
                    DocId = candidate.PrdDocId,
                    ItemId = candidate.ItemId,
                    LocationId = candidate.LocationId.Value,
                    QtyDelta = candidate.PlannedQty,
                    HuCode = candidate.HuCode
                });
                appliedPalletIds.Add(candidate.PalletId);
            }

            var afterWrite = BuildFilledLedgerRepairReport(store, normalized, dryRun: true);
            var closedDocIds = new List<long>();
            var refreshedOrderIds = new List<long>();
            if (normalized.CloseStaleInternalPrdDrafts)
            {
                foreach (var closeCandidate in afterWrite.StaleInternalPrdDraftCloseCandidates)
                {
                    var doc = store.GetDoc(closeCandidate.DocId);
                    if (doc?.Status != DocStatus.Draft || doc.Type != DocType.ProductionReceipt)
                    {
                        continue;
                    }

                    store.UpdateDocStatus(doc.Id, DocStatus.Closed, DateTime.UtcNow);
                    closedDocIds.Add(doc.Id);
                    var newStatus = new OrderService(store).RefreshPersistedStatus(closeCandidate.OrderId);
                    if (newStatus == OrderStatus.Shipped)
                    {
                        refreshedOrderIds.Add(closeCandidate.OrderId);
                    }
                }
            }

            var final = BuildFilledLedgerRepairReport(store, normalized, dryRun: false);
            report = new FilledLedgerRepairReport
            {
                DryRun = false,
                LedgerRowsWritten = appliedPalletIds.Count,
                AppliedPalletIds = appliedPalletIds.OrderBy(id => id).ToArray(),
                ClosedPrdDocIds = closedDocIds.OrderBy(id => id).ToArray(),
                RefreshedOrderIds = refreshedOrderIds.Distinct().OrderBy(id => id).ToArray(),
                Candidates = final.Candidates,
                StaleInternalPrdDraftCloseCandidates = final.StaleInternalPrdDraftCloseCandidates,
                Skipped = final.Skipped,
                Warnings = warnings
            };
        });

        return report ?? throw new InvalidOperationException("FILLED_LEDGER_REPAIR_FAILED");
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

    private static FilledLedgerRepairRequest NormalizeRepairRequest(FilledLedgerRepairRequest? request)
    {
        return request == null
            ? new FilledLedgerRepairRequest()
            : new FilledLedgerRepairRequest
            {
                OrderIds = request.OrderIds.Where(id => id > 0).Distinct().OrderBy(id => id).ToArray(),
                PrdDocIds = request.PrdDocIds.Where(id => id > 0).Distinct().OrderBy(id => id).ToArray(),
                PalletIds = request.PalletIds.Where(id => id > 0).Distinct().OrderBy(id => id).ToArray(),
                CloseStaleInternalPrdDrafts = request.CloseStaleInternalPrdDrafts
            };
    }

    private static FilledLedgerRepairReport BuildFilledLedgerRepairReport(
        IDataStore store,
        FilledLedgerRepairRequest request,
        bool dryRun)
    {
        var candidates = FindFilledLedgerRepairCandidates(store, request);
        var closeCandidates = request.CloseStaleInternalPrdDrafts
            ? FindStaleInternalDraftPrdCloseCandidates(store, request)
            : Array.Empty<FilledLedgerRepairPrdCloseCandidate>();
        return new FilledLedgerRepairReport
        {
            DryRun = dryRun,
            Candidates = candidates,
            StaleInternalPrdDraftCloseCandidates = closeCandidates,
            Skipped = candidates.Where(row => row.Decision != FilledLedgerRepairDecisions.SafeToBackfill).ToArray()
        };
    }

    private static IReadOnlyList<FilledLedgerRepairCandidate> FindFilledLedgerRepairCandidates(
        IDataStore store,
        FilledLedgerRepairRequest request)
    {
        var docs = store.GetDocs()
            .Where(doc => doc.Type == DocType.ProductionReceipt)
            .OrderBy(doc => doc.Id)
            .ToArray();
        var result = new List<FilledLedgerRepairCandidate>();
        foreach (var doc in docs)
        {
            foreach (var pallet in store.GetProductionPalletsByDoc(doc.Id).OrderBy(pallet => pallet.Id))
            {
                var orderId = pallet.OrderId ?? doc.OrderId;
                if (!MatchesRepairFilters(orderId, doc.Id, pallet.Id, request))
                {
                    continue;
                }

                result.Add(BuildFilledLedgerRepairCandidate(store, doc, pallet));
            }
        }

        return result;
    }

    private static FilledLedgerRepairCandidate BuildFilledLedgerRepairCandidate(
        IDataStore store,
        Doc doc,
        ProductionPallet pallet)
    {
        var orderId = pallet.OrderId ?? doc.OrderId;
        var order = orderId.HasValue ? store.GetOrder(orderId.Value) : null;
        var hu = NormalizeHu(pallet.HuCode);
        var receiptQty = string.IsNullOrWhiteSpace(hu) ? 0 : store.GetLedgerQtyByDocItemHu(doc.Id, pallet.ItemId, hu);
        var balanceQty = string.IsNullOrWhiteSpace(hu) ? 0 : GetHuBalance(store, pallet.ItemId, hu);
        var decision = ResolveFilledLedgerRepairDecision(pallet, hu, receiptQty);
        return new FilledLedgerRepairCandidate
        {
            OrderId = orderId,
            OrderRef = order?.OrderRef ?? doc.OrderRef ?? string.Empty,
            OrderType = order == null ? string.Empty : OrderStatusMapper.TypeToString(order.Type),
            OrderStatus = order == null ? string.Empty : OrderStatusMapper.StatusToString(order.Status),
            PrdDocId = doc.Id,
            PrdDocRef = doc.DocRef,
            PrdStatus = DocTypeMapper.StatusToString(doc.Status),
            PalletId = pallet.Id,
            HuCode = hu ?? string.Empty,
            ItemId = pallet.ItemId,
            LocationId = pallet.ToLocationId,
            PlannedQty = Math.Max(0, pallet.PlannedQty),
            CurrentReceiptQty = receiptQty,
            CurrentBalanceQty = balanceQty,
            Decision = decision
        };
    }

    private static string ResolveFilledLedgerRepairDecision(ProductionPallet pallet, string? hu, double receiptQty)
    {
        if (string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
        {
            return FilledLedgerRepairDecisions.SkipCancelled;
        }

        if (!string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
        {
            return FilledLedgerRepairDecisions.SkipNotFilled;
        }

        if (string.IsNullOrWhiteSpace(hu))
        {
            return FilledLedgerRepairDecisions.SkipNoHu;
        }

        if (!pallet.ToLocationId.HasValue)
        {
            return FilledLedgerRepairDecisions.SkipNoLocation;
        }

        return receiptQty > QtyTolerance
            ? FilledLedgerRepairDecisions.SkipAlreadyHasReceiptLedger
            : FilledLedgerRepairDecisions.SafeToBackfill;
    }

    private static IReadOnlyList<FilledLedgerRepairPrdCloseCandidate> FindStaleInternalDraftPrdCloseCandidates(
        IDataStore store,
        FilledLedgerRepairRequest request)
    {
        var result = new List<FilledLedgerRepairPrdCloseCandidate>();
        foreach (var doc in store.GetDocs()
                     .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status == DocStatus.Draft)
                     .OrderBy(doc => doc.Id))
        {
            if (!doc.OrderId.HasValue || !MatchesPrdCloseFilters(store, doc, request))
            {
                continue;
            }

            var order = store.GetOrder(doc.OrderId.Value);
            if (order?.Type != OrderType.Internal)
            {
                continue;
            }

            var activePallets = store.GetProductionPalletsByDoc(doc.Id)
                .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (activePallets.Any(pallet =>
                    !string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var orderLines = store.GetOrderLines(order.Id);
            var producedByLine = OrderReceiptRemainingCalculator.BuildGrossReceiptLedgerTotalsByOrderLine(store, order.Id, orderLines);
            var linesWithDemand = orderLines.Where(line => line.QtyOrdered > QtyTolerance).ToArray();
            if (linesWithDemand.Length == 0
                || linesWithDemand.Any(line => !producedByLine.TryGetValue(line.Id, out var produced)
                                               || produced + QtyTolerance < line.QtyOrdered))
            {
                continue;
            }

            result.Add(new FilledLedgerRepairPrdCloseCandidate
            {
                DocId = doc.Id,
                DocRef = doc.DocRef,
                OrderId = order.Id,
                OrderRef = order.OrderRef,
                GrossReceiptQtyByOrder = producedByLine.Values.Sum(),
                OrderedQtyByOrder = linesWithDemand.Sum(line => Math.Max(0, line.QtyOrdered)),
                Reason = "INTERNAL_GROSS_RECEIPT_COVERS_ORDER"
            });
        }

        return result;
    }

    private static bool MatchesRepairFilters(long? orderId, long prdDocId, long palletId, FilledLedgerRepairRequest request)
    {
        return (request.OrderIds.Count == 0 || (orderId.HasValue && request.OrderIds.Contains(orderId.Value)))
               && (request.PrdDocIds.Count == 0 || request.PrdDocIds.Contains(prdDocId))
               && (request.PalletIds.Count == 0 || request.PalletIds.Contains(palletId));
    }

    private static bool MatchesPrdCloseFilters(IDataStore store, Doc doc, FilledLedgerRepairRequest request)
    {
        if (request.OrderIds.Count > 0 && (!doc.OrderId.HasValue || !request.OrderIds.Contains(doc.OrderId.Value)))
        {
            return false;
        }

        if (request.PrdDocIds.Count > 0 && !request.PrdDocIds.Contains(doc.Id))
        {
            return false;
        }

        if (request.PalletIds.Count == 0)
        {
            return true;
        }

        return store.GetProductionPalletsByDoc(doc.Id).Any(pallet => request.PalletIds.Contains(pallet.Id));
    }
}
