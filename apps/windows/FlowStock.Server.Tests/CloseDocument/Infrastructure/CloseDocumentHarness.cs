using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.CloseDocument.Infrastructure;

internal sealed class CloseDocumentHarness
{
    private readonly Mock<IDataStore> _store;
    private readonly Dictionary<long, Doc> _docs = new();
    private readonly Dictionary<long, List<DocLine>> _linesByDoc = new();
    private readonly Dictionary<long, Item> _items = new();
    private readonly Dictionary<long, Location> _locations = new();
    private readonly Dictionary<(long ItemId, long LocationId, string? HuCode), double> _seedBalances = new();
    private readonly List<LedgerEntry> _postedLedger = new();

    public CloseDocumentHarness()
    {
        _store = new Mock<IDataStore>(MockBehavior.Strict);
        ConfigureStore();
    }

    public IReadOnlyList<LedgerEntry> LedgerEntries => _postedLedger;

    public DocumentService CreateService()
    {
        return new DocumentService(_store.Object);
    }

    public Doc GetDoc(long docId)
    {
        return _docs[docId];
    }

    public void SeedDoc(Doc doc)
    {
        _docs[doc.Id] = doc;
        if (!_linesByDoc.ContainsKey(doc.Id))
        {
            _linesByDoc[doc.Id] = new List<DocLine>();
        }
    }

    public void SeedLine(DocLine line)
    {
        if (!_linesByDoc.TryGetValue(line.DocId, out var lines))
        {
            lines = new List<DocLine>();
            _linesByDoc[line.DocId] = lines;
        }

        lines.Add(line);
    }

    public void SeedItem(Item item)
    {
        _items[item.Id] = item;
    }

    public void SeedLocation(Location location)
    {
        _locations[location.Id] = location;
    }

    public void SeedBalance(long itemId, long locationId, double qty, string? huCode = null)
    {
        _seedBalances[(itemId, locationId, NormalizeHu(huCode))] = qty;
    }

