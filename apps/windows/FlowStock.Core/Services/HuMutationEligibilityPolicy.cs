using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class HuMutationEligibilityPolicy
{
    private const double QtyTolerance = StockQuantityRules.QtyTolerance;

    public static HuMutationEligibilityDecision Evaluate(
        HuOperatorFacts facts,
        HuMutationEligibilityContext context)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(context);

        var huCode = NormalizeHu(facts.HuCode);
        var positiveLocations = facts.Stock
            .Where(row => row.Qty > QtyTolerance)
            .Select(row => row.LocationId)
            .Distinct()
            .Take(2)
            .ToArray();
        if (positiveLocations.Length > 1)
        {
            return Reject(
                HuMutationEligibilityReasonCode.HuMultipleLocations,
                huCode,
                "Положительный ledger composition физической HU находится в нескольких locations.");
        }

        if (HuOperatorClassifier.Classify(facts) is HuOperatorOperationalClassification
            {
                StateCode: OperationalHuSemanticCode.Inconsistent
            })
        {
            return Reject(
                HuMutationEligibilityReasonCode.HuInconsistent,
                huCode,
                "Факты HU противоречат атомарному physical lifecycle.");
        }

        var otherDraft = facts.Outbound.FirstOrDefault(row =>
            row.IsEffective
            && string.Equals(row.DocumentStatus, "DRAFT", StringComparison.OrdinalIgnoreCase)
            && (!context.CurrentOutboundDocumentId.HasValue
                || row.DocumentId != context.CurrentOutboundDocumentId.Value));
        if (otherDraft != null)
        {
            return Reject(
                HuMutationEligibilityReasonCode.HuInOtherOutbound,
                huCode,
                $"HU уже присутствует в другом черновике OUTBOUND {otherDraft.DocumentRef}.");
        }

        var foreignReservation = facts.Reservations.FirstOrDefault(reservation =>
            IsActiveCustomerReservation(reservation)
            && (!context.TargetOrderId.HasValue || reservation.OrderId != context.TargetOrderId.Value)
            && (!context.SourceOrderId.HasValue || reservation.OrderId != context.SourceOrderId.Value)
            && !context.PermittedReservationOrderIds.Contains(reservation.OrderId));
        if (foreignReservation != null)
        {
            return Reject(
                HuMutationEligibilityReasonCode.HuReservedByOtherOrder,
                huCode,
                $"HU уже зарезервирована под другой клиентский заказ {foreignReservation.OrderRef}.");
        }

        var positiveStock = facts.Stock
            .Where(row => row.Qty > QtyTolerance)
            .ToArray();
        if (positiveStock.Length == 0)
        {
            if (context.Operation == HuMutationOperation.ReleaseProducedStock
                && IsSupportedProductionOnlyRelease(facts, context))
            {
                return HuMutationEligibilityDecision.Allow();
            }

            return Reject(
                HuMutationEligibilityReasonCode.HuNotInPhysicalStock,
                huCode,
                "По HU отсутствует положительный физический остаток в ledger.");
        }

        var actualByItem = positiveStock
            .GroupBy(row => row.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Qty));
        if (context.Operation == HuMutationOperation.ReserveOrBind && actualByItem.Count > 1)
        {
            return Reject(
                HuMutationEligibilityReasonCode.HuMixedNotSupported,
                huCode,
                "Текущая команда привязки относится к одной строке заказа и не может атомарно привязать mixed HU.");
        }

        var requestedByItem = context.RequestedComponents
            .GroupBy(row => row.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Qty));
        var exact = actualByItem.Keys.ToHashSet().SetEquals(requestedByItem.Keys)
                    && actualByItem.All(pair =>
                        Math.Abs(requestedByItem.GetValueOrDefault(pair.Key) - pair.Value) <= QtyTolerance);
        if (!exact)
        {
            var sameItems = actualByItem.Keys.ToHashSet().SetEquals(requestedByItem.Keys);
            var isStrictSubset = sameItems
                                 && actualByItem.All(pair =>
                                     requestedByItem.GetValueOrDefault(pair.Key) <= pair.Value + QtyTolerance)
                                 && actualByItem.Any(pair =>
                                     requestedByItem.GetValueOrDefault(pair.Key) + QtyTolerance < pair.Value);
            return Reject(
                isStrictSubset
                    ? HuMutationEligibilityReasonCode.HuPartialQuantityNotAllowed
                    : HuMutationEligibilityReasonCode.HuCompositionMismatch,
                huCode,
                isStrictSubset
                    ? "Физическая HU должна отгружаться целиком."
                    : "Запрошенный состав не совпадает с полным текущим составом HU.");
        }

        var requestedWithLocation = context.RequestedComponents
            .Where(component => component.LocationId.HasValue)
            .ToArray();
        if (requestedWithLocation.Length > 0)
        {
            var actualByItemLocation = positiveStock
                .GroupBy(row => (row.ItemId, row.LocationId))
                .ToDictionary(group => group.Key, group => group.Sum(row => row.Qty));
            var requestedByItemLocation = requestedWithLocation
                .GroupBy(row => (row.ItemId, LocationId: row.LocationId!.Value))
                .ToDictionary(group => group.Key, group => group.Sum(row => row.Qty));
            if (!actualByItemLocation.Keys.ToHashSet().SetEquals(requestedByItemLocation.Keys)
                || actualByItemLocation.Any(pair =>
                    Math.Abs(requestedByItemLocation.GetValueOrDefault(pair.Key) - pair.Value) > QtyTolerance))
            {
                return Reject(
                    HuMutationEligibilityReasonCode.HuCompositionMismatch,
                    huCode,
                    "Запрошенная location/composition не совпадает с текущим ledger-составом HU.");
            }
        }

        return HuMutationEligibilityDecision.Allow();
    }

    private static bool IsSupportedProductionOnlyRelease(
        HuOperatorFacts facts,
        HuMutationEligibilityContext context)
    {
        var activePallets = facts.ProductionPallets
            .Where(pallet => ProductionPalletStatus.IsOperational(pallet.Status))
            .ToArray();
        if (activePallets.Length != 1)
        {
            return false;
        }

        var pallet = activePallets[0];
        if (!string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)
            || (context.SourceOrderId.HasValue && pallet.OwnerOrderId != context.SourceOrderId)
            || pallet.Components.Count == 0
            || pallet.Components.Any(component =>
                component.PlannedQty <= QtyTolerance
                || Math.Abs(component.FilledQty - component.PlannedQty) > QtyTolerance))
        {
            return false;
        }

        if (facts.Outbound.Any(row => row.IsEffective && row.Qty > QtyTolerance)
            || facts.Reservations.Any(reservation =>
                reservation.Qty > QtyTolerance
                && (!context.SourceOrderId.HasValue || reservation.OrderId != context.SourceOrderId.Value)))
        {
            return false;
        }

        var productionByItem = pallet.Components
            .GroupBy(component => component.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(component => component.PlannedQty));
        var requestedByItem = context.RequestedComponents
            .GroupBy(component => component.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(component => component.Qty));
        return productionByItem.Keys.ToHashSet().SetEquals(requestedByItem.Keys)
               && productionByItem.All(pair =>
                   Math.Abs(requestedByItem.GetValueOrDefault(pair.Key) - pair.Value) <= QtyTolerance);
    }

    private static HuMutationEligibilityDecision Reject(string code, string huCode, string details) =>
        HuMutationEligibilityDecision.Reject(new HuMutationEligibilityReason(code, huCode, details));

    private static string NormalizeHu(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

    private static bool IsActiveCustomerReservation(HuOperatorReservationFact reservation) =>
        reservation.Qty > QtyTolerance
        && string.Equals(reservation.OrderType, "CUSTOMER", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(reservation.OrderStatus, "SHIPPED", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(reservation.OrderStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(reservation.OrderStatus, "MERGED", StringComparison.OrdinalIgnoreCase);
}
