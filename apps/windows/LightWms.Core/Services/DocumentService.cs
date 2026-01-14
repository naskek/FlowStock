using System.Globalization;
using LightWms.Core.Abstractions;
using LightWms.Core.Models;

namespace LightWms.Core.Services;

public sealed class DocumentService
{
    private readonly IDataStore _data;

    public DocumentService(IDataStore data)
    {
        _data = data;
    }

    public IReadOnlyList<Doc> GetDocs()
    {
        return _data.GetDocs();
    }

    public long CreateDoc(DocType type, string docRef, string? comment, long? partnerId, string? orderRef, string? shippingRef)
    {
        if (string.IsNullOrWhiteSpace(docRef))
        {
            throw new ArgumentException("Номер документа обязателен.", nameof(docRef));
        }

        var trimmedRef = docRef.Trim();
        if (_data.FindDocByRef(trimmedRef, type) != null)
        {
            throw new ArgumentException("Документ с таким номером уже существует.", nameof(docRef));
        }

        if (partnerId.HasValue && _data.GetPartner(partnerId.Value) == null)
        {
            throw new ArgumentException("Контрагент не найден.", nameof(partnerId));
        }

        if (type == DocType.Outbound && !partnerId.HasValue)
        {
            throw new ArgumentException("Для отгрузки требуется контрагент.", nameof(partnerId));
        }

        var cleanedOrderRef = string.IsNullOrWhiteSpace(orderRef) ? null : orderRef.Trim();
        var cleanedShippingRef = string.IsNullOrWhiteSpace(shippingRef) ? null : shippingRef.Trim();
        var cleanedComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();

        var doc = new Doc
        {
            DocRef = trimmedRef,
            Type = type,
            Status = DocStatus.Draft,
            CreatedAt = DateTime.Now,
            ClosedAt = null,
            PartnerId = partnerId,
            OrderRef = cleanedOrderRef,
            ShippingRef = cleanedShippingRef,
            Comment = cleanedComment
        };

        return _data.AddDoc(doc);
    }

    public Doc? GetDoc(long docId)
    {
        return _data.GetDoc(docId);
    }

    public IReadOnlyList<DocLineView> GetDocLines(long docId)
    {
        return _data.GetDocLineViews(docId);
    }

    public IReadOnlyList<StockRow> GetStock(string? search)
    {
        return _data.GetStock(search);
    }

    public CloseDocResult TryCloseDoc(long docId, bool allowNegative)
    {
        var check = BuildCloseDocCheck(docId);
        if (check.Errors.Count > 0)
        {
            return new CloseDocResult
            {
                Success = false,
                Errors = check.Errors
            };
        }

        if (check.Warnings.Count > 0 && !allowNegative)
        {
            return new CloseDocResult
            {
                Success = false,
                Warnings = check.Warnings
            };
        }

        var closedAt = DateTime.Now;

        _data.ExecuteInTransaction(store =>
        {
            var doc = store.GetDoc(docId);
            if (doc == null || doc.Status == DocStatus.Closed)
            {
                return;
            }

            var lines = store.GetDocLines(docId);
            foreach (var line in lines)
            {
                switch (doc.Type)
                {
                    case DocType.Inbound:
                        if (line.ToLocationId.HasValue)
                        {
                            store.AddLedgerEntry(new LedgerEntry
                            {
                                Timestamp = closedAt,
                                DocId = docId,
                                ItemId = line.ItemId,
                                LocationId = line.ToLocationId.Value,
                                QtyDelta = line.Qty
                            });
                        }
                        break;
                    case DocType.WriteOff:
                        if (line.FromLocationId.HasValue)
                        {
                            store.AddLedgerEntry(new LedgerEntry
                            {
                                Timestamp = closedAt,
                                DocId = docId,
                                ItemId = line.ItemId,
                                LocationId = line.FromLocationId.Value,
                                QtyDelta = -line.Qty
                            });
                        }
                        break;
                    case DocType.Outbound:
                        if (line.FromLocationId.HasValue)
                        {
                            store.AddLedgerEntry(new LedgerEntry
                            {
                                Timestamp = closedAt,
                                DocId = docId,
                                ItemId = line.ItemId,
                                LocationId = line.FromLocationId.Value,
                                QtyDelta = -line.Qty
                            });
                        }
                        break;
                    case DocType.Move:
                        if (line.FromLocationId.HasValue)
                        {
                            store.AddLedgerEntry(new LedgerEntry
                            {
                                Timestamp = closedAt,
                                DocId = docId,
                                ItemId = line.ItemId,
                                LocationId = line.FromLocationId.Value,
                                QtyDelta = -line.Qty
                            });
                        }
                        if (line.ToLocationId.HasValue)
                        {
                            store.AddLedgerEntry(new LedgerEntry
                            {
                                Timestamp = closedAt,
                                DocId = docId,
                                ItemId = line.ItemId,
                                LocationId = line.ToLocationId.Value,
                                QtyDelta = line.Qty
                            });
                        }
                        break;
                    case DocType.Inventory:
                        // MVP: inventory ledger logic is deferred; keep the document close only.
                        break;
                }
            }

            store.UpdateDocStatus(docId, DocStatus.Closed, closedAt);
        });

        return new CloseDocResult { Success = true };
    }

