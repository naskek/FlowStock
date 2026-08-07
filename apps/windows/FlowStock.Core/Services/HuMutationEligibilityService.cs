using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class HuMutationEligibilityService
{
    private readonly IHuOperatorFactsStore _factsStore;

    public HuMutationEligibilityService(IHuOperatorFactsStore factsStore)
    {
        _factsStore = factsStore ?? throw new ArgumentNullException(nameof(factsStore));
    }

    public IReadOnlyDictionary<string, HuMutationEligibilityDecision> Evaluate(
        IReadOnlyCollection<string> huCodes,
        Func<HuOperatorFacts, HuMutationEligibilityContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(huCodes);
        ArgumentNullException.ThrowIfNull(contextFactory);

        var normalized = huCodes
            .Select(NormalizeHu)
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var factsByHu = _factsStore.GetForHus(normalized)
            .ToDictionary(facts => NormalizeHu(facts.HuCode), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, HuMutationEligibilityDecision>(StringComparer.OrdinalIgnoreCase);
        foreach (var huCode in normalized)
        {
            if (!factsByHu.TryGetValue(huCode, out var facts))
            {
                result[huCode] = HuMutationEligibilityDecision.Reject(
                    new HuMutationEligibilityReason(
                        HuMutationEligibilityReasonCode.HuStateChanged,
                        huCode,
                        "HU facts не найдены после повторной загрузки."));
                continue;
            }

            result[huCode] = HuMutationEligibilityPolicy.Evaluate(facts, contextFactory(facts));
        }

        return result;
    }

    public static IHuOperatorFactsStore LockMutationScope(
        IDataStore store,
        IReadOnlyCollection<long> orderIds,
        IReadOnlyCollection<string> huCodes,
        IReadOnlyCollection<long>? documentIds = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var factsStore = store as IHuOperatorFactsStore;
        var lockStore = store as IHuTransactionLockStore;
        if (factsStore == null || lockStore == null)
        {
            throw new InvalidOperationException(
                "Authoritative HU mutation requires transaction-scoped HU locks and facts on the same store.");
        }

        var normalizedOrders = orderIds.Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();
        if (!store.LockOrdersForUpdate(normalizedOrders))
        {
            throw new InvalidOperationException("HU_STATE_CHANGED: связанный заказ не найден после блокировки.");
        }

        var normalizedHus = huCodes
            .Select(NormalizeHu)
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();
        lockStore.LockNormalizedHus(normalizedHus);

        var normalizedDocuments = (documentIds ?? Array.Empty<long>())
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        if (normalizedDocuments.Length > 0)
        {
            lockStore.LockDocumentsForUpdate(normalizedDocuments);
        }

        return factsStore;
    }

    public static HuMutationEligibilityContext WholeStockContext(
        HuOperatorFacts facts,
        HuMutationOperation operation,
        long? targetOrderId = null,
        long? sourceOrderId = null,
        long? currentOutboundDocumentId = null) => new()
    {
        Operation = operation,
        TargetOrderId = targetOrderId,
        SourceOrderId = sourceOrderId,
        CurrentOutboundDocumentId = currentOutboundDocumentId,
        RequestedComponents = facts.Stock
            .Where(row => row.Qty > StockQuantityRules.QtyTolerance)
            .Select(row => new HuMutationRequestedComponent(row.ItemId, row.Qty, row.LocationId))
            .ToArray()
    };

    public static HuMutationEligibilityContext ReleaseProducedStockContext(
        HuOperatorFacts facts,
        long sourceOrderId) => new()
    {
        Operation = HuMutationOperation.ReleaseProducedStock,
        SourceOrderId = sourceOrderId,
        RequestedComponents = facts.ProductionPallets
            .Where(pallet => ProductionPalletStatus.IsOperational(pallet.Status))
            .SelectMany(pallet => pallet.Components)
            .GroupBy(component => component.ItemId)
            .Select(group => new HuMutationRequestedComponent(
                group.Key,
                group.Sum(component => component.PlannedQty)))
            .ToArray()
    };

    private static string NormalizeHu(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
}
