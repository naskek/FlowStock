using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Services;

public static class InternalOrderRedistributionGuard
{
    public static InternalOrderRedistributionGuardResult Evaluate(IDataStore store, long sourceInternalOrderId)
    {
        var sourceOrder = store.GetOrder(sourceInternalOrderId);
        var sourceOrderRef = sourceOrder?.OrderRef ?? sourceInternalOrderId.ToString();
        var draftPrdDocs = new List<InternalOrderRedistributionGuardPrdRow>();
        var prdDocsWithLedger = new List<InternalOrderRedistributionGuardPrdRow>();
        var activePallets = new List<InternalOrderRedistributionGuardPalletRow>();
        var seenPrdLedger = new HashSet<long>();

        foreach (var doc in store.GetDocsByOrder(sourceInternalOrderId)
                     .Where(doc => doc.Type == DocType.ProductionReceipt))
        {
            if (doc.Status == DocStatus.Draft)
            {
                draftPrdDocs.Add(new InternalOrderRedistributionGuardPrdRow
                {
                    DocId = doc.Id,
                    DocRef = doc.DocRef,
                    Status = DocTypeMapper.StatusToString(doc.Status)
                });
            }

            if (store.CountLedgerEntriesByDocId(doc.Id) > 0 && seenPrdLedger.Add(doc.Id))
            {
                prdDocsWithLedger.Add(new InternalOrderRedistributionGuardPrdRow
                {
                    DocId = doc.Id,
                    DocRef = doc.DocRef,
                    Status = DocTypeMapper.StatusToString(doc.Status)
                });
            }

            foreach (var pallet in store.GetProductionPalletsByDoc(doc.Id)
                         .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase)))
            {
                if (!string.Equals(pallet.Status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                activePallets.Add(new InternalOrderRedistributionGuardPalletRow
                {
                    PalletId = pallet.Id,
                    HuCode = pallet.HuCode,
                    Status = pallet.Status,
                    PrdDocId = doc.Id,
                    PrdDocRef = doc.DocRef,
                    ItemId = pallet.ItemId
                });
            }
        }

        var itemIds = store.GetOrderLines(sourceInternalOrderId)
            .Select(line => line.ItemId)
            .Distinct()
            .ToArray();
        var markingOrders = store.GetMarkingOrdersByItemIds(itemIds)
            .Where(order => order.OrderId == sourceInternalOrderId || order.SourceOrderId == sourceInternalOrderId)
            .Where(order => !string.Equals(order.Status, MarkingOrderStatus.Cancelled, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(order.Status, MarkingOrderStatus.Failed, StringComparison.OrdinalIgnoreCase))
            .Select(order => new InternalOrderRedistributionGuardMarkingRow
            {
                MarkingOrderId = order.Id,
                RequestNumber = order.RequestNumber,
                Status = order.Status,
                ItemId = order.ItemId,
                ReservedCodeCount = store.CountMarkingCodesByMarkingOrder(order.Id)
            })
            .ToList();

        var isBlocked = draftPrdDocs.Count > 0
                        || activePallets.Count > 0
                        || prdDocsWithLedger.Count > 0
                        || markingOrders.Count > 0;

        return new InternalOrderRedistributionGuardResult
        {
            IsBlocked = isBlocked,
            SourceOrderId = sourceInternalOrderId,
            SourceOrderRef = sourceOrderRef,
            DraftPrdDocs = draftPrdDocs,
            ActivePallets = activePallets,
            PrdDocsWithLedger = prdDocsWithLedger,
            MarkingOrders = markingOrders
        };
    }
}
