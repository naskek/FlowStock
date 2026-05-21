using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.CloseDocument.Infrastructure;

internal sealed class CloseDocumentHarness
{
    private readonly Mock<IDataStore> _store;
    private readonly Dictionary<long, Doc> _docs = new();
    private readonly Dictionary<long, List<DocLine>> _linesByDoc = new();
    private readonly Dictionary<long, Item> _items = new();
    private readonly Dictionary<long, ItemType> _itemTypes = new();
    private readonly Dictionary<long, Location> _locations = new();
    private readonly Dictionary<long, Partner> _partners = new();
    private readonly Dictionary<long, Order> _orders = new();
    private readonly Dictionary<long, List<OrderLine>> _orderLinesByOrder = new();
    private readonly Dictionary<long, long> _orderIdByOrderLineId = new();
    private readonly Dictionary<long, ItemRequest> _itemRequests = new();
    private readonly Dictionary<long, OrderRequest> _orderRequests = new();
    private readonly Dictionary<long, IReadOnlyList<OrderReceiptLine>> _orderReceiptRemaining = new();
    private readonly Dictionary<long, IReadOnlyList<OrderReceiptLine>> _orderReceiptRemainingWithoutReservedStock = new();
    private readonly Dictionary<long, IReadOnlyList<OrderReceiptPlanLine>> _orderReceiptPlanLines = new();
    private readonly Dictionary<long, ProductionPallet> _productionPallets = new();
    private readonly Dictionary<long, IReadOnlyDictionary<long, double>> _shippedTotalsByOrderLine = new();
    private readonly Dictionary<Guid, MarkingOrder> _markingOrders = new();
    private readonly Dictionary<Guid, MarkingCode> _markingCodes = new();
    private readonly Dictionary<long, int> _kmCodeCountByReceiptLine = new();
    private readonly HashSet<long> _ordersWithOutboundDocs = new();
    private readonly Dictionary<string, HuRecord> _hus = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(long ItemId, long LocationId, string? HuCode), double> _seedBalances = new();
    private readonly List<LedgerEntry> _postedLedger = new();
    private long _nextDocId = 1;
    private long _nextDocLineId = 1;
    private long _nextOrderId = 1;
    private long _nextOrderLineId = 1;
    private long _nextProductionPalletId = 1;
    private long _nextProductionPalletHuNumber = 1;

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
    public IReadOnlyList<MarkingOrder> MarkingOrders => _markingOrders.Values.OrderBy(order => order.CreatedAt).ToArray();
    public IReadOnlyList<MarkingCode> MarkingCodes => _markingCodes.Values.OrderBy(code => code.CreatedAt).ToArray();

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
        return BuildOrderSnapshot(_orders[orderId]);
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

    public IReadOnlyList<OrderReceiptPlanLine> GetOrderReceiptPlanLines(long orderId)
    {
        return _orderReceiptPlanLines.TryGetValue(orderId, out var lines)
            ? lines
                .OrderBy(line => line.SortOrder)
                .Select(CloneOrderReceiptPlanLine)
                .ToArray()
            : Array.Empty<OrderReceiptPlanLine>();
    }

