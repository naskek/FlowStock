using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class WarehouseBoardStateEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/planner/warehouse-board/state", HandleGet);
    }

    private static IResult HandleGet(IDataStore store)
    {
        var orderService = new OrderService(store);
        var documentService = new DocumentService(store);
        var orders = orderService.GetOrders();
        var itemsById = store.GetItems(null).ToDictionary(item => item.Id);
        var locations = store.GetLocations();
        var locationsById = locations.ToDictionary(location => location.Id);

        var orderRows = orders
            .OrderByDescending(order => HasPalletPlanForOrder(order, store))
            .ThenBy(order => order.OrderRef, StringComparer.OrdinalIgnoreCase)
            .Select(order => MapBoardOrder(order, store, documentService))
            .ToArray();

        var contextByKey = HuStockReadModelMapper.BuildContextMap(store.GetHuOrderContextRows());
        var huStock = store.GetHuStockRows()
            .Where(row => !StockQuantityRules.IsEffectivelyZero(row.Qty))
            .Select(row =>
            {
                var mapped = HuStockReadModelMapper.Map(row.ItemId, row.LocationId, row.HuCode, row.Qty, contextByKey);
                itemsById.TryGetValue(row.ItemId, out var item);
                locationsById.TryGetValue(row.LocationId, out var location);
                var orderId = mapped.ReservedCustomerOrderId ?? mapped.OriginInternalOrderId;
                var orderRef = mapped.ReservedCustomerOrderRef ?? mapped.OriginInternalOrderRef;
                return new
                {
                    hu_code = mapped.Hu,
                    item_id = mapped.ItemId,
                    item_name = item?.Name ?? string.Empty,
                    item_brand = item?.Brand ?? string.Empty,
                    qty = mapped.Qty,
                    location_id = mapped.LocationId,
                    location_code = location?.Code ?? string.Empty,
                    location_name = location?.Name ?? string.Empty,
                    order_id = orderId,
                    order_ref = orderRef ?? string.Empty,
                    reserved_customer_order_id = mapped.ReservedCustomerOrderId,
                    reserved_customer_order_ref = mapped.ReservedCustomerOrderRef,
                    origin_internal_order_id = mapped.OriginInternalOrderId,
                    origin_internal_order_ref = mapped.OriginInternalOrderRef,
                    reserved_customer_name = mapped.ReservedCustomerName
                };
            })
            .OrderBy(row => row.hu_code, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var palletPlans = orders
            .Where(order => HasPalletPlanForOrder(order, store))
            .Select(order =>
            {
                var prdDoc = store.GetDocsByOrder(order.Id)
                    .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status != DocStatus.Closed)
                    .OrderByDescending(doc => doc.CreatedAt)
                    .FirstOrDefault();
                if (prdDoc == null)
                {
                    return null;
                }

                var pallets = store.GetProductionPalletsByDoc(prdDoc.Id)
                    .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
                    .Select(pallet => new
                    {
                        hu_code = pallet.HuCode,
                        item_name = pallet.ItemName,
                        status = pallet.Status,
                        planned_qty = pallet.PlannedQty
                    })
                    .ToArray();
                return new
                {
                    order_id = order.Id,
                    order_ref = order.OrderRef,
                    prd_doc_id = prdDoc.Id,
                    prd_ref = prdDoc.DocRef,
                    planned_pallet_count = pallets.Length,
                    pallets
                };
            })
            .Where(plan => plan != null)
            .ToArray();

        return Results.Ok(new
        {
            success = true,
            orders = orderRows,
            hu_stock = huStock,
            pallet_plans = palletPlans,
            locations = locations
                .Select(location => new
                {
                    id = location.Id,
                    code = location.Code,
                    name = location.Name,
                    display = string.IsNullOrWhiteSpace(location.Name)
                        ? location.Code
                        : $"{location.Code} — {location.Name}"
                })
                .OrderBy(location => location.code, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        });
    }

    private static bool HasPalletPlanForOrder(Order order, IDataStore store)
    {
        return store.GetDocsByOrder(order.Id)
            .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status != DocStatus.Closed)
            .Select(doc => ProductionPalletService.BuildSummary(store.GetProductionPalletsByDoc(doc.Id)))
            .Any(summary => summary.PlannedPalletCount > 0);
    }

    private static object MapBoardOrder(Order order, IDataStore store, DocumentService documentService)
    {
        var needsProductionPalletPlan = order.Status is not (OrderStatus.Shipped or OrderStatus.Cancelled)
                                        && documentService.GetOrderReceiptRemaining(order.Id)
                                            .Any(line => line.QtyRemaining > 0.000001);
        var palletDocs = store.GetDocsByOrder(order.Id)
            .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status != DocStatus.Closed)
            .ToList();
        var palletSummaries = palletDocs
            .Select(doc => ProductionPalletService.BuildSummary(store.GetProductionPalletsByDoc(doc.Id)))
            .ToList();
        var hasProductionPalletPlan = palletSummaries.Any(summary => summary.PlannedPalletCount > 0);
        var palletSummary = CombineProductionPalletSummaries(palletSummaries);
        var prdDoc = palletDocs
            .OrderByDescending(doc => doc.CreatedAt)
            .FirstOrDefault();

        var isActive = order.Status is not (OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged);

        return new
        {
            id = order.Id,
            order_ref = order.OrderRef,
            type = OrderStatusMapper.TypeToString(order.Type),
            type_display = order.Type == OrderType.Internal ? "Внутренний" : "Клиентский",
            status = OrderStatusMapper.StatusToString(order.Status),
            status_display = OrderStatusMapper.StatusToDisplayName(order.Status, order.Type),
            partner_name = order.PartnerName ?? string.Empty,
            has_pallet_plan = hasProductionPalletPlan,
            planned_pallet_count = palletSummary.PlannedPalletCount,
            filled_pallet_count = palletSummary.FilledPalletCount,
            needs_pallet_plan = needsProductionPalletPlan,
            pallet_plan_status = BuildOrderPalletPlanStatus(needsProductionPalletPlan, hasProductionPalletPlan, palletSummary),
            prd_doc_id = prdDoc?.Id,
            prd_ref = prdDoc?.DocRef,
            is_active = isActive,
            can_adopt_source = order.Type == OrderType.Internal
                               && hasProductionPalletPlan
                               && isActive,
            can_adopt_target = order.Type == OrderType.Customer
                               && !hasProductionPalletPlan
                               && isActive
        };
    }

    private static ProductionPalletSummary CombineProductionPalletSummaries(IReadOnlyList<ProductionPalletSummary> summaries)
    {
        if (summaries.Count == 0)
        {
            return new ProductionPalletSummary();
        }

        return new ProductionPalletSummary
        {
            PlannedPalletCount = summaries.Sum(summary => summary.PlannedPalletCount),
            FilledPalletCount = summaries.Sum(summary => summary.FilledPalletCount),
            PlannedQty = summaries.Sum(summary => summary.PlannedQty),
            FilledQty = summaries.Sum(summary => summary.FilledQty),
            RemainingPalletCount = summaries.Sum(summary => summary.RemainingPalletCount),
            RemainingQty = summaries.Sum(summary => summary.RemainingQty)
        };
    }

    private static string BuildOrderPalletPlanStatus(
        bool needsProductionPalletPlan,
        bool hasProductionPalletPlan,
        ProductionPalletSummary summary)
    {
        if (!needsProductionPalletPlan && !hasProductionPalletPlan)
        {
            return "Не требуется";
        }

        if (!hasProductionPalletPlan)
        {
            return "План не создан";
        }

        if (summary.PlannedPalletCount <= 0)
        {
            return "План не создан";
        }

        if (summary.FilledPalletCount >= summary.PlannedPalletCount)
        {
            return $"Наполнено: {summary.FilledPalletCount} / {summary.PlannedPalletCount}";
        }

        return $"Наполнение: {summary.FilledPalletCount} / {summary.PlannedPalletCount}";
    }
}
