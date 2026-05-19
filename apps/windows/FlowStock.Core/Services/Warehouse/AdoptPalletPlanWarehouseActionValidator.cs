using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services.Warehouse;

public sealed class AdoptPalletPlanWarehouseActionValidator : IWarehouseActionValidator
{
    public string ActionType => WarehouseActionType.AdoptPalletPlan;

    public void ValidateForBundle(
        IDataStore store,
        long? bundleId,
        WarehouseActionLine line,
        ICollection<WarehouseBundleIssue> errors,
        ICollection<WarehouseBundleIssue> warnings)
    {
        var payload = WarehousePayloadParser.ParseAdopt(line);
        var lineNo = line.LineNo;

        if (!payload.SourceInternalOrderId.HasValue || payload.SourceInternalOrderId.Value <= 0)
        {
            errors.Add(Issue("MISSING_SOURCE_ORDER", "Не указан внутренний заказ-источник.", lineNo));
            return;
        }

        if (!payload.TargetCustomerOrderId.HasValue || payload.TargetCustomerOrderId.Value <= 0)
        {
            errors.Add(Issue("MISSING_TARGET_ORDER", "Не указан клиентский заказ-получатель.", lineNo));
            return;
        }

        var source = store.GetOrder(payload.SourceInternalOrderId.Value);
        if (source == null)
        {
            errors.Add(Issue("SOURCE_ORDER_NOT_FOUND", "Внутренний заказ-источник не найден.", lineNo));
            return;
        }

        var target = store.GetOrder(payload.TargetCustomerOrderId.Value);
        if (target == null)
        {
            errors.Add(Issue("TARGET_ORDER_NOT_FOUND", "Клиентский заказ-получатель не найден.", lineNo));
            return;
        }

        if (source.Type != OrderType.Internal)
        {
            errors.Add(Issue("SOURCE_NOT_INTERNAL", "Источник должен быть внутренним заказом.", lineNo));
        }

        if (target.Type != OrderType.Customer)
        {
            errors.Add(Issue("TARGET_NOT_CUSTOMER", "Получатель должен быть клиентским заказом.", lineNo));
        }

        if (source.Status is OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged)
        {
            errors.Add(Issue("SOURCE_ORDER_NOT_EDITABLE", "Внутренний заказ недоступен для переноса плана паллет.", lineNo));
        }

        if (target.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
        {
            errors.Add(Issue("TARGET_ORDER_NOT_EDITABLE", "Клиентский заказ недоступен для переноса плана паллет.", lineNo));
        }

        _ = bundleId;
        _ = warnings;
    }

    private static WarehouseBundleIssue Issue(string code, string message, int? lineNo) =>
        new() { Code = code, Message = message, LineNo = lineNo };
}
