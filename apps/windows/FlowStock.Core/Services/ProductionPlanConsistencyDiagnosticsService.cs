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

        var docLines = _dataStore.GetDocLines(prdDocId)
            .Where(line => line.Qty > StockQuantityRules.QtyTolerance)
            .GroupBy(line => line.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Qty));
        var palletLines = _dataStore.GetProductionPalletsByDoc(prdDocId)
            .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
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
}
