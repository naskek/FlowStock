using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Core.Services.Warehouse;
using Moq;

namespace FlowStock.Server.Tests.WarehouseTasks.Infrastructure;

internal sealed class WarehouseBundleServiceHarness
{
    private readonly Mock<IDataStore> _store = new(MockBehavior.Strict);
    private readonly WarehouseTaskStoreHarness _warehouse = new();
    private readonly Dictionary<long, Item> _items = new();
    private readonly Dictionary<long, Location> _locations = new();
    private readonly List<HuStockRow> _huStock = new();
    private readonly Dictionary<long, Doc> _docs = new();
    private readonly Dictionary<long, List<DocLine>> _docLines = new();
    private readonly List<LedgerEntry> _ledger = new();
    private long _nextItemId = 1;
    private long _nextLocationId = 1;
    private long _nextDocId = 1;
    private long _nextDocLineId = 1;

    public WarehouseBundleServiceHarness()
    {
        WireWarehouseFromInnerHarness();
        WireCatalogAndDocs();
    }

    public IDataStore Store => _store.Object;

    public WarehouseActionBundleService BundleService => new(_store.Object);

    public WarehouseTaskExecutionService TaskService => new(_store.Object);

    public DocumentService DocumentService => new(_store.Object);

    public IReadOnlyList<LedgerEntry> LedgerEntries => _ledger;

    public (long ItemId, long FromLocationId, long ToLocationId) SeedMoveScenario(string huCode = "HU-TEST-001", double qty = 10)
    {
        var itemId = SeedItem("Товар 1");
        var fromLoc = SeedLocation("A-01");
        var toLoc = SeedLocation("SHIP-01");
        _huStock.Add(new HuStockRow
        {
            HuCode = huCode,
            ItemId = itemId,
            LocationId = fromLoc,
            Qty = qty
        });
        return (itemId, fromLoc, toLoc);
    }

    public long SeedItem(string name)
    {
        var id = _nextItemId++;
        _items[id] = new Item
        {
            Id = id,
            Name = name,
            IsActive = true,
            Barcode = $"ITEM-{id}",
            BaseUom = "шт"
        };
        return id;
    }

    public long SeedLocation(string code)
    {
        var id = _nextLocationId++;
        _locations[id] = new Location { Id = id, Code = code, Name = code };
        return id;
    }