    public void UpdateDocHeader(long docId, long? partnerId, string? orderRef, string? shippingRef)
    {
        var doc = _data.GetDoc(docId) ?? throw new InvalidOperationException("Документ не найден.");
        if (doc.Status != DocStatus.Draft)
        {
            throw new InvalidOperationException("Документ уже закрыт.");
        }

        if (partnerId.HasValue && _data.GetPartner(partnerId.Value) == null)
        {
            throw new InvalidOperationException("Контрагент не найден.");
        }

        var cleanedOrderRef = string.IsNullOrWhiteSpace(orderRef) ? null : orderRef.Trim();
        var cleanedShippingRef = string.IsNullOrWhiteSpace(shippingRef) ? null : shippingRef.Trim();

        _data.UpdateDocHeader(docId, partnerId, cleanedOrderRef, cleanedShippingRef);
    }

    public void AddDocLine(long docId, long itemId, double qty, long? fromLocationId, long? toLocationId)
    {
        if (qty <= 0)
        {
            throw new ArgumentException("Количество должно быть больше 0.", nameof(qty));
        }

        var doc = _data.GetDoc(docId) ?? throw new InvalidOperationException("Документ не найден.");
        if (doc.Status != DocStatus.Draft)
        {
            throw new InvalidOperationException("Документ уже закрыт.");
        }

        if (_data.FindItemById(itemId) == null)
        {
            throw new InvalidOperationException("Товар не найден.");
        }

        ValidateLineLocations(doc.Type, fromLocationId, toLocationId);

        _data.AddDocLine(new DocLine
        {
            DocId = docId,
            ItemId = itemId,
            Qty = qty,
            FromLocationId = fromLocationId,
            ToLocationId = toLocationId
        });
    }

    public void UpdateDocLineQty(long docId, long docLineId, double qty)
    {
        if (qty <= 0)
        {
            throw new ArgumentException("Количество должно быть больше 0.", nameof(qty));
        }

        var doc = _data.GetDoc(docId) ?? throw new InvalidOperationException("Документ не найден.");
        if (doc.Status != DocStatus.Draft)
        {
            throw new InvalidOperationException("Документ уже закрыт.");
        }

        var line = _data.GetDocLines(docId).FirstOrDefault(l => l.Id == docLineId);
        if (line == null)
        {
            throw new InvalidOperationException("Строка не найдена.");
        }

        _data.UpdateDocLineQty(docLineId, qty);
    }

    public void DeleteDocLine(long docId, long docLineId)
    {
        var doc = _data.GetDoc(docId) ?? throw new InvalidOperationException("Документ не найден.");
        if (doc.Status != DocStatus.Draft)
        {
            throw new InvalidOperationException("Документ уже закрыт.");
        }

        var line = _data.GetDocLines(docId).FirstOrDefault(l => l.Id == docLineId);
        if (line == null)
        {
            throw new InvalidOperationException("Строка не найдена.");
        }

        _data.DeleteDocLine(docLineId);
    }