    private IReadOnlyList<DocLine> GetActiveDocLines(long docId)
    {
        var allowSignedQty = _docs.TryGetValue(docId, out var doc) && doc.Type == DocType.InventoryCorrection;
        return _linesByDoc.TryGetValue(docId, out var lines)
            ? lines
                .Where(line => !lines.Any(newer => newer.ReplacesLineId == line.Id))
                .Where(line => allowSignedQty
                    ? !StockQuantityRules.IsEffectivelyZero(line.Qty)
                    : line.Qty > 0)
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
            ProductionPurpose = line.ProductionPurpose,
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
            QtyOrdered = line.QtyOrdered,
            ProductionPurpose = line.ProductionPurpose,
            ProductionPalletGroup = line.ProductionPalletGroup
        };
    }

    private static Order CloneOrder(Order order)
    {
        return new Order
        {
            Id = order.Id,
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
            IsLegacyExcelGeneratedMarkingStatus = order.IsLegacyExcelGeneratedMarkingStatus,
            MarkingRequired = order.MarkingRequired,
            MarkingApplies = order.MarkingApplies,
            MarkingCodeCovered = order.MarkingCodeCovered,
            MarkingExcelGeneratedAt = order.MarkingExcelGeneratedAt,
            MarkingPrintedAt = order.MarkingPrintedAt
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
            SortOrder = line.SortOrder,
            ProductionPurpose = line.ProductionPurpose
        };
    }

    private static OrderReceiptPlanLine CloneOrderReceiptPlanLine(OrderReceiptPlanLine line)
    {
        return new OrderReceiptPlanLine
        {
            Id = line.Id,
            OrderId = line.OrderId,
            OrderLineId = line.OrderLineId,
            ItemId = line.ItemId,
            ItemName = line.ItemName,
            QtyPlanned = line.QtyPlanned,
            ToLocationId = line.ToLocationId,
            ToLocationCode = line.ToLocationCode,
            ToHu = line.ToHu,
            SortOrder = line.SortOrder
        };
    }

    private static ProductionPallet CloneProductionPallet(ProductionPallet pallet)
    {
        return new ProductionPallet
        {
            Id = pallet.Id,
            PrdDocId = pallet.PrdDocId,
            DocLineId = pallet.DocLineId,
            OrderId = pallet.OrderId,
            OrderLineId = pallet.OrderLineId,
            ItemId = pallet.ItemId,
            ItemName = pallet.ItemName,
            HuCode = pallet.HuCode,
            PlannedQty = pallet.PlannedQty,
            ToLocationId = pallet.ToLocationId,
            ToLocationCode = pallet.ToLocationCode,
            Status = pallet.Status,
            FilledAt = pallet.FilledAt,
            FilledByDeviceId = pallet.FilledByDeviceId,
            CreatedAt = pallet.CreatedAt,
            Lines = pallet.Lines.Select(line => new ProductionPalletComponentLine
            {
                Id = line.Id,
                ProductionPalletId = line.ProductionPalletId,
                DocLineId = line.DocLineId,
                OrderLineId = line.OrderLineId,
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                Brand = line.Brand,
                Uom = line.Uom,
                PlannedQty = line.PlannedQty,
                FilledQty = line.FilledQty,
                CreatedAt = line.CreatedAt
            }).ToArray()
        };
    }

    private static MarkingOrder CloneMarkingOrder(MarkingOrder order)
    {
        return new MarkingOrder
        {
            Id = order.Id,
            OrderId = order.OrderId,
            ItemId = order.ItemId,
            Gtin = order.Gtin,
            RequestedQuantity = order.RequestedQuantity,
            RequestNumber = order.RequestNumber,
            Status = order.Status,
            Notes = order.Notes,
            SourceType = order.SourceType,
            SourceOrderId = order.SourceOrderId,
            RequestedAt = order.RequestedAt,
            CodesBoundAt = order.CodesBoundAt,
            CreatedAt = order.CreatedAt,
            UpdatedAt = order.UpdatedAt
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

    public void SeedItemType(ItemType itemType)
    {
        _itemTypes[itemType.Id] = itemType;
    }

    public void SeedPartner(Partner partner)
    {
        _partners[partner.Id] = partner;
    }

    public void SeedOrder(Order order)
    {
        _orders[order.Id] = CloneOrder(order);
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
        _orderIdByOrderLineId[line.Id] = line.OrderId;
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

    public void SeedOrderReceiptPlanLines(long orderId, params OrderReceiptPlanLine[] lines)
    {
        _orderReceiptPlanLines[orderId] = (lines ?? Array.Empty<OrderReceiptPlanLine>())
            .Select(CloneOrderReceiptPlanLine)
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

    public void SeedKmCodeCountByReceiptLine(long docLineId, int count)
    {
        _kmCodeCountByReceiptLine[docLineId] = count;
    }

    public void SeedMarkingOrder(MarkingOrder order)
    {
        _markingOrders[order.Id] = CloneMarkingOrder(order);
    }

    public void SeedMarkingCodes(Guid markingOrderId, int count, string? gtin = null, long? receiptLineId = null)
    {
        for (var index = 0; index < count; index++)
        {
            var id = Guid.NewGuid();
            _markingCodes[id] = new MarkingCode
            {
                Id = id,
                Code = $"code-{markingOrderId:N}-{index + 1}",
                CodeHash = $"hash-{markingOrderId:N}-{index + 1}",
                Gtin = gtin,
                MarkingOrderId = markingOrderId,
                ImportId = Guid.NewGuid(),
                Status = receiptLineId.HasValue ? MarkingCodeStatus.Applied : MarkingCodeStatus.Reserved,
                ReceiptDocId = receiptLineId.HasValue ? 999 : null,
                ReceiptLineId = receiptLineId,
                SourceRowNumber = index + 1,
                CreatedAt = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc)
            };
        }
    }

    public void SeedHu(HuRecord hu)
    {
        _hus[hu.Code] = hu;
        _nextProductionPalletHuNumber = Math.Max(_nextProductionPalletHuNumber, ExtractHuNumber(hu.Code) + 1);
    }

    public void SeedBalance(long itemId, long locationId, double qty, string? huCode = null)
    {
        _seedBalances[(itemId, locationId, NormalizeHu(huCode))] = qty;
    }

    public void SeedLedgerEntry(long docId, long itemId, long locationId, double qtyDelta, string? huCode = null)
    {
        _postedLedger.Add(new LedgerEntry
        {
            Id = _postedLedger.Count + 1,
            Timestamp = DateTime.UtcNow,
            DocId = docId,
            ItemId = itemId,
            LocationId = locationId,
            QtyDelta = qtyDelta,
            HuCode = NormalizeHu(huCode)
        });
    }

    public void SeedClosedOutbound(long docId, string docRef, long orderId, long itemId, long locationId, double qty, string huCode)
    {
        SeedDoc(new Doc
        {
            Id = docId,
            DocRef = docRef,
            Type = DocType.Outbound,
            Status = DocStatus.Closed,
            OrderId = orderId,
            CreatedAt = DateTime.UtcNow,
            ClosedAt = DateTime.UtcNow
        });
        SeedLedgerEntry(docId, itemId, locationId, -qty, huCode);
    }

    public void SeedProductionPallet(ProductionPallet pallet)
    {
        _productionPallets[pallet.Id] = CloneProductionPallet(pallet);
        _nextProductionPalletId = Math.Max(_nextProductionPalletId, pallet.Id + 1);
        _nextProductionPalletHuNumber = Math.Max(_nextProductionPalletHuNumber, ExtractHuNumber(pallet.HuCode) + 1);
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

        _store.Setup(store => store.GetItemType(It.IsAny<long>()))
            .Returns<long>(itemTypeId => _itemTypes.TryGetValue(itemTypeId, out var itemType) ? itemType : null);

        _store.Setup(store => store.GetItemTypes(It.IsAny<bool>()))
            .Returns(() => _itemTypes.Values.OrderBy(itemType => itemType.Id).ToArray());

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
            .Returns<long>(orderId => _orders.TryGetValue(orderId, out var order) ? BuildOrderSnapshot(order) : null);

        _store.Setup(store => store.GetOrders())
            .Returns(() => _orders.Values.OrderBy(order => order.Id).Select(BuildOrderSnapshot).ToArray());

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
                    MarkingRequired = order.MarkingRequired,
                    MarkingApplies = order.MarkingApplies,
                    MarkingCodeCovered = order.MarkingCodeCovered,
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
                    MarkingRequired = current.MarkingRequired,
                    MarkingApplies = current.MarkingApplies,
                    MarkingCodeCovered = current.MarkingCodeCovered,
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
                    MarkingRequired = current.MarkingRequired,
                    MarkingApplies = current.MarkingApplies,
                    MarkingCodeCovered = current.MarkingCodeCovered,
                    MarkingExcelGeneratedAt = current.MarkingExcelGeneratedAt,
                    MarkingPrintedAt = current.MarkingPrintedAt
                };
            });

        _store.Setup(store => store.GetMarkingOrdersByItemIds(It.IsAny<IReadOnlyCollection<long>>()))
            .Returns<IReadOnlyCollection<long>>(itemIds =>
            {
                var ids = itemIds.ToHashSet();
                return _markingOrders.Values
                    .Where(order => order.ItemId.HasValue && ids.Contains(order.ItemId.Value))
                    .Select(CloneMarkingOrder)
                    .ToArray();
            });

        _store.Setup(store => store.GetMarkingOrdersByIds(It.IsAny<IReadOnlyCollection<Guid>>()))
            .Returns<IReadOnlyCollection<Guid>>(markingOrderIds =>
            {
                var ids = markingOrderIds.ToHashSet();
                return _markingOrders.Values
                    .Where(order => ids.Contains(order.Id))
                    .Select(CloneMarkingOrder)
                    .ToArray();
            });

        _store.Setup(store => store.AddMarkingOrder(It.IsAny<MarkingOrder>()))
            .Callback<MarkingOrder>(order =>
            {
                _markingOrders[order.Id] = CloneMarkingOrder(order);
            });

        _store.Setup(store => store.MarkMarkingOrdersPrinted(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<DateTime>()))
            .Callback<IReadOnlyCollection<Guid>, DateTime>((ids, printedAt) =>
            {
                foreach (var id in ids)
                {
                    if (!_markingOrders.TryGetValue(id, out var current))
                    {
                        continue;
                    }

                    _markingOrders[id] = new MarkingOrder
                    {
                        Id = current.Id,
                        OrderId = current.OrderId,
                        ItemId = current.ItemId,
                        Gtin = current.Gtin,
                        RequestedQuantity = current.RequestedQuantity,
                        RequestNumber = current.RequestNumber,
                        Status = MarkingOrderStatus.Printed,
                        Notes = current.Notes,
                        SourceType = current.SourceType,
                        SourceOrderId = current.SourceOrderId,
                        RequestedAt = current.RequestedAt,
                        CodesBoundAt = printedAt,
                        CreatedAt = current.CreatedAt,
                        UpdatedAt = printedAt
                    };
                }
            });

        _store.Setup(store => store.MarkOrdersPrinted(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<DateTime>()));

        _store.Setup(store => store.AddMarkingCodeImport(It.IsAny<MarkingCodeImport>()))
            .Returns<MarkingCodeImport>(import => import.Id);

        _store.Setup(store => store.AddMarkingCodes(It.IsAny<IReadOnlyList<MarkingCode>>()))
            .Callback<IReadOnlyList<MarkingCode>>(codes =>
            {
                foreach (var code in codes)
                {
                    _markingCodes[code.Id] = new MarkingCode
                    {
                        Id = code.Id,
                        Code = code.Code,
                        CodeHash = code.CodeHash,
                        Gtin = code.Gtin,
                        MarkingOrderId = code.MarkingOrderId,
                        ImportId = code.ImportId,
                        Status = code.Status,
                        ReceiptDocId = code.ReceiptDocId,
                        ReceiptLineId = code.ReceiptLineId,
                        SourceRowNumber = code.SourceRowNumber,
                        PrintedAt = code.PrintedAt,
                        AppliedAt = code.AppliedAt,
                        ReportedAt = code.ReportedAt,
                        IntroducedAt = code.IntroducedAt,
                        CreatedAt = code.CreatedAt,
                        UpdatedAt = code.UpdatedAt
                    };
                }
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

        _store.Setup(store => store.GetOrderIdsByOrderLineIds(It.IsAny<IReadOnlyCollection<long>>()))
            .Returns<IReadOnlyCollection<long>>(orderLineIds =>
            {
                var result = new Dictionary<long, long>();
                foreach (var orderLineId in orderLineIds.Distinct())
                {
                    if (_orderIdByOrderLineId.TryGetValue(orderLineId, out var orderId))
                    {
                        result[orderLineId] = orderId;
                    }
                }

                return result;
            });

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
                        QtyOrdered = line.QtyOrdered,
                        ProductionPurpose = line.ProductionPurpose,
                        ProductionPalletGroup = line.ProductionPalletGroup
                    })
                    .ToArray();
            });

        _store.Setup(store => store.GetOrderReceiptRemaining(It.IsAny<long>()))
            .Returns<long>(GetOrderReceiptRemainingLines);

        _store.Setup(store => store.GetOrderReceiptRemainingWithoutReservedStock(It.IsAny<long>()))
            .Returns<long>(orderId => _orderReceiptRemainingWithoutReservedStock.TryGetValue(orderId, out var lines)
                ? lines
                    .Select(CloneOrderReceiptLine)
                    .ToArray()
                : GetOrderReceiptRemainingLines(orderId));

        _store.Setup(store => store.GetOrderReceiptPlanLines(It.IsAny<long>()))
            .Returns<long>(orderId => _orderReceiptPlanLines.TryGetValue(orderId, out var lines)
                ? lines
                    .Select(CloneOrderReceiptPlanLine)
                    .ToArray()
                : Array.Empty<OrderReceiptPlanLine>());

        _store.Setup(store => store.ReplaceOrderReceiptPlanLines(It.IsAny<long>(), It.IsAny<IReadOnlyList<OrderReceiptPlanLine>>()))
            .Callback<long, IReadOnlyList<OrderReceiptPlanLine>>((orderId, lines) =>
            {
                _orderReceiptPlanLines[orderId] = (lines ?? Array.Empty<OrderReceiptPlanLine>())
                    .Select(CloneOrderReceiptPlanLine)
                    .ToArray();
            });

        _store.Setup(store => store.GetReservedOrderReceiptHuCodes(It.IsAny<long?>()))
            .Returns<long?>(excludeOrderId => _orderReceiptPlanLines
                .Where(pair => !excludeOrderId.HasValue || pair.Key != excludeOrderId.Value)
                .SelectMany(pair => pair.Value)
                .Select(line => NormalizeHu(line.ToHu))
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());

        _store.Setup(store => store.GetOrderShipmentRemaining(It.IsAny<long>()))
            .Returns<long>(orderId => BuildOrderShipmentRemaining(orderId));

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
                    QtyOrdered = line.QtyOrdered,
                    ProductionPurpose = line.ProductionPurpose,
                    ProductionPalletGroup = line.ProductionPalletGroup
                });
                _orderIdByOrderLineId[orderLineId] = line.OrderId;

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
                            QtyOrdered = qtyOrdered,
                            ProductionPurpose = current.ProductionPurpose,
                            ProductionPalletGroup = current.ProductionPalletGroup
                        };
                        return;
                    }
                }
            });

        _store.Setup(store => store.UpdateOrderLinePurpose(It.IsAny<long>(), It.IsAny<ProductionLinePurpose>()))
            .Callback<long, ProductionLinePurpose>((orderLineId, purpose) =>
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
                            QtyOrdered = current.QtyOrdered,
                            ProductionPurpose = purpose,
                            ProductionPalletGroup = current.ProductionPalletGroup
                        };
                        return;
                    }
                }
            });

        _store.Setup(store => store.UpdateOrderLineProductionPalletGroup(It.IsAny<long>(), It.IsAny<string?>()))
            .Callback<long, string?>((orderLineId, groupCode) =>
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
                            QtyOrdered = current.QtyOrdered,
                            ProductionPurpose = current.ProductionPurpose,
                            ProductionPalletGroup = groupCode
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

                _orderIdByOrderLineId.Remove(orderLineId);
            });

        _store.Setup(store => store.DeleteOrderLines(It.IsAny<long>()))
            .Callback<long>(orderId =>
            {
                if (_orderLinesByOrder.ContainsKey(orderId))
                {
                    foreach (var line in _orderLinesByOrder[orderId])
                    {
                        _orderIdByOrderLineId.Remove(line.Id);
                    }

                    _orderLinesByOrder[orderId].Clear();
                }
            });

        _store.Setup(store => store.DeleteOrder(It.IsAny<long>()))
            .Callback<long>(orderId =>
            {
                _orders.Remove(orderId);
                _orderLinesByOrder.Remove(orderId);
                foreach (var pair in _orderIdByOrderLineId.Where(pair => pair.Value == orderId).ToArray())
                {
                    _orderIdByOrderLineId.Remove(pair.Key);
                }
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
            .Returns<long>(orderId => BuildShippedTotalsByOrder(orderId));

        _store.Setup(store => store.GetShippedTotalsByOrderLine(It.IsAny<long>()))
            .Returns<long>(orderId => _shippedTotalsByOrderLine.TryGetValue(orderId, out var totals)
                ? new Dictionary<long, double>(totals)
                : BuildShippedTotalsByOrderLine(orderId));

        _store.As<IOverShippedOrderDiagnosticsStore>()
            .Setup(store => store.GetOverShippedOrderDiagnostics())
            .Returns(() => BuildOverShippedOrderDiagnostics());

        _store.As<IProductionPlanConsistencyDiagnosticsStore>()
            .Setup(store => store.GetProductionPlanConsistencyDiagnostics())
            .Returns(() => BuildProductionPlanConsistencyDiagnostics());

        _store.Setup(store => store.GetOrderShippedAt(It.IsAny<long>()))
            .Returns<long>(orderId => GetOrderShippedAtInternal(orderId));

        _store.Setup(store => store.HasOutboundDocs(It.IsAny<long>()))
            .Returns<long>(orderId => _ordersWithOutboundDocs.Contains(orderId));

        _store.Setup(store => store.GetAvailableQty(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>()))
            .Returns<long, long, string?>((itemId, locationId, huCode) => GetBalance(itemId, locationId, huCode));

        _store.Setup(store => store.GetLedgerBalance(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>()))
            .Returns<long, long, string?>((itemId, locationId, huCode) => GetBalance(itemId, locationId, huCode));

        _store.Setup(store => store.GetLedgerTotalsByItem())
            .Returns(() => BuildTotalsByItem());

        _store.Setup(store => store.GetStock(It.IsAny<string?>()))
            .Returns<string?>(search => BuildStockRows(search));

        _store.Setup(store => store.GetLedgerTotalsByHu())
            .Returns(() => BuildTotalsByHu());

        _store.Setup(store => store.GetHuStockRows())
            .Returns(() => BuildHuStockRows());

        _store.Setup(store => store.GetNegativeStockBalances())
            .Returns(() => BuildNegativeStockBalances());

        _store.Setup(store => store.GetHuOrderContextRows())
            .Returns(() => BuildHuOrderContextRows());

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
                    ProductionPurpose = line.ProductionPurpose,
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

        _store.Setup(store => store.DeleteDoc(It.IsAny<long>()))
            .Callback<long>(docId =>
            {
                _docs.Remove(docId);
                _linesByDoc.Remove(docId);
                foreach (var pallet in _productionPallets.Values.Where(pallet => pallet.PrdDocId == docId).ToArray())
                {
                    _productionPallets.Remove(pallet.Id);
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

        _store.Setup(store => store.PlanProductionPallets(It.IsAny<long>(), It.IsAny<DateTime>()))
            .Returns<long, DateTime>((docId, createdAt) =>
            {
                var doc = _docs.TryGetValue(docId, out var foundDoc) ? foundDoc : null;
                if (doc == null)
                {
                    return Array.Empty<ProductionPallet>();
                }

                foreach (var group in GetActiveDocLines(docId)
                             .Where(line => line.Qty > 0 && !string.IsNullOrWhiteSpace(line.ToHu))
                             .GroupBy(line => NormalizeHu(line.ToHu), StringComparer.OrdinalIgnoreCase))
                {
                    if (_productionPallets.Values.Any(pallet => pallet.PrdDocId == docId
                                                                && string.Equals(NormalizeHu(pallet.HuCode), group.Key, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var groupLines = group.OrderBy(line => line.Id).ToList();
                    var firstLine = groupLines[0];
                    var item = _items.TryGetValue(firstLine.ItemId, out var foundItem) ? foundItem : null;
                    var location = firstLine.ToLocationId.HasValue && _locations.TryGetValue(firstLine.ToLocationId.Value, out var foundLocation)
                        ? foundLocation
                        : null;
                    var palletId = _nextProductionPalletId++;
                    _productionPallets[palletId] = new ProductionPallet
                    {
                        Id = palletId,
                        PrdDocId = docId,
                        DocLineId = firstLine.Id,
                        OrderId = doc.OrderId,
                        OrderLineId = groupLines.Select(line => line.OrderLineId).Distinct().Count() == 1 ? firstLine.OrderLineId : null,
                        ItemId = firstLine.ItemId,
                        ItemName = item?.Name ?? string.Empty,
                        HuCode = firstLine.ToHu?.Trim() ?? string.Empty,
                        PlannedQty = groupLines.Sum(line => line.Qty),
                        ToLocationId = firstLine.ToLocationId,
                        ToLocationCode = location?.Code,
                        Status = ProductionPalletStatus.Planned,
                        CreatedAt = createdAt,
                        Lines = groupLines.Select((line, index) =>
                        {
                            var lineItem = _items.TryGetValue(line.ItemId, out var foundLineItem) ? foundLineItem : null;
                            return new ProductionPalletComponentLine
                            {
                                Id = (palletId * 1000) + index + 1,
                                ProductionPalletId = palletId,
                                DocLineId = line.Id,
                                OrderLineId = line.OrderLineId,
                                ItemId = line.ItemId,
                                ItemName = lineItem?.Name ?? string.Empty,
                                Brand = lineItem?.Brand,
                                Uom = string.IsNullOrWhiteSpace(lineItem?.BaseUom) ? "шт" : lineItem!.BaseUom,
                                PlannedQty = line.Qty,
                                CreatedAt = createdAt
                            };
                        }).ToArray()
                    };
                }

                return GetProductionPallets(docId);
            });

        _store.Setup(store => store.CreateProductionPalletHuCode(It.IsAny<string?>()))
            .Returns<string?>(createdBy =>
            {
                var existing = BuildExistingHuNumbers();
                while (existing.Contains(_nextProductionPalletHuNumber))
                {
                    _nextProductionPalletHuNumber++;
                }

                var code = $"HU-{_nextProductionPalletHuNumber:0000000}";
                _nextProductionPalletHuNumber++;
                _hus[code] = new HuRecord
                {
                    Id = _hus.Count + 1,
                    Code = code,
                    Status = "OPEN",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = createdBy
                };
                return code;
            });

        _store.Setup(store => store.GetProductionPalletsByDoc(It.IsAny<long>()))
            .Returns<long>(GetProductionPallets);

        _store.Setup(store => store.GetProductionPalletByHu(It.IsAny<string>()))
            .Returns<string>(huCode =>
            {
                var active = _productionPallets.Values.FirstOrDefault(pallet =>
                    string.Equals(NormalizeHu(pallet.HuCode), NormalizeHu(huCode), StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase));
                var found = active ?? _productionPallets.Values.FirstOrDefault(pallet =>
                    string.Equals(NormalizeHu(pallet.HuCode), NormalizeHu(huCode), StringComparison.OrdinalIgnoreCase));
                return found == null ? null : CloneProductionPallet(found);
            });

        _store.Setup(store => store.GetFilledProductionPalletsByItemAndLocation(It.IsAny<long>(), It.IsAny<long>()))
            .Returns<long, long>((itemId, locationId) => _productionPallets.Values
                .Where(pallet => string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
                .Where(pallet => pallet.ToLocationId == locationId)
                .Where(pallet => pallet.ItemId == itemId
                                 || pallet.Lines.Any(line => line.ItemId == itemId))
                .Select(CloneProductionPallet)
                .OrderBy(pallet => pallet.HuCode, StringComparer.OrdinalIgnoreCase)
                .ToArray());

        _store.Setup(store => store.GetFilledProductionPalletStockMetrics())
            .Returns(BuildFilledProductionPalletStockMetrics);

        _store.Setup(store => store.GetActiveProductionPalletWorkItems())
            .Returns(() => _productionPallets.Values
                .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
                .GroupBy(pallet => pallet.PrdDocId)
                .Select(group =>
                {
                    var doc = _docs.TryGetValue(group.Key, out var foundDoc) ? foundDoc : null;
                    var rows = group.ToList();
                    var filledRows = rows
                        .Where(pallet => string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    return new ProductionPalletWorkItem
                    {
                        PrdDocId = group.Key,
                        PrdDocRef = doc?.DocRef ?? string.Empty,
                        PrdStatus = doc == null ? string.Empty : DocTypeMapper.StatusToString(doc.Status),
                        OrderId = doc?.OrderId ?? rows.Select(pallet => pallet.OrderId).FirstOrDefault(id => id.HasValue),
                        OrderRef = doc?.OrderRef ?? rows.Select(pallet => pallet.OrderId.HasValue && _orders.TryGetValue(pallet.OrderId.Value, out var order) ? order.OrderRef : null).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                        Summary = new ProductionPalletSummary
                        {
                            PlannedPalletCount = rows.Count,
                            PlannedQty = rows.Sum(pallet => pallet.PlannedQty),
                            FilledPalletCount = filledRows.Count,
                            FilledQty = filledRows.Sum(pallet => pallet.PlannedQty),
                            RemainingPalletCount = rows.Count - filledRows.Count,
                            RemainingQty = Math.Max(0, rows.Sum(pallet => pallet.PlannedQty) - filledRows.Sum(pallet => pallet.PlannedQty))
                        }
                    };
                })
                .Where(item => item.Summary.FilledPalletCount < item.Summary.PlannedPalletCount)
                .OrderByDescending(item => item.PrdDocId)
                .ToArray());

        _store.Setup(store => store.HasProductionPallets(It.IsAny<long>()))
            .Returns<long>(docId => _productionPallets.Values.Any(pallet =>
                pallet.PrdDocId == docId
                && !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase)));

        _store.Setup(store => store.HasProductionPalletLinesForDoc(It.IsAny<long>()))
            .Returns(false);

        _store.Setup(store => store.ClearPlannedProductionPalletPlan(It.IsAny<long>()))
            .Callback<long>(docId =>
            {
                var activePallets = _productionPallets.Values
                    .Where(pallet => pallet.PrdDocId == docId
                                     && !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (activePallets.Any(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("План паллет уже напечатан или наполнен. Переназначение HU запрещено.");
                }

                ClearProductionPalletPlanInHarness(docId);
            });

        _store.Setup(store => store.CountLedgerEntriesByDocId(It.IsAny<long>()))
            .Returns<long>(docId => _postedLedger.Count(entry => entry.DocId == docId));

        _store.Setup(store => store.CancelProductionPalletPlan(It.IsAny<long>()))
            .Returns<long>(docId =>
            {
                if (_productionPallets.Values.Any(pallet =>
                        pallet.PrdDocId == docId
                        && string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("Нельзя удалить план паллет: есть уже наполненные паллеты.");
                }

                if (_postedLedger.Any(entry => entry.DocId == docId))
                {
                    throw new InvalidOperationException("Нельзя удалить план паллет: по выпуску уже есть движения склада.");
                }

                var removedPalletCount = _productionPallets.Values.Count(pallet =>
                    pallet.PrdDocId == docId
                    && !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase));
                var removedLineCount = _linesByDoc.TryGetValue(docId, out var docLines) ? docLines.Count : 0;
                ClearProductionPalletPlanInHarness(docId);
                return new ProductionPalletPlanCleanupCounts
                {
                    RemovedPalletCount = removedPalletCount,
                    RemovedLineCount = removedLineCount
                };
            });

        _store.Setup(store => store.AdoptProductionPalletPlan(
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<IReadOnlyDictionary<long, long>>()))
            .Returns<long, long, long, long, IReadOnlyDictionary<long, long>>((sourcePrdDocId, targetPrdDocId, sourceOrderId, targetOrderId, targetOrderLineIdByItemId) =>
            {
                var sourcePallets = _productionPallets.Values
                    .Where(pallet => pallet.PrdDocId == sourcePrdDocId
                                     && !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var transferredHuCodes = sourcePallets
                    .Select(pallet => pallet.HuCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var transferredLineCount = _linesByDoc.TryGetValue(sourcePrdDocId, out var sourceLines)
                    ? sourceLines.Count
                    : 0;

                if (!_linesByDoc.TryGetValue(targetPrdDocId, out var targetLines))
                {
                    targetLines = new List<DocLine>();
                    _linesByDoc[targetPrdDocId] = targetLines;
                }

                if (_linesByDoc.TryGetValue(sourcePrdDocId, out sourceLines))
                {
                    foreach (var line in sourceLines.ToArray())
                    {
                        if (!targetOrderLineIdByItemId.TryGetValue(line.ItemId, out var targetLineId))
                        {
                            continue;
                        }

                        sourceLines.Remove(line);
                        targetLines.Add(new DocLine
                        {
                            Id = line.Id,
                            DocId = targetPrdDocId,
                            ReplacesLineId = line.ReplacesLineId,
                            OrderLineId = targetLineId,
                            ProductionPurpose = ProductionLinePurpose.CustomerOrder,
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
                    }
                }

                foreach (var pallet in sourcePallets)
                {
                    var targetLineId = targetOrderLineIdByItemId.TryGetValue(pallet.ItemId, out var foundTargetLineId)
                        ? foundTargetLineId
                        : pallet.OrderLineId;
                    _productionPallets[pallet.Id] = new ProductionPallet
                    {
                        Id = pallet.Id,
                        PrdDocId = targetPrdDocId,
                        DocLineId = pallet.DocLineId,
                        OrderId = targetOrderId,
                        OrderLineId = targetLineId,
                        ItemId = pallet.ItemId,
                        ItemName = pallet.ItemName,
                        HuCode = pallet.HuCode,
                        PlannedQty = pallet.PlannedQty,
                        ToLocationId = pallet.ToLocationId,
                        ToLocationCode = pallet.ToLocationCode,
                        Status = pallet.Status,
                        FilledAt = pallet.FilledAt,
                        FilledByDeviceId = pallet.FilledByDeviceId,
                        CreatedAt = pallet.CreatedAt,
                        Lines = pallet.Lines.Select(line => new ProductionPalletComponentLine
                        {
                            Id = line.Id,
                            ProductionPalletId = line.ProductionPalletId,
                            DocLineId = line.DocLineId,
                            OrderLineId = targetOrderLineIdByItemId.TryGetValue(line.ItemId, out var componentTargetLineId)
                                ? componentTargetLineId
                                : line.OrderLineId,
                            ItemId = line.ItemId,
                            ItemName = line.ItemName,
                            Brand = line.Brand,
                            Uom = line.Uom,
                            PlannedQty = line.PlannedQty,
                            FilledQty = line.FilledQty,
                            CreatedAt = line.CreatedAt
                        }).ToArray()
                    };
                }

                return new ProductionPalletPlanAdoptionResult
                {
                    Success = true,
                    Message = "План паллет перенесён на клиентский заказ.",
                    SourceOrderId = sourceOrderId,
                    TargetOrderId = targetOrderId,
                    SourcePrdDocId = sourcePrdDocId,
                    TargetPrdDocId = targetPrdDocId,
                    TransferredPalletCount = sourcePallets.Length,
                    TransferredLineCount = transferredLineCount,
                    TransferredHuCodes = transferredHuCodes
                };
            });

        _store.Setup(store => store.GetFilledProductionPalletQtyByOrderLine(It.IsAny<long>(), It.IsAny<long?>()))
            .Returns<long, long?>((orderLineId, excludePalletId) => _productionPallets.Values
                .Where(pallet => (!excludePalletId.HasValue || pallet.Id != excludePalletId.Value)
                                 && string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
                .SelectMany(pallet => pallet.Lines.Count > 0
                    ? pallet.Lines
                    : new[]
                    {
                        new ProductionPalletComponentLine
                        {
                            OrderLineId = pallet.OrderLineId,
                            PlannedQty = pallet.PlannedQty
                        }
                    })
                .Where(line => line.OrderLineId == orderLineId)
                .Sum(line => line.PlannedQty));

        _store.Setup(store => store.MarkProductionPalletFilled(It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<string?>()))
            .Callback<long, DateTime, string?>((palletId, filledAt, deviceId) =>
            {
                if (!_productionPallets.TryGetValue(palletId, out var current)
                    || string.Equals(current.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _productionPallets[palletId] = new ProductionPallet
                {
                    Id = current.Id,
                    PrdDocId = current.PrdDocId,
                    DocLineId = current.DocLineId,
                    OrderId = current.OrderId,
                    OrderLineId = current.OrderLineId,
                    ItemId = current.ItemId,
                    ItemName = current.ItemName,
                    HuCode = current.HuCode,
                    PlannedQty = current.PlannedQty,
                    ToLocationId = current.ToLocationId,
                    ToLocationCode = current.ToLocationCode,
                    Status = ProductionPalletStatus.Filled,
                    FilledAt = filledAt,
                    FilledByDeviceId = deviceId,
                    CreatedAt = current.CreatedAt,
                    Lines = current.Lines.Select(line => new ProductionPalletComponentLine
                    {
                        Id = line.Id,
                        ProductionPalletId = line.ProductionPalletId,
                        DocLineId = line.DocLineId,
                        OrderLineId = line.OrderLineId,
                        ItemId = line.ItemId,
                        ItemName = line.ItemName,
                        Brand = line.Brand,
                        Uom = line.Uom,
                        PlannedQty = line.PlannedQty,
                        FilledQty = line.PlannedQty,
                        CreatedAt = line.CreatedAt
                    }).ToArray()
                };
            });

        _store.Setup(store => store.UpdateProductionPalletHu(It.IsAny<long>(), It.IsAny<string>()))
            .Callback<long, string>((palletId, huCode) =>
            {
                if (!_productionPallets.TryGetValue(palletId, out var current)
                    || !string.Equals(current.Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _productionPallets[palletId] = new ProductionPallet
                {
                    Id = current.Id,
                    PrdDocId = current.PrdDocId,
                    DocLineId = current.DocLineId,
                    OrderId = current.OrderId,
                    OrderLineId = current.OrderLineId,
                    ItemId = current.ItemId,
                    ItemName = current.ItemName,
                    HuCode = huCode,
                    PlannedQty = current.PlannedQty,
                    ToLocationId = current.ToLocationId,
                    ToLocationCode = current.ToLocationCode,
                    Status = current.Status,
                    FilledAt = current.FilledAt,
                    FilledByDeviceId = current.FilledByDeviceId,
                    CreatedAt = current.CreatedAt,
                    Lines = current.Lines
                };

                foreach (var line in current.Lines)
                {
                    if (!_linesByDoc.TryGetValue(current.PrdDocId, out var docLines))
                    {
                        continue;
                    }

                    for (var index = 0; index < docLines.Count; index++)
                    {
                        if (docLines[index].Id != line.DocLineId)
                        {
                            continue;
                        }

                        var docLine = docLines[index];
                        docLines[index] = new DocLine
                        {
                            Id = docLine.Id,
                            DocId = docLine.DocId,
                            ReplacesLineId = docLine.ReplacesLineId,
                            OrderLineId = docLine.OrderLineId,
                            ProductionPurpose = docLine.ProductionPurpose,
                            ItemId = docLine.ItemId,
                            Qty = docLine.Qty,
                            QtyInput = docLine.QtyInput,
                            UomCode = docLine.UomCode,
                            FromLocationId = docLine.FromLocationId,
                            ToLocationId = docLine.ToLocationId,
                            FromHu = docLine.FromHu,
                            ToHu = huCode,
                            PackSingleHu = docLine.PackSingleHu
                        };
                    }
                }
            });

        _store.Setup(store => store.MarkProductionPalletsPrintedByOrder(It.IsAny<long>(), It.IsAny<DateTime>()))
            .Returns<long, DateTime>((orderId, _) =>
            {
                var updated = 0;
                foreach (var pair in _productionPallets.ToArray())
                {
                    var current = pair.Value;
                    if (current.OrderId != orderId
                        || !string.Equals(current.Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    _productionPallets[pair.Key] = new ProductionPallet
                    {
                        Id = current.Id,
                        PrdDocId = current.PrdDocId,
                        DocLineId = current.DocLineId,
                        OrderId = current.OrderId,
                        OrderLineId = current.OrderLineId,
                        ItemId = current.ItemId,
                        ItemName = current.ItemName,
                        HuCode = current.HuCode,
                        PlannedQty = current.PlannedQty,
                        ToLocationId = current.ToLocationId,
                        ToLocationCode = current.ToLocationCode,
                        Status = ProductionPalletStatus.Printed,
                        FilledAt = current.FilledAt,
                        FilledByDeviceId = current.FilledByDeviceId,
                        CreatedAt = current.CreatedAt,
                        Lines = current.Lines
                    };
                    updated++;
                }

                return updated;
            });

        _store.Setup(store => store.CountKmCodesByReceiptLine(It.IsAny<long>()))
            .Returns<long>(docLineId => _kmCodeCountByReceiptLine.TryGetValue(docLineId, out var count) ? count : 0);

        _store.Setup(store => store.CountKmCodesByShipmentLine(It.IsAny<long>()))
            .Returns(0);

        _store.Setup(store => store.CountProductionMarkingCodesByReceiptLine(It.IsAny<long>()))
            .Returns<long>(docLineId => _markingCodes.Values.Count(code => code.ReceiptLineId == docLineId));

        _store.Setup(store => store.CountMarkingCodesByMarkingOrder(It.IsAny<Guid>()))
            .Returns<Guid>(markingOrderId => _markingCodes.Values.Count(code =>
                code.MarkingOrderId == markingOrderId
                && code.Status != MarkingCodeStatus.Voided));

        _store.Setup(store => store.CountFreeProductionMarkingCodesByItem(It.IsAny<long>(), It.IsAny<string?>()))
            .Returns<long, string?>((itemId, gtin) =>
            {
                var normalizedGtin = NormalizeText(gtin);
                return _markingCodes.Values
                    .Where(code => code.ReceiptDocId == null
                                   && code.ReceiptLineId == null
                                   && code.Status is MarkingCodeStatus.Reserved or MarkingCodeStatus.Printed)
                    .Select(code => (Code: code, Order: _markingOrders.TryGetValue(code.MarkingOrderId, out var order) ? order : null))
                    .Count(pair => pair.Order != null
                                   && pair.Order.Status is not MarkingOrderStatus.Cancelled and not MarkingOrderStatus.Failed
                                   && (pair.Order.ItemId == itemId
                                       || (!string.IsNullOrWhiteSpace(normalizedGtin)
                                           && (string.Equals(NormalizeText(pair.Order.Gtin), normalizedGtin, StringComparison.OrdinalIgnoreCase)
                                               || string.Equals(NormalizeText(pair.Code.Gtin), normalizedGtin, StringComparison.OrdinalIgnoreCase)))));
            });

        _store.Setup(store => store.CountAvailableProductionMarkingCodesForReceipt(It.IsAny<long?>(), It.IsAny<long>(), It.IsAny<string?>()))
            .Returns<long?, long, string?>((sourceOrderId, itemId, gtin) =>
                GetAvailableProductionMarkingCodes(sourceOrderId, itemId, gtin, int.MaxValue).Count);

        _store.Setup(store => store.GetAvailableProductionMarkingCodeIdsForReceipt(It.IsAny<long?>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<int>()))
            .Returns<long?, long, string?, int>((sourceOrderId, itemId, gtin, take) =>
                GetAvailableProductionMarkingCodes(sourceOrderId, itemId, gtin, take)
                    .Select(code => code.Id)
                    .ToArray());

        _store.Setup(store => store.AssignProductionMarkingCodesToReceipt(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<DateTime>()))
            .Returns<IReadOnlyList<Guid>, long, long, DateTime>((codeIds, docId, lineId, appliedAt) =>
            {
                var updated = 0;
                foreach (var codeId in codeIds)
                {
                    if (!_markingCodes.TryGetValue(codeId, out var code)
                        || code.ReceiptLineId.HasValue
                        || code.ReceiptDocId.HasValue
                        || code.Status is not MarkingCodeStatus.Reserved and not MarkingCodeStatus.Printed)
                    {
                        continue;
                    }

                    _markingCodes[codeId] = new MarkingCode
                    {
                        Id = code.Id,
                        Code = code.Code,
                        CodeHash = code.CodeHash,
                        Gtin = code.Gtin,
                        MarkingOrderId = code.MarkingOrderId,
                        ImportId = code.ImportId,
                        Status = MarkingCodeStatus.Applied,
                        ReceiptDocId = docId,
                        ReceiptLineId = lineId,
                        SourceRowNumber = code.SourceRowNumber,
                        PrintedAt = code.PrintedAt,
                        AppliedAt = appliedAt,
                        ReportedAt = code.ReportedAt,
                        IntroducedAt = code.IntroducedAt,
                        CreatedAt = code.CreatedAt,
                        UpdatedAt = appliedAt
                    };
                    updated++;
                }

                return updated;
            });
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

    private IReadOnlyList<ProductionPallet> GetProductionPallets(long docId)
    {
        return _productionPallets.Values
            .Where(pallet => pallet.PrdDocId == docId)
            .OrderBy(pallet => pallet.Id)
            .Select(CloneProductionPallet)
            .ToArray();
    }

    private IReadOnlyList<OrderReceiptLine> GetOrderReceiptRemainingLines(long orderId)
    {
        if (_orderReceiptRemaining.TryGetValue(orderId, out var seeded))
        {
            return seeded.Select(CloneOrderReceiptLine).ToArray();
        }

        if (!_orderLinesByOrder.TryGetValue(orderId, out var orderLines))
        {
            return Array.Empty<OrderReceiptLine>();
        }

        return orderLines
            .OrderBy(line => line.Id)
            .Select(line =>
            {
                var producedQty = GetProducedQtyForOrderLine(line.Id);
                var reservedQty = _orders.TryGetValue(orderId, out var order) && order.Type == OrderType.Customer
                    ? GetReservedQtyForOrderLine(line.Id)
                    : 0;
                var receivedQty = producedQty + reservedQty;
                return new OrderReceiptLine
                {
                    OrderLineId = line.Id,
                    OrderId = orderId,
                    ItemId = line.ItemId,
                    ItemName = _items.TryGetValue(line.ItemId, out var item) ? item.Name : string.Empty,
                    QtyOrdered = line.QtyOrdered,
                    QtyReceived = receivedQty,
                    QtyRemaining = Math.Max(0, line.QtyOrdered - receivedQty),
                    SortOrder = 0,
                    ProductionPurpose = line.ProductionPurpose
                };
            })
            .ToArray();
    }

    private Order BuildOrderSnapshot(Order order)
    {
        var lines = GetOrderLines(order.Id);
        var markableLines = lines
            .Where(line => _items.TryGetValue(line.ItemId, out var item) && item.IsChestnyZnakMarkingRequired)
            .ToArray();
        var markingApplies = markableLines.Length > 0;
        var markingCodeCovered = markingApplies && markableLines
            .GroupBy(line => line.ItemId)
            .All(group =>
            {
                var item = _items[group.Key];
                var requiredQty = group.Sum(line => GetRequiredMarkingQty(order, line));
                if (requiredQty <= 0.000001)
                {
                    return true;
                }

                var freeCodes = CountFreeMarkingCodesForItem(group.Key, item.Gtin);
                var boundCodes = group.Sum(line => CountBoundMarkingCodesForOrderLine(line.Id));
                return freeCodes + boundCodes + 0.000001 >= requiredQty;
            });

        return new Order
        {
            Id = order.Id,
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
            IsLegacyExcelGeneratedMarkingStatus = order.IsLegacyExcelGeneratedMarkingStatus,
            MarkingRequired = markingApplies && !markingCodeCovered,
            MarkingApplies = markingApplies,
            MarkingCodeCovered = markingCodeCovered,
            MarkingExcelGeneratedAt = order.MarkingExcelGeneratedAt,
            MarkingPrintedAt = order.MarkingPrintedAt
        };
    }

    private double GetRequiredMarkingQty(Order order, OrderLine line)
    {
        if (order.Type == OrderType.Internal)
        {
            return Math.Max(0, line.QtyOrdered);
        }

        var shippedQty = BuildShippedTotalsByOrderLine(order.Id).TryGetValue(line.Id, out var shipped)
            ? shipped
            : 0;
        return Math.Max(0, line.QtyOrdered - shipped - GetReservedQtyForOrderLine(line.Id));
    }

    private Dictionary<long, double> BuildShippedTotalsByOrder(long orderId)
    {
        var result = new Dictionary<long, double>();
        foreach (var pair in BuildShippedTotalsByOrderLine(orderId))
        {
            var itemId = _orderLinesByOrder[orderId].First(line => line.Id == pair.Key).ItemId;
            result[itemId] = result.TryGetValue(itemId, out var current)
                ? current + pair.Value
                : pair.Value;
        }

        return result;
    }

    private Dictionary<long, double> BuildShippedTotalsByOrderLine(long orderId)
    {
        var totals = new Dictionary<long, double>();
        foreach (var doc in _docs.Values.Where(doc => doc.OrderId == orderId
                                                      && doc.Type == DocType.Outbound
                                                      && doc.Status == DocStatus.Closed))
        {
            foreach (var line in GetActiveDocLines(doc.Id).Where(line => line.OrderLineId.HasValue && line.Qty > 0))
            {
                var orderLineId = line.OrderLineId!.Value;
                totals[orderLineId] = totals.TryGetValue(orderLineId, out var current)
                    ? current + line.Qty
                    : line.Qty;
            }
        }

        return totals;
    }

    private DateTime? GetOrderShippedAtInternal(long orderId)
    {
        return _docs.Values
            .Where(doc => doc.OrderId == orderId
                          && doc.Type == DocType.Outbound
                          && doc.Status == DocStatus.Closed
                          && doc.ClosedAt.HasValue)
            .Select(doc => doc.ClosedAt)
            .OrderByDescending(value => value)
            .FirstOrDefault();
    }

    private IReadOnlyList<OrderShipmentLine> BuildOrderShipmentRemaining(long orderId)
    {
        if (!_orderLinesByOrder.TryGetValue(orderId, out var lines))
        {
            return Array.Empty<OrderShipmentLine>();
        }

        var shippedTotals = _shippedTotalsByOrderLine.TryGetValue(orderId, out var seededTotals)
            ? seededTotals
            : BuildShippedTotalsByOrderLine(orderId);
        return lines
            .Select(line =>
            {
                var shippedQty = shippedTotals.TryGetValue(line.Id, out var shipped)
                    ? shipped
                    : 0d;
                var remainingQty = Math.Max(0d, line.QtyOrdered - shippedQty);
                var itemName = _items.TryGetValue(line.ItemId, out var item)
                    ? item.Name
                    : string.Empty;
                return new OrderShipmentLine
                {
                    OrderLineId = line.Id,
                    OrderId = orderId,
                    ItemId = line.ItemId,
                    ItemName = itemName,
                    QtyOrdered = line.QtyOrdered,
                    QtyShipped = shippedQty,
                    QtyRemaining = remainingQty
                };
            })
            .Where(line => line.QtyRemaining > 0.000001)
            .ToArray();
    }

    private IReadOnlyList<OverShippedOrderDiagnosticItem> BuildOverShippedOrderDiagnostics()
    {
        var result = new List<OverShippedOrderDiagnosticItem>();
        foreach (var order in _orders.Values
                     .Where(order => order.Type == OrderType.Customer
                                     && order.Status is not OrderStatus.Cancelled and not OrderStatus.Merged)
                     .OrderBy(order => order.OrderRef, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(order => order.Id))
        {
            if (!_orderLinesByOrder.TryGetValue(order.Id, out var orderLines))
            {
                continue;
            }

            var shippedByLine = _shippedTotalsByOrderLine.TryGetValue(order.Id, out var seededTotals)
                ? new Dictionary<long, double>(seededTotals)
                : BuildShippedTotalsByOrderLine(order.Id);
            var closedOutboundDocs = _docs.Values
                .Where(doc => doc.OrderId == order.Id
                              && doc.Type == DocType.Outbound
                              && doc.Status == DocStatus.Closed)
                .ToArray();
            var activeOutboundLines = closedOutboundDocs
                .SelectMany(doc => GetActiveDocLines(doc.Id).Select(line => (Doc: doc, Line: line)))
                .Where(row => row.Line.OrderLineId.HasValue && row.Line.Qty > 0)
                .ToArray();
            var ledgerEntries = closedOutboundDocs
                .SelectMany(doc => _postedLedger.Where(entry => entry.DocId == doc.Id))
                .ToArray();

            foreach (var group in orderLines.Where(line => line.QtyOrdered > StockQuantityRules.QtyTolerance)
                         .GroupBy(line => line.ItemId)
                         .OrderBy(group => _items.TryGetValue(group.Key, out var item) ? item.Name : string.Empty, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(group => group.Key))
            {
                var lineIds = group.Select(line => line.Id).ToHashSet();
                var qtyOrdered = group.Sum(line => Math.Max(0d, line.QtyOrdered));
                var shippedByApiReadModel = group.Sum(line => shippedByLine.TryGetValue(line.Id, out var shipped) ? Math.Max(0d, shipped) : 0d);
                var shippedByClosedOutbound = activeOutboundLines
                    .Where(row => row.Line.ItemId == group.Key && lineIds.Contains(row.Line.OrderLineId!.Value))
                    .Sum(row => row.Line.Qty);
                var shippedByLedger = ledgerEntries
                    .Where(entry => entry.ItemId == group.Key && entry.QtyDelta < 0)
                    .Sum(entry => -entry.QtyDelta);
                var overShippedQty = Math.Max(0d, Math.Max(shippedByApiReadModel, Math.Max(shippedByClosedOutbound, shippedByLedger)) - qtyOrdered);
                if (overShippedQty <= StockQuantityRules.QtyTolerance)
                {
                    continue;
                }

                var itemName = _items.TryGetValue(group.Key, out var item)
                    ? item.Name
                    : string.Empty;
                var row = new OverShippedOrderDiagnosticItem
                {
                    OrderId = order.Id,
                    OrderRef = order.OrderRef,
                    ItemId = group.Key,
                    ItemName = itemName,
                    QtyOrdered = qtyOrdered,
                    ShippedByApiReadModel = shippedByApiReadModel,
                    ShippedByClosedOutbound = shippedByClosedOutbound,
                    ShippedByLedger = shippedByLedger,
                    OverShippedQty = overShippedQty,
                    OutboundDocs = activeOutboundLines
                        .Where(entry => entry.Line.ItemId == group.Key && lineIds.Contains(entry.Line.OrderLineId!.Value))
                        .OrderBy(entry => entry.Doc.Id)
                        .ThenBy(entry => entry.Line.Id)
                        .Select(entry => new OverShippedOutboundDocLine
                        {
                            DocId = entry.Doc.Id,
                            DocRef = entry.Doc.DocRef,
                            Status = "CLOSED",
                            ClosedAt = entry.Doc.ClosedAt,
                            DocLineId = entry.Line.Id,
                            Qty = entry.Line.Qty,
                            FromHu = entry.Line.FromHu,
                            OrderLineId = entry.Line.OrderLineId
                        })
                        .ToArray(),
                    LedgerEntries = ledgerEntries
                        .Where(entry => entry.ItemId == group.Key)
                        .OrderBy(entry => entry.Id)
                        .Select(entry => new OverShippedLedgerEntry
                        {
                            LedgerId = entry.Id,
                            DocId = entry.DocId,
                            ItemId = entry.ItemId,
                            HuCode = entry.HuCode,
                            QtyDelta = entry.QtyDelta
                        })
                        .ToArray()
                };

                result.Add(new OverShippedOrderDiagnosticItem
                {
                    OrderId = row.OrderId,
                    OrderRef = row.OrderRef,
                    ItemId = row.ItemId,
                    ItemName = row.ItemName,
                    QtyOrdered = row.QtyOrdered,
                    ShippedByApiReadModel = row.ShippedByApiReadModel,
                    ShippedByClosedOutbound = row.ShippedByClosedOutbound,
                    ShippedByLedger = row.ShippedByLedger,
                    OverShippedQty = row.OverShippedQty,
                    OutboundDocs = row.OutboundDocs,
                    LedgerEntries = row.LedgerEntries,
                    Recommendation = BuildOverShippedRecommendation(row)
                });
            }
        }

        return result;
    }

    private static string BuildOverShippedRecommendation(OverShippedOrderDiagnosticItem row)
    {
        var activeOver = row.ShippedByClosedOutbound - row.QtyOrdered > StockQuantityRules.QtyTolerance;
        var ledgerOver = row.ShippedByLedger - row.QtyOrdered > StockQuantityRules.QtyTolerance;
        if (activeOver && ledgerOver)
        {
            return "REAL_OVER_SHIPMENT_REVIEW_REQUIRED";
        }

        if (activeOver)
        {
            return "DOC_LINES_OVER_ORDERED_LEDGER_NOT_OVER_SHIPPED_REVIEW_DOC_LINES";
        }

        if (ledgerOver)
        {
            return "LEDGER_OVER_ORDERED_REVIEW_LEDGER_AND_CREATE_CORRECTION_DRAFT_IF_CONFIRMED";
        }

        return "NO_ACTION";
    }

    private IReadOnlyList<ProductionPlanConsistencyDiagnosticItem> BuildProductionPlanConsistencyDiagnostics()
    {
        var allOrderLines = _orderLinesByOrder.Values
            .SelectMany(lines => lines)
            .ToDictionary(line => line.Id, line => line);
        var keys = new HashSet<(long OrderId, long ItemId)>();
        foreach (var line in allOrderLines.Values)
        {
            keys.Add((line.OrderId, line.ItemId));
        }

        var prdRows = BuildProductionPlanConsistencyPrdRows(allOrderLines);
        foreach (var row in prdRows)
        {
            keys.Add((row.OrderId, row.ItemId));
        }

        var palletRows = BuildProductionPlanConsistencyPalletRows();
        foreach (var row in palletRows)
        {
            keys.Add((row.OrderId, row.ItemId));
        }

        var ledgerQtyByKey = BuildProductionPlanConsistencyLedgerQty();
        foreach (var key in ledgerQtyByKey.Keys)
        {
            keys.Add(key);
        }

        var result = new List<ProductionPlanConsistencyDiagnosticItem>();
        foreach (var key in keys.OrderBy(key => _orders.TryGetValue(key.OrderId, out var order) ? order.OrderRef : string.Empty, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(key => _items.TryGetValue(key.ItemId, out var item) ? item.Name : string.Empty, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(key => key.ItemId))
        {
            if (!_orders.TryGetValue(key.OrderId, out var order))
            {
                continue;
            }

            var orderQty = _orderLinesByOrder.TryGetValue(key.OrderId, out var orderLines)
                ? orderLines.Where(line => line.ItemId == key.ItemId).Sum(line => Math.Max(0d, line.QtyOrdered))
                : 0d;
            var itemPrdRows = prdRows
                .Where(row => row.OrderId == key.OrderId && row.ItemId == key.ItemId)
                .ToArray();
            var itemPalletRows = palletRows
                .Where(row => row.OrderId == key.OrderId && row.ItemId == key.ItemId)
                .ToArray();
            var openPrdDocQty = itemPrdRows
                .Where(row => !string.Equals(row.PrdDoc.Status, "CLOSED", StringComparison.OrdinalIgnoreCase))
                .Sum(row => row.Qty);
            var closedPrdDocQty = itemPrdRows
                .Where(row => string.Equals(row.PrdDoc.Status, "CLOSED", StringComparison.OrdinalIgnoreCase))
                .Sum(row => row.Qty);
            var prdDocQty = openPrdDocQty + closedPrdDocQty;
            var openPalletRows = itemPalletRows
                .Where(row => _docs.TryGetValue(row.Pallet.PrdDocId, out var doc) && doc.Status != DocStatus.Closed)
                .ToArray();
            var openPalletPlannedQty = openPalletRows.Sum(row => row.PlannedQty);
            var palletPlannedQty = itemPalletRows.Sum(row => row.PlannedQty);
            var palletFilledQty = itemPalletRows.Sum(row => row.FilledQty);
            var openPalletFilledQty = openPalletRows.Sum(row => row.FilledQty);
            var ledgerQtyByDocStatus = BuildProductionPlanConsistencyLedgerQtyByDocStatus(key);
            var ledgerClosedPrdQty = ledgerQtyByDocStatus.ClosedQty;
            var ledgerOpenPrdQty = ledgerQtyByDocStatus.OpenQty;
            var ledgerPrdQty = ledgerClosedPrdQty + ledgerOpenPrdQty;
            var hasOpenPrd = openPrdDocQty > StockQuantityRules.QtyTolerance;
            var hasClosedPrd = closedPrdDocQty > StockQuantityRules.QtyTolerance;
            var openPrdMatchesOpenPallets = openPrdDocQty <= StockQuantityRules.QtyTolerance
                                            || Math.Abs(openPrdDocQty - openPalletPlannedQty) <= StockQuantityRules.QtyTolerance;
            var openPalletsMatchFill = openPalletPlannedQty <= StockQuantityRules.QtyTolerance
                                       || Math.Abs(openPalletPlannedQty - openPalletFilledQty) <= StockQuantityRules.QtyTolerance;

            var problemCode = ResolveProductionPlanConsistencyProblemCode(
                order,
                orderQty,
                openPrdDocQty,
                closedPrdDocQty,
                openPalletPlannedQty,
                palletFilledQty,
                ledgerClosedPrdQty,
                hasOpenPrd,
                hasClosedPrd);
            if (string.IsNullOrWhiteSpace(problemCode))
            {
                continue;
            }

            var severity = ResolveProductionPlanConsistencySeverity(
                order,
                problemCode,
                hasOpenPrd,
                openPalletPlannedQty,
                palletFilledQty,
                ledgerOpenPrdQty,
                openPrdMatchesOpenPallets,
                openPalletsMatchFill);

            result.Add(new ProductionPlanConsistencyDiagnosticItem
            {
                OrderId = order.Id,
                OrderRef = order.OrderRef,
                OrderType = OrderStatusMapper.TypeToString(order.Type),
                OrderStatus = OrderStatusMapper.StatusToString(order.Status),
                ItemId = key.ItemId,
                ItemName = _items.TryGetValue(key.ItemId, out var item) ? item.Name : string.Empty,
                OrderQty = orderQty,
                OpenPrdDocQty = openPrdDocQty,
                ClosedPrdDocQty = closedPrdDocQty,
                PrdDocQty = prdDocQty,
                OpenPalletPlannedQty = openPalletPlannedQty,
                PalletPlannedQty = palletPlannedQty,
                PalletFilledQty = palletFilledQty,
                LedgerClosedPrdQty = ledgerClosedPrdQty,
                LedgerOpenPrdQty = ledgerOpenPrdQty,
                LedgerPrdQty = ledgerPrdQty,
                Severity = severity,
                ProblemCode = problemCode,
                Recommendation = BuildProductionPlanConsistencyRecommendation(problemCode),
                Pallets = itemPalletRows.Select(row => row.Pallet).ToArray(),
                PrdDocs = itemPrdRows.Select(row => row.PrdDoc).ToArray()
            });
        }

        return result;
    }

    private IReadOnlyList<(long OrderId, long ItemId, ProductionPlanConsistencyPrdDocRow PrdDoc, double Qty)> BuildProductionPlanConsistencyPrdRows(
        IReadOnlyDictionary<long, OrderLine> orderLinesById)
    {
        var rows = new List<(long OrderId, long ItemId, ProductionPlanConsistencyPrdDocRow PrdDoc, double Qty)>();
        foreach (var doc in _docs.Values.Where(doc => doc.Type == DocType.ProductionReceipt && doc.OrderId.HasValue))
        {
            foreach (var line in GetActiveDocLines(doc.Id).Where(line => line.Qty > StockQuantityRules.QtyTolerance))
            {
                var orderId = line.OrderLineId.HasValue && orderLinesById.TryGetValue(line.OrderLineId.Value, out var orderLine)
                    ? orderLine.OrderId
                    : doc.OrderId!.Value;
                var row = new ProductionPlanConsistencyPrdDocRow
                {
                    DocId = doc.Id,
                    DocRef = doc.DocRef,
                    Status = DocTypeMapper.StatusToString(doc.Status),
                    ClosedAt = doc.ClosedAt,
                    DocLineId = line.Id,
                    OrderLineId = line.OrderLineId,
                    ItemId = line.ItemId,
                    Qty = line.Qty
                };
                rows.Add((orderId, line.ItemId, row, line.Qty));
            }
        }

        return rows;
    }

    private IReadOnlyList<(long OrderId, long ItemId, ProductionPlanConsistencyPalletRow Pallet, double PlannedQty, double FilledQty)> BuildProductionPlanConsistencyPalletRows()
    {
        var rows = new List<(long OrderId, long ItemId, ProductionPlanConsistencyPalletRow Pallet, double PlannedQty, double FilledQty)>();
        foreach (var pallet in _productionPallets.Values
                     .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(pallet => pallet.Id))
        {
            var doc = _docs.TryGetValue(pallet.PrdDocId, out var foundDoc) ? foundDoc : null;
            var orderId = pallet.OrderId ?? doc?.OrderId;
            if (!orderId.HasValue)
            {
                continue;
            }

            var lines = pallet.Lines.Count > 0
                ? pallet.Lines
                : new[]
                {
                    new ProductionPalletComponentLine
                    {
                        DocLineId = pallet.DocLineId,
                        OrderLineId = pallet.OrderLineId,
                        ItemId = pallet.ItemId,
                        PlannedQty = pallet.PlannedQty,
                        FilledQty = string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase) ? pallet.PlannedQty : 0d
                    }
                };
            foreach (var line in lines)
            {
                var plannedQty = line.PlannedQty;
                var filledQty = line.FilledQty;
                rows.Add((orderId.Value, line.ItemId, new ProductionPlanConsistencyPalletRow
                {
                    PalletId = pallet.Id,
                    PrdDocId = pallet.PrdDocId,
                    PrdDocRef = doc?.DocRef,
                    DocLineId = line.DocLineId,
                    OrderLineId = line.OrderLineId,
                    ItemId = line.ItemId,
                    HuCode = pallet.HuCode,
                    Status = pallet.Status,
                    PlannedQty = plannedQty,
                    FilledQty = filledQty
                }, plannedQty, filledQty));
            }
        }

        return rows;
    }

    private Dictionary<(long OrderId, long ItemId), double> BuildProductionPlanConsistencyLedgerQty()
    {
        return BuildProductionPlanConsistencyLedgerQtyByDocStatusInternal()
            .ToDictionary(pair => pair.Key, pair => pair.Value.ClosedQty + pair.Value.OpenQty);
    }

    private (double ClosedQty, double OpenQty) BuildProductionPlanConsistencyLedgerQtyByDocStatus((long OrderId, long ItemId) key)
    {
        return BuildProductionPlanConsistencyLedgerQtyByDocStatusInternal().TryGetValue(key, out var totals)
            ? totals
            : (0d, 0d);
    }

    private Dictionary<(long OrderId, long ItemId), (double ClosedQty, double OpenQty)> BuildProductionPlanConsistencyLedgerQtyByDocStatusInternal()
    {
        var result = new Dictionary<(long OrderId, long ItemId), (double ClosedQty, double OpenQty)>();
        foreach (var entry in _postedLedger.Where(entry => entry.QtyDelta > StockQuantityRules.QtyTolerance))
        {
            if (!_docs.TryGetValue(entry.DocId, out var doc)
                || doc.Type != DocType.ProductionReceipt
                || !doc.OrderId.HasValue)
            {
                continue;
            }

            var huCode = NormalizeHu(entry.HuCode);
            var pallet = _productionPallets.Values.FirstOrDefault(pallet =>
                pallet.PrdDocId == doc.Id
                && string.Equals(NormalizeHu(pallet.HuCode), huCode, StringComparison.OrdinalIgnoreCase)
                && (pallet.ItemId == entry.ItemId || pallet.Lines.Any(line => line.ItemId == entry.ItemId)));
            var orderId = pallet?.OrderId ?? doc.OrderId.Value;
            var key = (orderId, entry.ItemId);
            if (!result.TryGetValue(key, out var totals))
            {
                totals = (0d, 0d);
            }

            if (doc.Status == DocStatus.Closed)
            {
                totals.ClosedQty += entry.QtyDelta;
            }
            else
            {
                totals.OpenQty += entry.QtyDelta;
            }

            result[key] = totals;
        }

        return result;
    }

    private static string? ResolveProductionPlanConsistencyProblemCode(
        Order order,
        double orderQty,
        double openPrdDocQty,
        double closedPrdDocQty,
        double openPalletPlannedQty,
        double palletFilledQty,
        double ledgerClosedPrdQty,
        bool hasOpenPrd,
        bool hasClosedPrd)
    {
        if (order.Status == OrderStatus.Merged && openPalletPlannedQty > StockQuantityRules.QtyTolerance)
        {
            return ProductionPlanConsistencyProblemCode.MergedOrderWithPalletPlan;
        }

        if (order.Type == OrderType.Customer && order.Status == OrderStatus.Shipped && hasOpenPrd)
        {
            return ProductionPlanConsistencyProblemCode.ShippedCustomerWithOpenPrd;
        }

        if (orderQty <= StockQuantityRules.QtyTolerance && openPalletPlannedQty > StockQuantityRules.QtyTolerance)
        {
            return ProductionPlanConsistencyProblemCode.OrderZeroButPalletsExist;
        }

        if (hasOpenPrd
            && !(order.Type == OrderType.Customer && order.Status == OrderStatus.Shipped)
            && openPalletPlannedQty - orderQty > StockQuantityRules.QtyTolerance)
        {
            return ProductionPlanConsistencyProblemCode.PalletsExceedOrderQty;
        }

        if (hasOpenPrd
            && !(order.Type == OrderType.Customer && order.Status == OrderStatus.Shipped)
            && openPrdDocQty - orderQty > StockQuantityRules.QtyTolerance)
        {
            return ProductionPlanConsistencyProblemCode.PrdLinesExceedOrderQty;
        }

        if (hasClosedPrd && Math.Abs(closedPrdDocQty - ledgerClosedPrdQty) > StockQuantityRules.QtyTolerance)
        {
            return ProductionPlanConsistencyProblemCode.ClosedPrdLedgerMismatch;
        }

        if (palletFilledQty > StockQuantityRules.QtyTolerance && hasOpenPrd)
        {
            return ProductionPlanConsistencyProblemCode.FilledPalletsWithDraftPrd;
        }

        return null;
    }

    private static string ResolveProductionPlanConsistencySeverity(
        Order order,
        string problemCode,
        bool hasOpenPrd,
        double openPalletPlannedQty,
        double palletFilledQty,
        double ledgerOpenPrdQty,
        bool openPrdMatchesOpenPallets,
        bool openPalletsMatchFill)
    {
        if (string.Equals(problemCode, ProductionPlanConsistencyProblemCode.ShippedCustomerWithOpenPrd, StringComparison.Ordinal)
            && order.Type == OrderType.Customer
            && order.Status == OrderStatus.Shipped
            && hasOpenPrd)
        {
            if (openPalletPlannedQty <= StockQuantityRules.QtyTolerance
                && palletFilledQty <= StockQuantityRules.QtyTolerance
                && ledgerOpenPrdQty <= StockQuantityRules.QtyTolerance)
            {
                return ProductionPlanConsistencySeverity.Warning;
            }

            if ((openPalletPlannedQty > StockQuantityRules.QtyTolerance
                 || palletFilledQty > StockQuantityRules.QtyTolerance
                 || ledgerOpenPrdQty > StockQuantityRules.QtyTolerance)
                && (!openPrdMatchesOpenPallets || !openPalletsMatchFill))
            {
                return ProductionPlanConsistencySeverity.Error;
            }

            return ProductionPlanConsistencySeverity.Warning;
        }

        if (string.Equals(problemCode, ProductionPlanConsistencyProblemCode.FilledPalletsWithDraftPrd, StringComparison.Ordinal))
        {
            return ProductionPlanConsistencySeverity.Warning;
        }

        return ProductionPlanConsistencySeverity.Error;
    }

    private static string BuildProductionPlanConsistencyRecommendation(string problemCode)
    {
        return problemCode switch
        {
            ProductionPlanConsistencyProblemCode.OrderZeroButPalletsExist =>
                "Order line quantity is zero but active pallet plan remains. Review merge/redistribution history and create a manual repair plan before closing PRD.",
            ProductionPlanConsistencyProblemCode.PalletsExceedOrderQty =>
                "Active pallet plan exceeds current order quantity. Do not close PRD until pallet plan is cancelled, transferred, or manually repaired.",
            ProductionPlanConsistencyProblemCode.PrdLinesExceedOrderQty =>
                "Active PRD document lines exceed current order quantity. Review draft PRD lines and order redistribution before closing.",
            ProductionPlanConsistencyProblemCode.FilledPalletsWithDraftPrd =>
                "Filled pallet ledger exists while PRD is still open. If quantities are aligned, close the PRD; otherwise review diagnostics before closing.",
            ProductionPlanConsistencyProblemCode.ShippedCustomerWithOpenPrd =>
                "Customer order is already shipped but has an open PRD/pallet plan. Review and cancel or repair the open production plan.",
            ProductionPlanConsistencyProblemCode.MergedOrderWithPalletPlan =>
                "Merged order still has active pallet plan. Manual review is required; do not silently edit production pallets.",
            ProductionPlanConsistencyProblemCode.ClosedPrdLedgerMismatch =>
                "Closed PRD ledger does not match PRD/pallet quantities. Do not edit ledger manually; create an explicit correction document if confirmed.",
            _ => "Review production plan consistency diagnostics."
        };
    }

    private IReadOnlyList<HuOrderContextRow> BuildHuOrderContextRows()
    {
        var activeHuKeys = BuildHuStockRows()
            .Where(row => !string.IsNullOrWhiteSpace(row.HuCode))
            .Select(row => (row.ItemId, HuCode: NormalizeHu(row.HuCode)!))
            .ToHashSet();
        var result = new List<HuOrderContextRow>();

        foreach (var pair in _orderReceiptPlanLines)
        {
            if (!_orders.TryGetValue(pair.Key, out var order)
                || order.Type != OrderType.Customer
                || order.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
            {
                continue;
            }

            foreach (var line in pair.Value.Where(line => line.QtyPlanned > 0 && !string.IsNullOrWhiteSpace(line.ToHu)))
            {
                var huCode = NormalizeHu(line.ToHu);
                if (huCode == null || !activeHuKeys.Contains((line.ItemId, huCode)))
                {
                    continue;
                }

                result.Add(new HuOrderContextRow
                {
                    HuCode = huCode,
                    ItemId = line.ItemId,
                    ReservedCustomerOrderId = order.Id,
                    ReservedCustomerOrderRef = order.OrderRef,
                    ReservedCustomerId = order.PartnerId,
                    ReservedCustomerName = order.PartnerName
                });
            }
        }

        foreach (var doc in _docs.Values.Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status == DocStatus.Closed && doc.OrderId.HasValue))
        {
            var orderId = doc.OrderId;
            if (!orderId.HasValue || !_orders.TryGetValue(orderId.Value, out var order))
            {
                continue;
            }

            if (order.Type == OrderType.Internal)
            {
                foreach (var line in GetActiveDocLines(doc.Id).Where(line => line.Qty > 0 && !string.IsNullOrWhiteSpace(line.ToHu)))
                {
                    var huCode = NormalizeHu(line.ToHu);
                    if (huCode == null || !activeHuKeys.Contains((line.ItemId, huCode)))
                    {
                        continue;
                    }

                    result.Add(new HuOrderContextRow
                    {
                        HuCode = huCode,
                        ItemId = line.ItemId,
                        OriginInternalOrderId = order.Id,
                        OriginInternalOrderRef = order.OrderRef
                    });
                }
            }

            if (order.Type == OrderType.Customer)
            {
                foreach (var line in GetActiveDocLines(doc.Id).Where(line => line.Qty > 0 && !string.IsNullOrWhiteSpace(line.ToHu)))
                {
                    var huCode = NormalizeHu(line.ToHu);
                    if (huCode == null || !activeHuKeys.Contains((line.ItemId, huCode)))
                    {
                        continue;
                    }

                    result.Add(new HuOrderContextRow
                    {
                        HuCode = huCode,
                        ItemId = line.ItemId,
                        ReservedCustomerOrderId = order.Id,
                        ReservedCustomerOrderRef = order.OrderRef,
                        ReservedCustomerId = order.PartnerId,
                        ReservedCustomerName = order.PartnerName
                    });
                }
            }
        }

        return result
            .GroupBy(row => (row.ItemId, HuCode: NormalizeHu(row.HuCode)))
            .Select(group => group.First())
            .ToArray();
    }

    private double GetProducedQtyForOrderLine(long orderLineId)
    {
        var total = 0d;
        foreach (var doc in _docs.Values.Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status == DocStatus.Closed))
        {
            if (_productionPallets.Values.Any(pallet =>
                    pallet.PrdDocId == doc.Id
                    && !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            total += GetActiveDocLines(doc.Id)
                .Where(line => line.OrderLineId == orderLineId && line.Qty > 0)
                .Sum(line => line.Qty);
        }

        total += _productionPallets.Values
            .Where(pallet => pallet.OrderLineId == orderLineId
                             && string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
            .Sum(pallet => pallet.PlannedQty);

        return total;
    }

    private double GetReservedQtyForOrderLine(long orderLineId)
    {
        return _orderReceiptPlanLines.Values
            .SelectMany(lines => lines)
            .Where(line => line.OrderLineId == orderLineId && line.QtyPlanned > 0)
            .Sum(line => line.QtyPlanned);
    }

    private int CountFreeMarkingCodesForItem(long itemId, string? gtin)
    {
        var normalizedGtin = NormalizeText(gtin);
        return _markingCodes.Values
            .Where(code => code.ReceiptDocId == null
                           && code.ReceiptLineId == null
                           && code.Status is MarkingCodeStatus.Reserved or MarkingCodeStatus.Printed)
            .Select(code => (Code: code, Order: _markingOrders.TryGetValue(code.MarkingOrderId, out var order) ? order : null))
            .Count(pair => pair.Order != null
                           && pair.Order.Status is not MarkingOrderStatus.Cancelled and not MarkingOrderStatus.Failed
                           && (pair.Order.ItemId == itemId
                               || (!string.IsNullOrWhiteSpace(normalizedGtin)
                                   && (string.Equals(NormalizeText(pair.Order.Gtin), normalizedGtin, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(NormalizeText(pair.Code.Gtin), normalizedGtin, StringComparison.OrdinalIgnoreCase)))));
    }

    private int CountBoundMarkingCodesForOrderLine(long orderLineId)
    {
        return _markingCodes.Values.Count(code => code.ReceiptLineId.HasValue
                                                 && GetAllActiveOrderLineIdsForReceiptLine(code.ReceiptLineId.Value).Contains(orderLineId)
                                                 && code.Status != MarkingCodeStatus.Voided);
    }

    private HashSet<long> GetAllActiveOrderLineIdsForReceiptLine(long receiptLineId)
    {
        foreach (var lines in _linesByDoc.Values)
        {
            var line = lines.FirstOrDefault(candidate => candidate.Id == receiptLineId);
            if (line?.OrderLineId is long orderLineId)
            {
                return new HashSet<long> { orderLineId };
            }
        }

        return new HashSet<long>();
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

    private IReadOnlyList<StockRow> BuildStockRows(string? search)
    {
        var normalized = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var reservedByItem = _orders.Values
            .Where(order => order.Type == OrderType.Customer
                            && order.Status is not OrderStatus.Draft and not OrderStatus.Shipped and not OrderStatus.Cancelled)
            .SelectMany(order => (_orderReceiptPlanLines.TryGetValue(order.Id, out var lines) ? lines : Array.Empty<OrderReceiptPlanLine>())
                .GroupBy(line => line.ItemId)
                .Select(group => new { group.Key, Qty = group.Sum(line => line.QtyPlanned) }))
            .GroupBy(entry => entry.Key)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Qty));

        var totals = new Dictionary<(long ItemId, long LocationId), double>();
        foreach (var balance in _seedBalances)
        {
            var key = (balance.Key.ItemId, balance.Key.LocationId);
            totals[key] = totals.TryGetValue(key, out var current)
                ? current + balance.Value
                : balance.Value;
        }

        foreach (var entry in _postedLedger)
        {
            var key = (entry.ItemId, entry.LocationId);
            totals[key] = totals.TryGetValue(key, out var current)
                ? current + entry.QtyDelta
                : entry.QtyDelta;
        }

        return totals
            .Select(entry =>
            {
                _items.TryGetValue(entry.Key.ItemId, out var item);
                _locations.TryGetValue(entry.Key.LocationId, out var location);
                return new StockRow
                {
                    ItemId = entry.Key.ItemId,
                    ItemName = item?.Name ?? string.Empty,
                    Barcode = item?.Barcode,
                    LocationCode = location?.Code ?? string.Empty,
                    Qty = entry.Value,
                    ReservedCustomerOrderQty = reservedByItem.TryGetValue(entry.Key.ItemId, out var reservedQty) ? reservedQty : 0,
                    ItemTypeId = item?.ItemTypeId,
                    ItemTypeName = item?.ItemTypeId is long itemTypeId && _itemTypes.TryGetValue(itemTypeId, out var itemType)
                        ? itemType.Name
                        : item?.ItemTypeName,
                    ItemTypeEnableMinStockControl = item?.ItemTypeEnableMinStockControl ?? false,
                    MinStockQty = item?.MinStockQty,
                    AvailableForMinStockQty = entry.Value
                };
            })
            .Where(row => normalized == null
                          || row.ItemName.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                          || (!string.IsNullOrWhiteSpace(row.Barcode)
                              && row.Barcode.Contains(normalized, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(row => row.ItemId)
            .ThenBy(row => row.LocationCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
            .Where(pair => !StockQuantityRules.IsEffectivelyZero(pair.Value))
            .Select(pair => new HuStockRow
            {
                ItemId = pair.Key.ItemId,
                LocationId = pair.Key.LocationId,
                HuCode = pair.Key.HuCode,
                Qty = pair.Value
            })
            .ToArray();
    }

    private IReadOnlyList<NegativeStockBalanceRow> BuildNegativeStockBalances()
    {
        return BuildHuStockRows()
            .Where(row => StockQuantityRules.IsNegativeStockQty(row.Qty))
            .Select(row =>
            {
                var lastEntry = _postedLedger
                    .Where(entry => entry.ItemId == row.ItemId
                                    && entry.LocationId == row.LocationId
                                    && string.Equals(
                                        NormalizeHu(entry.HuCode),
                                        NormalizeHu(row.HuCode),
                                        StringComparison.Ordinal))
                    .OrderByDescending(entry => entry.Timestamp)
                    .ThenByDescending(entry => entry.Id)
                    .FirstOrDefault();
                var lastDoc = lastEntry?.DocId is long docId && _docs.TryGetValue(docId, out var doc) ? doc : null;
                _items.TryGetValue(row.ItemId, out var item);
                _locations.TryGetValue(row.LocationId, out var location);
                return new NegativeStockBalanceRow
                {
                    ItemId = row.ItemId,
                    ItemName = item?.Name ?? $"#{row.ItemId}",
                    LocationId = row.LocationId,
                    LocationCode = location?.Code ?? $"#{row.LocationId}",
                    HuCode = row.HuCode,
                    Qty = row.Qty,
                    LastLedgerEntryId = lastEntry?.Id,
                    LastDocId = lastEntry?.DocId,
                    LastDocRef = lastDoc?.DocRef,
                    LastDocType = lastDoc?.Type,
                    OrderId = lastDoc?.OrderId,
                    OrderRef = lastDoc?.OrderRef,
                    LastMovementAt = lastEntry?.Timestamp
                };
            })
            .ToArray();
    }

    private IReadOnlyList<MarkingCode> GetAvailableProductionMarkingCodes(long? sourceOrderId, long itemId, string? gtin, int take)
    {
        var normalizedGtin = NormalizeText(gtin);
        return _markingCodes.Values
            .Where(code => code.ReceiptDocId == null
                           && code.ReceiptLineId == null
                           && code.Status is MarkingCodeStatus.Reserved or MarkingCodeStatus.Printed)
            .Select(code => (Code: code, Order: _markingOrders.TryGetValue(code.MarkingOrderId, out var order) ? order : null))
            .Where(pair => pair.Order != null
                           && pair.Order.Status is not MarkingOrderStatus.Cancelled and not MarkingOrderStatus.Failed)
            .Where(pair => pair.Order!.ItemId == itemId
                           || (!string.IsNullOrWhiteSpace(normalizedGtin)
                               && (string.Equals(NormalizeText(pair.Order.Gtin), normalizedGtin, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(NormalizeText(pair.Code.Gtin), normalizedGtin, StringComparison.OrdinalIgnoreCase))))
            .Where(pair =>
                string.Equals(pair.Order!.SourceType, MarkingNeedCreationService.ProductionNeedSourceType, StringComparison.OrdinalIgnoreCase)
                && (!pair.Order.SourceOrderId.HasValue || (sourceOrderId.HasValue && pair.Order.SourceOrderId == sourceOrderId))
                || string.Equals(pair.Order!.SourceType, MarkingNeedCreationService.ProductionOrderSourceType, StringComparison.OrdinalIgnoreCase)
                && sourceOrderId.HasValue
                && pair.Order.SourceOrderId == sourceOrderId
                || sourceOrderId.HasValue
                && pair.Order.OrderId == sourceOrderId)
            .OrderBy(pair => pair.Order!.CreatedAt)
            .ThenBy(pair => pair.Code.SourceRowNumber ?? int.MaxValue)
            .Select(pair => pair.Code)
            .Take(take)
            .ToArray();
    }

    private static string? NormalizeHu(string? huCode)
    {
        return string.IsNullOrWhiteSpace(huCode) ? null : huCode.Trim();
    }

    private HashSet<long> BuildExistingHuNumbers()
    {
        var result = new HashSet<long>();
        foreach (var code in _hus.Keys)
        {
            result.Add(ExtractHuNumber(code));
        }
        foreach (var pallet in _productionPallets.Values)
        {
            result.Add(ExtractHuNumber(pallet.HuCode));
        }
        foreach (var lines in _linesByDoc.Values)
        {
            foreach (var line in lines)
            {
                result.Add(ExtractHuNumber(line.FromHu));
                result.Add(ExtractHuNumber(line.ToHu));
            }
        }
        foreach (var entry in _postedLedger)
        {
            result.Add(ExtractHuNumber(entry.HuCode));
        }

        result.Remove(0);
        return result;
    }

    private static long ExtractHuNumber(string? huCode)
    {
        var normalized = NormalizeHu(huCode);
        if (string.IsNullOrWhiteSpace(normalized) || !normalized.StartsWith("HU-", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var digits = new string(normalized.Skip(3).Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var value) ? value : 0;
    }

    private static string? NormalizeText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private IReadOnlyList<FilledProductionPalletStockMetrics> BuildFilledProductionPalletStockMetrics()
    {
        var rows = new List<FilledProductionPalletStockMetrics>();
        foreach (var pallet in _productionPallets.Values
                     .Where(pallet => string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
                     .Where(pallet => pallet.ToLocationId.HasValue))
        {
            var components = pallet.Lines.Count > 0
                ? pallet.Lines.Select(line => (line.ItemId, line.PlannedQty, line.ItemName))
                : new[] { (pallet.ItemId, pallet.PlannedQty, pallet.ItemName) };
            var prdDoc = _docs.TryGetValue(pallet.PrdDocId, out var foundPrdDoc) ? foundPrdDoc : null;
            var orderId = pallet.OrderId ?? prdDoc?.OrderId;
            Order? order = null;
            if (orderId.HasValue)
            {
                _orders.TryGetValue(orderId.Value, out order);
            }

            foreach (var (itemId, plannedQty, itemName) in components)
            {
                var currentLedgerQty = _postedLedger
                    .Where(entry => entry.ItemId == itemId
                                    && entry.LocationId == pallet.ToLocationId
                                    && string.Equals(NormalizeHu(entry.HuCode), NormalizeHu(pallet.HuCode), StringComparison.OrdinalIgnoreCase))
                    .Sum(entry => entry.QtyDelta);
                var outboundByHu = SumOutboundQty(itemId, pallet.ToLocationId, pallet.HuCode, orderId: null);
                var outboundDocsByHu = JoinOutboundDocRefs(itemId, pallet.ToLocationId, pallet.HuCode, orderId: null);
                var outboundByOrderItem = orderId.HasValue
                    ? SumOutboundQty(itemId, locationId: null, huCode: null, orderId: orderId.Value)
                    : 0d;
                var outboundDocsByOrderItem = orderId.HasValue
                    ? JoinOutboundDocRefs(itemId, locationId: null, huCode: null, orderId: orderId.Value)
                    : string.Empty;

                rows.Add(new FilledProductionPalletStockMetrics
                {
                    PalletId = pallet.Id,
                    PrdDocId = pallet.PrdDocId,
                    PrdDocRef = prdDoc?.DocRef ?? string.Empty,
                    OrderId = orderId,
                    OrderRef = order?.OrderRef,
                    OrderStatus = order == null ? null : OrderStatusMapper.StatusToString(order.Status),
                    ItemId = itemId,
                    ItemName = itemName,
                    HuCode = pallet.HuCode,
                    ToLocationId = pallet.ToLocationId,
                    ToLocationCode = pallet.ToLocationCode,
                    PlannedQty = plannedQty,
                    CurrentLedgerQty = currentLedgerQty,
                    OutboundBySameHuQty = outboundByHu,
                    OutboundDocsBySameHu = outboundDocsByHu,
                    OutboundByOrderItemQty = outboundByOrderItem,
                    OutboundDocsByOrderItem = outboundDocsByOrderItem,
                    Status = pallet.Status,
                    FilledAt = pallet.FilledAt
                });
            }
        }

        return rows
            .OrderBy(row => row.PrdDocId)
            .ThenBy(row => row.HuCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ItemId)
            .ToArray();
    }

    private double SumOutboundQty(long itemId, long? locationId, string? huCode, long? orderId)
    {
        return _postedLedger
            .Where(entry => entry.QtyDelta < -StockQuantityRules.QtyTolerance)
            .Where(entry => entry.ItemId == itemId)
            .Where(entry => _docs.TryGetValue(entry.DocId, out var doc)
                            && doc.Type == DocType.Outbound
                            && doc.Status == DocStatus.Closed)
            .Where(entry => !orderId.HasValue || (_docs[entry.DocId].OrderId == orderId))
            .Where(entry => !locationId.HasValue
                            || entry.LocationId == locationId)
            .Where(entry => string.IsNullOrWhiteSpace(huCode)
                            || string.Equals(NormalizeHu(entry.HuCode), NormalizeHu(huCode), StringComparison.OrdinalIgnoreCase))
            .Sum(entry => -entry.QtyDelta);
    }

    private string JoinOutboundDocRefs(long itemId, long? locationId, string? huCode, long? orderId)
    {
        return string.Join(
            ", ",
            _postedLedger
                .Where(entry => entry.QtyDelta < -StockQuantityRules.QtyTolerance)
                .Where(entry => entry.ItemId == itemId)
                .Where(entry => _docs.TryGetValue(entry.DocId, out var doc)
                                && doc.Type == DocType.Outbound
                                && doc.Status == DocStatus.Closed)
                .Where(entry => !orderId.HasValue || (_docs[entry.DocId].OrderId == orderId))
                .Where(entry => !locationId.HasValue
                                || entry.LocationId == locationId)
                .Where(entry => string.IsNullOrWhiteSpace(huCode)
                                || string.Equals(NormalizeHu(entry.HuCode), NormalizeHu(huCode), StringComparison.OrdinalIgnoreCase))
                .Select(entry => _docs[entry.DocId].DocRef)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(docRef => docRef, StringComparer.OrdinalIgnoreCase));
    }

    private void ClearProductionPalletPlanInHarness(long docId)
    {
        foreach (var pallet in _productionPallets.Values.Where(pallet => pallet.PrdDocId == docId).ToArray())
        {
            _productionPallets.Remove(pallet.Id);
        }

        if (_linesByDoc.TryGetValue(docId, out var docLines))
        {
            docLines.Clear();
        }
    }
}
