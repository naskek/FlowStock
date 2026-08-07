using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class HuOperatorReadModelService
{
    private readonly IHuOperatorFactsStore _store;

    public HuOperatorReadModelService(IHuOperatorFactsStore store)
    {
        _store = store;
    }

    public IReadOnlyDictionary<long, OrderLineHuPresentation> GetForOrder(long orderId)
    {
        var builders = new Dictionary<long, OrderLinePresentationBuilder>();
        foreach (var facts in _store.GetForOrder(orderId))
        {
            var classification = HuOperatorClassifier.Classify(facts);
            if (classification is HuOperatorOperationalClassification operational)
            {
                AddOperationalRows(builders, orderId, facts, operational);
                continue;
            }

            if (classification is not HuOperatorProductionClassification production
                || string.Equals(production.StateCode, ProductionTaskSemanticCode.LabelNotPrinted, StringComparison.Ordinal))
            {
                continue;
            }

            var pallet = facts.ProductionPallets.SingleOrDefault(pallet =>
                ProductionPalletStatus.IsOperational(pallet.Status));
            if (pallet == null)
            {
                continue;
            }

            foreach (var componentGroup in pallet.Components
                         .Where(component => component.OrderLineOrderId == orderId && component.OrderLineId.HasValue)
                         .GroupBy(component => component.OrderLineId!.Value))
            {
                var components = componentGroup.ToArray();
                var row = new ProductionTaskPresentation
                {
                    HuCode = classification.HuCode,
                    Qty = components.Sum(component => component.PlannedQty),
                    Uom = CommonUom(components),
                    State = new HuSemanticStatePresentation(
                        production.StateCode,
                        ProductionLabel(production)),
                    Progress = production.CompletedComponents.HasValue && production.TotalComponents.HasValue
                        ? new HuProductionProgressPresentation(
                            production.CompletedComponents.Value,
                            production.TotalComponents.Value)
                        : null,
                    Components = ToProductionPresentationComponents(pallet.Components)
                };
                GetBuilder(builders, componentGroup.Key).ProductionTasks.Add(row);
            }
        }

        return builders.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Build());
    }

    public GlobalHuOperatorReadModel GetForHu(string huCode)
    {
        var normalizedHu = NormalizeHu(huCode);
        var facts = _store.GetForHu(normalizedHu);
        if (facts == null)
        {
            return new GlobalHuOperatorReadModel { HuCode = normalizedHu };
        }

        return ProjectGlobal(facts);
    }

    public static GlobalHuOperatorReadModel ProjectGlobal(HuOperatorFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var classification = HuOperatorClassifier.Classify(facts);
        ProductionTaskPresentation? productionTask = null;
        OperationalHuPresentation? operationalHu = null;
        if (classification is HuOperatorProductionClassification production
            && !string.Equals(production.StateCode, ProductionTaskSemanticCode.LabelNotPrinted, StringComparison.Ordinal))
        {
            var pallet = facts.ProductionPallets.SingleOrDefault(pallet =>
                ProductionPalletStatus.IsOperational(pallet.Status));
            if (pallet != null)
            {
                var uom = CommonUom(pallet.Components);
                productionTask = new ProductionTaskPresentation
                {
                    HuCode = classification.HuCode,
                    Qty = uom.Length == 0 ? 0 : pallet.Components.Sum(component => component.PlannedQty),
                    Uom = uom,
                    State = new HuSemanticStatePresentation(production.StateCode, ProductionLabel(production)),
                    Progress = production.CompletedComponents.HasValue && production.TotalComponents.HasValue
                        ? new HuProductionProgressPresentation(
                            production.CompletedComponents.Value,
                            production.TotalComponents.Value)
                        : null,
                    Components = ToProductionPresentationComponents(pallet.Components)
                };
            }
        }
        else if (classification is HuOperatorOperationalClassification operational)
        {
            operationalHu = BuildOperationalPresentation(
                facts,
                operational,
                currentOrderId: null,
                qty: null,
                uom: null,
                deriveQtyWhenMissing: true);
        }

        return new GlobalHuOperatorReadModel
        {
            Known = facts.RegistryKnown
                    || facts.Stock.Count > 0
                    || facts.ProductionPallets.Count > 0
                    || facts.Reservations.Count > 0
                    || facts.Outbound.Count > 0
                    || facts.LedgerMovements.Count > 0,
            HuCode = classification.HuCode,
            OperatorPresentation = new GlobalHuOperatorPresentation
            {
                ProductionTask = productionTask,
                OperationalHu = operationalHu
            },
            HistoryAvailable = facts.Stock.Count > 0
                               || facts.ProductionPallets.Count > 0
                               || facts.Reservations.Count > 0
                               || facts.Outbound.Count > 0
                               || facts.LedgerMovements.Count > 0
        };
    }

    public IReadOnlyList<ProductionTaskPresentation> GetProductionForOrder(long orderId)
    {
        var rows = new List<ProductionTaskPresentation>();
        foreach (var facts in _store.GetForOrder(orderId))
        {
            if (HuOperatorClassifier.Classify(facts) is not HuOperatorProductionClassification production)
            {
                continue;
            }

            var pallet = facts.ProductionPallets.SingleOrDefault(pallet =>
                ProductionPalletStatus.IsOperational(pallet.Status));
            if (pallet == null)
            {
                continue;
            }

            var uom = CommonUom(pallet.Components);
            rows.Add(new ProductionTaskPresentation
            {
                HuCode = production.HuCode,
                Qty = uom.Length == 0 ? 0 : pallet.Components.Sum(component => component.PlannedQty),
                Uom = uom,
                State = new HuSemanticStatePresentation(production.StateCode, ProductionLabel(production)),
                Progress = production.CompletedComponents.HasValue && production.TotalComponents.HasValue
                    ? new HuProductionProgressPresentation(
                        production.CompletedComponents.Value,
                        production.TotalComponents.Value)
                    : null,
                Components = ToProductionPresentationComponents(pallet.Components)
            });
        }

        return rows.OrderBy(row => row.HuCode, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddOperationalRows(
        IDictionary<long, OrderLinePresentationBuilder> builders,
        long orderId,
        HuOperatorFacts facts,
        HuOperatorOperationalClassification classification)
    {
        var lineQuantities = ResolveOrderLineQuantities(orderId, facts, classification);
        if (lineQuantities.Count == 0)
        {
            return;
        }

        foreach (var pair in lineQuantities)
        {
            GetBuilder(builders, pair.Key).OperationalHus.Add(
                BuildOperationalPresentation(
                    facts,
                    classification,
                    orderId,
                    pair.Value.Qty,
                    pair.Value.Uom,
                    deriveQtyWhenMissing: false));
        }
    }

    private static OperationalHuPresentation BuildOperationalPresentation(
        HuOperatorFacts facts,
        HuOperatorOperationalClassification classification,
        long? currentOrderId,
        double? qty,
        string? uom,
        bool deriveQtyWhenMissing)
    {
        var components = ResolveOperationalComponents(facts, classification);
        var commonUom = uom ?? CommonUom(components);
        var locations = facts.Stock
            .Where(row => row.Qty > StockQuantityRules.QtyTolerance)
            .GroupBy(row => row.LocationId)
            .Select(group => group.First())
            .Take(2)
            .ToArray();
        var location = locations.Length == 1
            ? new HuLocationPresentation(
                locations[0].LocationId,
                locations[0].LocationCode,
                locations[0].LocationName)
            : null;

        return new OperationalHuPresentation
        {
            HuCode = classification.HuCode,
            Qty = qty ?? (deriveQtyWhenMissing ? ScalarQty(components) : null),
            Uom = string.IsNullOrEmpty(commonUom) ? null : commonUom,
            State = new HuSemanticStatePresentation(
                classification.StateCode,
                OperationalLabel(classification, currentOrderId)),
            Components = components,
            Location = location,
            ReservationTarget = classification.ReservationTarget,
            ShipmentTarget = classification.ShipmentTarget,
            IsMixed = components.Select(component => component.ItemId).Distinct().Take(2).Count() > 1,
            Diagnostics = classification.DiagnosticReasons
        };
    }

    private static Dictionary<long, (double? Qty, string? Uom)> ResolveOrderLineQuantities(
        long orderId,
        HuOperatorFacts facts,
        HuOperatorOperationalClassification classification)
    {
        var stateCode = classification.StateCode;
        IEnumerable<(long LineId, double Qty, string Uom)> rows;
        if (string.Equals(stateCode, OperationalHuSemanticCode.Shipped, StringComparison.Ordinal))
        {
            var outboundRows = facts.Outbound
                .Where(row => row.IsEffective
                              && string.Equals(row.DocumentStatus, "CLOSED", StringComparison.OrdinalIgnoreCase)
                              && row.DocumentId == classification.CurrentShipmentDocumentId
                              && row.OrderId == orderId
                              && row.OrderLineId.HasValue)
                .Select(row => (row.OrderLineId!.Value, row.Qty, row.Uom))
                .ToArray();
            rows = outboundRows.Length > 0 ? outboundRows : ProductionRowsForOrder(facts, orderId);
        }
        else if (string.Equals(stateCode, OperationalHuSemanticCode.Reserved, StringComparison.Ordinal))
        {
            var reservationRows = facts.Reservations
                .Where(row => row.OrderId == orderId && row.OrderLineId.HasValue)
                .Select(row => (row.OrderLineId!.Value, row.Qty, ResolveUom(facts, row.ItemId)))
                .ToArray();
            rows = reservationRows.Length > 0 ? reservationRows : ProductionRowsForOrder(facts, orderId);
        }
        else if (string.Equals(stateCode, OperationalHuSemanticCode.Inconsistent, StringComparison.Ordinal))
        {
            rows = facts.Outbound
                .Where(row => row.IsEffective && row.OrderId == orderId && row.OrderLineId.HasValue)
                .Select(row => (row.OrderLineId!.Value, row.Qty, row.Uom))
                .Concat(facts.Reservations
                    .Where(row => row.OrderId == orderId && row.OrderLineId.HasValue)
                    .Select(row => (row.OrderLineId!.Value, row.Qty, ResolveUom(facts, row.ItemId))))
                .Concat(ProductionRowsForOrder(facts, orderId));
        }
        else
        {
            rows = ProductionRowsForOrder(facts, orderId);
        }

        return rows
            .GroupBy(row => row.LineId)
            .ToDictionary(
                group => group.Key,
                group => string.Equals(stateCode, OperationalHuSemanticCode.Inconsistent, StringComparison.Ordinal)
                    ? ((double?)null, (string?)CommonUom(group.Select(row => row.Uom)))
                    : ((double?)group.Sum(row => row.Qty), (string?)CommonUom(group.Select(row => row.Uom))));
    }

    private static IEnumerable<(long LineId, double Qty, string Uom)> ProductionRowsForOrder(
        HuOperatorFacts facts,
        long orderId) =>
        facts.ProductionPallets
            .SelectMany(pallet => pallet.Components)
            .Where(component => component.OrderLineOrderId == orderId && component.OrderLineId.HasValue)
            .Select(component => (component.OrderLineId!.Value, component.PlannedQty, component.Uom));

    private static IReadOnlyList<HuComponentPresentation> ResolveOperationalComponents(
        HuOperatorFacts facts,
        HuOperatorOperationalClassification classification)
    {
        var stateCode = classification.StateCode;
        var stockComponents = facts.Stock
            .Where(row => row.Qty > StockQuantityRules.QtyTolerance)
            .GroupBy(row => new { row.ItemId, row.ItemName, row.Uom })
            .Select(group => new HuComponentPresentation(
                group.Key.ItemId,
                group.Key.ItemName,
                group.Sum(row => row.Qty),
                group.Key.Uom))
            .ToArray();
        if (stockComponents.Length > 0)
        {
            return stockComponents;
        }

        if (string.Equals(stateCode, OperationalHuSemanticCode.Shipped, StringComparison.Ordinal)
            || string.Equals(stateCode, OperationalHuSemanticCode.Inconsistent, StringComparison.Ordinal))
        {
            return facts.Outbound
                .Where(row => row.IsEffective
                              && string.Equals(row.DocumentStatus, "CLOSED", StringComparison.OrdinalIgnoreCase)
                              && (!string.Equals(stateCode, OperationalHuSemanticCode.Shipped, StringComparison.Ordinal)
                                  || row.DocumentId == classification.CurrentShipmentDocumentId))
                .GroupBy(row => new { row.ItemId, row.ItemName, row.Uom })
                .Select(group => new HuComponentPresentation(
                    group.Key.ItemId,
                    group.Key.ItemName,
                    group.Sum(row => row.Qty),
                    group.Key.Uom))
                .ToArray();
        }

        return Array.Empty<HuComponentPresentation>();
    }

    private static IReadOnlyList<HuComponentPresentation> ToProductionPresentationComponents(
        IEnumerable<HuOperatorComponentFact> components) =>
        components
            .GroupBy(component => new { component.ItemId, component.ItemName, component.Uom })
            .Select(group => new HuComponentPresentation(
                group.Key.ItemId,
                group.Key.ItemName,
                group.Sum(component => component.PlannedQty),
                group.Key.Uom))
            .ToArray();

    private static double? ScalarQty(IReadOnlyCollection<HuComponentPresentation> components)
    {
        if (components.Select(component => component.ItemId).Distinct().Take(2).Count() != 1
            || string.IsNullOrEmpty(CommonUom(components)))
        {
            return null;
        }

        return components.Sum(component => component.Qty);
    }

    private static string ResolveUom(HuOperatorFacts facts, long itemId) =>
        facts.Stock.FirstOrDefault(row => row.ItemId == itemId)?.Uom
        ?? facts.ProductionPallets.SelectMany(pallet => pallet.Components).FirstOrDefault(row => row.ItemId == itemId)?.Uom
        ?? "шт";

    private static string OperationalLabel(
        HuOperatorOperationalClassification classification,
        long? currentOrderId) =>
        classification.StateCode switch
        {
            OperationalHuSemanticCode.AwaitingShipment => "Ожидает отгрузки",
            OperationalHuSemanticCode.Reserved when classification.ReservationTarget?.OrderId == currentOrderId =>
                "Зарезервирован",
            OperationalHuSemanticCode.Reserved when classification.ReservationTarget != null =>
                $"Зарезервирован для заказа {classification.ReservationTarget.OrderRef}",
            OperationalHuSemanticCode.Reserved => "Зарезервирован",
            OperationalHuSemanticCode.OnStock => "На складе",
            OperationalHuSemanticCode.Shipped => "Отгружен",
            OperationalHuSemanticCode.Inconsistent => "Несогласованное состояние",
            _ => "Требует проверки"
        };

    private static string ProductionLabel(HuOperatorProductionClassification classification) =>
        classification.StateCode switch
        {
            ProductionTaskSemanticCode.LabelNotPrinted => "Этикетка не напечатана",
            ProductionTaskSemanticCode.AwaitingFill => "Ожидает наполнения",
            ProductionTaskSemanticCode.Filling =>
                $"Наполняется: {classification.CompletedComponents} из {classification.TotalComponents} компонентов",
            ProductionTaskSemanticCode.ReleaseNotPosted => "Выпуск не проведён",
            _ => "Требует проверки"
        };

    private static string CommonUom(IReadOnlyCollection<HuOperatorComponentFact> components)
    {
        var uoms = components
            .Select(component => component.Uom)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        return uoms.Length == 1 ? uoms[0] : string.Empty;
    }

    private static string CommonUom(IReadOnlyCollection<HuComponentPresentation> components)
    {
        var uoms = components
            .Select(component => component.Uom)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        return uoms.Length == 1 ? uoms[0] : string.Empty;
    }

    private static string CommonUom(IEnumerable<string> values)
    {
        var uoms = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        return uoms.Length == 1 ? uoms[0] : string.Empty;
    }

    private static string NormalizeHu(string? huCode) =>
        string.IsNullOrWhiteSpace(huCode) ? string.Empty : huCode.Trim().ToUpperInvariant();

    private static OrderLinePresentationBuilder GetBuilder(
        IDictionary<long, OrderLinePresentationBuilder> builders,
        long orderLineId)
    {
        if (!builders.TryGetValue(orderLineId, out var builder))
        {
            builder = new OrderLinePresentationBuilder();
            builders[orderLineId] = builder;
        }

        return builder;
    }

    private sealed class OrderLinePresentationBuilder
    {
        public List<ProductionTaskPresentation> ProductionTasks { get; } = [];
        public List<OperationalHuPresentation> OperationalHus { get; } = [];

        public OrderLineHuPresentation Build() => new()
        {
            ProductionTasks = ProductionTasks
                .OrderBy(row => row.HuCode, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            OperationalHus = OperationalHus
                .OrderBy(row => row.HuCode, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }
}
