using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class HuOperatorClassifier
{
    public static HuOperatorClassification Classify(HuOperatorFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var huCode = NormalizeHu(facts.HuCode);
        var consistency = HuFactConsistencyAnalyzer.Analyze(facts);
        if (consistency.Issues.Count > 0)
        {
            return Inconsistent(
                huCode,
                consistency.Issues
                    .Select(issue => new HuOperatorDiagnosticReason(
                        DiagnosticCodeForIssue(issue.Code),
                        issue.Message,
                        issue.RelatedOrders,
                        issue.RelatedDocuments))
                    .ToArray());
        }

        var activePallets = facts.ProductionPallets
            .Where(pallet => ProductionPalletStatus.IsOperational(pallet.Status))
            .ToArray();
        var positiveStock = facts.Stock
            .Where(row => row.Qty > StockQuantityRules.QtyTolerance)
            .ToArray();
        var activeReservations = facts.Reservations
            .Where(IsActiveCustomerReservation)
            .ToArray();

        if (positiveStock.Length == 0
            && consistency.Shipment.Kind == HuShipmentFactKind.WholeHuShipped
            && consistency.Shipment.DocumentId.HasValue)
        {
            var shipmentTarget = facts.Outbound
                .Where(row => row.IsEffective
                              && row.DocumentId == consistency.Shipment.DocumentId.Value
                              && string.Equals(row.DocumentStatus, "CLOSED", StringComparison.OrdinalIgnoreCase)
                              && row.OrderId.HasValue)
                .Select(row => new HuOperatorOrderReference(
                    row.OrderId!.Value,
                    string.IsNullOrWhiteSpace(row.OrderRef) ? row.OrderId.Value.ToString() : row.OrderRef!))
                .Distinct()
                .Single();
            return new HuOperatorOperationalClassification(
                huCode,
                OperationalHuSemanticCode.Shipped,
                ShipmentTarget: shipmentTarget,
                CurrentShipmentDocumentId: consistency.Shipment.DocumentId);
        }

        if (positiveStock.Length > 0)
        {
            var reservationTarget = activeReservations
                .Select(reservation => new HuOperatorOrderReference(reservation.OrderId, reservation.OrderRef))
                .Distinct()
                .SingleOrDefault();
            if (reservationTarget != null)
            {
                return new HuOperatorOperationalClassification(
                    huCode,
                    OperationalHuSemanticCode.Reserved,
                    ReservationTarget: reservationTarget);
            }

            if (activePallets.Length == 1 && IsAwaitingShipmentOwner(activePallets[0]))
            {
                return new HuOperatorOperationalClassification(
                    huCode,
                    OperationalHuSemanticCode.AwaitingShipment);
            }

            return new HuOperatorOperationalClassification(huCode, OperationalHuSemanticCode.OnStock);
        }

        if (activePallets.Length == 1
            && (string.Equals(activePallets[0].Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase)
                || string.Equals(activePallets[0].Status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase)))
        {
            return new HuOperatorProductionClassification(huCode, ProductionTaskSemanticCode.AwaitingFill);
        }

        return new HuOperatorNoCurrentClassification(huCode);
    }

    private static string DiagnosticCodeForIssue(string issueCode) => issueCode switch
    {
        HuFactConsistencyIssueCode.NegativeLedgerBalance => HuOperatorDiagnosticCode.ProductionLedgerContradiction,
        HuFactConsistencyIssueCode.ProductionStatusFillMismatch => HuOperatorDiagnosticCode.ProductionLedgerContradiction,
        HuFactConsistencyIssueCode.FilledPalletCompositionIncomplete => HuOperatorDiagnosticCode.ProductionLedgerContradiction,
        HuFactConsistencyIssueCode.UnfinishedProductionWithLedger => HuOperatorDiagnosticCode.ProductionLedgerContradiction,
        HuFactConsistencyIssueCode.ActiveReservationWithoutStock => HuOperatorDiagnosticCode.ProductionLedgerContradiction,
        HuFactConsistencyIssueCode.ProductionLedgerCompositionMismatch => HuOperatorDiagnosticCode.ProductionLedgerContradiction,
        HuFactConsistencyIssueCode.MultipleActiveProductionPallets => HuOperatorDiagnosticCode.CorrectionLineageUncertain,
        HuFactConsistencyIssueCode.ShipmentLineageUncertain => HuOperatorDiagnosticCode.CorrectionLineageUncertain,
        HuFactConsistencyIssueCode.MultiplePositiveLocations => HuOperatorDiagnosticCode.MixedOperationalTargetConflict,
        HuFactConsistencyIssueCode.OperationalCompositionConflict => HuOperatorDiagnosticCode.MixedOperationalTargetConflict,
        _ => issueCode
    };

    private static HuOperatorOperationalClassification Inconsistent(
        string huCode,
        params HuOperatorDiagnosticReason[] reasons) =>
        new(huCode, OperationalHuSemanticCode.Inconsistent, reasons);

    private static string NormalizeHu(string? huCode) =>
        string.IsNullOrWhiteSpace(huCode) ? string.Empty : huCode.Trim().ToUpperInvariant();

    private static bool IsActiveCustomerReservation(HuOperatorReservationFact reservation) =>
        string.Equals(reservation.OrderType, "CUSTOMER", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(reservation.OrderStatus, "SHIPPED", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(reservation.OrderStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(reservation.OrderStatus, "MERGED", StringComparison.OrdinalIgnoreCase)
        && reservation.Qty > StockQuantityRules.QtyTolerance;

    private static bool IsAwaitingShipmentOwner(HuOperatorProductionPalletFact pallet) =>
        string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)
        && pallet.OwnerOrderId.HasValue
        && string.Equals(pallet.OwnerOrderType, "CUSTOMER", StringComparison.OrdinalIgnoreCase)
        && (string.Equals(pallet.OwnerOrderStatus, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(pallet.OwnerOrderStatus, "ACCEPTED", StringComparison.OrdinalIgnoreCase))
        && pallet.Components.Count > 0
        && pallet.Components.All(component =>
            component.OrderLineOrderId == pallet.OwnerOrderId
            && component.FilledQty + StockQuantityRules.QtyTolerance >= component.PlannedQty);
}
