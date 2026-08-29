using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

internal sealed record ProductionFuturePlanTarget(
    long OrderLineId,
    double OrderedQty,
    double ProtectedQty,
    double FuturePlanQty);

/// <summary>
/// Единая арифметическая граница между factual/protected coverage и изменяемым
/// future production plan. Активные PLANNED/PRINTED паллеты намеренно не входят
/// в ProtectedQty: они являются объектом reconcile, а не второй формой coverage.
/// </summary>
internal static class ProductionFuturePlanTargetCalculator
{
    public static ProductionFuturePlanTarget Calculate(
        IDataStore store,
        Order order,
        OrderLine orderLine,
        double? orderedQtyOverride = null)
    {
        var orderedQty = Math.Max(0, orderedQtyOverride ?? orderLine.QtyOrdered);
        double protectedQty;
        if (order.Type == OrderType.Customer)
        {
            var coverage = CustomerProtectedCoverageCalculator.BuildByOrderLine(
                    store,
                    order.Id,
                    includeUnconfirmedFilledPallets: true)
                .GetValueOrDefault(orderLine.Id);
            protectedQty = coverage?.ResolveProtectedQty(orderedQty) ?? 0d;
        }
        else
        {
            var confirmed = ProductionPalletService.BuildInternalPlanningCoverage(
                store,
                order.Id,
                store.GetOrderLines(order.Id));
            protectedQty = confirmed.TryGetValue(orderLine.Id, out var qty) ? Math.Max(0, qty) : 0d;
        }

        protectedQty = Math.Min(orderedQty, Math.Max(0, protectedQty));
        return new ProductionFuturePlanTarget(
            orderLine.Id,
            orderedQty,
            protectedQty,
            Math.Max(0, orderedQty - protectedQty));
    }
}