    private CloseDocCheck BuildCloseDocCheck(long docId)
    {
        var check = new CloseDocCheck();
        var doc = _data.GetDoc(docId);
        if (doc == null)
        {
            check.Errors.Add("Документ не найден.");
            return check;
        }

        if (doc.Status == DocStatus.Closed)
        {
            check.Errors.Add("Документ уже закрыт.");
            return check;
        }

        var lines = _data.GetDocLines(docId);
        var itemsById = _data.GetItems(null).ToDictionary(item => item.Id, item => item.Name);
        var locationsById = _data.GetLocations().ToDictionary(location => location.Id, location => location.Code);

        var outgoing = new Dictionary<(long itemId, long locationId), double>();

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var itemLabel = itemsById.TryGetValue(line.ItemId, out var name) ? name : $"ID {line.ItemId}";
            var rowLabel = $"Строка {index + 1} ({itemLabel})";

            if (line.Qty <= 0)
            {
                check.Errors.Add($"{rowLabel}: количество должно быть > 0.");
            }

            switch (doc.Type)
            {
                case DocType.Inbound:
                    if (!line.ToLocationId.HasValue)
                    {
                        check.Errors.Add($"{rowLabel}: требуется место хранения получателя.");
                    }
                    break;
                case DocType.WriteOff:
                    if (!line.FromLocationId.HasValue)
                    {
                        check.Errors.Add($"{rowLabel}: требуется место хранения списания.");
                    }
                    break;
                case DocType.Outbound:
                    if (!line.FromLocationId.HasValue)
                    {
                        check.Errors.Add($"{rowLabel}: требуется место хранения отгрузки.");
                    }
                    break;
                case DocType.Move:
                    if (!line.FromLocationId.HasValue || !line.ToLocationId.HasValue)
                    {
                        check.Errors.Add($"{rowLabel}: требуются оба места хранения (откуда/куда).");
                    }
                    else if (line.FromLocationId.Value == line.ToLocationId.Value)
                    {
                        check.Errors.Add($"{rowLabel}: места хранения откуда/куда должны быть разными.");
                    }
                    break;
            }

            if (doc.Type is DocType.WriteOff or DocType.Move or DocType.Outbound)
            {
                if (line.Qty > 0 && line.FromLocationId.HasValue)
                {
                    if (doc.Type is DocType.WriteOff or DocType.Outbound || line.ToLocationId.HasValue)
                    {
                        if (doc.Type != DocType.Move || line.FromLocationId != line.ToLocationId)
                        {
                            var key = (line.ItemId, line.FromLocationId.Value);
                            outgoing[key] = outgoing.TryGetValue(key, out var current) ? current + line.Qty : line.Qty;
                        }
                    }
                }
            }
        }

        if (doc.Type is DocType.WriteOff or DocType.Move or DocType.Outbound)
        {
            foreach (var entry in outgoing)
            {
                var current = _data.GetLedgerBalance(entry.Key.itemId, entry.Key.locationId);
                var future = current - entry.Value;
                if (future < 0)
                {
                    var itemLabel = itemsById.TryGetValue(entry.Key.itemId, out var name) ? name : $"ID {entry.Key.itemId}";
                    var locationLabel = locationsById.TryGetValue(entry.Key.locationId, out var code) ? code : $"ID {entry.Key.locationId}";
                    check.Warnings.Add($"{itemLabel} @ {locationLabel}: {FormatQty(current)} -> {FormatQty(future)} (дельта -{FormatQty(entry.Value)})");
                }
            }
        }

        check.Doc = doc;
        if (doc.Type == DocType.Outbound && !doc.PartnerId.HasValue)
        {
            check.Errors.Add("Для отгрузки требуется контрагент.");
        }
        if (doc.Type == DocType.Outbound && string.IsNullOrWhiteSpace(doc.OrderRef))
        {
            check.Errors.Add("Для отгрузки требуется номер заказа.");
        }
        return check;
    }

    private static string FormatQty(double value)
    {
        return value.ToString("0.###", CultureInfo.CurrentCulture);
    }

    private static void ValidateLineLocations(DocType type, long? fromLocationId, long? toLocationId)
    {
        switch (type)
        {
            case DocType.Inbound:
                if (!toLocationId.HasValue)
                {
                    throw new ArgumentException("Для приемки требуется место хранения получателя.");
                }
                break;
            case DocType.WriteOff:
                if (!fromLocationId.HasValue)
                {
                    throw new ArgumentException("Для списания требуется место хранения источника.");
                }
                break;
            case DocType.Outbound:
                if (!fromLocationId.HasValue)
                {
                    throw new ArgumentException("Для отгрузки требуется место хранения источника.");
                }
                break;
            case DocType.Move:
                if (!fromLocationId.HasValue || !toLocationId.HasValue)
                {
                    throw new ArgumentException("Для перемещения требуются оба места хранения (откуда/куда).");
                }
                if (fromLocationId.Value == toLocationId.Value)
                {
                    throw new ArgumentException("Места хранения откуда/куда должны быть разными.");
                }
                break;
        }
    }

    private sealed class CloseDocCheck
    {
        public Doc? Doc { get; set; }
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
    }
}
