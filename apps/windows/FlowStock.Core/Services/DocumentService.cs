using System.Globalization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class DocumentService
{
    private readonly IDataStore _data;
    private const string AutoHuCreatedBy = "WINDOWS-AUTO";
    private static bool KmWorkflowEnabled => false;

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
        if (order != null && type == DocType.Outbound && order.Type != OrderType.Customer)
        {
            throw new ArgumentException("Внутренний заказ нельзя использовать в клиентской отгрузке.", nameof(orderId));
        }
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
            if (doc.Type == DocType.Outbound)
            {
                foreach (var line in store.GetOrderShipmentRemaining(orderId.Value))
                {
                    if (line.QtyRemaining <= 0)
                    {
                        continue;
                    }

                    store.AddDocLine(new DocLine
                    {
                        DocId = docId,
                        OrderLineId = line.OrderLineId,
                        ItemId = line.ItemId,
                        Qty = line.QtyRemaining,
                        QtyInput = null,
                        UomCode = null,
                        FromLocationId = null,
                        ToLocationId = null,
                        FromHu = fromHu,
                        ToHu = toHu
                    });
                }
                return;
            }

            if (doc.Type == DocType.ProductionReceipt)
            {
                foreach (var line in store.GetOrderReceiptRemaining(orderId.Value))
                {
                    if (line.QtyRemaining <= 0)
                    {
                        continue;
                    }

                    store.AddDocLine(new DocLine
                    {
                        DocId = docId,
                        OrderLineId = line.OrderLineId,
                        ItemId = line.ItemId,
                        Qty = line.QtyRemaining,
                        QtyInput = null,
                        UomCode = null,
                        FromLocationId = null,
                        ToLocationId = null,
                        FromHu = fromHu,
                        ToHu = toHu
                    });
                }
                return;
            }

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

    public IReadOnlyList<OrderReceiptLine> GetOrderReceiptRemaining(long orderId)
    {
        return _data.GetOrderReceiptRemaining(orderId);
    }

    public IReadOnlyList<OrderShipmentLine> GetOrderShipmentRemaining(long orderId)
    {
        return _data.GetOrderShipmentRemaining(orderId);
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

            var lines = EnsureOrderLineLinks(store, doc, store.GetDocLines(docId));
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

            if (KmWorkflowEnabled && doc.Type == DocType.Outbound)
            {
                AutoAssignOutboundKmCodes(store, doc, lines, docHu, docId);
            }

            foreach (var line in lines)
            {
                var (fromHu, toHu) = ResolveLedgerHu(doc, line, docHu);
                switch (doc.Type)
                {
                    case DocType.Inbound:
                    case DocType.ProductionReceipt:
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

                            var outboundHu = NormalizeHuValue(fromHu);
                            if (!string.IsNullOrWhiteSpace(outboundHu))
                            {
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
                                break;
                            }

                            var locationCodes = locations.ToDictionary(location => location.Id, location => location.Code);
                            var nonHuSources = locations
                                .Select(location => new
                                {
                                    LocationId = location.Id,
                                    location.Code,
                                    HuCode = (string?)null,
                                    Qty = store.GetLedgerBalance(line.ItemId, location.Id, null)
                                })
                                .Where(source => source.Qty > 0);

                            var huSources = store.GetHuStockRows()
                                .Where(row => row.ItemId == line.ItemId && row.Qty > 0)
                                .Select(row => new
                                {
                                    row.LocationId,
                                    Code = locationCodes.TryGetValue(row.LocationId, out var code) ? code : row.LocationId.ToString(CultureInfo.InvariantCulture),
                                    HuCode = NormalizeHuValue(row.HuCode),
                                    row.Qty
                                })
                                .Where(source => !string.IsNullOrWhiteSpace(source.HuCode));

                            var sources = nonHuSources
                                .Concat(huSources)
                                .OrderBy(source => source.Code, StringComparer.OrdinalIgnoreCase)
                                .ThenBy(source => string.IsNullOrWhiteSpace(source.HuCode) ? 0 : 1)
                                .ThenBy(source => source.HuCode, StringComparer.OrdinalIgnoreCase)
                                .ToList();

                            foreach (var source in sources)
                            {
                                if (remaining <= 0)
                                {
                                    break;
                                }

                                var take = Math.Min(source.Qty, remaining);
                                if (take <= 0)
                                {
                                    continue;
                                }

                                store.AddLedgerEntry(new LedgerEntry
                                {
                                    Timestamp = closedAt,
                                    DocId = docId,
                                    ItemId = line.ItemId,
                                    LocationId = source.LocationId,
                                    QtyDelta = -take,
                                    HuCode = source.HuCode
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

    public void UpdateDocReason(long docId, string? reasonCode)
    {
        var doc = _data.GetDoc(docId) ?? throw new InvalidOperationException("Документ не найден.");
        if (doc.Status != DocStatus.Draft)
        {
            throw new InvalidOperationException("Документ уже закрыт.");
        }

        var cleanedReason = string.IsNullOrWhiteSpace(reasonCode) ? null : reasonCode.Trim();
        _data.UpdateDocReason(docId, cleanedReason);
    }

    public void UpdateDocComment(long docId, string? comment)
    {
        var doc = _data.GetDoc(docId) ?? throw new InvalidOperationException("Документ не найден.");
        if (doc.Status != DocStatus.Draft)
        {
            throw new InvalidOperationException("Документ уже закрыт.");
        }

        var cleaned = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        _data.UpdateDocComment(docId, cleaned);
    }

    public void UpdateDocProductionBatch(long docId, string? productionBatchNo)
    {
        var doc = _data.GetDoc(docId) ?? throw new InvalidOperationException("Документ не найден.");
        if (doc.Status != DocStatus.Draft)
        {
            throw new InvalidOperationException("Документ уже закрыт.");
        }

        var cleaned = string.IsNullOrWhiteSpace(productionBatchNo) ? null : productionBatchNo.Trim();
        _data.UpdateDocProductionBatch(docId, cleaned);
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
        if (order.Type != OrderType.Customer)
        {
            throw new InvalidOperationException("Внутренний заказ нельзя использовать в отгрузке.");
        }
        var cleanedOrderRef = order.OrderRef.Trim();

        var addedLines = 0;
        _data.ExecuteInTransaction(store =>
        {
            store.UpdateDocHeader(docId, order.PartnerId, cleanedOrderRef, doc.ShippingRef);
            store.UpdateDocOrder(docId, order.Id, cleanedOrderRef);
            store.DeleteDocLines(docId);
            var (fromHu, toHu) = ResolveHeaderHu(doc.Type, doc.ShippingRef);

            foreach (var line in store.GetOrderShipmentRemaining(orderId))
            {
                if (line.QtyRemaining <= 0)
                {
                    continue;
                }

                store.AddDocLine(new DocLine
                {
                    DocId = docId,
                    OrderLineId = line.OrderLineId,
                    ItemId = line.ItemId,
                    Qty = line.QtyRemaining,
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

    public int ApplyOrderToProductionReceipt(long docId, long orderId, long? toLocationId, string? toHu, bool replaceLines)
    {
        var doc = _data.GetDoc(docId) ?? throw new InvalidOperationException("Документ не найден.");
        if (doc.Status != DocStatus.Draft)
        {
            throw new InvalidOperationException("Документ уже закрыт.");
        }

        if (doc.Type != DocType.ProductionReceipt)
        {
            throw new InvalidOperationException("Документ не является выпуском продукции.");
        }

        if (!toLocationId.HasValue)
        {
            throw new InvalidOperationException("Требуется локация приемки.");
        }

        var order = _data.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        var cleanedOrderRef = order.OrderRef.Trim();
        var normalizedToHu = NormalizeHuValue(toHu);

        var addedLines = 0;
        _data.ExecuteInTransaction(store =>
        {
            store.UpdateDocOrder(docId, order.Id, cleanedOrderRef);
            if (replaceLines)
            {
                store.DeleteDocLines(docId);
            }

            var remaining = store.GetOrderReceiptRemaining(orderId);
            foreach (var line in remaining)
            {
                if (line.QtyRemaining <= 0)
                {
                    continue;
                }

                store.AddDocLine(new DocLine
                {
                    DocId = docId,
                    OrderLineId = line.OrderLineId,
                    ItemId = line.ItemId,
                    Qty = line.QtyRemaining,
                    QtyInput = null,
                    UomCode = null,
                    FromLocationId = null,
                    ToLocationId = toLocationId,
                    FromHu = null,
                    ToHu = normalizedToHu
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

    public void UpdateDocOrderBinding(long docId, long? orderId)
    {
        var doc = _data.GetDoc(docId) ?? throw new InvalidOperationException("Документ не найден.");
        if (doc.Status != DocStatus.Draft)
        {
            throw new InvalidOperationException("Документ уже закрыт.");
        }

        if (!orderId.HasValue)
        {
            _data.UpdateDocOrder(docId, null, null);
            return;
        }

        var order = _data.GetOrder(orderId.Value) ?? throw new InvalidOperationException("Заказ не найден.");
        if (doc.Type == DocType.Outbound && order.Type != OrderType.Customer)
        {
            throw new InvalidOperationException("Внутренний заказ нельзя использовать в отгрузке.");
        }
        var cleanedOrderRef = order.OrderRef.Trim();
        _data.UpdateDocOrder(docId, order.Id, cleanedOrderRef);
    }

    public void AddDocLine(long docId, long itemId, double qty, long? fromLocationId, long? toLocationId, double? qtyInput = null, string? uomCode = null, string? fromHu = null, string? toHu = null, long? orderLineId = null)
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
            OrderLineId = orderLineId,
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

        var lines = EnsureOrderLineLinks(_data, doc, _data.GetDocLines(docId));
        var line = lines.FirstOrDefault(l => l.Id == docLineId);
        if (line == null)
        {
            throw new InvalidOperationException("Строка не найдена.");
        }

        if (doc.Type == DocType.ProductionReceipt && line.OrderLineId.HasValue)
        {
            if (!doc.OrderId.HasValue)
            {
                throw new InvalidOperationException("Для строки заказа требуется указать заказ в документе.");
            }

            var remaining = _data.GetOrderReceiptRemaining(doc.OrderId.Value)
                .ToDictionary(entry => entry.OrderLineId, entry => entry.QtyRemaining);
            if (!remaining.TryGetValue(line.OrderLineId.Value, out var limit))
            {
                throw new InvalidOperationException("Строка заказа не найдена.");
            }

            var total = lines
                .Where(l => l.OrderLineId == line.OrderLineId)
                .Sum(l => l.Id == docLineId ? qty : l.Qty);
            if (total > limit + 0.000001)
            {
                throw new InvalidOperationException($"Количество превышает остаток по заказу: доступно {FormatQty(limit)}.");
            }
        }
        else if (doc.Type == DocType.Outbound && line.OrderLineId.HasValue)
        {
            if (!doc.OrderId.HasValue)
            {
                throw new InvalidOperationException("Для строки заказа требуется указать заказ в документе.");
            }

            var remaining = _data.GetOrderShipmentRemaining(doc.OrderId.Value)
                .ToDictionary(entry => entry.OrderLineId, entry => entry.QtyRemaining);
            if (!remaining.TryGetValue(line.OrderLineId.Value, out var limit))
            {
                throw new InvalidOperationException("Строка заказа не найдена.");
            }

            var total = lines
                .Where(l => l.OrderLineId == line.OrderLineId)
                .Sum(l => l.Id == docLineId ? qty : l.Qty);
            if (total > limit + 0.000001)
            {
                throw new InvalidOperationException($"Количество превышает остаток по заказу: доступно {FormatQty(limit)}.");
            }
        }

        _data.UpdateDocLineQty(docLineId, qty, qtyInput, uomCode);
    }

    public void AssignDocLineHu(long docId, long docLineId, double qty, string? fromHu, string? toHu)
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

        var lines = EnsureOrderLineLinks(_data, doc, _data.GetDocLines(docId));
        var line = lines.FirstOrDefault(l => l.Id == docLineId);
        if (line == null)
        {
            throw new InvalidOperationException("Строка не найдена.");
        }

        var normalizedFromHu = NormalizeHuValue(fromHu);
        var normalizedToHu = NormalizeHuValue(toHu);
        ValidateLineLocations(doc.Type, line.FromLocationId, line.ToLocationId, normalizedFromHu, normalizedToHu);
        EnsureHuAssignmentAllowed(_data, doc.Type, line.Id);

        if (qty > line.Qty + 0.000001)
        {
            throw new InvalidOperationException($"Количество превышает строку: доступно {FormatQty(line.Qty)}.");
        }

        var sameHu = string.Equals(NormalizeHuValue(line.FromHu), normalizedFromHu, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(NormalizeHuValue(line.ToHu), normalizedToHu, StringComparison.OrdinalIgnoreCase);

        if (sameHu)
        {
            if (Math.Abs(qty - line.Qty) <= 0.000001)
            {
                return;
            }

            throw new InvalidOperationException("Строка уже привязана к выбранному HU.");
        }

        if (Math.Abs(qty - line.Qty) <= 0.000001)
        {
            _data.UpdateDocLineHu(docLineId, normalizedFromHu, normalizedToHu);
            return;
        }

        var ratio = line.QtyInput.HasValue && line.Qty > 0
            ? line.QtyInput.Value / line.Qty
            : (double?)null;
        var allocatedInput = ratio.HasValue ? ratio.Value * qty : (double?)null;
        var remainingQty = line.Qty - qty;
        var remainingInput = ratio.HasValue ? ratio.Value * remainingQty : (double?)null;

        _data.ExecuteInTransaction(store =>
        {
            store.UpdateDocLineQty(docLineId, remainingQty, remainingInput, line.UomCode);
            store.AddDocLine(new DocLine
            {
                DocId = docId,
                OrderLineId = line.OrderLineId,
                ItemId = line.ItemId,
                Qty = qty,
                QtyInput = allocatedInput,
                UomCode = line.UomCode,
                FromLocationId = line.FromLocationId,
                ToLocationId = line.ToLocationId,
                FromHu = normalizedFromHu,
                ToHu = normalizedToHu
            });
        });
    }

    public void DistributeProductionLineByHuCapacity(long docId, long docLineId, double maxQtyPerHu, IReadOnlyList<string> huCodes)
    {
        if (maxQtyPerHu <= 0)
        {
            throw new ArgumentException("Лимит на HU должен быть больше 0.", nameof(maxQtyPerHu));
        }

        if (huCodes == null || huCodes.Count == 0)
        {
            throw new ArgumentException("Не переданы HU для распределения.", nameof(huCodes));
        }

        var normalizedHus = huCodes
            .Select(NormalizeHuValue)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedHus.Count == 0)
        {
            throw new InvalidOperationException("Не переданы корректные HU для распределения.");
        }

        var doc = _data.GetDoc(docId) ?? throw new InvalidOperationException("Документ не найден.");
        if (doc.Status != DocStatus.Draft)
        {
            throw new InvalidOperationException("Документ уже закрыт.");
        }

        if (doc.Type != DocType.ProductionReceipt)
        {
            throw new InvalidOperationException("Распределение по вместимости доступно только для выпуска продукции.");
        }

        var lines = EnsureOrderLineLinks(_data, doc, _data.GetDocLines(docId));
        var line = lines.FirstOrDefault(l => l.Id == docLineId);
        if (line == null)
        {
            throw new InvalidOperationException("Строка не найдена.");
        }

        if (line.Qty <= 0)
        {
            throw new InvalidOperationException("Количество в строке должно быть больше 0.");
        }

        EnsureHuAssignmentAllowed(_data, doc.Type, line.Id);

        var requiredHuCount = (int)Math.Ceiling(line.Qty / maxQtyPerHu);
        if (requiredHuCount <= 1)
        {
            AssignDocLineHu(docId, docLineId, line.Qty, line.FromHu, normalizedHus[0]);
            return;
        }

        if (normalizedHus.Count < requiredHuCount)
        {
            throw new InvalidOperationException(
                $"Недостаточно HU для распределения. Нужно: {requiredHuCount}, передано: {normalizedHus.Count}.");
        }

        var chunks = new List<(double qty, string toHu)>(requiredHuCount);
        var remainingQty = line.Qty;
        for (var i = 0; i < requiredHuCount; i++)
        {
            var qty = i == requiredHuCount - 1
                ? remainingQty
                : Math.Min(maxQtyPerHu, remainingQty);
            if (qty <= 0.000001)
            {
                continue;
            }

            var targetHu = normalizedHus[i]!;
            ValidateLineLocations(doc.Type, line.FromLocationId, line.ToLocationId, NormalizeHuValue(line.FromHu), targetHu);
            chunks.Add((qty, targetHu));
            remainingQty -= qty;
        }

        if (chunks.Count == 0)
        {
            throw new InvalidOperationException("Не удалось рассчитать распределение по HU.");
        }

        var inputRatio = line.QtyInput.HasValue && line.Qty > 0
            ? line.QtyInput.Value / line.Qty
            : (double?)null;

        _data.ExecuteInTransaction(store =>
        {
            store.DeleteDocLine(docLineId);

            var remainingInput = line.QtyInput;
            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                var chunkInput = (double?)null;
                if (inputRatio.HasValue)
                {
                    chunkInput = i == chunks.Count - 1
                        ? remainingInput
                        : inputRatio.Value * chunk.qty;
                    if (remainingInput.HasValue)
                    {
                        remainingInput -= chunkInput;
                    }
                }

                store.AddDocLine(new DocLine
                {
                    DocId = docId,
                    OrderLineId = line.OrderLineId,
                    ItemId = line.ItemId,
                    Qty = chunk.qty,
                    QtyInput = chunkInput,
                    UomCode = line.UomCode,
                    FromLocationId = line.FromLocationId,
                    ToLocationId = line.ToLocationId,
                    FromHu = NormalizeHuValue(line.FromHu),
                    ToHu = chunk.toHu
                });
            }
        });
    }

    public int AutoDistributeProductionReceiptHus(long docId, IReadOnlyCollection<long>? docLineIds = null)
    {
        var requestedIds = docLineIds?
            .Distinct()
            .ToList();

        var usedHuCount = 0;
        _data.ExecuteInTransaction(store =>
        {
            var doc = store.GetDoc(docId) ?? throw new InvalidOperationException("Документ не найден.");
            if (doc.Status != DocStatus.Draft)
            {
                throw new InvalidOperationException("Документ уже закрыт.");
            }

            if (doc.Type != DocType.ProductionReceipt)
            {
                throw new InvalidOperationException("Автораспределение HU доступно только для выпуска продукции.");
            }

            var allLines = EnsureOrderLineLinks(store, doc, store.GetDocLines(docId))
                .OrderBy(line => line.Id)
                .ToList();
            if (allLines.Count == 0)
            {
                throw new InvalidOperationException("В документе нет строк для распределения.");
            }

            var selectedIds = requestedIds?.ToHashSet() ?? allLines.Select(line => line.Id).ToHashSet();
            var targetLines = allLines
                .Where(line => selectedIds.Contains(line.Id))
                .ToList();
            if (targetLines.Count == 0)
            {
                throw new InvalidOperationException("Не выбраны строки для распределения.");
            }

            var itemsById = store.GetItems(null).ToDictionary(item => item.Id, item => item);
            var requiredHuCount = 0;
            foreach (var line in targetLines)
            {
                EnsureHuAssignmentAllowed(store, doc.Type, line.Id);

                if (!line.ToLocationId.HasValue)
                {
                    throw new InvalidOperationException("Для выпуска продукции требуется локация приёмки.");
                }

                if (!itemsById.TryGetValue(line.ItemId, out var item))
                {
                    throw new InvalidOperationException($"Товар ID {line.ItemId} не найден.");
                }

                if (!item.MaxQtyPerHu.HasValue || item.MaxQtyPerHu.Value <= 0)
                {
                    throw new InvalidOperationException($"Для товара \"{item.Name}\" не задан лимит шт. на HU.");
                }

                requiredHuCount += (int)Math.Ceiling(line.Qty / item.MaxQtyPerHu.Value);
            }

            var reservedHuCodes = allLines
                .Where(line => !selectedIds.Contains(line.Id))
                .Select(line => NormalizeHuValue(line.ToHu))
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var allocatedHuCodes = AllocateProductionHuCodes(store, requiredHuCount, reservedHuCodes);
            usedHuCount = allocatedHuCodes.Count;

            var huQueue = new Queue<string>(allocatedHuCodes);
            var replacementLines = new List<DocLine>();
            foreach (var line in targetLines)
            {
                var item = itemsById[line.ItemId];
                var maxQtyPerHu = item.MaxQtyPerHu!.Value;
                var ratio = line.QtyInput.HasValue && line.Qty > 0
                    ? line.QtyInput.Value / line.Qty
                    : (double?)null;
                var remainingQty = line.Qty;
                var remainingInput = line.QtyInput;

                while (remainingQty > 0.000001)
                {
                    var chunkQty = Math.Min(maxQtyPerHu, remainingQty);
                    var chunkInput = (double?)null;
                    if (ratio.HasValue)
                    {
                        chunkInput = remainingQty - chunkQty <= 0.000001
                            ? remainingInput
                            : ratio.Value * chunkQty;
                        if (remainingInput.HasValue)
                        {
                            remainingInput -= chunkInput;
                        }
                    }

                    replacementLines.Add(new DocLine
                    {
                        DocId = docId,
                        OrderLineId = line.OrderLineId,
                        ItemId = line.ItemId,
                        Qty = chunkQty,
                        QtyInput = chunkInput,
                        UomCode = line.UomCode,
                        FromLocationId = line.FromLocationId,
                        ToLocationId = line.ToLocationId,
                        FromHu = NormalizeHuValue(line.FromHu),
                        ToHu = huQueue.Dequeue()
                    });

                    remainingQty -= chunkQty;
                }
            }

            foreach (var line in targetLines)
            {
                store.DeleteDocLine(line.Id);
            }

            foreach (var line in replacementLines)
            {
                store.AddDocLine(line);
            }
        });

        return usedHuCount;
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

    public void DeleteDocLines(long docId, IReadOnlyCollection<long> docLineIds)
    {
        if (docLineIds == null || docLineIds.Count == 0)
        {
            throw new ArgumentException("Не выбраны строки для удаления.", nameof(docLineIds));
        }

        var doc = _data.GetDoc(docId) ?? throw new InvalidOperationException("Документ не найден.");
        if (doc.Status != DocStatus.Draft)
        {
            throw new InvalidOperationException("Документ уже закрыт.");
        }

        var selectedIds = docLineIds
            .Distinct()
            .ToList();
        var existingIds = _data.GetDocLines(docId)
            .Select(line => line.Id)
            .ToHashSet();
        var missingId = selectedIds.FirstOrDefault(id => !existingIds.Contains(id));
        if (missingId != 0)
        {
            throw new InvalidOperationException("Одна из выбранных строк не найдена.");
        }

        _data.ExecuteInTransaction(store =>
        {
            foreach (var id in selectedIds)
            {
                store.DeleteDocLine(id);
            }
        });
    }

    private static IReadOnlyList<DocLine> EnsureOrderLineLinks(IDataStore store, Doc doc, IReadOnlyList<DocLine> lines)
    {
        if (!doc.OrderId.HasValue || lines.Count == 0)
        {
            return lines;
        }

        if (doc.Type != DocType.ProductionReceipt && doc.Type != DocType.Outbound)
        {
            return lines;
        }

        var orderLines = store.GetOrderLines(doc.OrderId.Value);
        if (orderLines.Count == 0)
        {
            return lines;
        }

        var orderLineIds = orderLines.Select(line => line.Id).ToHashSet();
        var orderLinesByItem = orderLines
            .GroupBy(line => line.ItemId)
            .ToDictionary(group => group.Key, group => group.OrderBy(line => line.Id).ToList());

        List<DocLine>? remappedLines = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!line.OrderLineId.HasValue || orderLineIds.Contains(line.OrderLineId.Value))
            {
                remappedLines?.Add(line);
                continue;
            }

            if (!orderLinesByItem.TryGetValue(line.ItemId, out var candidates) || candidates.Count != 1)
            {
                remappedLines?.Add(line);
                continue;
            }

            var targetOrderLineId = candidates[0].Id;
            store.UpdateDocLineOrderLineId(line.Id, targetOrderLineId);

            remappedLines ??= new List<DocLine>(lines.Count);
            if (remappedLines.Count == 0)
            {
                for (var copied = 0; copied < i; copied++)
                {
                    remappedLines.Add(lines[copied]);
                }
            }

            remappedLines.Add(CloneDocLineWithOrderLineId(line, targetOrderLineId));
        }

        return remappedLines ?? lines;
    }

    private static DocLine CloneDocLineWithOrderLineId(DocLine line, long? orderLineId)
    {
        return new DocLine
        {
            Id = line.Id,
            DocId = line.DocId,
            OrderLineId = orderLineId,
            ItemId = line.ItemId,
            Qty = line.Qty,
            QtyInput = line.QtyInput,
            UomCode = line.UomCode,
            FromLocationId = line.FromLocationId,
            ToLocationId = line.ToLocationId,
            FromHu = line.FromHu,
            ToHu = line.ToHu
        };
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

        if (doc.Type == DocType.WriteOff && string.IsNullOrWhiteSpace(doc.ReasonCode))
        {
            check.Errors.Add("Для списания требуется причина.");
        }

        var docHu = NormalizeHuValue(doc.ShippingRef);
        var lines = EnsureOrderLineLinks(_data, doc, _data.GetDocLines(docId));
        if (lines.Count == 0)
        {
            check.Errors.Add("Добавьте хотя бы один товар в документ перед проведением.");
            return check;
        }
        var receiptRemaining = doc.Type == DocType.ProductionReceipt && doc.OrderId.HasValue
            ? _data.GetOrderReceiptRemaining(doc.OrderId.Value)
                .ToDictionary(entry => entry.OrderLineId, entry => entry.QtyRemaining)
            : new Dictionary<long, double>();
        var receiptRequested = new Dictionary<long, double>();
        var shipmentRemaining = doc.Type == DocType.Outbound && doc.OrderId.HasValue
            ? _data.GetOrderShipmentRemaining(doc.OrderId.Value)
                .ToDictionary(entry => entry.OrderLineId, entry => entry.QtyRemaining)
            : new Dictionary<long, double>();
        var shipmentRequested = new Dictionary<long, double>();
        var itemsById = _data.GetItems(null).ToDictionary(item => item.Id, item => item);
        var locations = _data.GetLocations();
        var locationsById = locations.ToDictionary(location => location.Id, location => location.Code);

        var outgoingBySource = new Dictionary<StockKey, double>();
        var outboundByItem = new Dictionary<long, double>();

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var item = itemsById.TryGetValue(line.ItemId, out var found) ? found : null;
            var itemLabel = item?.Name ?? $"ID {line.ItemId}";
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
                case DocType.ProductionReceipt:
                    if (!line.ToLocationId.HasValue)
                    {
                        check.Errors.Add($"{rowLabel}: требуется место хранения получателя.");
                    }
                    if (string.IsNullOrWhiteSpace(NormalizeHuValue(toHu)))
                    {
                        check.Errors.Add($"{rowLabel}: требуется HU.");
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

            if (doc.Type == DocType.ProductionReceipt
                && item?.MaxQtyPerHu is double maxQtyPerHu
                && maxQtyPerHu > 0
                && line.Qty > maxQtyPerHu + 0.000001)
            {
                check.Errors.Add(
                    $"{rowLabel}: количество {FormatQty(line.Qty)} превышает лимит {FormatQty(maxQtyPerHu)} на один HU. Разбейте строку на несколько HU.");
            }

            if (KmWorkflowEnabled
                && item?.IsMarked == true
                && (doc.Type == DocType.ProductionReceipt || doc.Type == DocType.Outbound))
            {
                var rounded = Math.Round(line.Qty);
                if (Math.Abs(line.Qty - rounded) > 0.0001)
                {
                    check.Errors.Add($"{rowLabel}: количество для маркируемого товара должно быть целым.");
                }
                else
                {
                    var required = (int)rounded;
                    if (doc.Type == DocType.ProductionReceipt)
                    {
                        var assigned = _data.CountKmCodesByReceiptLine(line.Id);
                        if (assigned != required)
                        {
                            check.Errors.Add($"{rowLabel}: требуется привязать {required} код(ов) КМ, сейчас {assigned}.");
                        }
                    }
                    else
                    {
                        var assigned = _data.CountKmCodesByShipmentLine(line.Id);
                        if (assigned > required)
                        {
                            check.Errors.Add($"{rowLabel}: привязано больше кодов КМ ({assigned}), чем количество в строке ({required}).");
                            continue;
                        }

                        var missing = required - assigned;
                        if (missing > 0)
                        {
                            var gtin14 = NormalizeGtinForKm(item.Gtin);
                            var huId = ResolveHuId(_data, fromHu);
                            var availableForAuto = GetAvailableKmForOutbound(_data, doc.OrderId, line.ItemId, gtin14, line.FromLocationId, huId, missing).Count;
                            if (availableForAuto < missing)
                            {
                                check.Errors.Add(
                                    $"{rowLabel}: недостаточно КМ для авто-отгрузки. " +
                                    $"Нужно {required}, уже привязано {assigned}, доступно {assigned + availableForAuto}.");
                            }
                        }
                    }
                }
            }

            if (doc.Type == DocType.ProductionReceipt && line.OrderLineId.HasValue)
            {
                if (!doc.OrderId.HasValue)
                {
                    check.Errors.Add($"{rowLabel}: не указан заказ документа.");
                }
                else
                {
                    var orderLineId = line.OrderLineId.Value;
                    receiptRequested[orderLineId] = receiptRequested.TryGetValue(orderLineId, out var current)
                        ? current + line.Qty
                        : line.Qty;
                }
            }
            else if (doc.Type == DocType.Outbound && line.OrderLineId.HasValue)
            {
                if (!doc.OrderId.HasValue)
                {
                    check.Errors.Add($"{rowLabel}: не указан заказ документа.");
                }
                else
                {
                    var orderLineId = line.OrderLineId.Value;
                    shipmentRequested[orderLineId] = shipmentRequested.TryGetValue(orderLineId, out var current)
                        ? current + line.Qty
                        : line.Qty;
                }
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
                    var itemLabel = itemsById.TryGetValue(entry.Key.ItemId, out var name) ? name.Name : $"ID {entry.Key.ItemId}";
                    var locationLabel = locationsById.TryGetValue(entry.Key.LocationId, out var code) ? code : $"ID {entry.Key.LocationId}";
                    var huLabel = string.IsNullOrWhiteSpace(entry.Key.Hu) ? string.Empty : $" (HU {entry.Key.Hu})";
                    check.Errors.Add($"{itemLabel} @ {locationLabel}{huLabel}: на складе {FormatQty(current)}, требуется {FormatQty(entry.Value)}.");
                }
            }
        }

        if (doc.Type == DocType.Outbound)
        {
            var autoAllocation = lines.Any(line => !line.FromLocationId.HasValue);
            IReadOnlyDictionary<long, double>? totalsByItem = autoAllocation
                ? _data.GetLedgerTotalsByItem()
                : null;
            foreach (var entry in outboundByItem)
            {
                var current = autoAllocation
                    ? (totalsByItem != null && totalsByItem.TryGetValue(entry.Key, out var total) ? total : 0)
                    : GetTotalAvailableQty(entry.Key, docHu, locations);
                var future = current - entry.Value;
                if (future < 0)
                {
                    var itemLabel = itemsById.TryGetValue(entry.Key, out var name) ? name.Name : $"ID {entry.Key}";
                    check.Errors.Add($"{itemLabel}: на складе {FormatQty(current)}, требуется {FormatQty(entry.Value)}.");
                }
            }
        }

        if (doc.Type == DocType.ProductionReceipt && receiptRequested.Count > 0)
        {
            foreach (var entry in receiptRequested)
            {
                if (!receiptRemaining.TryGetValue(entry.Key, out var remaining))
                {
                    check.Errors.Add($"Строка заказа {entry.Key}: не найдена.");
                    continue;
                }

                if (entry.Value > remaining + 0.000001)
                {
                    check.Errors.Add($"Строка заказа {entry.Key}: превышен остаток {FormatQty(remaining)}.");
                }
            }
        }
        if (doc.Type == DocType.Outbound && shipmentRequested.Count > 0)
        {
            foreach (var entry in shipmentRequested)
            {
                if (!shipmentRemaining.TryGetValue(entry.Key, out var remaining))
                {
                    check.Errors.Add($"Строка заказа {entry.Key}: не найдена.");
                    continue;
                }

                if (entry.Value > remaining + 0.000001)
                {
                    check.Errors.Add($"Строка заказа {entry.Key}: превышен остаток {FormatQty(remaining)}.");
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
            case DocType.ProductionReceipt:
                if (!toLocationId.HasValue)
                {
                    throw new ArgumentException("Для выпуска продукции требуется место хранения получателя.");
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

    private static List<string> AllocateProductionHuCodes(
        IDataStore store,
        int requiredCount,
        IReadOnlyCollection<string> reservedHuCodes)
    {
        if (requiredCount <= 0)
        {
            return new List<string>();
        }

        var reserved = reservedHuCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var totalsByHu = store.GetLedgerTotalsByHu();
        var reusable = store.GetHus(null, 10000)
            .Where(record => !string.IsNullOrWhiteSpace(record.Code))
            .Where(record => !reserved.Contains(record.Code))
            .Where(record => !string.Equals(record.Status, "VOID", StringComparison.OrdinalIgnoreCase))
            .Where(record => !totalsByHu.TryGetValue(record.Code, out var qty) || Math.Abs(qty) <= 0.000001)
            .OrderBy(record => record.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var codes = new List<string>(requiredCount);
        foreach (var record in reusable)
        {
            if (codes.Count >= requiredCount)
            {
                break;
            }

            if (string.Equals(record.Status, "CLOSED", StringComparison.OrdinalIgnoreCase))
            {
                store.ReopenHu(record.Code, AutoHuCreatedBy, "Автовозврат в оборот для выпуска продукции");
            }

            codes.Add(record.Code);
        }

        while (codes.Count < requiredCount)
        {
            codes.Add(store.CreateHuRecord(AutoHuCreatedBy).Code);
        }

        return codes;
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

        if (doc.Type == DocType.Inbound || doc.Type == DocType.Inventory || doc.Type == DocType.ProductionReceipt)
        {
            return (null, lineTo ?? docHu);
        }

        if (doc.Type == DocType.Outbound || doc.Type == DocType.WriteOff)
        {
            return (lineFrom ?? docHu, null);
        }

        return (lineFrom, lineTo);
    }

    private static string? ResolveShippingRefFromLines(DocType type, IReadOnlyList<DocLine> lines)
    {
        IEnumerable<string?> values = type switch
        {
            DocType.Inbound or DocType.Inventory => lines.Select(line => line.ToHu),
            DocType.ProductionReceipt => lines.Select(line => line.ToHu),
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
            DocType.ProductionReceipt => (null, normalized),
            DocType.Outbound => (normalized, null),
            DocType.WriteOff => (normalized, null),
            DocType.Move => (null, normalized),
            _ => (null, null)
        };
    }

    private static void AutoAssignOutboundKmCodes(
        IDataStore store,
        Doc doc,
        IReadOnlyList<DocLine> lines,
        string? docHu,
        long docId)
    {
        var itemsById = store.GetItems(null).ToDictionary(item => item.Id, item => item);
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (!itemsById.TryGetValue(line.ItemId, out var item) || !item.IsMarked)
            {
                continue;
            }

            var rounded = Math.Round(line.Qty);
            if (Math.Abs(line.Qty - rounded) > 0.0001)
            {
                throw new InvalidOperationException($"Строка {index + 1} ({item.Name}): количество для маркируемого товара должно быть целым.");
            }

            var required = (int)rounded;
            var assigned = store.CountKmCodesByShipmentLine(line.Id);
            var missing = required - assigned;
            if (missing <= 0)
            {
                continue;
            }

            var (fromHu, _) = ResolveLedgerHu(doc, line, docHu);
            var huId = ResolveHuId(store, fromHu);
            var gtin14 = NormalizeGtinForKm(item.Gtin);
            var ids = GetAvailableKmForOutbound(store, doc.OrderId, line.ItemId, gtin14, line.FromLocationId, huId, missing);
            if (ids.Count < missing)
            {
                throw new InvalidOperationException(
                    $"Строка {index + 1} ({item.Name}): недостаточно КМ для авто-отгрузки. " +
                    $"Нужно {required}, уже привязано {assigned}, доступно {assigned + ids.Count}.");
            }

            foreach (var codeId in ids)
            {
                store.MarkKmCodeShipped(codeId, docId, line.Id, doc.OrderId);
            }
        }
    }

    private static void EnsureHuAssignmentAllowed(IDataStore store, DocType type, long docLineId)
    {
        if (!KmWorkflowEnabled)
        {
            return;
        }

        if (type == DocType.ProductionReceipt && store.CountKmCodesByReceiptLine(docLineId) > 0)
        {
            throw new InvalidOperationException("Нельзя менять HU строки после привязки КМ.");
        }

        if (type == DocType.Outbound && store.CountKmCodesByShipmentLine(docLineId) > 0)
        {
            throw new InvalidOperationException("Нельзя менять HU строки после привязки КМ.");
        }
    }

    private static long? ResolveHuId(IDataStore store, string? huCode)
    {
        if (string.IsNullOrWhiteSpace(huCode))
        {
            return null;
        }

        var record = store.GetHuByCode(huCode.Trim());
        return record?.Id;
    }

    private static IReadOnlyList<long> GetAvailableKmForOutbound(
        IDataStore store,
        long? orderId,
        long itemId,
        string? gtin14,
        long? locationId,
        long? huId,
        int take)
    {
        var onHand = store.GetAvailableKmOnHandCodeIds(orderId, itemId, gtin14, locationId, huId, take);
        if (onHand.Count >= take)
        {
            return onHand;
        }

        var missing = take - onHand.Count;
        var inPool = store.GetAvailableKmCodeIds(null, orderId, itemId, gtin14, missing);
        if (onHand.Count == 0)
        {
            return inPool;
        }

        return onHand.Concat(inPool).ToArray();
    }

    private static string? NormalizeGtinForKm(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!trimmed.All(char.IsDigit))
        {
            return null;
        }

        if (trimmed.Length == 14)
        {
            return trimmed;
        }

        return trimmed.Length == 13 ? "0" + trimmed : null;
    }

    private readonly record struct StockKey(long ItemId, long LocationId, string? Hu);

    private sealed class CloseDocCheck
    {
        public Doc? Doc { get; set; }
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
    }
}

