using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using Moq;

namespace FlowStock.Server.Tests.Support;

internal static class HuMutationTestStore
{
    public static void Configure(
        Mock<IDataStore> store,
        Func<string, HuOperatorFacts?> factsFactory)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(factsFactory);

        store.Setup(candidate => candidate.LockOrdersForUpdate(It.IsAny<IReadOnlyCollection<long>>()))
            .Returns(true);
        var lockStore = store.As<IHuTransactionLockStore>();
        lockStore.Setup(candidate => candidate.LockNormalizedHus(It.IsAny<IReadOnlyCollection<string>>()));
        lockStore.Setup(candidate => candidate.LockDocumentsForUpdate(It.IsAny<IReadOnlyCollection<long>>()));

        var factsStore = store.As<IHuOperatorFactsStore>();
        factsStore.Setup(candidate => candidate.GetForHu(It.IsAny<string>()))
            .Returns<string>(factsFactory);
        factsStore.Setup(candidate => candidate.GetForOrder(It.IsAny<long>()))
            .Returns(Array.Empty<HuOperatorFacts>());
        factsStore.Setup(candidate => candidate.GetForHus(It.IsAny<IReadOnlyCollection<string>>()))
            .Returns<IReadOnlyCollection<string>>(huCodes => huCodes
                .Select(factsFactory)
                .Where(facts => facts != null)
                .Cast<HuOperatorFacts>()
                .ToArray());
    }

    public static HuOperatorFacts? BuildFacts(
        string huCode,
        IEnumerable<HuStockRow> stockRows,
        IReadOnlyDictionary<long, List<OrderReceiptPlanLine>> plans,
        IReadOnlyDictionary<long, Order> orders,
        IEnumerable<Doc> docs,
        IReadOnlyDictionary<long, IReadOnlyList<DocLine>> docLines)
    {
        var normalizedHu = NormalizeHu(huCode);
        if (normalizedHu == null)
        {
            return null;
        }

        var stock = stockRows
            .Where(row => string.Equals(NormalizeHu(row.HuCode), normalizedHu, StringComparison.Ordinal))
            .Select(row => new HuOperatorStockFact
            {
                ItemId = row.ItemId,
                ItemName = $"Товар {row.ItemId}",
                LocationId = row.LocationId,
                LocationCode = row.LocationId.ToString(),
                Qty = row.Qty
            })
            .ToArray();
        var reservations = plans
            .SelectMany(pair => pair.Value)
            .Where(line => string.Equals(NormalizeHu(line.ToHu), normalizedHu, StringComparison.Ordinal))
            .Select(line =>
            {
                orders.TryGetValue(line.OrderId, out var order);
                return new HuOperatorReservationFact
                {
                    OrderId = line.OrderId,
                    OrderRef = order?.OrderRef ?? line.OrderId.ToString(),
                    OrderType = order == null ? "CUSTOMER" : OrderStatusMapper.TypeToString(order.Type),
                    OrderStatus = order == null ? "IN_PROGRESS" : OrderStatusMapper.StatusToString(order.Status),
                    OrderLineId = line.OrderLineId,
                    ItemId = line.ItemId,
                    Qty = line.QtyPlanned
                };
            })
            .ToArray();
        var outbound = docs
            .Where(doc => doc.Type == DocType.Outbound)
            .SelectMany(doc => docLines.TryGetValue(doc.Id, out var lines)
                ? lines.Where(line => string.Equals(
                        NormalizeHu(line.FromHu ?? doc.ShippingRef),
                        normalizedHu,
                        StringComparison.Ordinal))
                    .Select(line =>
                    {
                        Order? order = null;
                        if (doc.OrderId.HasValue)
                        {
                            orders.TryGetValue(doc.OrderId.Value, out order);
                        }

                        return new HuOperatorOutboundFact
                        {
                            DocumentId = doc.Id,
                            DocumentRef = doc.DocRef,
                            DocumentStatus = DocTypeMapper.StatusToString(doc.Status),
                            OrderId = doc.OrderId,
                            OrderRef = order?.OrderRef,
                            OrderType = order == null ? null : OrderStatusMapper.TypeToString(order.Type),
                            OrderStatus = order == null ? null : OrderStatusMapper.StatusToString(order.Status),
                            OrderLineId = line.OrderLineId,
                            ItemId = line.ItemId,
                            ItemName = $"Товар {line.ItemId}",
                            Qty = line.Qty,
                            ClosedAt = doc.ClosedAt
                        };
                    })
                : Array.Empty<HuOperatorOutboundFact>())
            .ToArray();

        return new HuOperatorFacts
        {
            HuCode = normalizedHu,
            RegistryKnown = true,
            Stock = stock,
            Reservations = reservations,
            Outbound = outbound
        };
    }

    private static string? NormalizeHu(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
}