    private void ConfigureStore()
    {
        _store.Setup(store => store.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
            .Callback<Action<IDataStore>>(work => work(_store.Object));

        _store.Setup(store => store.GetDoc(It.IsAny<long>()))
            .Returns<long>(docId => _docs.TryGetValue(docId, out var doc) ? doc : null);

        _store.Setup(store => store.GetDocLines(It.IsAny<long>()))
            .Returns<long>(docId => _linesByDoc.TryGetValue(docId, out var lines)
                ? lines.ToArray()
                : Array.Empty<DocLine>());

        _store.Setup(store => store.GetItems(It.IsAny<string?>()))
            .Returns(() => _items.Values.ToArray());

        _store.Setup(store => store.GetLocations())
            .Returns(() => _locations.Values.OrderBy(location => location.Id).ToArray());

        _store.Setup(store => store.GetOrderLines(It.IsAny<long>()))
            .Returns(Array.Empty<OrderLine>());

        _store.Setup(store => store.GetOrderReceiptRemaining(It.IsAny<long>()))
            .Returns(Array.Empty<OrderReceiptLine>());

        _store.Setup(store => store.GetOrderShipmentRemaining(It.IsAny<long>()))
            .Returns(Array.Empty<OrderShipmentLine>());

        _store.Setup(store => store.GetAvailableQty(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>()))
            .Returns<long, long, string?>((itemId, locationId, huCode) => GetBalance(itemId, locationId, huCode));

        _store.Setup(store => store.GetLedgerBalance(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>()))
            .Returns<long, long, string?>((itemId, locationId, huCode) => GetBalance(itemId, locationId, huCode));

        _store.Setup(store => store.GetLedgerTotalsByItem())
            .Returns(() => BuildTotalsByItem());

        _store.Setup(store => store.GetHuStockRows())
            .Returns(() => BuildHuStockRows());

        _store.Setup(store => store.UpdateDocHeader(It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Callback<long, long?, string?, string?>((docId, partnerId, orderRef, shippingRef) =>
            {
                if (!_docs.TryGetValue(docId, out var current))
                {
                    return;
                }

                _docs[docId] = new Doc
                {
                    Id = current.Id,
                    DocRef = current.DocRef,
                    Type = current.Type,
                    Status = current.Status,
                    CreatedAt = current.CreatedAt,
                    ClosedAt = current.ClosedAt,
                    PartnerId = partnerId,
                    OrderId = current.OrderId,
                    OrderRef = orderRef,
                    ShippingRef = shippingRef,
                    ReasonCode = current.ReasonCode,
                    Comment = current.Comment,
                    ProductionBatchNo = current.ProductionBatchNo,
                    PartnerName = current.PartnerName,
                    PartnerCode = current.PartnerCode,
                    LineCount = current.LineCount,
                    SourceDeviceId = current.SourceDeviceId,
                    ApiDocUid = current.ApiDocUid
                };
            });

        _store.Setup(store => store.UpdateDocLineOrderLineId(It.IsAny<long>(), It.IsAny<long?>()))
            .Callback<long, long?>((docLineId, orderLineId) =>
            {
                foreach (var pair in _linesByDoc)
                {
                    for (var index = 0; index < pair.Value.Count; index++)
                    {
                        if (pair.Value[index].Id != docLineId)
                        {
                            continue;
                        }

                        var current = pair.Value[index];
                        pair.Value[index] = new DocLine
                        {
                            Id = current.Id,
                            DocId = current.DocId,
                            OrderLineId = orderLineId,
                            ItemId = current.ItemId,
                            Qty = current.Qty,
                            QtyInput = current.QtyInput,
                            UomCode = current.UomCode,
                            FromLocationId = current.FromLocationId,
                            ToLocationId = current.ToLocationId,
                            FromHu = current.FromHu,
                            ToHu = current.ToHu
                        };
                        return;
                    }
                }
            });

        _store.Setup(store => store.AddLedgerEntry(It.IsAny<LedgerEntry>()))
            .Callback<LedgerEntry>(entry =>
            {
                _postedLedger.Add(new LedgerEntry
                {
                    Id = _postedLedger.Count + 1,
                    Timestamp = entry.Timestamp,
                    DocId = entry.DocId,
                    ItemId = entry.ItemId,
                    LocationId = entry.LocationId,
                    QtyDelta = entry.QtyDelta,
                    HuCode = NormalizeHu(entry.HuCode)
                });
            });

        _store.Setup(store => store.UpdateDocStatus(It.IsAny<long>(), It.IsAny<DocStatus>(), It.IsAny<DateTime?>()))
            .Callback<long, DocStatus, DateTime?>((docId, status, closedAt) =>
            {
                if (!_docs.TryGetValue(docId, out var current))
                {
                    return;
                }

                _docs[docId] = new Doc
                {
                    Id = current.Id,
                    DocRef = current.DocRef,
                    Type = current.Type,
                    Status = status,
                    CreatedAt = current.CreatedAt,
                    ClosedAt = closedAt,
                    PartnerId = current.PartnerId,
                    OrderId = current.OrderId,
                    OrderRef = current.OrderRef,
                    ShippingRef = current.ShippingRef,
                    ReasonCode = current.ReasonCode,
                    Comment = current.Comment,
                    ProductionBatchNo = current.ProductionBatchNo,
                    PartnerName = current.PartnerName,
                    PartnerCode = current.PartnerCode,
                    LineCount = current.LineCount,
                    SourceDeviceId = current.SourceDeviceId,
                    ApiDocUid = current.ApiDocUid
                };
            });

        _store.Setup(store => store.CountKmCodesByReceiptLine(It.IsAny<long>()))
            .Returns(0);

        _store.Setup(store => store.CountKmCodesByShipmentLine(It.IsAny<long>()))
            .Returns(0);
    }

    private double GetBalance(long itemId, long locationId, string? huCode)
    {
        var normalizedHu = NormalizeHu(huCode);
        var seed = _seedBalances.TryGetValue((itemId, locationId, normalizedHu), out var value)
            ? value
            : 0d;

        var delta = _postedLedger
            .Where(entry => entry.ItemId == itemId
                            && entry.LocationId == locationId
                            && string.Equals(NormalizeHu(entry.HuCode), normalizedHu, StringComparison.Ordinal))
            .Sum(entry => entry.QtyDelta);

        return seed + delta;
    }

    private IReadOnlyDictionary<long, double> BuildTotalsByItem()
    {
        var totals = new Dictionary<long, double>();

        foreach (var balance in _seedBalances)
        {
            totals[balance.Key.ItemId] = totals.TryGetValue(balance.Key.ItemId, out var current)
                ? current + balance.Value
                : balance.Value;
        }

        foreach (var entry in _postedLedger)
        {
            totals[entry.ItemId] = totals.TryGetValue(entry.ItemId, out var current)
                ? current + entry.QtyDelta
                : entry.QtyDelta;
        }

        return totals;
    }

    private IReadOnlyList<HuStockRow> BuildHuStockRows()
    {
        var totals = new Dictionary<(long ItemId, long LocationId, string HuCode), double>();

        foreach (var balance in _seedBalances)
        {
            if (string.IsNullOrWhiteSpace(balance.Key.HuCode))
            {
                continue;
            }

            var key = (balance.Key.ItemId, balance.Key.LocationId, balance.Key.HuCode!);
            totals[key] = totals.TryGetValue(key, out var current)
                ? current + balance.Value
                : balance.Value;
        }

        foreach (var entry in _postedLedger)
        {
            var huCode = NormalizeHu(entry.HuCode);
            if (string.IsNullOrWhiteSpace(huCode))
            {
                continue;
            }

            var key = (entry.ItemId, entry.LocationId, huCode!);
            totals[key] = totals.TryGetValue(key, out var current)
                ? current + entry.QtyDelta
                : entry.QtyDelta;
        }

        return totals
            .Where(pair => Math.Abs(pair.Value) > 0.000001)
            .Select(pair => new HuStockRow
            {
                ItemId = pair.Key.ItemId,
                LocationId = pair.Key.LocationId,
                HuCode = pair.Key.HuCode,
                Qty = pair.Value
            })
            .ToArray();
    }

    private static string? NormalizeHu(string? huCode)
    {
        return string.IsNullOrWhiteSpace(huCode) ? null : huCode.Trim();
    }
}
