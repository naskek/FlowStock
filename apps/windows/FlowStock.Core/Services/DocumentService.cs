using System.Globalization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

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

    public IReadOnlyList<Doc> GetDocsByOrder(long orderId)
    {
        return _data.GetDocsByOrder(orderId);
    }

    public string GenerateDocRef(DocType type, DateTime date)
    {
        return DocRefGenerator.Generate(_data, type, date.Date);
    }

    public long CreateDoc(DocType type, string docRef, string? comment, long? partnerId, string? orderRef, string? shippingRef, long? orderId = null)
    {
        if (string.IsNullOrWhiteSpace(docRef))
        {
            throw new ArgumentException("Номер документа обязателен.", nameof(docRef));
        }

        var trimmedRef = docRef.Trim();
        if (_data.FindDocByRef(trimmedRef) != null)
        {
            throw new ArgumentException("Документ с таким номером уже существует.", nameof(docRef));
        }
        if (TryParseDocRefSequence(trimmedRef, out var year, out var sequence)
            && _data.IsDocRefSequenceTaken(year, sequence))
        {
            throw new ArgumentException("Документ с таким номером уже существует.", nameof(docRef));
        }

        if (partnerId.HasValue && _data.GetPartner(partnerId.Value) == null)
        {
            throw new ArgumentException("Контрагент не найден.", nameof(partnerId));
        }

        if (orderId.HasValue && _data.GetOrder(orderId.Value) == null)
        {
            throw new ArgumentException("Заказ не найден.", nameof(orderId));
        }

        var order = orderId.HasValue ? _data.GetOrder(orderId.Value) : null;
        var resolvedOrderRef = order?.OrderRef ?? orderRef;
        var cleanedOrderRef = string.IsNullOrWhiteSpace(resolvedOrderRef) ? null : resolvedOrderRef.Trim();
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
            OrderId = orderId,
            OrderRef = cleanedOrderRef,
            ShippingRef = cleanedShippingRef,
            Comment = cleanedComment
        };

        if (!orderId.HasValue)
        {
            return _data.AddDoc(doc);
        }

        long docId = 0;
        _data.ExecuteInTransaction(store =>
        {
            docId = store.AddDoc(doc);
            var (fromHu, toHu) = ResolveHeaderHu(doc.Type, doc.ShippingRef);
            foreach (var line in store.GetOrderLines(orderId.Value))
            {
                if (line.QtyOrdered <= 0)
                {
                    continue;
                }

                store.AddDocLine(new DocLine
                {
                    DocId = docId,
                    ItemId = line.ItemId,
                    Qty = line.QtyOrdered,
                    QtyInput = null,
                    UomCode = null,
                    FromLocationId = null,
                    ToLocationId = null,
                    FromHu = fromHu,
                    ToHu = toHu
                });
            }
        });

        return docId;
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
            Dictionary<StockKey, double>? inventoryTotals = doc.Type == DocType.Inventory
                ? new Dictionary<StockKey, double>()
                : null;
            var docHu = NormalizeHuValue(doc.ShippingRef);
            if (string.IsNullOrWhiteSpace(docHu))
            {
                var inferred = ResolveShippingRefFromLines(doc.Type, lines);
                if (!string.IsNullOrWhiteSpace(inferred))
                {
                    store.UpdateDocHeader(docId, doc.PartnerId, doc.OrderRef, inferred);
                    docHu = inferred;
                }
            }
            foreach (var line in lines)
            {
                var (fromHu, toHu) = ResolveLedgerHu(doc, line, docHu);
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
                                QtyDelta = line.Qty,
                                HuCode = toHu
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
                                QtyDelta = -line.Qty,
                                HuCode = fromHu
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
                                QtyDelta = -line.Qty,
                                HuCode = fromHu
                            });
                        }
                        else
                        {
                            var remaining = line.Qty;
                            var locations = store.GetLocations()
                                .OrderBy(location => location.Code, StringComparer.OrdinalIgnoreCase)
                                .ToList();

                            var outboundHu = (string?)null;
                            foreach (var location in locations)
                            {
                                if (remaining <= 0)
                                {
                                    break;
                                }

                                var available = store.GetLedgerBalance(line.ItemId, location.Id, outboundHu);
                                if (available <= 0)
                                {
                                    continue;
                                }

                                var take = Math.Min(available, remaining);
                                store.AddLedgerEntry(new LedgerEntry
                                {
                                    Timestamp = closedAt,
                                    DocId = docId,
                                    ItemId = line.ItemId,
                                    LocationId = location.Id,
                                    QtyDelta = -take,
                                    HuCode = outboundHu
                                });
                                remaining -= take;
                            }
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
                                QtyDelta = -line.Qty,
                                HuCode = fromHu
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
                                QtyDelta = line.Qty,
                                HuCode = toHu
                            });
                        }
                        break;
                    case DocType.Inventory:
                        if (line.ToLocationId.HasValue && inventoryTotals != null)
                        {
                            var key = new StockKey(line.ItemId, line.ToLocationId.Value, NormalizeHuValue(toHu));
                            inventoryTotals[key] = inventoryTotals.TryGetValue(key, out var current)
                                ? current + line.Qty
                                : line.Qty;
                        }
                        break;
                }
            }

            if (inventoryTotals != null)
            {
                foreach (var entry in inventoryTotals)
                {
                    var current = store.GetAvailableQty(entry.Key.ItemId, entry.Key.LocationId, entry.Key.Hu);
                    var delta = entry.Value - current;
                    if (Math.Abs(delta) <= 0.000001)
                    {
                        continue;
                    }

                    store.AddLedgerEntry(new LedgerEntry
                    {
                        Timestamp = closedAt,
                        DocId = docId,
                        ItemId = entry.Key.ItemId,
                        LocationId = entry.Key.LocationId,
                        QtyDelta = delta,
                        HuCode = entry.Key.Hu
                    });
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

    public void MarkDocForRecount(long docId)
    {
        var doc = _data.GetDoc(docId) ?? throw new InvalidOperationException("Документ не найден.");
        if (doc.Status != DocStatus.Draft)
        {
            throw new InvalidOperationException("Документ уже закрыт.");
        }
        if (doc.Type != DocType.Inventory)
        {
            throw new InvalidOperationException("На пересчет можно отправить только инвентаризацию.");
        }

        var current = doc.Comment?.Trim();
        var next = BuildRecountComment(current);
        _data.UpdateDocComment(docId, next);
    }

    public int ApplyOrderToDoc(long docId, long orderId)
    {
        var doc = _data.GetDoc(docId) ?? throw new InvalidOperationException("Документ не найден.");
        if (doc.Status != DocStatus.Draft)
        {
            throw new InvalidOperationException("Документ уже закрыт.");
        }

        var order = _data.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        var cleanedOrderRef = order.OrderRef.Trim();

        var addedLines = 0;
        _data.ExecuteInTransaction(store =>
        {
            store.UpdateDocHeader(docId, order.PartnerId, cleanedOrderRef, doc.ShippingRef);
            store.UpdateDocOrder(docId, order.Id, cleanedOrderRef);
            store.DeleteDocLines(docId);
            var (fromHu, toHu) = ResolveHeaderHu(doc.Type, doc.ShippingRef);

            var orderedByItem = new Dictionary<long, double>();
            foreach (var line in store.GetOrderLines(orderId))
            {
                if (line.QtyOrdered <= 0)
                {
                    continue;
                }

                orderedByItem[line.ItemId] = orderedByItem.TryGetValue(line.ItemId, out var current)
                    ? current + line.QtyOrdered
                    : line.QtyOrdered;
            }

            var shippedByItem = store.GetShippedTotalsByOrder(orderId);
            foreach (var entry in orderedByItem)
            {
                var shipped = shippedByItem.TryGetValue(entry.Key, out var shippedQty) ? shippedQty : 0;
                var remaining = entry.Value - shipped;
                if (remaining <= 0)
                {
                    continue;
                }

                store.AddDocLine(new DocLine
                {
                    DocId = docId,
                    ItemId = entry.Key,
                    Qty = remaining,
                    QtyInput = null,
                    UomCode = null,
                    FromLocationId = null,
                    ToLocationId = null,
                    FromHu = fromHu,
                    ToHu = toHu
                });
                addedLines++;
            }
        });

        return addedLines;
    }

    public void ClearDocOrder(long docId, long? partnerId)
    {
        var doc = _data.GetDoc(docId) ?? throw new InvalidOperationException("Документ не найден.");
        if (doc.Status != DocStatus.Draft)
        {
            throw new InvalidOperationException("Документ уже закрыт.");
        }

        _data.ExecuteInTransaction(store =>
        {
            store.UpdateDocHeader(docId, partnerId, null, doc.ShippingRef);
            store.UpdateDocOrder(docId, null, null);
        });
    }

    public void AddDocLine(long docId, long itemId, double qty, long? fromLocationId, long? toLocationId, double? qtyInput = null, string? uomCode = null, string? fromHu = null, string? toHu = null)
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

        ValidateLineLocations(doc.Type, fromLocationId, toLocationId, NormalizeHuValue(fromHu), NormalizeHuValue(toHu));

        _data.AddDocLine(new DocLine
        {
            DocId = docId,
            ItemId = itemId,
            Qty = qty,
            QtyInput = qtyInput,
            UomCode = uomCode,
            FromLocationId = fromLocationId,
            ToLocationId = toLocationId,
            FromHu = NormalizeHuValue(fromHu),
            ToHu = NormalizeHuValue(toHu)
        });
    }

    public void UpdateDocLineQty(long docId, long docLineId, double qty, double? qtyInput = null, string? uomCode = null)
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

        _data.UpdateDocLineQty(docLineId, qty, qtyInput, uomCode);
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

        var docHu = NormalizeHuValue(doc.ShippingRef);
        var lines = _data.GetDocLines(docId);
        if (lines.Count == 0)
        {
            check.Errors.Add("Добавьте хотя бы один товар в документ перед проведением.");
            return check;
        }
        var itemsById = _data.GetItems(null).ToDictionary(item => item.Id, item => item.Name);
        var locations = _data.GetLocations();
        var locationsById = locations.ToDictionary(location => location.Id, location => location.Code);

        var outgoingBySource = new Dictionary<StockKey, double>();
        var outboundByItem = new Dictionary<long, double>();

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var itemLabel = itemsById.TryGetValue(line.ItemId, out var name) ? name : $"ID {line.ItemId}";
            var rowLabel = $"Строка {index + 1} ({itemLabel})";
            var (fromHu, toHu) = ResolveLedgerHu(doc, line, docHu);

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
                    break;
                case DocType.Move:
                    if (!line.FromLocationId.HasValue || !line.ToLocationId.HasValue)
                    {
                        check.Errors.Add($"{rowLabel}: требуются оба места хранения (откуда/куда).");
                    }
                    else if (line.FromLocationId.Value == line.ToLocationId.Value
                             && string.IsNullOrWhiteSpace(NormalizeHuValue(fromHu))
                             && string.IsNullOrWhiteSpace(NormalizeHuValue(toHu)))
                    {
                        check.Errors.Add(
                            $"{rowLabel}: места хранения откуда/куда должны быть разными. Если вы хотите упаковать в HU в том же месте - заполните HU.");
                    }
                    break;
                case DocType.Inventory:
                    if (!line.ToLocationId.HasValue)
                    {
                        check.Errors.Add($"{rowLabel}: требуется место хранения.");
                    }
                    break;
            }

            if (doc.Type is DocType.WriteOff or DocType.Move or DocType.Outbound)
            {
                if (line.Qty > 0 && line.FromLocationId.HasValue)
                {
                    var key = new StockKey(line.ItemId, line.FromLocationId.Value, NormalizeHuValue(fromHu));
                    outgoingBySource[key] = outgoingBySource.TryGetValue(key, out var current) ? current + line.Qty : line.Qty;
                }
                else if (doc.Type == DocType.Outbound)
                {
                    outboundByItem[line.ItemId] = outboundByItem.TryGetValue(line.ItemId, out var current)
                        ? current + line.Qty
                        : line.Qty;
                }
            }
        }

        if (outgoingBySource.Count > 0)
        {
            foreach (var entry in outgoingBySource)
            {
                var current = _data.GetAvailableQty(entry.Key.ItemId, entry.Key.LocationId, entry.Key.Hu);
                var future = current - entry.Value;
                if (future < 0)
                {
                    var itemLabel = itemsById.TryGetValue(entry.Key.ItemId, out var name) ? name : $"ID {entry.Key.ItemId}";
                    var locationLabel = locationsById.TryGetValue(entry.Key.LocationId, out var code) ? code : $"ID {entry.Key.LocationId}";
                    var huLabel = string.IsNullOrWhiteSpace(entry.Key.Hu) ? string.Empty : $" (HU {entry.Key.Hu})";
                    check.Errors.Add($"{itemLabel} @ {locationLabel}{huLabel}: на складе {FormatQty(current)}, требуется {FormatQty(entry.Value)}.");
                }
            }
        }

        if (doc.Type == DocType.Outbound)
        {
            var autoAllocation = lines.Any(line => !line.FromLocationId.HasValue);
            foreach (var entry in outboundByItem)
            {
                var current = GetTotalAvailableQty(entry.Key, autoAllocation ? null : docHu, locations);
                var future = current - entry.Value;
                if (future < 0)
                {
                    var itemLabel = itemsById.TryGetValue(entry.Key, out var name) ? name : $"ID {entry.Key}";
                    check.Errors.Add($"{itemLabel}: на складе {FormatQty(current)}, требуется {FormatQty(entry.Value)}.");
                }
            }
        }

        foreach (var error in BuildHuLocationErrors(doc, lines, locationsById))
        {
            check.Errors.Add(error);
        }

        check.Doc = doc;
        if (doc.Type == DocType.Outbound && !doc.PartnerId.HasValue)
        {
            check.Errors.Add("Для отгрузки требуется контрагент.");
        }
        return check;
    }

    private IEnumerable<string> BuildHuLocationErrors(
        Doc doc,
        IReadOnlyList<DocLine> lines,
        IReadOnlyDictionary<long, string> locationsById)
    {
        var docHu = NormalizeHuValue(doc.ShippingRef);
        var touchedHu = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var (fromHu, toHu) = ResolveLedgerHu(doc, line, docHu);
            if (!string.IsNullOrWhiteSpace(fromHu))
            {
                touchedHu.Add(fromHu);
            }
            if (!string.IsNullOrWhiteSpace(toHu))
            {
                touchedHu.Add(toHu);
            }
        }

        if (touchedHu.Count == 0)
        {
            return Array.Empty<string>();
        }

        var totalsByHu = new Dictionary<string, Dictionary<long, double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _data.GetHuStockRows())
        {
            if (string.IsNullOrWhiteSpace(row.HuCode) || !touchedHu.Contains(row.HuCode))
            {
                continue;
            }

            AddHuLocationQty(totalsByHu, row.HuCode, row.LocationId, row.Qty);
        }

        foreach (var line in lines)
        {
            var (fromHu, toHu) = ResolveLedgerHu(doc, line, docHu);
            if (line.FromLocationId.HasValue && !string.IsNullOrWhiteSpace(fromHu))
            {
                AddHuLocationQty(totalsByHu, fromHu, line.FromLocationId.Value, -line.Qty);
            }
            if (line.ToLocationId.HasValue && !string.IsNullOrWhiteSpace(toHu))
            {
                AddHuLocationQty(totalsByHu, toHu, line.ToLocationId.Value, line.Qty);
            }
        }

        var errors = new List<string>();
        foreach (var hu in touchedHu)
        {
            if (!totalsByHu.TryGetValue(hu, out var locationTotals))
            {
                continue;
            }

            var locations = locationTotals
                .Where(entry => Math.Abs(entry.Value) > 0.000001)
                .Select(entry => locationsById.TryGetValue(entry.Key, out var code) ? code : $"ID {entry.Key}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (locations.Count > 1)
            {
                errors.Add($"HU {hu} должен находиться только в одной локации. Сейчас: {string.Join(", ", locations)}.");
            }
        }

        return errors;
    }

    private static void AddHuLocationQty(
        IDictionary<string, Dictionary<long, double>> totalsByHu,
        string hu,
        long locationId,
        double delta)
    {
        if (!totalsByHu.TryGetValue(hu, out var locationTotals))
        {
            locationTotals = new Dictionary<long, double>();
            totalsByHu[hu] = locationTotals;
        }

        locationTotals[locationId] = locationTotals.TryGetValue(locationId, out var current)
            ? current + delta
            : delta;
    }

    private static bool TryParseDocRefSequence(string docRef, out int year, out int sequence)
    {
        year = 0;
        sequence = 0;
        if (string.IsNullOrWhiteSpace(docRef))
        {
            return false;
        }

        var parts = docRef.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return false;
        }

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out year))
        {
            year = 0;
            return false;
        }

        if (!int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out sequence))
        {
            sequence = 0;
            return false;
        }

        return year > 0 && sequence > 0;
    }

    private static string FormatQty(double value)
    {
        return value.ToString("0.###", CultureInfo.CurrentCulture);
    }

    private double GetTotalAvailableQty(long itemId, string? huCode, IReadOnlyList<Location> locations)
    {
        var total = 0d;
        foreach (var location in locations)
        {
            total += _data.GetAvailableQty(itemId, location.Id, huCode);
        }

        return total;
    }

    private static void ValidateLineLocations(DocType type, long? fromLocationId, long? toLocationId, string? fromHu, string? toHu)
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
                if (fromLocationId.Value == toLocationId.Value
                    && string.IsNullOrWhiteSpace(NormalizeHuValue(fromHu))
                    && string.IsNullOrWhiteSpace(NormalizeHuValue(toHu)))
                {
                    throw new ArgumentException(
                        "Для перемещения места хранения должны быть разными. Если вы хотите упаковать в HU в том же месте - заполните HU.");
                }
                break;
            case DocType.Inventory:
                if (!toLocationId.HasValue)
                {
                    throw new ArgumentException("Для инвентаризации требуется место хранения.");
                }
                break;
        }
    }

    private static string? NormalizeHuValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string BuildRecountComment(string? current)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return "TSD:RECOUNT";
        }

        if (current.IndexOf("RECOUNT", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return current;
        }

        if (current.StartsWith("TSD", StringComparison.OrdinalIgnoreCase))
        {
            return "TSD:RECOUNT";
        }

        return $"{current} RECOUNT";
    }

    private static (string? fromHu, string? toHu) ResolveLedgerHu(Doc doc, DocLine line, string? docHu)
    {
        var lineFrom = NormalizeHuValue(line.FromHu);
        var lineTo = NormalizeHuValue(line.ToHu);

        if (doc.Type == DocType.Move)
        {
            if (!string.IsNullOrWhiteSpace(lineFrom) || !string.IsNullOrWhiteSpace(lineTo))
            {
                if (!string.IsNullOrWhiteSpace(lineFrom)
                    && string.IsNullOrWhiteSpace(lineTo)
                    && line.FromLocationId.HasValue
                    && line.ToLocationId.HasValue
                    && line.FromLocationId.Value != line.ToLocationId.Value)
                {
                    return (lineFrom, lineFrom);
                }

                return (lineFrom, lineTo);
            }

            if (!string.IsNullOrWhiteSpace(docHu))
            {
                return (null, docHu);
            }

            return (lineFrom, lineTo);
        }

        if (!string.IsNullOrWhiteSpace(docHu))
        {
            return doc.Type switch
            {
                DocType.Inbound => (null, docHu),
                DocType.Inventory => (null, docHu),
                DocType.Outbound => (docHu, null),
                DocType.WriteOff => (docHu, null),
                _ => (lineFrom, lineTo)
            };
        }

        return (lineFrom, lineTo);
    }

    private static string? ResolveShippingRefFromLines(DocType type, IReadOnlyList<DocLine> lines)
    {
        IEnumerable<string?> values = type switch
        {
            DocType.Inbound or DocType.Inventory => lines.Select(line => line.ToHu),
            DocType.Outbound or DocType.WriteOff => lines.Select(line => line.FromHu),
            DocType.Move => lines.Select(line => line.ToHu),
            _ => Enumerable.Empty<string?>()
        };

        var distinct = values
            .Select(NormalizeHuValue)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return distinct.Count == 1 ? distinct[0] : null;
    }

    private static (string? fromHu, string? toHu) ResolveHeaderHu(DocType type, string? huCode)
    {
        var normalized = NormalizeHuValue(huCode);
        return type switch
        {
            DocType.Inbound => (null, normalized),
            DocType.Inventory => (null, normalized),
            DocType.Outbound => (normalized, null),
            DocType.WriteOff => (normalized, null),
            DocType.Move => (null, normalized),
            _ => (null, null)
        };
    }

    private readonly record struct StockKey(long ItemId, long LocationId, string? Hu);

    private sealed class CloseDocCheck
    {
        public Doc? Doc { get; set; }
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
    }
}

