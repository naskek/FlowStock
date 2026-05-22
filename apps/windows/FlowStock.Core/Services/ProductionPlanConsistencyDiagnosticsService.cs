using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class ProductionPlanConsistencyDiagnosticsService(IDataStore dataStore)
{
    private readonly IDataStore _dataStore = dataStore;

    public IReadOnlyList<ProductionPlanConsistencyDiagnosticItem> GetItems()
    {
        if (_dataStore is IProductionPlanConsistencyDiagnosticsStore diagnosticsStore)
        {
            return diagnosticsStore.GetProductionPlanConsistencyDiagnostics();
        }

        return Array.Empty<ProductionPlanConsistencyDiagnosticItem>();
    }

    public bool BlocksPrdClose(long prdDocId)
    {
        return GetBlockingItemsForPrd(prdDocId).Count > 0
               || HasPrdPalletLineMismatch(prdDocId);
    }

    public IReadOnlyList<ProductionPlanConsistencyDiagnosticItem> GetBlockingItemsForPrd(long prdDocId)
    {
        return GetItems()
            .Where(item => IsBlockingSeverity(item.Severity)
                           && IsBlockingProblem(item.ProblemCode)
                           && (item.PrdDocs.Any(doc => doc.DocId == prdDocId)
                               || item.Pallets.Any(pallet => pallet.PrdDocId == prdDocId)))
            .ToArray();
    }

    public static bool IsBlockingSeverity(string severity)
    {
        return string.Equals(severity, ProductionPlanConsistencySeverity.Error, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBlockingProblem(string problemCode)
    {
        return problemCode is ProductionPlanConsistencyProblemCode.OrderZeroButPalletsExist
            or ProductionPlanConsistencyProblemCode.PalletsExceedOrderQty
            or ProductionPlanConsistencyProblemCode.PrdLinesExceedOrderQty
            or ProductionPlanConsistencyProblemCode.MergedOrderWithPalletPlan;
    }

    private bool HasPrdPalletLineMismatch(long prdDocId)
    {
        var doc = _dataStore.GetDoc(prdDocId);
        if (doc == null || doc.Status == DocStatus.Closed || !_dataStore.HasProductionPallets(prdDocId))
        {
            return false;
        }

        if (doc.OrderId.HasValue)
        {
            var order = _dataStore.GetOrder(doc.OrderId.Value);
            if (order is { Type: OrderType.Customer, Status: OrderStatus.Shipped })
            {
                return false;
            }
        }

        var allDocLines = _dataStore.GetDocLines(prdDocId);
        var supersededDocLineIds = allDocLines
            .Where(line => line.ReplacesLineId.HasValue)
            .Select(line => line.ReplacesLineId!.Value)
            .ToHashSet();
        var docLines = allDocLines
            .Where(line => line.Qty > StockQuantityRules.QtyTolerance && !supersededDocLineIds.Contains(line.Id))
            .GroupBy(line => line.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Qty));
        var reservedHuByItem = _dataStore.GetHuOrderContextRows()
            .Where(row => row.ReservedCustomerOrderId.HasValue && !string.IsNullOrWhiteSpace(row.HuCode))
            .GroupBy(row => row.ItemId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => NormalizeHu(row.HuCode))
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Cast<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));
        var palletLines = _dataStore.GetProductionPalletsByDoc(prdDocId)
            .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            .Where(pallet => !supersededDocLineIds.Contains(pallet.DocLineId))
            .Where(pallet => !IsReservedForCustomer(pallet, reservedHuByItem))
            .SelectMany(pallet => pallet.Lines.Count > 0
                ? pallet.Lines
                : new[]
                {
                    new ProductionPalletComponentLine
                    {
                        ItemId = pallet.ItemId,
                        PlannedQty = pallet.PlannedQty
                    }
                })
            .GroupBy(line => line.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.PlannedQty));

        var itemIds = docLines.Keys.Union(palletLines.Keys).ToArray();
        foreach (var itemId in itemIds)
        {
            docLines.TryGetValue(itemId, out var docQty);
            palletLines.TryGetValue(itemId, out var palletQty);
            if (Math.Abs(docQty - palletQty) > StockQuantityRules.QtyTolerance)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReservedForCustomer(
        ProductionPallet pallet,
        IReadOnlyDictionary<long, HashSet<string>> reservedHuByItem)
    {
        var huCode = NormalizeHu(pallet.HuCode);
        return !string.IsNullOrWhiteSpace(huCode)
               && reservedHuByItem.TryGetValue(pallet.ItemId, out var reservedHu)
               && reservedHu.Contains(huCode);
    }

    private static string? NormalizeHu(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    }
}