    private void WireWarehouseFromInnerHarness()
    {
        var inner = _warehouse.Store;
        _store.Setup(store => store.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
            .Callback<Action<IDataStore>>(work => work(_store.Object));

        _store.Setup(s => s.GetWarehouseActionBundle(It.IsAny<long>())).Returns<long>(id => inner.GetWarehouseActionBundle(id));
        _store.Setup(s => s.FindWarehouseBundleByRef(It.IsAny<string>())).Returns<string>(r => inner.FindWarehouseBundleByRef(r));
        _store.Setup(s => s.GetWarehouseActionBundles(It.IsAny<string?>())).Returns<string?>(st => inner.GetWarehouseActionBundles(st));
        _store.Setup(s => s.GetMaxWarehouseBundleRefSequenceByYear(It.IsAny<int>())).Returns<int>(y => inner.GetMaxWarehouseBundleRefSequenceByYear(y));
        _store.Setup(s => s.AddWarehouseActionBundle(It.IsAny<WarehouseActionBundle>())).Returns<WarehouseActionBundle>(b => inner.AddWarehouseActionBundle(b));
        _store.Setup(s => s.UpdateWarehouseActionBundleStatus(
                It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<string?>(),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .Callback<long, string, DateTime?, string?, DateTime?, DateTime?, DateTime?, string?, string?, string?>(
                (id, st, a, ab, ex, comp, rej, rb, ec, em) =>
                    inner.UpdateWarehouseActionBundleStatus(id, st, a, ab, ex, comp, rej, rb, ec, em));
        _store.Setup(s => s.GetWarehouseActionLine(It.IsAny<long>())).Returns<long>(id => inner.GetWarehouseActionLine(id));
        _store.Setup(s => s.GetWarehouseActionLines(It.IsAny<long>())).Returns<long>(id => inner.GetWarehouseActionLines(id));
        _store.Setup(s => s.GetNextWarehouseActionLineNo(It.IsAny<long>())).Returns<long>(id => inner.GetNextWarehouseActionLineNo(id));
        _store.Setup(s => s.AddWarehouseActionLine(It.IsAny<WarehouseActionLine>())).Returns<WarehouseActionLine>(l => inner.AddWarehouseActionLine(l));
        _store.Setup(s => s.UpdateWarehouseActionLine(
                It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTime>()))
            .Callback<long, string, long?, string?, string?, string?, DateTime>(
                (id, st, doc, res, ec, em, up) => inner.UpdateWarehouseActionLine(id, st, doc, res, ec, em, up));
        _store.Setup(s => s.GetWarehouseTask(It.IsAny<long>())).Returns<long>(id => inner.GetWarehouseTask(id));
        _store.Setup(s => s.FindWarehouseTaskByRef(It.IsAny<string>())).Returns<string>(r => inner.FindWarehouseTaskByRef(r));
        _store.Setup(s => s.GetWarehouseTasksByBundle(It.IsAny<long>())).Returns<long>(id => inner.GetWarehouseTasksByBundle(id));
        _store.Setup(s => s.GetActiveWarehouseTasks(It.IsAny<string?>())).Returns<string?>(d => inner.GetActiveWarehouseTasks(d));
        _store.Setup(s => s.GetMaxWarehouseTaskRefSequenceByYear(It.IsAny<int>())).Returns<int>(y => inner.GetMaxWarehouseTaskRefSequenceByYear(y));
        _store.Setup(s => s.AddWarehouseTask(It.IsAny<WarehouseTask>())).Returns<WarehouseTask>(t => inner.AddWarehouseTask(t));
        _store.Setup(s => s.UpdateWarehouseTaskStatus(
                It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Callback<long, string, DateTime?, DateTime?, DateTime?, DateTime?, string?, string?>(
                (id, st, sa, ex, conf, can, dev, user) =>
                    inner.UpdateWarehouseTaskStatus(id, st, sa, ex, conf, can, dev, user));
        _store.Setup(s => s.GetWarehouseTaskLine(It.IsAny<long>())).Returns<long>(id => inner.GetWarehouseTaskLine(id));
        _store.Setup(s => s.GetWarehouseTaskLines(It.IsAny<long>())).Returns<long>(id => inner.GetWarehouseTaskLines(id));
        _store.Setup(s => s.AddWarehouseTaskLine(It.IsAny<WarehouseTaskLine>())).Returns<WarehouseTaskLine>(l => inner.AddWarehouseTaskLine(l));
        _store.Setup(s => s.UpdateWarehouseTaskLineScan(
                It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<long?>(),
                It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Callback<long, string, string?, long?, DateTime?, string?, string?, string?, string?>(
                (id, st, hu, loc, at, dev, op, ec, em) =>
                    inner.UpdateWarehouseTaskLineScan(id, st, hu, loc, at, dev, op, ec, em));
        _store.Setup(s => s.AddWarehouseTaskEvent(It.IsAny<WarehouseTaskEvent>())).Returns<WarehouseTaskEvent>(e => inner.AddWarehouseTaskEvent(e));
        _store.Setup(s => s.GetWarehouseTaskEvents(It.IsAny<long>())).Returns<long>(id => inner.GetWarehouseTaskEvents(id));
        _store.Setup(s => s.IsHuLockedByActiveWarehouseTask(It.IsAny<string>(), It.IsAny<long?>()))
            .Returns<string, long?>((hu, ex) => inner.IsHuLockedByActiveWarehouseTask(hu, ex));
    }

    private void WireCatalogAndDocs()
    {
        _store.Setup(s => s.FindItemById(It.IsAny<long>())).Returns<long>(id => _items.TryGetValue(id, out var item) ? item : null);
        _store.Setup(s => s.GetItems(It.IsAny<string?>())).Returns<string?>(_ => _items.Values.ToArray());
        _store.Setup(s => s.FindLocationById(It.IsAny<long>())).Returns<long>(id => _locations.TryGetValue(id, out var loc) ? loc : null);
        _store.Setup(s => s.GetLocations()).Returns(() => _locations.Values.ToArray());
        _store.Setup(s => s.GetHuStockRows()).Returns(() => _huStock.ToArray());

        _store.Setup(s => s.GetAvailableQty(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>()))
            .Returns<long, long, string?>((itemId, locationId, huCode) => GetBalance(itemId, locationId, huCode));

        _store.Setup(s => s.GetLedgerBalance(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>()))
            .Returns<long, long, string?>((itemId, locationId, huCode) => GetBalance(itemId, locationId, huCode));

        _store.Setup(s => s.FindDocByRef(It.IsAny<string>())).Returns<string>(docRef =>
            _docs.Values.FirstOrDefault(doc => string.Equals(doc.DocRef, docRef, StringComparison.OrdinalIgnoreCase)));

        _store.Setup(s => s.GetDoc(It.IsAny<long>())).Returns<long>(id => _docs.TryGetValue(id, out var doc) ? doc : null);

        _store.Setup(s => s.GetDocs()).Returns(() => _docs.Values.ToArray());

        _store.Setup(s => s.GetMaxDocRefSequenceByYear(It.IsAny<int>())).Returns<int>(year =>
            _docs.Values.Select(doc => doc.DocRef).Select(r => ParseSeq(r, year)).DefaultIfEmpty(0).Max());

        _store.Setup(s => s.IsDocRefSequenceTaken(It.IsAny<int>(), It.IsAny<int>())).Returns(false);

        _store.Setup(s => s.AddDoc(It.IsAny<Doc>())).Returns<Doc>(doc =>
        {
            var id = _nextDocId++;
            _docs[id] = new Doc
            {
                Id = id,
                DocRef = doc.DocRef,
                Type = doc.Type,
                Status = doc.Status,
                CreatedAt = doc.CreatedAt,
                Comment = doc.Comment
            };
            _docLines[id] = new List<DocLine>();
            return id;
        });

        _store.Setup(s => s.GetDocLines(It.IsAny<long>())).Returns<long>(docId =>
            _docLines.TryGetValue(docId, out var lines)
                ? lines.Where(l => l.Qty > 0).ToArray()
                : Array.Empty<DocLine>());

        _store.Setup(s => s.AddDocLine(It.IsAny<DocLine>())).Returns<DocLine>(line =>
        {
            var id = _nextDocLineId++;
            var stored = new DocLine
            {
                Id = id,
                DocId = line.DocId,
                ItemId = line.ItemId,
                Qty = line.Qty,
                FromLocationId = line.FromLocationId,
                ToLocationId = line.ToLocationId,
                FromHu = line.FromHu,
                ToHu = line.ToHu
            };
            if (!_docLines.TryGetValue(line.DocId, out var list))
            {
                list = new List<DocLine>();
                _docLines[line.DocId] = list;
            }

            list.Add(stored);
            return id;
        });

        _store.Setup(s => s.DeleteDocLines(It.IsAny<long>())).Callback<long>(docId =>
        {
            if (_docLines.TryGetValue(docId, out var lines))
            {
                lines.Clear();
            }
        });

        _store.Setup(s => s.UpdateDocStatus(It.IsAny<long>(), It.IsAny<DocStatus>(), It.IsAny<DateTime?>()))
            .Callback<long, DocStatus, DateTime?>((docId, status, closedAt) =>
            {
                if (_docs.TryGetValue(docId, out var doc))
                {
                    _docs[docId] = new Doc
                    {
                        Id = doc.Id,
                        DocRef = doc.DocRef,
                        Type = doc.Type,
                        Status = status,
                        CreatedAt = doc.CreatedAt,
                        ClosedAt = closedAt,
                        Comment = doc.Comment,
                        ShippingRef = doc.ShippingRef
                    };
                }
            });

        _store.Setup(s => s.UpdateDocHeader(It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Callback<long, long?, string?, string?>((docId, partnerId, orderRef, shippingRef) =>
            {
                if (!_docs.TryGetValue(docId, out var doc))
                {
                    return;
                }

                _docs[docId] = new Doc
                {
                    Id = doc.Id,
                    DocRef = doc.DocRef,
                    Type = doc.Type,
                    Status = doc.Status,
                    CreatedAt = doc.CreatedAt,
                    ClosedAt = doc.ClosedAt,
                    PartnerId = partnerId ?? doc.PartnerId,
                    OrderRef = orderRef ?? doc.OrderRef,
                    ShippingRef = shippingRef ?? doc.ShippingRef,
                    Comment = doc.Comment
                };
            });

        _store.Setup(s => s.AddLedgerEntry(It.IsAny<LedgerEntry>())).Callback<LedgerEntry>(entry => _ledger.Add(entry));

        _store.Setup(s => s.HasProductionPallets(It.IsAny<long>())).Returns(false);
        _store.Setup(s => s.GetItemTypes(It.IsAny<bool>())).Returns(Array.Empty<ItemType>());
    }

    private double GetBalance(long itemId, long locationId, string? huCode)
    {
        return _huStock
            .Where(row => row.ItemId == itemId
                          && row.LocationId == locationId
                          && (string.IsNullOrWhiteSpace(huCode)
                              || string.Equals(row.HuCode, huCode, StringComparison.OrdinalIgnoreCase)))
            .Sum(row => row.Qty);
    }

    private static int ParseSeq(string refValue, int year)
    {
        var parts = refValue.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !string.Equals(parts[1], year.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return int.TryParse(parts[^1], out var seq) ? seq : 0;
    }
}
