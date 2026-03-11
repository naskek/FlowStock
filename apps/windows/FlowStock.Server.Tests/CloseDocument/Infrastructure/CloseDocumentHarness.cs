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
    private readonly Dictionary<long, Partner> _partners = new();
    private readonly Dictionary<long, Order> _orders = new();
    private readonly Dictionary<string, HuRecord> _hus = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(long ItemId, long LocationId, string? HuCode), double> _seedBalances = new();
    private readonly List<LedgerEntry> _postedLedger = new();
    private long _nextDocId = 1;
    private long _nextDocLineId = 1;

    public CloseDocumentHarness()
    {
        _store = new Mock<IDataStore>(MockBehavior.Strict);
        ConfigureStore();
    }

    public IReadOnlyList<LedgerEntry> LedgerEntries => _postedLedger;
    public IDataStore Store => _store.Object;
    public int DocCount => _docs.Count;
    public int TotalDocLineCount => _linesByDoc.Values.Sum(lines => lines.Count);

    public DocumentService CreateService()
    {
        return new DocumentService(_store.Object);
    }

    public Doc GetDoc(long docId)
    {
        return _docs[docId];
    }

    public Doc? FindDocByRef(string docRef)
    {
        return _docs.Values.FirstOrDefault(doc => string.Equals(doc.DocRef, docRef, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<DocLine> GetDocLines(long docId)
    {
        return _linesByDoc.TryGetValue(docId, out var lines)
            ? lines
                .OrderBy(line => line.Id)
                .Select(line => new DocLine
                {
                    Id = line.Id,
                    DocId = line.DocId,
                    OrderLineId = line.OrderLineId,
                    ItemId = line.ItemId,
                    Qty = line.Qty,
                    QtyInput = line.QtyInput,
                    UomCode = line.UomCode,
                    FromLocationId = line.FromLocationId,
                    ToLocationId = line.ToLocationId,
                    FromHu = line.FromHu,
                    ToHu = line.ToHu
                })
                .ToArray()
            : Array.Empty<DocLine>();
    }

    public void SeedDoc(Doc doc)
    {
        _docs[doc.Id] = doc;
        _nextDocId = Math.Max(_nextDocId, doc.Id + 1);
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
        _nextDocLineId = Math.Max(_nextDocLineId, line.Id + 1);
    }

    public void SeedItem(Item item)
    {
        _items[item.Id] = item;
    }

    public void SeedLocation(Location location)
    {
        _locations[location.Id] = location;
    }

    public void SeedPartner(Partner partner)
    {
        _partners[partner.Id] = partner;
    }

    public void SeedOrder(Order order)
    {
        _orders[order.Id] = order;
    }

    public void SeedHu(HuRecord hu)
    {
        _hus[hu.Code] = hu;
    }

    public void SeedBalance(long itemId, long locationId, double qty, string? huCode = null)
    {
        _seedBalances[(itemId, locationId, NormalizeHu(huCode))] = qty;
    }

    private void ConfigureStore()
    {
        _store.Setup(store => store.Initialize());

        _store.Setup(store => store.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
            .Callback<Action<IDataStore>>(work => work(_store.Object));

        _store.Setup(store => store.GetDoc(It.IsAny<long>()))
            .Returns<long>(docId => _docs.TryGetValue(docId, out var doc) ? doc : null);

        _store.Setup(store => store.FindDocByRef(It.IsAny<string>()))
            .Returns<string>(docRef => _docs.Values.FirstOrDefault(doc =>
                string.Equals(doc.DocRef, docRef?.Trim(), StringComparison.OrdinalIgnoreCase)));

        _store.Setup(store => store.GetDocLines(It.IsAny<long>()))
            .Returns<long>(docId => _linesByDoc.TryGetValue(docId, out var lines)
                ? lines.ToArray()
                : Array.Empty<DocLine>());

        _store.Setup(store => store.FindItemByBarcode(It.IsAny<string>()))
            .Returns<string>(barcode => _items.Values.FirstOrDefault(item =>
                string.Equals(item.Barcode, barcode?.Trim(), StringComparison.OrdinalIgnoreCase)));

        _store.Setup(store => store.FindItemById(It.IsAny<long>()))
            .Returns<long>(itemId => _items.TryGetValue(itemId, out var item) ? item : null);

        _store.Setup(store => store.GetItems(It.IsAny<string?>()))
            .Returns(() => _items.Values.ToArray());

        _store.Setup(store => store.FindLocationById(It.IsAny<long>()))
            .Returns<long>(locationId => _locations.TryGetValue(locationId, out var location) ? location : null);

        _store.Setup(store => store.FindLocationByCode(It.IsAny<string>()))
            .Returns<string>(code => _locations.Values.FirstOrDefault(location =>
                string.Equals(location.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase)));

        _store.Setup(store => store.GetLocations())
            .Returns(() => _locations.Values.OrderBy(location => location.Id).ToArray());

        _store.Setup(store => store.GetPartner(It.IsAny<long>()))
            .Returns<long>(partnerId => _partners.TryGetValue(partnerId, out var partner) ? partner : null);

        _store.Setup(store => store.GetPartners())
            .Returns(() => _partners.Values.OrderBy(partner => partner.Id).ToArray());

        _store.Setup(store => store.GetOrder(It.IsAny<long>()))
            .Returns<long>(orderId => _orders.TryGetValue(orderId, out var order) ? order : null);

        _store.Setup(store => store.GetOrders())
            .Returns(() => _orders.Values.OrderBy(order => order.Id).ToArray());

        _store.Setup(store => store.GetDocs())
            .Returns(() => _docs.Values.OrderBy(doc => doc.Id).ToArray());

        _store.Setup(store => store.GetDocsByOrder(It.IsAny<long>()))
            .Returns<long>(orderId => _docs.Values.Where(doc => doc.OrderId == orderId).OrderBy(doc => doc.Id).ToArray());

        _store.Setup(store => store.IsDocRefSequenceTaken(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(false);

        _store.Setup(store => store.GetMaxDocRefSequenceByYear(It.IsAny<int>()))
            .Returns(0);

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

        _store.Setup(store => store.GetHuByCode(It.IsAny<string>()))
            .Returns<string>(code => _hus.TryGetValue(code.Trim(), out var hu) ? hu : null);

        _store.Setup(store => store.AddDoc(It.IsAny<Doc>()))
            .Returns<Doc>(doc =>
            {
                var docId = doc.Id > 0 ? doc.Id : _nextDocId++;
                _docs[docId] = new Doc
                {
                    Id = docId,
                    DocRef = doc.DocRef,
                    Type = doc.Type,
                    Status = doc.Status,
                    CreatedAt = doc.CreatedAt,
                    ClosedAt = doc.ClosedAt,
                    PartnerId = doc.PartnerId,
                    OrderId = doc.OrderId,
                    OrderRef = doc.OrderRef,
                    ShippingRef = doc.ShippingRef,
                    ReasonCode = doc.ReasonCode,
                    Comment = doc.Comment,
                    ProductionBatchNo = doc.ProductionBatchNo,
                    PartnerName = doc.PartnerName,
                    PartnerCode = doc.PartnerCode,
                    LineCount = doc.LineCount,
                    SourceDeviceId = doc.SourceDeviceId,
                    ApiDocUid = doc.ApiDocUid
                };
                _linesByDoc.TryAdd(docId, new List<DocLine>());
                return docId;
            });

        _store.Setup(store => store.AddDocLine(It.IsAny<DocLine>()))
            .Returns<DocLine>(line =>
            {
                var lineId = line.Id > 0 ? line.Id : _nextDocLineId++;
                if (!_linesByDoc.TryGetValue(line.DocId, out var lines))
                {
                    lines = new List<DocLine>();
                    _linesByDoc[line.DocId] = lines;
                }

                lines.Add(new DocLine
                {
                    Id = lineId,
                    DocId = line.DocId,
                    OrderLineId = line.OrderLineId,
                    ItemId = line.ItemId,
                    Qty = line.Qty,
                    QtyInput = line.QtyInput,
                    UomCode = line.UomCode,
                    FromLocationId = line.FromLocationId,
                    ToLocationId = line.ToLocationId,
                    FromHu = line.FromHu,
                    ToHu = line.ToHu
                });

                return lineId;
            });

        _store.Setup(store => store.DeleteDocLines(It.IsAny<long>()))
            .Callback<long>(docId =>
            {
                if (_linesByDoc.ContainsKey(docId))
                {
                    _linesByDoc[docId].Clear();
                }
            });

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

        _store.Setup(store => store.UpdateDocReason(It.IsAny<long>(), It.IsAny<string?>()))
            .Callback<long, string?>((docId, reasonCode) =>
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
                    PartnerId = current.PartnerId,
                    OrderId = current.OrderId,
                    OrderRef = current.OrderRef,
                    ShippingRef = current.ShippingRef,
                    ReasonCode = reasonCode,
                    Comment = current.Comment,
                    ProductionBatchNo = current.ProductionBatchNo,
                    PartnerName = current.PartnerName,
                    PartnerCode = current.PartnerCode,
                    LineCount = current.LineCount,
                    SourceDeviceId = current.SourceDeviceId,
                    ApiDocUid = current.ApiDocUid
                };
            });

        _store.Setup(store => store.UpdateDocComment(It.IsAny<long>(), It.IsAny<string?>()))
            .Callback<long, string?>((docId, comment) =>
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
                    PartnerId = current.PartnerId,
                    OrderId = current.OrderId,
                    OrderRef = current.OrderRef,
                    ShippingRef = current.ShippingRef,
                    ReasonCode = current.ReasonCode,
                    Comment = comment,
                    ProductionBatchNo = current.ProductionBatchNo,
                    PartnerName = current.PartnerName,
                    PartnerCode = current.PartnerCode,
                    LineCount = current.LineCount,
                    SourceDeviceId = current.SourceDeviceId,
                    ApiDocUid = current.ApiDocUid
                };
            });

        _store.Setup(store => store.UpdateDocOrder(It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<string?>()))
            .Callback<long, long?, string?>((docId, orderId, orderRef) =>
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
                    PartnerId = current.PartnerId,
                    OrderId = orderId,
                    OrderRef = orderRef,
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
