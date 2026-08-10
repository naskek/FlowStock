using System.Globalization;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class HuFactConsistencyAnalyzer
{
    public static HuFactConsistencyAnalysis Analyze(HuOperatorFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var issues = new List<HuFactConsistencyIssue>();
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
            Add(issues, HuFactConsistencyIssueCode.NegativeLedgerBalance,
                "По HU найден отрицательный ledger balance.");
        }

        if (activePallets.Length > 1)
        {
            Add(issues, HuFactConsistencyIssueCode.MultipleActiveProductionPallets,
                "Для одного HU найдено несколько активных production pallets.");
        }

        foreach (var pallet in activePallets)
        {
            if ((string.Equals(pallet.Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(pallet.Status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase))
                && pallet.Components.Any(component => component.FilledQty > StockQuantityRules.QtyTolerance))
            {
                Add(issues, HuFactConsistencyIssueCode.ProductionStatusFillMismatch,
                    "Production pallet имеет статус до наполнения, но component fill facts уже сохранены.");
            }

            var completed = pallet.Components.Count(component =>
                component.PlannedQty > StockQuantityRules.QtyTolerance
                && component.FilledQty + StockQuantityRules.QtyTolerance >= component.PlannedQty);
            var hasPartialQuantity = pallet.Components.Any(component =>
                component.FilledQty > StockQuantityRules.QtyTolerance
                && component.FilledQty + StockQuantityRules.QtyTolerance < component.PlannedQty);
            if (hasPartialQuantity || completed > 0 && completed < pallet.Components.Count)
            {
                Add(issues, HuFactConsistencyIssueCode.PartialComponentFill,
                    "Для production HU найдено частичное наполнение component composition.");
            }

            if (string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)
                && !IsCompleteFilledPallet(pallet))
            {
                Add(issues, HuFactConsistencyIssueCode.FilledPalletCompositionIncomplete,
                    "Production pallet имеет статус FILLED, но не все компоненты заполнены полностью.");
            }
        }

        if (positiveStock.Select(row => row.LocationId).Distinct().Take(2).Count() > 1)
        {
            Add(issues, HuFactConsistencyIssueCode.MultiplePositiveLocations,
                "Положительный остаток HU найден в нескольких локациях.");
        }

        if (positiveStock.Length > 0 && activePallets.Any(pallet => !IsCompleteFilledPallet(pallet)))
        {
            Add(issues, HuFactConsistencyIssueCode.UnfinishedProductionWithLedger,
                "По незавершённой production HU найден положительный ledger balance.");
        }

        var shipment = AnalyzeShipment(facts, closedOutbound, positiveStock);
        if (positiveStock.Length == 0 && closedOutbound.Length > 0)
        {
            if (shipment.Kind != HuShipmentFactKind.WholeHuShipped)
            {
                Add(issues, HuFactConsistencyIssueCode.ShipmentLineageUncertain, shipment.Message);
            }
            else
            {
                var filledPallet = activePallets.Length == 1 && IsCompleteFilledPallet(activePallets[0])
                    ? activePallets[0]
                    : null;
                var currentShipmentRows = closedOutbound
                    .Where(row => row.DocumentId == shipment.DocumentId)
                    .ToArray();
                if (filledPallet is { Components.Count: > 0 }
                    && !SameQuantitiesByItem(
                        filledPallet.Components.Select(component => (component.ItemId, component.PlannedQty)),
                        currentShipmentRows.Select(row => (row.ItemId, row.Qty))))
                {
                    Add(issues, HuFactConsistencyIssueCode.OperationalCompositionConflict,
                        "Не все компоненты HU подтверждены одной целой отгрузкой.");
                }

                var shipmentTargets = currentShipmentRows
                    .Where(row => row.OrderId.HasValue)
                    .Select(row => new HuOperatorOrderReference(
                        row.OrderId!.Value,
                        string.IsNullOrWhiteSpace(row.OrderRef) ? row.OrderId.Value.ToString() : row.OrderRef!))
                    .Distinct()
                    .ToArray();
                if (shipmentTargets.Length != 1)
                {
                    Add(issues, HuFactConsistencyIssueCode.OperationalCompositionConflict,
                        "Не удалось однозначно определить заказ целой отгрузки HU.", shipmentTargets);
                }
            }
        }

        if (positiveStock.Length == 0
            && activeReservations.Length > 0
            && !ReservationsMatchCurrentWholeHuShipment(activeReservations, closedOutbound, shipment))
        {
            Add(issues, HuFactConsistencyIssueCode.ActiveReservationWithoutStock,
                "Для активной резервации HU отсутствует положительный ledger balance.");
        }

        if (positiveStock.Length > 0)
        {
            if (closedOutbound.Length > 0)
            {
                if (shipment.Kind == HuShipmentFactKind.PartialHuWithRemainder)
                {
                    var currentShipmentRows = closedOutbound
                        .Where(row => !shipment.DocumentId.HasValue || row.DocumentId == shipment.DocumentId)
                        .ToArray();
                    Add(
                        issues,
                        HuFactConsistencyIssueCode.PartialClosedOutboundWithRemainder,
                        $"Проведена частичная отгрузка, но по HU осталось {FormatQty(positiveStock.Sum(row => row.Qty))} в ledger.",
                        currentShipmentRows
                            .Where(row => row.OrderId.HasValue)
                            .Select(row => new HuOperatorOrderReference(
                                row.OrderId!.Value,
                                string.IsNullOrWhiteSpace(row.OrderRef) ? row.OrderId.Value.ToString() : row.OrderRef!))
                            .Distinct()
                            .ToArray(),
                        currentShipmentRows
                            .Select(row => new HuOperatorDocumentReference(row.DocumentId, row.DocumentRef))
                            .Distinct()
                            .ToArray());
                }
                else if (shipment.Kind != HuShipmentFactKind.WholeHuThenRestored)
                {
                    Add(issues, HuFactConsistencyIssueCode.ShipmentLineageUncertain, shipment.Message);
                }
            }

            var reservationTargets = activeReservations
                .Select(reservation => new HuOperatorOrderReference(reservation.OrderId, reservation.OrderRef))
                .Distinct()
                .ToArray();
            if (reservationTargets.Length > 1)
            {
                Add(issues, HuFactConsistencyIssueCode.ConflictingActiveReservations,
                    "HU одновременно зарезервирована для нескольких активных заказов.", reservationTargets);
            }
            else if (reservationTargets.Length == 1
                     && !SameQuantitiesByItem(
                         positiveStock.Select(row => (row.ItemId, row.Qty)),
                         activeReservations.Select(row => (row.ItemId, row.Qty))))
            {
                Add(issues, HuFactConsistencyIssueCode.OperationalCompositionConflict,
                    "Компоненты mixed HU имеют разные operational target facts.");
            }

            if (activePallets.Length == 1
                && IsAwaitingShipmentOwner(activePallets[0])
                && !SameQuantitiesByItem(
                    positiveStock.Select(row => (row.ItemId, row.Qty)),
                    activePallets[0].Components.Select(component => (component.ItemId, component.PlannedQty))))
            {
                Add(issues, HuFactConsistencyIssueCode.ProductionLedgerCompositionMismatch,
                    "Ledger balance не совпадает с полным составом готовой CUSTOMER production HU.");
            }
        }

        if (positiveStock.Length == 0
            && closedOutbound.Length == 0
            && activePallets.Any(pallet =>
                string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)))
        {
            Add(issues, HuFactConsistencyIssueCode.FilledWithoutLedgerStock,
                "Production pallet имеет статус FILLED, но physical ledger stock отсутствует.");
        }

        return new HuFactConsistencyAnalysis
        {
            Issues = issues,
            Shipment = shipment
        };
    }

    private static HuShipmentFactConclusion AnalyzeShipment(
        HuOperatorFacts facts,
        IReadOnlyCollection<HuOperatorOutboundFact> closedOutbound,
        IReadOnlyCollection<HuOperatorStockFact> positiveStock)
    {
        if (closedOutbound.Count == 0)
        {
            return new HuShipmentFactConclusion(HuShipmentFactKind.None, string.Empty);
        }

        var orderedMovements = facts.LedgerMovements.OrderBy(row => row.LedgerId).ToArray();
        if (orderedMovements.Length == 0)
        {
            return new HuShipmentFactConclusion(
                HuShipmentFactKind.Uncertain,
                "Для CLOSED OUTBOUND отсутствует достаточная ledger history HU.");
        }

        if (!CurrentLedgerMatchesFacts(orderedMovements, facts.Stock))
        {
            return new HuShipmentFactConclusion(
                HuShipmentFactKind.Uncertain,
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
                return Uncertain("Один ledger document содержит движения HU разных типов.");
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
                    return Uncertain("Ledger history не подтверждает однозначную effective CLOSED OUTBOUND epoch HU.");
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
                    return Uncertain("OUTBOUND lines не совпадают с фактически проведёнными ledger movements HU.");
                }

                var positiveBeforeShipment = balanceByItem
                    .Where(pair => pair.Value > StockQuantityRules.QtyTolerance)
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                if (!expectedShipment.Keys.ToHashSet().SetEquals(positiveBeforeShipment.Keys))
                {
                    return Uncertain("Состав HU непосредственно перед OUTBOUND нельзя доказать однозначно.");
                }

                if (positiveBeforeShipment.Any(pair =>
                        pair.Value > expectedShipment.GetValueOrDefault(pair.Key) + StockQuantityRules.QtyTolerance))
                {
                    return new HuShipmentFactConclusion(
                        HuShipmentFactKind.PartialHuWithRemainder,
                        "CLOSED OUTBOUND провёл только часть текущего состава HU.",
                        documentMovements.Key);
                }

                if (!SameQuantitiesByItem(expectedShipment, positiveBeforeShipment))
                {
                    return Uncertain("Количество OUTBOUND не совпадает с полным балансом HU перед проведением.");
                }

                ApplyMovements(balanceByItem, movements);
                currentShipmentDocumentId = documentMovements.Key;
                continue;
            }

            if (currentShipmentDocumentId.HasValue)
            {
                var isProvenRestoration = string.Equals(documentType, "INVENTORY_CORRECTION", StringComparison.Ordinal)
                                          && movements.All(row =>
                                              string.Equals(row.DocumentStatus, "CLOSED", StringComparison.OrdinalIgnoreCase)
                                              && row.QtyDelta > StockQuantityRules.QtyTolerance)
                                          && balanceByItem.Values.All(value =>
                                              Math.Abs(value) <= StockQuantityRules.QtyTolerance);
                if (!isProvenRestoration)
                {
                    return Uncertain("После whole-HU OUTBOUND новый lifecycle не подтверждён CLOSED INVENTORY_CORRECTION.");
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
            return Uncertain("Не для всех effective CLOSED OUTBOUND найдены соответствующие ledger movements HU.");
        }

        if (currentShipmentDocumentId.HasValue)
        {
            return positiveStock.Count == 0
                ? new HuShipmentFactConclusion(
                    HuShipmentFactKind.WholeHuShipped,
                    string.Empty,
                    currentShipmentDocumentId)
                : Uncertain("После текущей whole-HU OUTBOUND остался положительный ledger balance.");
        }

        if (hasRestoredShipmentHistory && positiveStock.Count > 0)
        {
            return new HuShipmentFactConclusion(HuShipmentFactKind.WholeHuThenRestored, string.Empty);
        }

        return Uncertain("Текущий shipment lifecycle HU нельзя доказать однозначно.");
    }

    private static HuShipmentFactConclusion Uncertain(string message) =>
        new(HuShipmentFactKind.Uncertain, message);

    private static bool ReservationsMatchCurrentWholeHuShipment(
        IReadOnlyCollection<HuOperatorReservationFact> reservations,
        IReadOnlyCollection<HuOperatorOutboundFact> closedOutbound,
        HuShipmentFactConclusion shipment)
    {
        if (shipment.Kind != HuShipmentFactKind.WholeHuShipped || !shipment.DocumentId.HasValue)
        {
            return false;
        }

        var shipmentRows = closedOutbound
            .Where(row => row.DocumentId == shipment.DocumentId.Value)
            .ToArray();
        if (reservations.Any(row => !row.OrderLineId.HasValue)
            || shipmentRows.Length == 0
            || shipmentRows.Any(row => !row.OrderId.HasValue || !row.OrderLineId.HasValue))
        {
            return false;
        }

        var reservedByTarget = reservations
            .GroupBy(row => (row.OrderId, OrderLineId: row.OrderLineId!.Value, row.ItemId))
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Qty));
        var shippedByTarget = shipmentRows
            .GroupBy(row => (OrderId: row.OrderId!.Value, OrderLineId: row.OrderLineId!.Value, row.ItemId))
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Qty));

        return SameQuantitiesByTarget(reservedByTarget, shippedByTarget);
    }

    private static bool SameQuantitiesByTarget(
        IReadOnlyDictionary<(long OrderId, long OrderLineId, long ItemId), double> left,
        IReadOnlyDictionary<(long OrderId, long OrderLineId, long ItemId), double> right) =>
        left.Keys.ToHashSet().SetEquals(right.Keys)
        && left.All(pair =>
            Math.Abs(right.GetValueOrDefault(pair.Key) - pair.Value) <= StockQuantityRules.QtyTolerance);

    private static void ApplyMovements(
        IDictionary<long, double> balanceByItem,
        IEnumerable<HuOperatorLedgerMovementFact> movements)
    {
        foreach (var movement in movements)
        {
            balanceByItem.TryGetValue(movement.ItemId, out var current);
            balanceByItem[movement.ItemId] = current + movement.QtyDelta;
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
        return movementBalances.Keys.Concat(stockBalances.Keys).Distinct().All(key =>
            Math.Abs(movementBalances.GetValueOrDefault(key) - stockBalances.GetValueOrDefault(key))
            <= StockQuantityRules.QtyTolerance);
    }

    private static bool SameQuantitiesByItem(
        IReadOnlyDictionary<long, double> left,
        IReadOnlyDictionary<long, double> right) =>
        left.Keys.ToHashSet().SetEquals(right.Keys)
        && left.All(pair =>
            Math.Abs(right.GetValueOrDefault(pair.Key) - pair.Value) <= StockQuantityRules.QtyTolerance);

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
        return SameQuantitiesByItem(leftByItem, rightByItem);
    }

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

    private static void Add(
        ICollection<HuFactConsistencyIssue> issues,
        string code,
        string message,
        IReadOnlyList<HuOperatorOrderReference>? relatedOrders = null,
        IReadOnlyList<HuOperatorDocumentReference>? relatedDocuments = null)
    {
        if (issues.Any(issue => string.Equals(issue.Code, code, StringComparison.Ordinal)
                                && string.Equals(issue.Message, message, StringComparison.Ordinal)))
        {
            return;
        }

        issues.Add(new HuFactConsistencyIssue(code, message, relatedOrders, relatedDocuments));
    }

    private static string FormatQty(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
