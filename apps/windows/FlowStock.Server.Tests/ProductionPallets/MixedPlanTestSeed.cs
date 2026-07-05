using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.ProductionPallets;

/// <summary>
/// Test seeding for production pallet plans. Since the legacy <c>production_pallet_group</c>
/// planning path was removed (server <c>/plan</c> now rejects grouped lines and the explicit
/// constructor owns mixed pallets), tests that need an existing mixed/single pallet plan seed
/// it directly through the same store primitives the server uses: a DRAFT PRODUCTION_RECEIPT
/// with <c>doc_lines</c> sharing one <c>to_hu</c> per physical pallet, materialized by
/// <see cref="IDataStore.PlanProductionPallets"/> into <c>production_pallets</c>/
/// <c>production_pallet_lines</c>.
/// </summary>
internal static class MixedPlanTestSeed
{
    internal sealed record Component(long OrderLineId, long ItemId, double Qty);

    internal sealed record Pallet(string HuCode, IReadOnlyList<Component> Components);

    internal sealed record SeededPlan(long PrdDocId);

    public static Pallet Single(string huCode, long orderLineId, long itemId, double qty)
        => new(huCode, new[] { new Component(orderLineId, itemId, qty) });

    public static Pallet Mixed(string huCode, params Component[] components)
        => new(huCode, components);

    public static Component C(long orderLineId, long itemId, double qty)
        => new(orderLineId, itemId, qty);

    public static SeededPlan SeedPlan(
        CloseDocumentHarness harness,
        long prdDocId,
        long orderId,
        string orderRef,
        params Pallet[] pallets)
    {
        harness.SeedDoc(new Doc
        {
            Id = prdDocId,
            DocRef = $"PRD-SEED-{prdDocId}",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = orderId,
            OrderRef = orderRef,
            CreatedAt = DateTime.UtcNow
        });

        var lineId = prdDocId * 100 + 1;
        foreach (var pallet in pallets)
        {
            foreach (var component in pallet.Components)
            {
                harness.SeedLine(new DocLine
                {
                    Id = lineId++,
                    DocId = prdDocId,
                    OrderLineId = component.OrderLineId,
                    ItemId = component.ItemId,
                    Qty = component.Qty,
                    ToLocationId = 1,
                    ToHu = pallet.HuCode,
                    PackSingleHu = true
                });
            }
        }

        harness.Store.PlanProductionPallets(prdDocId, DateTime.UtcNow);
        return new SeededPlan(prdDocId);
    }
}
