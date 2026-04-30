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
    private readonly Dictionary<long, List<OrderLine>> _orderLinesByOrder = new();
    private readonly Dictionary<long, ItemRequest> _itemRequests = new();
    private readonly Dictionary<long, OrderRequest> _orderRequests = new();
    private readonly Dictionary<long, IReadOnlyList<OrderReceiptLine>> _orderReceiptRemaining = new();
    private readonly Dictionary<long, IReadOnlyList<OrderReceiptLine>> _orderReceiptRemainingWithoutReservedStock = new();
    private readonly Dictionary<long, IReadOnlyDictionary<long, double>> _shippedTotalsByOrderLine = new();
    private readonly HashSet<long> _ordersWithOutboundDocs = new();
    private readonly Dictionary<string, HuRecord> _hus = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(long ItemId, long LocationId, string? HuCode), double> _seedBalances = new();
    private readonly List<LedgerEntry> _postedLedger = new();
    private long _nextDocId = 1;
    private long _nextDocLineId = 1;
    private long _nextOrderId = 1;
    private long _nextOrderLineId = 1;

    public CloseDocumentHarness()
    {
        _store = new Mock<IDataStore>(MockBehavior.Strict);
        ConfigureStore();
    }

    public IReadOnlyList<LedgerEntry> LedgerEntries => _postedLedger;
    public IDataStore Store => _store.Object;
    public int DocCount => _docs.Count;
    public int TotalDocLineCount => _linesByDoc.Values.Sum(lines => lines.Count);
    public int OrderCount => _orders.Count;
    public int TotalOrderLineCount => _orderLinesByOrder.Values.Sum(lines => lines.Count);

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

    public Order GetOrder(long orderId)
    {
        return _orders[orderId];
    }

    public IReadOnlyList<OrderLine> GetOrderLines(long orderId)
    {
        return _orderLinesByOrder.TryGetValue(orderId, out var lines)
            ? lines
                .OrderBy(line => line.Id)
                .Select(CloneOrderLine)
                .ToArray()
            : Array.Empty<OrderLine>();
    }

    public OrderRequest? GetOrderRequest(long requestId)
    {
        return _orderRequests.TryGetValue(requestId, out var request)
            ? CloneOrderRequest(request)
            : null;
    }

    public IReadOnlyList<ItemRequest> GetItemRequests(bool includeResolved)
    {
        return _itemRequests.Values
            .Where(request => includeResolved
                              || !string.Equals(request.Status, "RESOLVED", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(request => request.CreatedAt)
            .Select(CloneItemRequest)
            .ToArray();
    }

    public IReadOnlyList<OrderRequest> GetOrderRequests(bool includeResolved)
    {
        return _orderRequests.Values
            .Where(request => includeResolved
                              || !string.Equals(request.Status, OrderRequestStatus.Approved, StringComparison.OrdinalIgnoreCase)
                              && !string.Equals(request.Status, OrderRequestStatus.Rejected, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(request => request.CreatedAt)
            .Select(CloneOrderRequest)
            .ToArray();
    }

    public IReadOnlyList<DocLine> GetDocLines(long docId)
    {
        return GetActiveDocLines(docId);
    }

    public IReadOnlyList<DocLine> GetAllDocLines(long docId)
    {
        return _linesByDoc.TryGetValue(docId, out var lines)
            ? lines
                .OrderBy(line => line.Id)
                .Select(CloneDocLine)
                .ToArray()
            : Array.Empty<DocLine>();
    }

    private IReadOnlyList<DocLine> GetActiveDocLines(long docId)
    {
        return _linesByDoc.TryGetValue(docId, out var lines)
            ? lines
                .Where(line => line.Qty > 0 && !lines.Any(newer => newer.ReplacesLineId == line.Id))
                .OrderBy(line => line.Id)
                .Select(CloneDocLine)
                .ToArray()
            : Array.Empty<DocLine>();
    }

    private static DocLine CloneDocLine(DocLine line)
    {
        return new DocLine
        {
            Id = line.Id,
            DocId = line.DocId,
            ReplacesLineId = line.ReplacesLineId,
            OrderLineId = line.OrderLineId,
            ItemId = line.ItemId,
            Qty = line.Qty,
            QtyInput = line.QtyInput,
            UomCode = line.UomCode,
            FromLocationId = line.FromLocationId,
            ToLocationId = line.ToLocationId,
            FromHu = line.FromHu,
            ToHu = line.ToHu,
            PackSingleHu = line.PackSingleHu
        };
    }

    private static OrderLine CloneOrderLine(OrderLine line)
    {
        return new OrderLine
        {
            Id = line.Id,
            OrderId = line.OrderId,
            ItemId = line.ItemId,
            QtyOrdered = line.QtyOrdered
        };
    }

    private static OrderReceiptLine CloneOrderReceiptLine(OrderReceiptLine line)
    {
        return new OrderReceiptLine
        {
            OrderLineId = line.OrderLineId,
            OrderId = line.OrderId,
            ItemId = line.ItemId,
            ItemName = line.ItemName,
            QtyOrdered = line.QtyOrdered,
            QtyReceived = line.QtyReceived,
            QtyRemaining = line.QtyRemaining,
            ToLocationId = line.ToLocationId,
            ToLocation = line.ToLocation,
            ToHu = line.ToHu,
            SortOrder = line.SortOrder
        };
    }

    private static ItemRequest CloneItemRequest(ItemRequest request)
    {
        return new ItemRequest
        {
            Id = request.Id,
            Barcode = request.Barcode,
            Comment = request.Comment,
            DeviceId = request.DeviceId,
            Login = request.Login,
            CreatedAt = request.CreatedAt,
            Status = request.Status,
            ResolvedAt = request.ResolvedAt
        };
    }

    private static OrderRequest CloneOrderRequest(OrderRequest request)
    {
        return new OrderRequest
        {
            Id = request.Id,
            RequestType = request.RequestType,
            PayloadJson = request.PayloadJson,
            Status = request.Status,
            CreatedAt = request.CreatedAt,
            CreatedByLogin = request.CreatedByLogin,
            CreatedByDeviceId = request.CreatedByDeviceId,
            ResolvedAt = request.ResolvedAt,
            ResolvedBy = request.ResolvedBy,
            ResolutionNote = request.ResolutionNote,
            AppliedOrderId = request.AppliedOrderId
        };
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
        _nextOrderId = Math.Max(_nextOrderId, order.Id + 1);
        _orderLinesByOrder.TryAdd(order.Id, new List<OrderLine>());
    }

    public void SeedOrderLine(OrderLine line)
    {
        if (!_orderLinesByOrder.TryGetValue(line.OrderId, out var lines))
        {
            lines = new List<OrderLine>();
            _orderLinesByOrder[line.OrderId] = lines;
        }

        lines.Add(CloneOrderLine(line));
        _nextOrderLineId = Math.Max(_nextOrderLineId, line.Id + 1);
    }

    public void SeedOrderRequest(OrderRequest request)
    {
        _orderRequests[request.Id] = CloneOrderRequest(request);
    }

    public void SeedItemRequest(ItemRequest request)
    {
        _itemRequests[request.Id] = CloneItemRequest(request);
    }

    public void SeedOrderReceiptRemaining(long orderId, params OrderReceiptLine[] lines)
    {
        _orderReceiptRemaining[orderId] = (lines ?? Array.Empty<OrderReceiptLine>())
            .Select(CloneOrderReceiptLine)
            .ToArray();
        _orderReceiptRemainingWithoutReservedStock[orderId] = _orderReceiptRemaining[orderId];
    }

    public void SeedOrderReceiptRemainingWithoutReservedStock(long orderId, params OrderReceiptLine[] lines)
    {
        _orderReceiptRemainingWithoutReservedStock[orderId] = (lines ?? Array.Empty<OrderReceiptLine>())
            .Select(CloneOrderReceiptLine)
            .ToArray();
    }

    public void SeedShippedTotalsByOrderLine(long orderId, IReadOnlyDictionary<long, double> totals)
    {
        _shippedTotalsByOrderLine[orderId] = new Dictionary<long, double>(totals ?? new Dictionary<long, double>());
    }

    public void SeedHasOutboundDocs(long orderId, bool value = true)
    {
        if (value)
        {
            _ordersWithOutboundDocs.Add(orderId);
        }
        else
        {
            _ordersWithOutboundDocs.Remove(orderId);
        }
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
            .Returns<long>(docId => GetActiveDocLines(docId));

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

        _store.Setup(store => store.AddOrder(It.IsAny<Order>()))
            .Returns<Order>(order =>
            {
                var orderId = order.Id > 0 ? order.Id : _nextOrderId++;
                _orders[orderId] = new Order
                {
                    Id = orderId,
                    OrderRef = order.OrderRef,
                    Type = order.Type,
                    PartnerId = order.PartnerId,
                    DueDate = order.DueDate,
                    Status = order.Status,
                    Comment = order.Comment,
                    CreatedAt = order.CreatedAt,
                    ShippedAt = order.ShippedAt,
                    PartnerName = order.PartnerName,
                    PartnerCode = order.PartnerCode,
                    UseReservedStock = order.UseReservedStock,
                    MarkingStatus = order.MarkingStatus,
                    MarkingExcelGeneratedAt = order.MarkingExcelGeneratedAt,
                    MarkingPrintedAt = order.MarkingPrintedAt
                };
                _orderLinesByOrder.TryAdd(orderId, new List<OrderLine>());
                return orderId;
            });

        _store.Setup(store => store.UpdateOrder(It.IsAny<Order>()))
            .Callback<Order>(order =>
            {
                if (!_orders.TryGetValue(order.Id, out var current))
                {
                    return;
                }

                _orders[order.Id] = new Order
                {
                    Id = current.Id,
                    OrderRef = order.OrderRef,
                    Type = current.Type,
                    PartnerId = order.PartnerId,
                    DueDate = order.DueDate,
                    Status = order.Status,
                    Comment = order.Comment,
                    CreatedAt = current.CreatedAt,
                    ShippedAt = current.ShippedAt,
                    PartnerName = current.PartnerName,
                    PartnerCode = current.PartnerCode,
                    UseReservedStock = order.UseReservedStock,
                    MarkingStatus = current.MarkingStatus,
                    MarkingExcelGeneratedAt = current.MarkingExcelGeneratedAt,
                    MarkingPrintedAt = current.MarkingPrintedAt
                };
            });

        _store.Setup(store => store.UpdateOrderStatus(It.IsAny<long>(), It.IsAny<OrderStatus>()))
            .Callback<long, OrderStatus>((orderId, status) =>
            {
                if (!_orders.TryGetValue(orderId, out var current))
                {
                    return;
                }

                _orders[orderId] = new Order
                {
                    Id = current.Id,
                    OrderRef = current.OrderRef,
                    Type = current.Type,
                    PartnerId = current.PartnerId,
                    DueDate = current.DueDate,
                    Status = status,
                    Comment = current.Comment,
                    CreatedAt = current.CreatedAt,
                    ShippedAt = current.ShippedAt,
                    PartnerName = current.PartnerName,
                    PartnerCode = current.PartnerCode,
                    UseReservedStock = current.UseReservedStock,
                    MarkingStatus = current.MarkingStatus,
                    MarkingExcelGeneratedAt = current.MarkingExcelGeneratedAt,
                    MarkingPrintedAt = current.MarkingPrintedAt
                };
            });

        _store.Setup(store => store.GetDocs())
            .Returns(() => _docs.Values.OrderBy(doc => doc.Id).ToArray());

        _store.Setup(store => store.GetDocsByOrder(It.IsAny<long>()))
            .Returns<long>(orderId => _docs.Values.Where(doc => doc.OrderId == orderId).OrderBy(doc => doc.Id).ToArray());

        _store.Setup(store => store.IsDocRefSequenceTaken(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(false);

        _store.Setup(store => store.GetMaxDocRefSequenceByYear(It.IsAny<int>()))
            .Returns(0);

        _store.Setup(store => store.GetOrderLines(It.IsAny<long>()))
            .Returns<long>(orderId => GetOrderLines(orderId));

        _store.Setup(store => store.GetOrderLineViews(It.IsAny<long>()))
            .Returns<long>(orderId =>
            {
                return GetOrderLines(orderId)
                    .Select(line => new OrderLineView
                    {
                        Id = line.Id,
                        OrderId = line.OrderId,
                        ItemId = line.ItemId,
                        ItemName = _items.TryGetValue(line.ItemId, out var item) ? item.Name : string.Empty,
                        QtyOrdered = line.QtyOrdered
                    })
                    .ToArray();
            });

        _store.Setup(store => store.GetOrderReceiptRemaining(It.IsAny<long>()))
            .Returns<long>(orderId => _orderReceiptRemaining.TryGetValue(orderId, out var lines)
                ? lines
                    .Select(CloneOrderReceiptLine)
                    .ToArray()
                : Array.Empty<OrderReceiptLine>());

        _store.Setup(store => store.GetOrderReceiptRemainingWithoutReservedStock(It.IsAny<long>()))
            .Returns<long>(orderId => _orderReceiptRemainingWithoutReservedStock.TryGetValue(orderId, out var lines)
                ? lines
                    .Select(CloneOrderReceiptLine)
                    .ToArray()
                : Array.Empty<OrderReceiptLine>());

        _store.Setup(store => store.GetOrderShipmentRemaining(It.IsAny<long>()))
            .Returns(Array.Empty<OrderShipmentLine>());

        _store.Setup(store => store.AddOrderLine(It.IsAny<OrderLine>()))
            .Returns<OrderLine>(line =>
            {
                var orderLineId = line.Id > 0 ? line.Id : _nextOrderLineId++;
                if (!_orderLinesByOrder.TryGetValue(line.OrderId, out var lines))
                {
                    lines = new List<OrderLine>();
                    _orderLinesByOrder[line.OrderId] = lines;
                }

                lines.Add(new OrderLine
                {
                    Id = orderLineId,
                    OrderId = line.OrderId,
                    ItemId = line.ItemId,
                    QtyOrdered = line.QtyOrdered
                });

                return orderLineId;
            });

        _store.Setup(store => store.UpdateOrderLineQty(It.IsAny<long>(), It.IsAny<double>()))
            .Callback<long, double>((orderLineId, qtyOrdered) =>
            {
                foreach (var pair in _orderLinesByOrder)
                {
                    for (var index = 0; index < pair.Value.Count; index++)
                    {
                        if (pair.Value[index].Id != orderLineId)
                        {
                            continue;
                        }

                        var current = pair.Value[index];
                        pair.Value[index] = new OrderLine
                        {
                            Id = current.Id,
                            OrderId = current.OrderId,
                            ItemId = current.ItemId,
                            QtyOrdered = qtyOrdered
                        };
                        return;
                    }
                }
            });

        _store.Setup(store => store.DeleteOrderLine(It.IsAny<long>()))
            .Callback<long>(orderLineId =>
            {
                foreach (var pair in _orderLinesByOrder)
                {
                    pair.Value.RemoveAll(line => line.Id == orderLineId);
                }
            });

        _store.Setup(store => store.DeleteOrderLines(It.IsAny<long>()))
            .Callback<long>(orderId =>
            {
                if (_orderLinesByOrder.ContainsKey(orderId))
                {
                    _orderLinesByOrder[orderId].Clear();
                }
            });

        _store.Setup(store => store.DeleteOrder(It.IsAny<long>()))
            .Callback<long>(orderId =>
            {
                _orders.Remove(orderId);
                _orderLinesByOrder.Remove(orderId);
            });

        _store.Setup(store => store.AddOrderRequest(It.IsAny<OrderRequest>()))
            .Returns<OrderRequest>(request =>
            {
                var requestId = request.Id > 0
                    ? request.Id
                    : (_orderRequests.Count == 0 ? 1 : _orderRequests.Keys.Max() + 1);
                _orderRequests[requestId] = new OrderRequest
                {
                    Id = requestId,
                    RequestType = request.RequestType,
                    PayloadJson = request.PayloadJson,
                    Status = request.Status,
                    CreatedAt = request.CreatedAt,
                    CreatedByLogin = request.CreatedByLogin,
                    CreatedByDeviceId = request.CreatedByDeviceId,
                    ResolvedAt = request.ResolvedAt,
                    ResolvedBy = request.ResolvedBy,
                    ResolutionNote = request.ResolutionNote,
                    AppliedOrderId = request.AppliedOrderId
                };
                return requestId;
            });

        _store.Setup(store => store.AddItemRequest(It.IsAny<ItemRequest>()))
            .Returns<ItemRequest>(request =>
            {
                var requestId = request.Id > 0
                    ? request.Id
                    : (_itemRequests.Count == 0 ? 1 : _itemRequests.Keys.Max() + 1);
                _itemRequests[requestId] = new ItemRequest
                {
                    Id = requestId,
                    Barcode = request.Barcode,
                    Comment = request.Comment,
                    DeviceId = request.DeviceId,
                    Login = request.Login,
                    CreatedAt = request.CreatedAt,
                    Status = request.Status,
                    ResolvedAt = request.ResolvedAt
                };
                return requestId;
            });

        _store.Setup(store => store.GetItemRequests(It.IsAny<bool>()))
            .Returns<bool>(includeResolved => GetItemRequests(includeResolved));

        _store.Setup(store => store.MarkItemRequestResolved(It.IsAny<long>()))
            .Callback<long>(requestId =>
            {
                if (!_itemRequests.TryGetValue(requestId, out var current))
                {
                    return;
                }

                _itemRequests[requestId] = new ItemRequest
                {
                    Id = current.Id,
                    Barcode = current.Barcode,
                    Comment = current.Comment,
                    DeviceId = current.DeviceId,
                    Login = current.Login,
                    CreatedAt = current.CreatedAt,
                    Status = "RESOLVED",
                    ResolvedAt = DateTime.Now
                };
            });

        _store.Setup(store => store.GetOrderRequests(It.IsAny<bool>()))
            .Returns<bool>(includeResolved => GetOrderRequests(includeResolved));

        _store.Setup(store => store.ResolveOrderRequest(
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<long?>()))
            .Callback<long, string, string, string?, long?>((requestId, status, resolvedBy, note, appliedOrderId) =>
            {
                if (!_orderRequests.TryGetValue(requestId, out var current))
                {
                    return;
                }

                _orderRequests[requestId] = new OrderRequest
                {
                    Id = current.Id,
                    RequestType = current.RequestType,
                    PayloadJson = current.PayloadJson,
                    Status = status,
                    CreatedAt = current.CreatedAt,
                    CreatedByLogin = current.CreatedByLogin,
                    CreatedByDeviceId = current.CreatedByDeviceId,
                    ResolvedAt = DateTime.Now,
                    ResolvedBy = resolvedBy,
                    ResolutionNote = note,
                    AppliedOrderId = appliedOrderId
                };
            });

        _store.Setup(store => store.GetShippedTotalsByOrder(It.IsAny<long>()))
            .Returns(() => new Dictionary<long, double>());

        _store.Setup(store => store.GetShippedTotalsByOrderLine(It.IsAny<long>()))
            .Returns<long>(orderId => _shippedTotalsByOrderLine.TryGetValue(orderId, out var totals)
                ? new Dictionary<long, double>(totals)
                : new Dictionary<long, double>());

        _store.Setup(store => store.GetOrderShippedAt(It.IsAny<long>()))
            .Returns((DateTime?)null);

        _store.Setup(store => store.HasOutboundDocs(It.IsAny<long>()))
            .Returns<long>(orderId => _ordersWithOutboundDocs.Contains(orderId));

        _store.Setup(store => store.GetAvailableQty(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>()))
            .Returns<long, long, string?>((itemId, locationId, huCode) => GetBalance(itemId, locationId, huCode));

        _store.Setup(store => store.GetLedgerBalance(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>()))
            .Returns<long, long, string?>((itemId, locationId, huCode) => GetBalance(itemId, locationId, huCode));

        _store.Setup(store => store.GetLedgerTotalsByItem())
            .Returns(() => BuildTotalsByItem());

        _store.Setup(store => store.GetLedgerTotalsByHu())
            .Returns(() => BuildTotalsByHu());

        _store.Setup(store => store.GetHuStockRows())
            .Returns(() => BuildHuStockRows());

        _store.Setup(store => store.GetHuByCode(It.IsAny<string>()))
            .Returns<string>(code => _hus.TryGetValue(code.Trim(), out var hu) ? hu : null);

        _store.Setup(store => store.GetHus(It.IsAny<string?>(), It.IsAny<int>()))
            .Returns<string?, int>((_, take) => _hus.Values
                .OrderBy(hu => hu.Code, StringComparer.OrdinalIgnoreCase)
                .Take(take)
                .ToArray());

        _store.Setup(store => store.CreateHuRecord(It.IsAny<string?>()))
            .Returns<string?>(createdBy =>
            {
                var code = $"HU-{_hus.Count + 1:000000}";
                var hu = new HuRecord
                {
                    Id = _hus.Count + 1,
                    Code = code,
                    Status = "ACTIVE",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = createdBy
                };
                _hus[code] = hu;
                return hu;
            });

        _store.Setup(store => store.CreateHuRecord(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<string, string?>((code, createdBy) =>
            {
                var normalized = code.Trim();
                var hu = new HuRecord
                {
                    Id = _hus.Count + 1,
                    Code = normalized,
                    Status = "ACTIVE",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = createdBy
                };
                _hus[normalized] = hu;
                return hu;
            });

        _store.Setup(store => store.ReopenHu(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Callback<string, string?, string?>((code, reopenedBy, note) =>
            {
                var normalized = code.Trim();
                if (!_hus.TryGetValue(normalized, out var current))
                {
                    return;
                }

                _hus[normalized] = new HuRecord
                {
                    Id = current.Id,
                    Code = current.Code,
                    Status = "ACTIVE",
                    CreatedAt = current.CreatedAt,
                    CreatedBy = current.CreatedBy,
                    ClosedAt = null,
                    Note = note ?? current.Note
                };
            });

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
                    ReplacesLineId = line.ReplacesLineId,
                    OrderLineId = line.OrderLineId,
                    ItemId = line.ItemId,
                    Qty = line.Qty,
                    QtyInput = line.QtyInput,
                    UomCode = line.UomCode,
                    FromLocationId = line.FromLocationId,
                    ToLocationId = line.ToLocationId,
                    FromHu = line.FromHu,
                    ToHu = line.ToHu,
                    PackSingleHu = line.PackSingleHu
                });

                return lineId;
            });

        _store.Setup(store => store.DeleteDocLine(It.IsAny<long>()))
            .Callback<long>(docLineId =>
            {
                foreach (var pair in _linesByDoc)
                {
                    var index = pair.Value.FindIndex(line => line.Id == docLineId);
                    if (index < 0)
                    {
                        continue;
                    }

                    pair.Value.RemoveAt(index);
                    return;
                }
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
                            ReplacesLineId = current.ReplacesLineId,
                            OrderLineId = orderLineId,
                            ItemId = current.ItemId,
                            Qty = current.Qty,
                            QtyInput = current.QtyInput,
                            UomCode = current.UomCode,
                            FromLocationId = current.FromLocationId,
                            ToLocationId = current.ToLocationId,
                            FromHu = current.FromHu,
                            ToHu = current.ToHu,
                            PackSingleHu = current.PackSingleHu
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

    private IReadOnlyDictionary<string, double> BuildTotalsByHu()
    {
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var balance in _seedBalances)
        {
            if (string.IsNullOrWhiteSpace(balance.Key.HuCode))
            {
                continue;
            }

            totals[balance.Key.HuCode!] = totals.TryGetValue(balance.Key.HuCode!, out var current)
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

            totals[huCode] = totals.TryGetValue(huCode, out var current)
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
