using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class ProductionPalletService
{
    private const double QtyTolerance = 0.000001d;
    private const string PlanHuCreatedBy = "PRODUCTION-PALLET-PLAN";
    private readonly IDataStore _data;

    public ProductionPalletService(IDataStore data)
    {
        _data = data;
    }

    public ProductionPalletDocument Plan(long docId)
    {
        var doc = RequireProductionReceipt(docId);
        if (doc.Status == DocStatus.Closed)
        {
            throw new InvalidOperationException("Документ уже закрыт.");
        }

        _data.PlanProductionPallets(docId, DateTime.Now);
        return Get(docId);
    }

    public ProductionPalletOrderPlanResult PlanOrder(long orderId)
    {
        var prdDocId = 0L;
        var wasExisting = false;
        _data.ExecuteInTransaction(store =>
        {
            var order = store.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
            if (order.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
            {
                throw new InvalidOperationException("Заказ недоступен для планирования паллет.");
            }

            var preparedDoc = FindPreparedOpenProductionReceipt(store, orderId, requireRemaining: false);
            if (preparedDoc != null)
            {
                prdDocId = preparedDoc.Id;
                wasExisting = true;
                store.ClearPlannedProductionPalletPlan(preparedDoc.Id);
            }

            var remainingLines = OrderReceiptRemainingCalculator.GetRemaining(store, order)
                .Where(line => line.QtyRemaining > QtyTolerance)
                .OrderBy(line => line.OrderLineId)
                .ToList();
            if (remainingLines.Count == 0)
            {
                throw new InvalidOperationException("Нет остатка к наполнению по заказу.");
            }

            var itemsById = store.GetItems(null).ToDictionary(item => item.Id, item => item);
            var orderLinesById = store.GetOrderLines(orderId).ToDictionary(line => line.Id, line => line);
            foreach (var line in remainingLines)
            {
                if (!itemsById.TryGetValue(line.ItemId, out var item)
                    || !item.MaxQtyPerHu.HasValue
                    || item.MaxQtyPerHu.Value <= QtyTolerance)
                {
                    throw new InvalidOperationException("Не задано количество на паллете для номенклатуры");
                }
            }

            var targetLocation = ResolveProductionPalletPlanLocation(store);
            if (prdDocId == 0)
            {
                prdDocId = FindReusableEmptyProductionReceipt(store, orderId)?.Id ?? CreateProductionReceipt(store, order).Id;
            }

            var mixedLineIds = new HashSet<long>();
            foreach (var group in remainingLines
                         .Where(line => orderLinesById.TryGetValue(line.OrderLineId, out var orderLine)
                                        && !string.IsNullOrWhiteSpace(orderLine.ProductionPalletGroup))
                         .GroupBy(line => orderLinesById[line.OrderLineId].ProductionPalletGroup!.Trim().ToUpperInvariant())
                         .Where(group => group.Count() > 1))
            {
                var groupLines = group.OrderBy(line => line.OrderLineId).ToList();
                var load = 0d;
                foreach (var line in groupLines)
                {
                    if (!itemsById.TryGetValue(line.ItemId, out var item)
                        || !item.MaxQtyPerHu.HasValue
                        || item.MaxQtyPerHu.Value <= QtyTolerance)
                    {
                        throw new InvalidOperationException("Не задано правило вместимости для микс-паллеты");
                    }

                    load += line.QtyRemaining / item.MaxQtyPerHu.Value;
                }

                if (load > 1d + QtyTolerance)
                {
                    throw new InvalidOperationException("Микс-паллета превышает вместимость. Разделите группу паллеты.");
                }

                AddMixedPlannedPalletLines(store, prdDocId, groupLines, targetLocation.Id);
                foreach (var line in groupLines)
                {
                    mixedLineIds.Add(line.OrderLineId);
                }
            }

            foreach (var line in remainingLines.Where(line => !mixedLineIds.Contains(line.OrderLineId)))
            {
                var item = itemsById[line.ItemId];
                AddPlannedPalletLines(store, prdDocId, line, item.MaxQtyPerHu!.Value, targetLocation.Id);
            }

            store.PlanProductionPallets(prdDocId, DateTime.Now);
        });

        return BuildOrderPlanResult(orderId, prdDocId, wasExisting);
    }

    public ProductionPalletCancelPlanResult CancelOrderPlan(long orderId)
    {
        var order = _data.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        if (order.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
        {
            throw new InvalidOperationException("Заказ недоступен для удаления плана паллет.");
        }

        var docWithPlan = FindProductionReceiptWithPalletPlan(_data, orderId)
                          ?? throw new InvalidOperationException("План паллет не найден.");
        if (docWithPlan.Status == DocStatus.Closed)
        {
            throw new InvalidOperationException("Нельзя удалить план паллет: выпуск уже закрыт.");
        }

        ProductionPalletPlanCleanupCounts cleanup = null!;
        _data.ExecuteInTransaction(store =>
        {
            var doc = store.GetDoc(docWithPlan.Id) ?? throw new InvalidOperationException("Документ выпуска не найден.");
            if (doc.Status == DocStatus.Closed)
            {
                throw new InvalidOperationException("Нельзя удалить план паллет: выпуск уже закрыт.");
            }

            if (!store.HasProductionPallets(doc.Id))
            {
                throw new InvalidOperationException("План паллет не найден.");
            }

            cleanup = store.CancelProductionPalletPlan(doc.Id);
        });

        return new ProductionPalletCancelPlanResult
        {
            OrderId = order.Id,
            PrdDocId = docWithPlan.Id,
            Message = "План паллет удалён.",
            RemovedPalletCount = cleanup.RemovedPalletCount,
            RemovedLineCount = cleanup.RemovedLineCount
        };
    }

    public ProductionPalletDocument Get(long docId)
    {
        var doc = RequireProductionReceipt(docId);
        var pallets = _data.GetProductionPalletsByDoc(doc.Id);
        return BuildDocument(doc.Id, pallets);
    }

    public IReadOnlyList<ProductionPalletWorkItem> GetActiveWorkItems()
    {
        return _data.GetActiveProductionPalletWorkItems();
    }

    public IReadOnlyList<ProductionFillingOrder> GetFillingOrders()
    {
        return _data.GetActiveProductionPalletWorkItems()
            .Where(item => item.OrderId.HasValue)
            .GroupBy(item => item.OrderId!.Value)
            .Select(group => BuildFillingOrder(group.Key, group.ToList()))
            .Where(row => row != null)
            .Cast<ProductionFillingOrder>()
            .OrderBy(row => row.OrderType == OrderStatusMapper.TypeToString(OrderType.Internal) ? 0 : 1)
            .ThenByDescending(row => TryParseLong(row.OrderRef, out var number) ? number : long.MinValue)
            .ThenByDescending(row => row.OrderId)
            .ToList();
    }

    public ProductionFillingContext StartFilling(long orderId)
    {
        return GetFillingContext(orderId);
    }

    public ProductionFillingContext GetFillingContext(long orderId)
    {
        var order = _data.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        if (order.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
        {
            throw new InvalidOperationException("Заказ недоступен для наполнения.");
        }

        var openDoc = FindPreparedOpenProductionReceipt(_data, orderId, requireRemaining: true);
        if (openDoc == null)
        {
            throw new InvalidOperationException("Для заказа не сформирован план паллет. Сформируйте и напечатайте паллетные этикетки перед наполнением.");
        }

        return BuildFillingContext(orderId, openDoc.Id);
    }

    public IReadOnlyList<ProductionPalletPrintRow> GetPrintRows(long orderId)
    {
        var order = _data.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        var doc = FindPrintableProductionReceipt(_data, order);
        if (doc == null)
        {
            throw new InvalidOperationException("Сначала сформируйте план паллет");
        }

        var pallets = _data.GetProductionPalletsByDoc(doc.Id)
            .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pallet => pallet.Id)
            .ToList();
        if (pallets.Count == 0)
        {
            throw new InvalidOperationException("Сначала сформируйте план паллет");
        }

        var itemsById = pallets
            .SelectMany(pallet => GetPalletLines(pallet).Select(line => line.ItemId))
            .Distinct()
            .Select(id => _data.FindItemById(id))
            .Where(item => item != null)
            .ToDictionary(item => item!.Id, item => item!);
        var locationsById = _data.GetLocations().ToDictionary(location => location.Id, location => location.Code);
        var rows = new List<ProductionPalletPrintRow>(pallets.Count);
        for (var index = 0; index < pallets.Count; index++)
        {
            var pallet = pallets[index];
            if (string.IsNullOrWhiteSpace(pallet.HuCode))
            {
                throw new InvalidOperationException("Для паллеты не задан HU.");
            }

            var componentLines = GetPalletLines(pallet)
                .OrderBy(line => line.Id)
                .ToList();
            var isMixed = componentLines.Count > 1;
            var firstLine = componentLines.FirstOrDefault();
            if (firstLine == null || !itemsById.TryGetValue(firstLine.ItemId, out var item))
            {
                throw new InvalidOperationException("Номенклатура паллеты не найдена.");
            }

            if (string.IsNullOrWhiteSpace(isMixed ? "Микс-паллета" : item.Name))
            {
                throw new InvalidOperationException("Для паллеты не задана номенклатура.");
            }

            var plannedQty = componentLines.Sum(line => line.PlannedQty);
            if (plannedQty <= QtyTolerance)
            {
                throw new InvalidOperationException("Для паллеты не задано количество.");
            }

            var printLines = componentLines.Select(line =>
            {
                var lineItem = itemsById.TryGetValue(line.ItemId, out var found) ? found : null;
                return new ProductionPalletPrintLine
                {
                    ItemName = lineItem?.Name ?? line.ItemName,
                    Qty = line.PlannedQty,
                    Uom = string.IsNullOrWhiteSpace(lineItem?.BaseUom) ? line.Uom : lineItem!.BaseUom!
                };
            }).ToList();
            var composition = string.Join("; ", printLines.Select(line => $"{line.ItemName} - {FormatQty(line.Qty)} {line.Uom}"));

            rows.Add(new ProductionPalletPrintRow
            {
                PalletId = pallet.Id,
                OrderId = order.Id,
                OrderRef = order.OrderRef,
                PrdDocId = doc.Id,
                PrdRef = doc.DocRef,
                HuCode = pallet.HuCode,
                ItemId = firstLine.ItemId,
                ItemName = isMixed ? "Микс-паллета" : item.Name,
                Brand = isMixed ? string.Empty : item.Brand ?? string.Empty,
                Qty = plannedQty,
                Uom = isMixed ? string.Empty : string.IsNullOrWhiteSpace(item.BaseUom) ? "шт" : item.BaseUom!,
                PalletNo = index + 1,
                PalletCount = pallets.Count,
                StoragePlace = pallet.ToLocationId.HasValue && locationsById.TryGetValue(pallet.ToLocationId.Value, out var locationCode)
                    ? locationCode
                    : pallet.ToLocationCode ?? string.Empty,
                ProductionDate = doc.CreatedAt.Date,
                Comment = isMixed ? composition : doc.Comment ?? string.Empty,
                IsMixedPallet = isMixed,
                Composition = composition,
                Lines = printLines,
                Status = pallet.Status
            });
        }

        return rows;
    }

    public int MarkPrinted(long orderId, DateTime printedAt)
    {
        var rows = GetPrintRows(orderId);
        if (rows.Count == 0)
        {
            throw new InvalidOperationException("Сначала сформируйте план паллет");
        }

        return _data.MarkProductionPalletsPrintedByOrder(orderId, printedAt);
    }

    public ProductionPalletScanResult Scan(long? orderId, long? prdDocId, string? huCode)
    {
        var normalizedHu = NormalizeHu(huCode);
        if (string.IsNullOrWhiteSpace(normalizedHu))
        {
            return ProductionPalletScanResult.Failure("Укажите код паллеты.");
        }

        var pallet = _data.GetProductionPalletByHu(normalizedHu);
        if (pallet == null)
        {
            return ProductionPalletScanResult.Failure("Паллета не найдена в плане выпуска");
        }

        if ((prdDocId.HasValue && pallet.PrdDocId != prdDocId.Value)
            || (orderId.HasValue && pallet.OrderId != orderId.Value))
        {
            return ProductionPalletScanResult.Failure("Эта паллета относится к другому заказу");
        }

        var doc = _data.GetDoc(pallet.PrdDocId);
        if (doc == null || doc.Type != DocType.ProductionReceipt)
        {
            return ProductionPalletScanResult.Failure("Документ выпуска не найден.");
        }

        if (doc.Status == DocStatus.Closed)
        {
            return ProductionPalletScanResult.Failure("Документ выпуска уже закрыт.");
        }

        if (string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
        {
            return ProductionPalletScanResult.Failure("Паллета отменена");
        }

        var palletLines = GetPalletLines(pallet);
        var docLinesById = _data.GetDocLines(doc.Id).ToDictionary(line => line.Id, line => line);
        foreach (var palletLine in palletLines)
        {
            if (!docLinesById.TryGetValue(palletLine.DocLineId, out var docLine)
                || docLine.ItemId != palletLine.ItemId
                || docLine.OrderLineId != palletLine.OrderLineId)
            {
                return ProductionPalletScanResult.Failure("План паллеты не совпадает со строкой выпуска.");
            }

            if (pallet.OrderId.HasValue && palletLine.OrderLineId.HasValue)
            {
                var orderLine = _data.GetOrderLines(pallet.OrderId.Value)
                    .FirstOrDefault(line => line.Id == palletLine.OrderLineId.Value);
                if (orderLine == null || orderLine.ItemId != palletLine.ItemId)
                {
                    return ProductionPalletScanResult.Failure("Строка заказа для паллеты не найдена.");
                }

                if (!string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
                {
                    var alreadyFilled = _data.GetFilledProductionPalletQtyByOrderLine(orderLine.Id, pallet.Id);
                    if (alreadyFilled + palletLine.PlannedQty > orderLine.QtyOrdered + QtyTolerance)
                    {
                        return ProductionPalletScanResult.Failure("Выпуск превышает остаток по строке заказа");
                    }
                }
            }
        }

        var pallets = _data.GetProductionPalletsByDoc(doc.Id);
        var activePallets = pallets
            .Where(row => !string.Equals(row.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.Id)
            .ToList();
        var index = activePallets.FindIndex(row => row.Id == pallet.Id);
        var firstLine = palletLines.First();
        var item = _data.FindItemById(firstLine.ItemId);
        var order = pallet.OrderId.HasValue ? _data.GetOrder(pallet.OrderId.Value) : null;
        var scanLines = palletLines.Select(line =>
        {
            var lineItem = _data.FindItemById(line.ItemId);
            return new ProductionPalletScanLine
            {
                ItemId = line.ItemId,
                ItemName = lineItem?.Name ?? line.ItemName,
                Brand = lineItem?.Brand ?? line.Brand,
                Qty = line.PlannedQty,
                Uom = string.IsNullOrWhiteSpace(lineItem?.BaseUom) ? line.Uom : lineItem!.BaseUom!
            };
        }).ToList();

        return new ProductionPalletScanResult
        {
            Success = true,
            AlreadyFilled = string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase),
            OrderId = pallet.OrderId,
            OrderRef = order?.OrderRef ?? doc.OrderRef,
            PrdDocId = doc.Id,
            PrdDocRef = doc.DocRef,
            PalletId = pallet.Id,
            HuCode = pallet.HuCode,
            ItemId = firstLine.ItemId,
            ItemName = palletLines.Count > 1 ? "Микс-паллета" : item?.Name ?? pallet.ItemName,
            ItemBrand = palletLines.Count > 1 ? null : item?.Brand,
            BaseUom = string.IsNullOrWhiteSpace(item?.BaseUom) ? "шт" : item!.BaseUom,
            PlannedQty = palletLines.Sum(line => line.PlannedQty),
            IsMixedPallet = palletLines.Count > 1,
            Lines = scanLines,
            PalletIndex = index >= 0 ? index + 1 : 0,
            PalletCount = activePallets.Count,
            PalletStatus = pallet.Status,
            Document = BuildDocument(doc.Id, pallets)
        };
    }

    public ProductionPalletFillResult Fill(string? huCode, string? deviceId, long? orderId = null, long? prdDocId = null)
    {
        var normalizedHu = NormalizeHu(huCode);
        if (string.IsNullOrWhiteSpace(normalizedHu))
        {
            return ProductionPalletFillResult.Failure("Укажите код паллеты.");
        }

        ProductionPalletFillResult? result = null;
        _data.ExecuteInTransaction(store =>
        {
            var pallet = store.GetProductionPalletByHu(normalizedHu);
            if (pallet == null)
            {
                result = ProductionPalletFillResult.Failure("Паллета не найдена в плане выпуска.");
                return;
            }

            if ((prdDocId.HasValue && pallet.PrdDocId != prdDocId.Value)
                || (orderId.HasValue && pallet.OrderId != orderId.Value))
            {
                result = ProductionPalletFillResult.Failure("Эта паллета относится к другому заказу");
                return;
            }

            var doc = store.GetDoc(pallet.PrdDocId);
            if (doc == null || doc.Type != DocType.ProductionReceipt)
            {
                result = ProductionPalletFillResult.Failure("Документ выпуска не найден.");
                return;
            }

            if (doc.Status == DocStatus.Closed)
            {
                result = ProductionPalletFillResult.Failure("Документ выпуска уже закрыт.");
                return;
            }

            if (string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            {
                result = ProductionPalletFillResult.Failure("Паллета отменена.");
                return;
            }

            if (string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
            {
                result = new ProductionPalletFillResult
                {
                    Success = true,
                    AlreadyFilled = true,
                    Pallet = pallet,
                    Document = BuildDocument(doc.Id, store.GetProductionPalletsByDoc(doc.Id))
                };
                return;
            }

            var palletLines = GetPalletLines(pallet);
            var docLinesById = store.GetDocLines(doc.Id).ToDictionary(line => line.Id, line => line);
            foreach (var palletLine in palletLines)
            {
                if (!docLinesById.TryGetValue(palletLine.DocLineId, out var docLine))
                {
                    result = ProductionPalletFillResult.Failure("Строка паллеты не найдена в документе выпуска.");
                    return;
                }

                if (docLine.ItemId != palletLine.ItemId || docLine.OrderLineId != palletLine.OrderLineId)
                {
                    result = ProductionPalletFillResult.Failure("План паллеты не совпадает со строкой выпуска.");
                    return;
                }

                if (!docLine.ToLocationId.HasValue)
                {
                    result = ProductionPalletFillResult.Failure("Для паллеты не указано место хранения.");
                    return;
                }

                if (pallet.OrderId.HasValue && palletLine.OrderLineId.HasValue)
                {
                    var orderLine = store.GetOrderLines(pallet.OrderId.Value)
                        .FirstOrDefault(line => line.Id == palletLine.OrderLineId.Value);
                    if (orderLine == null || orderLine.ItemId != palletLine.ItemId)
                    {
                        result = ProductionPalletFillResult.Failure("Строка заказа для паллеты не найдена.");
                        return;
                    }

                    var alreadyFilled = store.GetFilledProductionPalletQtyByOrderLine(orderLine.Id, pallet.Id);
                    if (alreadyFilled + palletLine.PlannedQty > orderLine.QtyOrdered + QtyTolerance)
                    {
                        result = ProductionPalletFillResult.Failure("Выпуск превышает остаток по строке заказа");
                        return;
                    }
                }
            }

            var filledAt = DateTime.Now;
            foreach (var palletLine in palletLines)
            {
                var docLine = docLinesById[palletLine.DocLineId];
                store.AddLedgerEntry(new LedgerEntry
                {
                    Timestamp = filledAt,
                    DocId = doc.Id,
                    ItemId = palletLine.ItemId,
                    LocationId = docLine.ToLocationId!.Value,
                    QtyDelta = palletLine.PlannedQty,
                    HuCode = pallet.HuCode
                });
            }
            store.MarkProductionPalletFilled(pallet.Id, filledAt, NormalizeDeviceId(deviceId));

            var filledPallet = store.GetProductionPalletByHu(normalizedHu) ?? pallet;
            result = new ProductionPalletFillResult
            {
                Success = true,
                AlreadyFilled = false,
                Pallet = filledPallet,
                Document = BuildDocument(doc.Id, store.GetProductionPalletsByDoc(doc.Id))
            };
        });

        return result ?? ProductionPalletFillResult.Failure("Не удалось наполнить паллету.");
    }

    private Doc RequireProductionReceipt(long docId)
    {
        var doc = _data.GetDoc(docId) ?? throw new InvalidOperationException("Документ не найден.");
        if (doc.Type != DocType.ProductionReceipt)
        {
            throw new InvalidOperationException("Документ не является выпуском продукции.");
        }

        return doc;
    }

    private ProductionFillingOrder? BuildFillingOrder(long orderId, IReadOnlyList<ProductionPalletWorkItem> workItems)
    {
        var order = _data.GetOrder(orderId);
        if (order == null || order.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
        {
            return null;
        }

        var activeItems = workItems
            .Where(item => item.Summary.RemainingPalletCount > 0 || item.Summary.RemainingQty > QtyTolerance)
            .ToList();
        if (activeItems.Count == 0)
        {
            return null;
        }

        var summary = CombineSummary(activeItems.Select(item => item.Summary));
        if (summary.RemainingQty <= QtyTolerance && summary.RemainingPalletCount <= 0)
        {
            return null;
        }

        var primaryWorkItem = activeItems.First();
        return new ProductionFillingOrder
        {
            OrderId = order.Id,
            OrderRef = order.OrderRef,
            OrderType = OrderStatusMapper.TypeToString(order.Type),
            OrderTypeDisplay = OrderStatusMapper.TypeToDisplayName(order.Type),
            OrderStatus = OrderStatusMapper.StatusToString(order.Status),
            OrderStatusDisplay = OrderStatusMapper.StatusToDisplayName(order.Status, order.Type),
            PartnerName = order.PartnerDisplay,
            PrdDocId = primaryWorkItem.PrdDocId,
            PrdDocRef = primaryWorkItem.PrdDocRef,
            Summary = summary
        };
    }

    private ProductionFillingContext BuildFillingContext(long orderId, long prdDocId)
    {
        var order = _data.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        var doc = _data.GetDoc(prdDocId) ?? throw new InvalidOperationException("Документ выпуска не найден.");
        return new ProductionFillingContext
        {
            OrderId = order.Id,
            OrderRef = order.OrderRef,
            OrderType = OrderStatusMapper.TypeToString(order.Type),
            OrderTypeDisplay = OrderStatusMapper.TypeToDisplayName(order.Type),
            OrderStatus = OrderStatusMapper.StatusToString(order.Status),
            OrderStatusDisplay = OrderStatusMapper.StatusToDisplayName(order.Status, order.Type),
            PartnerName = order.PartnerDisplay,
            PrdDocId = doc.Id,
            PrdDocRef = doc.DocRef,
            Document = Get(doc.Id)
        };
    }

    private ProductionPalletOrderPlanResult BuildOrderPlanResult(long orderId, long prdDocId, bool wasExisting)
    {
        var order = _data.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        var doc = _data.GetDoc(prdDocId) ?? throw new InvalidOperationException("Документ выпуска не найден.");
        var document = Get(doc.Id);
        return new ProductionPalletOrderPlanResult
        {
            OrderId = order.Id,
            OrderRef = order.OrderRef,
            PrdDocId = doc.Id,
            PrdDocRef = doc.DocRef,
            WasExisting = wasExisting,
            Summary = document.Summary,
            Document = document
        };
    }

    private static Doc? FindProductionReceiptWithPalletPlan(IDataStore store, long orderId)
    {
        Doc? closedWithPlan = null;
        foreach (var doc in store.GetDocsByOrder(orderId)
                     .Where(doc => doc.Type == DocType.ProductionReceipt)
                     .OrderByDescending(doc => doc.Id))
        {
            if (!store.HasProductionPallets(doc.Id))
            {
                continue;
            }

            if (doc.Status != DocStatus.Closed)
            {
                return doc;
            }

            closedWithPlan ??= doc;
        }

        return closedWithPlan;
    }

    private static Doc? FindPreparedOpenProductionReceipt(IDataStore store, long orderId, bool requireRemaining)
    {
        foreach (var doc in store.GetDocsByOrder(orderId)
                     .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status != DocStatus.Closed)
                     .OrderByDescending(doc => doc.Id))
        {
            var summary = BuildSummary(store.GetProductionPalletsByDoc(doc.Id));
            if (summary.PlannedPalletCount <= 0)
            {
                continue;
            }

            if (requireRemaining && summary.RemainingPalletCount <= 0 && summary.RemainingQty <= QtyTolerance)
            {
                continue;
            }

            return doc;
        }

        return null;
    }

    private static Doc? FindPrintableProductionReceipt(IDataStore store, Order order)
    {
        var openDoc = FindPreparedOpenProductionReceipt(store, order.Id, requireRemaining: false);
        if (openDoc != null)
        {
            return openDoc;
        }

        if (order.Status is not (OrderStatus.Shipped or OrderStatus.Accepted))
        {
            return null;
        }

        foreach (var doc in store.GetDocsByOrder(order.Id)
                     .Where(doc => doc.Type == DocType.ProductionReceipt)
                     .OrderByDescending(doc => doc.Id))
        {
            var pallets = store.GetProductionPalletsByDoc(doc.Id)
                .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (pallets.Count > 0)
            {
                return doc;
            }
        }

        return null;
    }

    private static Doc? FindReusableEmptyProductionReceipt(IDataStore store, long orderId)
    {
        return store.GetDocsByOrder(orderId)
            .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status != DocStatus.Closed)
            .OrderByDescending(doc => doc.Id)
            .FirstOrDefault(doc => !store.GetDocLines(doc.Id).Any() && !store.HasProductionPallets(doc.Id));
    }

    private static Doc CreateProductionReceipt(IDataStore store, Order order)
    {
        var docRef = DocRefGenerator.Generate(store, DocType.ProductionReceipt, DateTime.Now);
        var docId = store.AddDoc(new Doc
        {
            DocRef = docRef,
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            CreatedAt = DateTime.Now,
            OrderId = order.Id,
            OrderRef = order.OrderRef
        });

        return store.GetDoc(docId) ?? throw new InvalidOperationException("Документ выпуска не найден.");
    }

    private static Location ResolveProductionPalletPlanLocation(IDataStore store)
    {
        var locations = store.GetLocations()
            .OrderBy(location => location.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (locations.Count == 0)
        {
            throw new InvalidOperationException("Нет доступных локаций для плана паллет.");
        }

        return locations.FirstOrDefault(location => location.AutoHuDistributionEnabled) ?? locations[0];
    }

    private static void AddPlannedPalletLines(
        IDataStore store,
        long prdDocId,
        OrderReceiptLine line,
        double palletQty,
        long toLocationId)
    {
        var remainingQty = line.QtyRemaining;
        while (remainingQty > QtyTolerance)
        {
            var chunkQty = Math.Min(palletQty, remainingQty);
            if (chunkQty <= QtyTolerance)
            {
                break;
            }

            store.AddDocLine(new DocLine
            {
                DocId = prdDocId,
                OrderLineId = line.OrderLineId,
                ProductionPurpose = line.ProductionPurpose,
                ItemId = line.ItemId,
                Qty = chunkQty,
                QtyInput = null,
                UomCode = null,
                FromLocationId = null,
                ToLocationId = toLocationId,
                FromHu = null,
                ToHu = store.CreateProductionPalletHuCode(PlanHuCreatedBy),
                PackSingleHu = true
            });

            remainingQty -= chunkQty;
        }
    }

    private static void AddMixedPlannedPalletLines(
        IDataStore store,
        long prdDocId,
        IReadOnlyList<OrderReceiptLine> lines,
        long toLocationId)
    {
        var huCode = store.CreateProductionPalletHuCode(PlanHuCreatedBy);
        foreach (var line in lines)
        {
            store.AddDocLine(new DocLine
            {
                DocId = prdDocId,
                OrderLineId = line.OrderLineId,
                ProductionPurpose = line.ProductionPurpose,
                ItemId = line.ItemId,
                Qty = line.QtyRemaining,
                QtyInput = null,
                UomCode = null,
                FromLocationId = null,
                ToLocationId = toLocationId,
                FromHu = null,
                ToHu = huCode,
                PackSingleHu = true
            });
        }
    }

    private ProductionPalletDocument BuildDocument(long docId, IReadOnlyList<ProductionPallet> pallets)
    {
        var summary = BuildSummary(pallets);
        var palletLineRows = pallets
            .SelectMany(pallet => GetPalletLines(pallet).Select(line => new { Pallet = pallet, Line = line }))
            .ToList();
        var orderLineIds = palletLineRows
            .Where(row => row.Pallet.OrderId.HasValue && row.Line.OrderLineId.HasValue)
            .Select(row => (OrderId: row.Pallet.OrderId!.Value, OrderLineId: row.Line.OrderLineId!.Value))
            .Distinct()
            .ToList();
        var orderLinesById = new Dictionary<long, OrderLine>();
        foreach (var group in orderLineIds.GroupBy(row => row.OrderId))
        {
            foreach (var line in _data.GetOrderLines(group.Key))
            {
                orderLinesById[line.Id] = line;
            }
        }

        var lines = palletLineRows
            .GroupBy(row => new { row.Line.OrderLineId, row.Line.ItemId, row.Line.ItemName })
            .Select(group =>
            {
                var orderedQty = group.Key.OrderLineId.HasValue
                                  && orderLinesById.TryGetValue(group.Key.OrderLineId.Value, out var orderLine)
                    ? orderLine.QtyOrdered
                    : group.Sum(row => row.Line.PlannedQty);
                var groupRows = group.ToList();
                var plannedPalletCount = groupRows.Select(row => row.Pallet.Id).Distinct().Count();
                var filledRows = groupRows
                    .Where(row => string.Equals(row.Pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var filledPalletCount = filledRows.Select(row => row.Pallet.Id).Distinct().Count();
                var plannedQty = groupRows.Sum(row => row.Line.PlannedQty);
                var filledQty = filledRows.Sum(row => row.Line.PlannedQty);
                return new ProductionPalletLineSummary
                {
                    OrderLineId = group.Key.OrderLineId,
                    ItemId = group.Key.ItemId,
                    ItemName = group.Key.ItemName,
                    OrderedQty = orderedQty,
                    PlannedPalletCount = plannedPalletCount,
                    PlannedQty = plannedQty,
                    FilledPalletCount = filledPalletCount,
                    FilledQty = filledQty,
                    RemainingPalletCount = plannedPalletCount - filledPalletCount,
                    RemainingQty = Math.Max(0, orderedQty - filledQty)
                };
            })
            .OrderBy(line => line.OrderLineId ?? long.MaxValue)
            .ThenBy(line => line.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProductionPalletDocument
        {
            PrdDocId = docId,
            Summary = summary,
            Lines = lines,
            Pallets = pallets
        };
    }

    public static ProductionPalletSummary BuildSummary(IReadOnlyList<ProductionPallet> pallets)
    {
        var active = pallets
            .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var filled = active
            .Where(pallet => string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new ProductionPalletSummary
        {
            PlannedPalletCount = active.Count,
            PlannedQty = active.Sum(pallet => pallet.PlannedQty),
            FilledPalletCount = filled.Count,
            FilledQty = filled.Sum(pallet => pallet.PlannedQty),
            RemainingPalletCount = active.Count - filled.Count,
            RemainingQty = Math.Max(0, active.Sum(pallet => pallet.PlannedQty) - filled.Sum(pallet => pallet.PlannedQty))
        };
    }

    private static ProductionPalletSummary CombineSummary(IEnumerable<ProductionPalletSummary> summaries)
    {
        var plannedPalletCount = 0;
        var plannedQty = 0d;
        var filledPalletCount = 0;
        var filledQty = 0d;
        var remainingPalletCount = 0;
        var remainingQty = 0d;
        foreach (var summary in summaries)
        {
            plannedPalletCount += summary.PlannedPalletCount;
            plannedQty += summary.PlannedQty;
            filledPalletCount += summary.FilledPalletCount;
            filledQty += summary.FilledQty;
            remainingPalletCount += summary.RemainingPalletCount;
            remainingQty += summary.RemainingQty;
        }

        return new ProductionPalletSummary
        {
            PlannedPalletCount = plannedPalletCount,
            PlannedQty = plannedQty,
            FilledPalletCount = filledPalletCount,
            FilledQty = filledQty,
            RemainingPalletCount = remainingPalletCount,
            RemainingQty = remainingQty
        };
    }

    private static bool TryParseLong(string? value, out long result)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out result);
    }

    private static string? NormalizeHu(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? NormalizeDeviceId(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static IReadOnlyList<ProductionPalletComponentLine> GetPalletLines(ProductionPallet pallet)
    {
        if (pallet.Lines.Count > 0)
        {
            return pallet.Lines;
        }

        return new[]
        {
            new ProductionPalletComponentLine
            {
                ProductionPalletId = pallet.Id,
                DocLineId = pallet.DocLineId,
                OrderLineId = pallet.OrderLineId,
                ItemId = pallet.ItemId,
                ItemName = pallet.ItemName,
                PlannedQty = pallet.PlannedQty,
                FilledQty = string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)
                    ? pallet.PlannedQty
                    : 0,
                CreatedAt = pallet.CreatedAt
            }
        };
    }

    private static string FormatQty(double value)
    {
        return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
}
