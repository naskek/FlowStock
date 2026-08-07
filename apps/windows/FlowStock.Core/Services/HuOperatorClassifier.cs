using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class HuOperatorClassifier
{
    public static HuOperatorClassification Classify(HuOperatorFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var huCode = NormalizeHu(facts.HuCode);
        var activePallets = facts.ProductionPallets
            .Where(pallet => ProductionPalletStatus.IsOperational(pallet.Status))
            .ToArray();
        var positiveStock = facts.Stock
            .Where(row => row.Qty > StockQuantityRules.QtyTolerance)
            .ToArray();
        var closedOutbound = facts.Outbound
            .Where(row => row.IsEffective
                          && string.Equals(row.DocumentStatus, "CLOSED", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var activeReservations = facts.Reservations
            .Where(IsActiveCustomerReservation)
            .ToArray();

        if (facts.Stock.Any(row => row.Qty < -StockQuantityRules.QtyTolerance))
        {
            return Inconsistent(
                huCode,
                new HuOperatorDiagnosticReason(
                    HuOperatorDiagnosticCode.ProductionLedgerContradiction,
                    "По HU найден отрицательный ledger balance."));
        }

        if (activePallets.Length > 1)
        {
            return Inconsistent(
                huCode,
                new HuOperatorDiagnosticReason(
                    HuOperatorDiagnosticCode.CorrectionLineageUncertain,
                    "Для одного HU найдено несколько активных production pallets."));
        }

        var invalidProductionProgress = activePallets
            .SelectMany(pallet => pallet.Components.Select(component => (pallet, component)))
            .FirstOrDefault(pair =>
                pair.component.FilledQty > StockQuantityRules.QtyTolerance
                && pair.component.FilledQty + StockQuantityRules.QtyTolerance < pair.component.PlannedQty);
        if (invalidProductionProgress.component != null)
        {
            return Inconsistent(
                huCode,
                new HuOperatorDiagnosticReason(
                    HuOperatorDiagnosticCode.ProductionLedgerContradiction,
                    "Для production HU найден неподдерживаемый частичный прогресс компонента."));
        }

        var incompleteFilledPallet = activePallets.FirstOrDefault(pallet =>
            string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)
            && !IsCompleteFilledPallet(pallet));
        if (incompleteFilledPallet != null)
        {
            return Inconsistent(
                huCode,
                new HuOperatorDiagnosticReason(
                    HuOperatorDiagnosticCode.ProductionLedgerContradiction,
                    "Production pallet имеет статус FILLED, но не все компоненты заполнены полностью."));
        }

        if (positiveStock.Select(row => row.LocationId).Distinct().Take(2).Count() > 1)
        {
            return Inconsistent(
                huCode,
                new HuOperatorDiagnosticReason(
                    HuOperatorDiagnosticCode.MixedOperationalTargetConflict,
                    "Положительный остаток HU найден в нескольких локациях."));
        }

        if (positiveStock.Length > 0 && activePallets.Any(pallet => !IsCompleteFilledPallet(pallet)))
        {
            return Inconsistent(
                huCode,
                new HuOperatorDiagnosticReason(
                    HuOperatorDiagnosticCode.ProductionLedgerContradiction,
                    "По незавершённой production HU найден положительный ledger balance."));
        }

        var shipmentProof = AnalyzeShipment(facts, closedOutbound, positiveStock);
        if (positiveStock.Length == 0 && closedOutbound.Length > 0)
        {
            if (shipmentProof.Kind != ShipmentProofKind.WholeHuShipped)
            {
                return Inconsistent(
                    huCode,
                    new HuOperatorDiagnosticReason(
                        HuOperatorDiagnosticCode.CorrectionLineageUncertain,
                        shipmentProof.Message));
            }

            var filledPallet = activePallets.SingleOrDefault(IsCompleteFilledPallet);
            var currentShipmentRows = closedOutbound
                .Where(row => row.DocumentId == shipmentProof.DocumentId)
                .ToArray();
            if (filledPallet is { Components.Count: > 0 })
            {
                var plannedByItem = filledPallet.Components
                    .GroupBy(component => component.ItemId)
                    .ToDictionary(group => group.Key, group => group.Sum(component => component.PlannedQty));
                var shippedByItem = currentShipmentRows
                    .GroupBy(row => row.ItemId)
                    .ToDictionary(group => group.Key, group => group.Sum(row => row.Qty));
                if (!plannedByItem.Keys.ToHashSet().SetEquals(shippedByItem.Keys)
                    || plannedByItem.Any(pair =>
                        Math.Abs(shippedByItem.GetValueOrDefault(pair.Key) - pair.Value)
                        > StockQuantityRules.QtyTolerance))
                {
                    return Inconsistent(
                        huCode,
                        new HuOperatorDiagnosticReason(
                            HuOperatorDiagnosticCode.MixedOperationalTargetConflict,
                            "Не все компоненты HU подтверждены одной целой отгрузкой."));
                }
            }

            var shipmentTargets = currentShipmentRows
                .Where(row => row.OrderId.HasValue)
                .Select(row => new HuOperatorOrderReference(
                    row.OrderId!.Value,
                    string.IsNullOrWhiteSpace(row.OrderRef) ? row.OrderId.Value.ToString() : row.OrderRef!))
                .Distinct()
                .ToArray();
            if (shipmentTargets.Length == 1)
            {
                return new HuOperatorOperationalClassification(
                    huCode,
                    OperationalHuSemanticCode.Shipped,
                    ShipmentTarget: shipmentTargets[0],
                    CurrentShipmentDocumentId: shipmentProof.DocumentId);
            }


            return Inconsistent(
                huCode,
                new HuOperatorDiagnosticReason(
                    HuOperatorDiagnosticCode.MixedOperationalTargetConflict,
                    "Не удалось однозначно определить заказ целой отгрузки HU.",
                    shipmentTargets));
        }

        if (positiveStock.Length == 0 && activeReservations.Length > 0)
        {
            return Inconsistent(
                huCode,
                new HuOperatorDiagnosticReason(
                    HuOperatorDiagnosticCode.ProductionLedgerContradiction,
                    "Для активной резервации HU отсутствует положительный ledger balance."));
        }

        if (positiveStock.Length > 0)
        {
            if (closedOutbound.Length > 0)
            {
                if (shipmentProof.Kind == ShipmentProofKind.PartialHuWithRemainder)
                {
                    var remainingQty = positiveStock.Sum(row => row.Qty);
                    var currentShipmentRows = closedOutbound
                        .Where(row => !shipmentProof.DocumentId.HasValue
                                      || row.DocumentId == shipmentProof.DocumentId)
                        .ToArray();
                    var relatedOrders = currentShipmentRows
                        .Where(row => row.OrderId.HasValue)
                        .Select(row => new HuOperatorOrderReference(
                            row.OrderId!.Value,
                            string.IsNullOrWhiteSpace(row.OrderRef) ? row.OrderId.Value.ToString() : row.OrderRef!))
                        .Distinct()
                        .ToArray();
                    var relatedDocuments = currentShipmentRows
                        .Select(row => new HuOperatorDocumentReference(row.DocumentId, row.DocumentRef))
                        .Distinct()
                        .ToArray();
                    return Inconsistent(
                        huCode,
                        new HuOperatorDiagnosticReason(
                            HuOperatorDiagnosticCode.PartialClosedOutboundWithRemainder,
                            $"Проведена частичная отгрузка, но по HU осталось {FormatQty(remainingQty)} в ledger.",
                            relatedOrders,
                            relatedDocuments));
                }

                if (shipmentProof.Kind != ShipmentProofKind.WholeHuThenRestored)
                {
                    return Inconsistent(
                        huCode,
                        new HuOperatorDiagnosticReason(
                            HuOperatorDiagnosticCode.CorrectionLineageUncertain,
                            shipmentProof.Message));
                }
            }

            var reservationTargets = activeReservations
                .Select(reservation => new HuOperatorOrderReference(reservation.OrderId, reservation.OrderRef))
                .Distinct()
                .ToArray();
            if (reservationTargets.Length > 1)
            {
                return Inconsistent(
                    huCode,
                    new HuOperatorDiagnosticReason(
                        HuOperatorDiagnosticCode.ConflictingActiveReservations,
                        "HU одновременно зарезервирована для нескольких активных заказов.",
                        reservationTargets));
            }

            if (reservationTargets.Length == 1)
            {
                if (!SameQuantitiesByItem(
                        positiveStock.Select(row => (row.ItemId, row.Qty)),
                        activeReservations.Select(row => (row.ItemId, row.Qty))))
                {
                    return Inconsistent(
                        huCode,
                        new HuOperatorDiagnosticReason(
                            HuOperatorDiagnosticCode.MixedOperationalTargetConflict,
                            "Компоненты mixed HU имеют разные operational target facts."));
                }

                return new HuOperatorOperationalClassification(
                    huCode,
                    OperationalHuSemanticCode.Reserved,
                    ReservationTarget: reservationTargets[0]);
            }

            if (activePallets.Length == 1 && IsAwaitingShipmentOwner(activePallets[0]))
            {
                if (!SameQuantitiesByItem(
                        positiveStock.Select(row => (row.ItemId, row.Qty)),
                        activePallets[0].Components.Select(component => (component.ItemId, component.PlannedQty))))
                {
                    return Inconsistent(
                        huCode,
                        new HuOperatorDiagnosticReason(
                            HuOperatorDiagnosticCode.ProductionLedgerContradiction,
                            "Ledger balance не совпадает с полным составом готовой CUSTOMER production HU."));
                }

                return new HuOperatorOperationalClassification(
                    huCode,
                    OperationalHuSemanticCode.AwaitingShipment);
            }

            return new HuOperatorOperationalClassification(
                huCode,
                OperationalHuSemanticCode.OnStock);
        }

        if (activePallets.Length == 1)
        {
            var components = activePallets[0].Components;
            var completedComponents = components.Count(component =>
                component.FilledQty + StockQuantityRules.QtyTolerance >= component.PlannedQty);
            if (components.Count > 1 && completedComponents > 0 && completedComponents < components.Count)
            {
                return new HuOperatorProductionClassification(
                    huCode,
                    ProductionTaskSemanticCode.Filling,
                    completedComponents,
                    components.Count);
            }
        }

        if (activePallets.Length == 1
            && string.Equals(activePallets[0].Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase))
        {
            return new HuOperatorProductionClassification(
                huCode,
                ProductionTaskSemanticCode.LabelNotPrinted);
        }

        if (activePallets.Length == 1
            && string.Equals(activePallets[0].Status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase))
        {
            return new HuOperatorProductionClassification(
                huCode,
                ProductionTaskSemanticCode.AwaitingFill);
        }

        if (activePallets.Length == 1
            && string.Equals(activePallets[0].Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
        {
            return new HuOperatorProductionClassification(
                huCode,
                ProductionTaskSemanticCode.ReleaseNotPosted);
        }

        return new HuOperatorNoCurrentClassification(huCode);
    }

    private static ShipmentProof AnalyzeShipment(
        HuOperatorFacts facts,
        IReadOnlyCollection<HuOperatorOutboundFact> closedOutbound,
        IReadOnlyCollection<HuOperatorStockFact> positiveStock)
    {
        if (closedOutbound.Count == 0)
        {
            return new ShipmentProof(ShipmentProofKind.None, string.Empty);
        }

        var orderedMovements = facts.LedgerMovements.OrderBy(row => row.LedgerId).ToArray();
        if (orderedMovements.Length == 0)
        {
            return new ShipmentProof(
                ShipmentProofKind.Uncertain,
                "Для CLOSED OUTBOUND отсутствует достаточная ledger history HU.");
        }

        if (!CurrentLedgerMatchesFacts(orderedMovements, facts.Stock))
        {
            return new ShipmentProof(
                ShipmentProofKind.Uncertain,
                "Текущий ledger balance не согласуется с загруженной ledger history HU.");
        }

        var outboundByDocument = closedOutbound
            .GroupBy(row => row.DocumentId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var processedOutboundDocuments = new HashSet<long>();
        var balanceByItem = new Dictionary<long, double>();
        long? currentShipmentDocumentId = null;
        var hasRestoredShipmentHistory = false;

        foreach (var documentMovements in orderedMovements
                     .GroupBy(row => row.DocumentId)
                     .OrderBy(group => group.Min(row => row.LedgerId)))
        {
            var movements = documentMovements.OrderBy(row => row.LedgerId).ToArray();
            var documentTypes = movements
                .Select(row => row.DocumentType.Trim().ToUpperInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (documentTypes.Length != 1)
            {
                return new ShipmentProof(
                    ShipmentProofKind.Uncertain,
                    "Один ledger document содержит движения HU разных типов.");
            }

            var documentType = documentTypes[0];
            if (string.Equals(documentType, "OUTBOUND", StringComparison.Ordinal))
            {
                if (currentShipmentDocumentId.HasValue
                    || !outboundByDocument.TryGetValue(documentMovements.Key, out var shipmentRows)
                    || movements.Any(row =>
                        !string.Equals(row.DocumentStatus, "CLOSED", StringComparison.OrdinalIgnoreCase)
                        || row.QtyDelta >= -StockQuantityRules.QtyTolerance))
                {
                    return new ShipmentProof(
                        ShipmentProofKind.Uncertain,
                        "Ledger history не подтверждает однозначную effective CLOSED OUTBOUND epoch HU.");
                }

                processedOutboundDocuments.Add(documentMovements.Key);
                var expectedShipment = shipmentRows
                    .GroupBy(row => row.ItemId)
                    .ToDictionary(group => group.Key, group => group.Sum(row => row.Qty));
                var postedShipment = movements
                    .GroupBy(row => row.ItemId)
                    .ToDictionary(group => group.Key, group => -group.Sum(row => row.QtyDelta));
                if (!SameQuantitiesByItem(expectedShipment, postedShipment))
                {
                    return new ShipmentProof(
                        ShipmentProofKind.Uncertain,
                        "OUTBOUND lines не совпадают с фактически проведёнными ledger movements HU.");
                }

                var positiveBeforeShipment = balanceByItem
                    .Where(pair => pair.Value > StockQuantityRules.QtyTolerance)
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                if (!expectedShipment.Keys.ToHashSet().SetEquals(positiveBeforeShipment.Keys))
                {
                    return new ShipmentProof(
                        ShipmentProofKind.Uncertain,
                        "Состав HU непосредственно перед OUTBOUND нельзя доказать однозначно.");
                }

                if (positiveBeforeShipment.Any(pair =>
                        pair.Value > expectedShipment.GetValueOrDefault(pair.Key) + StockQuantityRules.QtyTolerance))
                {
                    return new ShipmentProof(
                        ShipmentProofKind.PartialHuWithRemainder,
                        "CLOSED OUTBOUND провёл только часть текущего состава HU.",
                        documentMovements.Key);
                }

                if (!SameQuantitiesByItem(expectedShipment, positiveBeforeShipment))
                {
                    return new ShipmentProof(
                        ShipmentProofKind.Uncertain,
                        "Количество OUTBOUND не совпадает с полным балансом HU перед проведением.");
                }

                ApplyMovements(balanceByItem, movements);
                currentShipmentDocumentId = documentMovements.Key;
                continue;
            }

            if (currentShipmentDocumentId.HasValue)
            {
                var isProvenRestoration = string.Equals(
                                              documentType,
                                              "INVENTORY_CORRECTION",
                                              StringComparison.Ordinal)
                                          && movements.All(row =>
                                              string.Equals(row.DocumentStatus, "CLOSED", StringComparison.OrdinalIgnoreCase)
                                              && row.QtyDelta > StockQuantityRules.QtyTolerance)
                                          && balanceByItem.Values.All(value =>
                                              Math.Abs(value) <= StockQuantityRules.QtyTolerance);
                if (!isProvenRestoration)
                {
                    return new ShipmentProof(
                        ShipmentProofKind.Uncertain,
                        "После whole-HU OUTBOUND новый lifecycle не подтверждён CLOSED INVENTORY_CORRECTION.");
                }

                ApplyMovements(balanceByItem, movements);
                currentShipmentDocumentId = null;
                hasRestoredShipmentHistory = true;
                continue;
            }

            ApplyMovements(balanceByItem, movements);
        }

        if (!outboundByDocument.Keys.ToHashSet().SetEquals(processedOutboundDocuments))
        {
            return new ShipmentProof(
                ShipmentProofKind.Uncertain,
                "Не для всех effective CLOSED OUTBOUND найдены соответствующие ledger movements HU.");
        }

        if (currentShipmentDocumentId.HasValue)
        {
            return positiveStock.Count == 0
                ? new ShipmentProof(
                    ShipmentProofKind.WholeHuShipped,
                    string.Empty,
                    currentShipmentDocumentId)
                : new ShipmentProof(
                    ShipmentProofKind.Uncertain,
                    "После текущей whole-HU OUTBOUND остался положительный ledger balance.");
        }

        if (hasRestoredShipmentHistory && positiveStock.Count > 0)
        {
            return new ShipmentProof(ShipmentProofKind.WholeHuThenRestored, string.Empty);
        }

        return new ShipmentProof(
            ShipmentProofKind.Uncertain,
            "Текущий shipment lifecycle HU нельзя доказать однозначно.");
    }

    private static void ApplyMovements(
        IDictionary<long, double> balanceByItem,
        IEnumerable<HuOperatorLedgerMovementFact> movements)
    {
        foreach (var movement in movements)
        {
            balanceByItem.TryGetValue(movement.ItemId, out var current);
            balanceByItem[movement.ItemId] =
                current + movement.QtyDelta;
        }
    }

    private static bool CurrentLedgerMatchesFacts(
        IEnumerable<HuOperatorLedgerMovementFact> movements,
        IEnumerable<HuOperatorStockFact> stock)
    {
        var movementBalances = movements
            .GroupBy(row => (row.ItemId, row.LocationId))
            .ToDictionary(group => group.Key, group => group.Sum(row => row.QtyDelta));
        var stockBalances = stock
            .GroupBy(row => (row.ItemId, row.LocationId))
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Qty));
        var keys = movementBalances.Keys.Concat(stockBalances.Keys).Distinct();
        return keys.All(key =>
            Math.Abs(movementBalances.GetValueOrDefault(key) - stockBalances.GetValueOrDefault(key))
            <= StockQuantityRules.QtyTolerance);
    }

    private static bool SameQuantitiesByItem(
        IReadOnlyDictionary<long, double> left,
        IReadOnlyDictionary<long, double> right) =>
        left.Keys.ToHashSet().SetEquals(right.Keys)
        && left.All(pair =>
            Math.Abs(right.GetValueOrDefault(pair.Key) - pair.Value) <= StockQuantityRules.QtyTolerance);

    private enum ShipmentProofKind
    {
        None,
        WholeHuShipped,
        WholeHuThenRestored,
        PartialHuWithRemainder,
        Uncertain
    }

    private sealed record ShipmentProof(
        ShipmentProofKind Kind,
        string Message,
        long? DocumentId = null);

    private static string NormalizeHu(string? huCode) =>
        string.IsNullOrWhiteSpace(huCode) ? string.Empty : huCode.Trim().ToUpperInvariant();

    private static HuOperatorOperationalClassification Inconsistent(
        string huCode,
        params HuOperatorDiagnosticReason[] reasons) =>
        new(huCode, OperationalHuSemanticCode.Inconsistent, reasons);

    private static string FormatQty(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static bool IsActiveCustomerReservation(HuOperatorReservationFact reservation) =>
        string.Equals(reservation.OrderType, "CUSTOMER", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(reservation.OrderStatus, "SHIPPED", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(reservation.OrderStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(reservation.OrderStatus, "MERGED", StringComparison.OrdinalIgnoreCase)
        && reservation.Qty > StockQuantityRules.QtyTolerance;

    private static bool IsAwaitingShipmentOwner(HuOperatorProductionPalletFact pallet) =>
        IsCompleteFilledPallet(pallet)
        && pallet.OwnerOrderId.HasValue
        && string.Equals(pallet.OwnerOrderType, "CUSTOMER", StringComparison.OrdinalIgnoreCase)
        && (string.Equals(pallet.OwnerOrderStatus, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(pallet.OwnerOrderStatus, "ACCEPTED", StringComparison.OrdinalIgnoreCase))
        && pallet.Components.Count > 0
        && pallet.Components.All(component =>
            component.OrderLineOrderId == pallet.OwnerOrderId
            && component.FilledQty + StockQuantityRules.QtyTolerance >= component.PlannedQty);

    private static bool IsCompleteFilledPallet(HuOperatorProductionPalletFact pallet) =>
        string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)
        && (pallet.Components.Count == 0
            || pallet.Components.All(component =>
                component.FilledQty + StockQuantityRules.QtyTolerance >= component.PlannedQty));

    private static bool SameQuantitiesByItem(
        IEnumerable<(long ItemId, double Qty)> left,
        IEnumerable<(long ItemId, double Qty)> right)
    {
        var leftByItem = left
            .GroupBy(row => row.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Qty));
        var rightByItem = right
            .GroupBy(row => row.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Qty));
        return leftByItem.Keys.ToHashSet().SetEquals(rightByItem.Keys)
               && leftByItem.All(pair =>
                   Math.Abs(pair.Value - rightByItem.GetValueOrDefault(pair.Key))
                   <= StockQuantityRules.QtyTolerance);
    }
}
