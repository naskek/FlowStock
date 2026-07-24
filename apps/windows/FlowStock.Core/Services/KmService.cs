using System.Security.Cryptography;
using System.Linq;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class KmService
{
    private readonly IDataStore _data;

    public KmService(IDataStore data)
    {
        _data = data;
    }

    public IReadOnlyList<KmCodeBatch> GetBatches()
    {
        var batches = _data.GetKmCodeBatches();
        if (batches.Count == 0)
        {
            return batches;
        }

        var result = new List<KmCodeBatch>(batches.Count);
        foreach (var batch in batches)
        {
            var inPool = _data.CountKmCodesByBatch(batch.Id, KmCodeStatus.InPool);
            var onHand = _data.CountKmCodesByBatch(batch.Id, KmCodeStatus.OnHand);
            var shipped = _data.CountKmCodesByBatch(batch.Id, KmCodeStatus.Shipped);
            var blocked = _data.CountKmCodesByBatch(batch.Id, KmCodeStatus.Blocked);

            result.Add(new KmCodeBatch
            {
                Id = batch.Id,
                OrderId = batch.OrderId,
                OrderRef = batch.OrderRef,
                FileName = batch.FileName,
                FileHash = batch.FileHash,
                ImportedAt = batch.ImportedAt,
                ImportedBy = batch.ImportedBy,
                TotalCodes = batch.TotalCodes,
                ErrorCount = batch.ErrorCount,
                BatchStatusDisplay = BuildBatchStatusDisplay(inPool, onHand, shipped, blocked)
            });
        }

        return result;
    }

    public IReadOnlyList<KmCode> GetCodes(long batchId, string? search, KmCodeStatus? status, int take = 1000)
    {
        return _data.GetKmCodesByBatch(batchId, search, status, take);
    }

    public int CountUnmatchedSku(long batchId)
    {
        return _data.CountKmCodesWithoutSku(batchId);
    }

    public int GetAssignedCountForReceiptLine(long receiptLineId)
    {
        return _data.CountKmCodesByReceiptLine(receiptLineId);
    }

    public int GetAssignedCountForShipmentLine(long shipLineId)
    {
        return _data.CountKmCodesByShipmentLine(shipLineId);
    }

    public int DeleteInPoolCodes(long batchId, IReadOnlyList<long> codeIds)
    {
        if (batchId <= 0)
        {
            throw new ArgumentException("Пакет КМ не задан.", nameof(batchId));
        }

        if (codeIds.Count == 0)
        {
            return 0;
        }

        var uniqueIds = codeIds.Where(id => id > 0).Distinct().ToArray();
        if (uniqueIds.Length == 0)
        {
            return 0;
        }

        return _data.DeleteKmCodesFromBatch(batchId, uniqueIds);
    }

    public void DeleteBatch(long batchId)
    {
        if (batchId <= 0)
        {
            throw new ArgumentException("Пакет КМ не задан.", nameof(batchId));
        }

        _data.DeleteKmBatch(batchId);
    }

    public IReadOnlyList<KmCode> GetShipmentCodes(long shipLineId)
    {
        return _data.GetKmCodesByShipmentLine(shipLineId);
    }

    public void UpdateBatchOrder(long batchId, long? orderId)
    {
        _data.ExecuteInTransaction(store => store.UpdateKmCodeBatchOrder(batchId, orderId));
    }

    public KmImportResult ImportCodes(string filePath, long? orderId, string? importedBy)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new KmImportResult
            {
                FileName = string.Empty,
                FileHash = string.Empty,
                Errors = 1
            };
        }

        var fileHash = ComputeFileHash(filePath);
        if (_data.FindKmCodeBatchByHash(fileHash) != null)
        {
            return new KmImportResult
            {
                FileName = Path.GetFileName(filePath),
                FileHash = fileHash,
                IsDuplicateFile = true
            };
        }

        var firstLine = File.ReadLines(filePath).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        var delimiter = DetectDelimiter(firstLine);
        var batchId = 0L;
        var imported = 0;
        var duplicates = 0;
        var errors = 0;
        var invalidGtins = 0;
        var emptyCodes = 0;
        var unmatchedSku = 0;

        _data.ExecuteInTransaction(store =>
        {
            batchId = store.AddKmCodeBatch(new KmCodeBatch
            {
                OrderId = orderId,
                FileName = Path.GetFileName(filePath),
                FileHash = fileHash,
                ImportedAt = DateTime.Now,
                ImportedBy = importedBy,
                TotalCodes = 0,
                ErrorCount = 0
            });

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skuByGtin = new Dictionary<string, long?>(StringComparer.Ordinal);
            var markedItems = new HashSet<long>();
            foreach (var rawLine in File.ReadLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                var parts = rawLine.Split(delimiter);
                var codeRaw = parts.Length > 0 ? NormalizeCodeRaw(parts[0]) : string.Empty;
                var gtinRaw = parts.Length > 1 ? parts[1].Trim() : null;
                var nameRaw = parts.Length > 2 ? parts[2].Trim() : null;

                if (string.IsNullOrWhiteSpace(codeRaw))
                {
                    emptyCodes++;
                    errors++;
                    continue;
                }

                if (!seen.Add(codeRaw))
                {
                    duplicates++;
                    continue;
                }

                var gtin14 = NormalizeGtin(gtinRaw, out var gtinInvalid);
                if (gtinInvalid)
                {
                    invalidGtins++;
                    errors++;
                }

                long? skuId = null;
                if (gtin14 != null)
                {
                    if (!skuByGtin.TryGetValue(gtin14, out skuId))
                    {
                        skuId = ResolveItemByGtin(store, gtin14)?.Id;
                        skuByGtin[gtin14] = skuId;
                    }
                }
                if (!skuId.HasValue)
                {
                    unmatchedSku++;
                }
                else if (markedItems.Add(skuId.Value))
                {
                    EnsureItemMarked(store, skuId.Value);
                }

                try
                {
                    if (store.ExistsKmCodeByRawIgnoreCase(codeRaw))
                    {
                        duplicates++;
                        continue;
                    }

                    store.AddKmCode(new KmCode
                    {
                        BatchId = batchId,
                        CodeRaw = codeRaw,
                        Gtin14 = gtin14,
                        SkuId = skuId,
                        ProductName = string.IsNullOrWhiteSpace(nameRaw) ? null : nameRaw,
                        Status = KmCodeStatus.InPool,
                        OrderId = orderId
                    });
                    imported++;
                }
                catch
                {
                    if (store.ExistsKmCodeByRawIgnoreCase(codeRaw))
                    {
                        duplicates++;
                        continue;
                    }

                    errors++;
                }
            }

            store.UpdateKmCodeBatchStats(batchId, imported, errors);
        });

        return new KmImportResult
        {
            BatchId = batchId,
            FileName = Path.GetFileName(filePath),
            FileHash = fileHash,
            Imported = imported,
            Duplicates = duplicates,
            Errors = errors,
            InvalidGtins = invalidGtins,
            EmptyCodes = emptyCodes,
            UnmatchedSku = unmatchedSku
        };
    }

    public int AssignCodesToReceipt(long docId, DocLine line, Item item, long? batchId, long? orderId)
    {
        if (!line.ToLocationId.HasValue)
        {
            throw new InvalidOperationException("Не указана локация приемки.");
        }

        if (string.IsNullOrWhiteSpace(line.ToHu))
        {
            throw new InvalidOperationException("Для выпуска продукции требуется HU.");
        }

        var required = EnsureIntegerQty(line.Qty);
        var gtin14 = NormalizeGtin(item.Gtin, out _);
        var assigned = 0;
        _data.ExecuteInTransaction(store =>
        {
            var ids = store.GetAvailableKmCodeIds(batchId, orderId, item.Id, gtin14, required);
            if (ids.Count < required)
            {
                throw new InvalidOperationException(BuildInsufficientCodesMessage(store, batchId, orderId, item, gtin14, required, ids.Count));
            }

            var huId = ResolveHuId(store, line.ToHu);
            var locationId = line.ToLocationId;
            var updated = store.AssignKmCodesToReceipt(ids, docId, line.Id, huId, locationId);
            if (updated != required)
            {
                throw new InvalidOperationException($"Не удалось привязать все коды. Привязано {updated} из {required}.");
            }

            assigned = updated;
        });

        return assigned;
    }

    public void AssignCodeToShipment(string codeRaw, long docId, DocLine line, Item item, long? orderId)
    {
        if (string.IsNullOrWhiteSpace(codeRaw))
        {
            throw new ArgumentException("Код КМ не задан.");
        }

        var trimmed = codeRaw.Trim();
        var code = _data.FindKmCodeByRaw(trimmed);
        if (code == null)
        {
            throw new InvalidOperationException("Код не найден в реестре.");
        }

        if (code.Status != KmCodeStatus.OnHand && code.Status != KmCodeStatus.InPool)
        {
            throw new InvalidOperationException($"Код имеет статус {KmCodeStatusMapper.ToDisplayName(code.Status)}.");
        }

        if (!IsCodeMatchingItem(code, item))
        {
            throw new InvalidOperationException("Код не соответствует выбранному SKU.");
        }

        _data.MarkKmCodeShipped(code.Id, docId, line.Id, orderId);
    }

    public int AssignCodesToShipment(long docId, DocLine line, Item item, long? orderId)
    {
        var required = EnsureIntegerQty(line.Qty);
        var gtin14 = NormalizeGtin(item.Gtin, out _);
        var assigned = 0;
        _data.ExecuteInTransaction(store =>
        {
            var alreadyAssigned = store.CountKmCodesByShipmentLine(line.Id);
            var toAssign = required - alreadyAssigned;
            if (toAssign <= 0)
            {
                assigned = 0;
                return;
            }

            var huId = ResolveHuId(store, line.FromHu);
            var onHandIds = store.GetAvailableKmOnHandCodeIds(orderId, item.Id, gtin14, line.FromLocationId, huId, toAssign);
            var ids = onHandIds.ToList();
            if (ids.Count < toAssign)
            {
                var fromPool = store.GetAvailableKmCodeIds(null, orderId, item.Id, gtin14, toAssign - ids.Count);
                ids.AddRange(fromPool);
            }

            if (ids.Count < toAssign)
            {
                throw new InvalidOperationException($"Недостаточно кодов для {item.Name}. Нужно {toAssign}, доступно {ids.Count}.");
            }

            foreach (var id in ids)
            {
                store.MarkKmCodeShipped(id, docId, line.Id, orderId);
            }

            assigned = ids.Count;
        });

        return assigned;
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

    private static bool IsCodeMatchingItem(KmCode code, Item item)
    {
        if (code.SkuId.HasValue)
        {
            return code.SkuId.Value == item.Id;
        }

        var gtin14 = NormalizeGtin(item.Gtin, out _);
        if (string.IsNullOrWhiteSpace(gtin14))
        {
            return false;
        }

        return string.Equals(code.Gtin14, gtin14, StringComparison.OrdinalIgnoreCase);
    }

    private static int EnsureIntegerQty(double qty)
    {
        var rounded = Math.Round(qty);
        if (Math.Abs(qty - rounded) > 0.0001)
        {
            throw new InvalidOperationException("Количество для маркируемого товара должно быть целым.");
        }

        return (int)rounded;
    }

    private static void EnsureItemMarked(IDataStore store, long itemId)
    {
        var item = store.FindItemById(itemId);
        if (item == null || item.IsMarked)
        {
            return;
        }

        store.UpdateItem(new Item
        {
            Id = item.Id,
            Name = item.Name,
            Barcode = item.Barcode,
            Gtin = item.Gtin,
            BaseUom = item.BaseUom,
            DefaultPackagingId = item.DefaultPackagingId,
            Brand = item.Brand,
            Volume = item.Volume,
            ShelfLifeMonths = item.ShelfLifeMonths,
            StorageConditions = item.StorageConditions,
            TaraId = item.TaraId,
            IsMarked = true,
            DefaultSalePriceGross = item.DefaultSalePriceGross,
            DefaultSaleVatRateId = item.DefaultSaleVatRateId
        });
    }

    private static Item? ResolveItemByGtin(IDataStore store, string gtin14)
    {
        var direct = store.FindItemByGtin(gtin14);
        if (direct != null)
        {
            return direct;
        }

        if (gtin14.Length == 14 && gtin14[0] == '0')
        {
            var shortGtin = gtin14.Substring(1);
            return store.FindItemByGtin(shortGtin);
        }

        return null;
    }

    private static string BuildInsufficientCodesMessage(
        IDataStore store,
        long? batchId,
        long? orderId,
        Item item,
        string? gtin14,
        int required,
        int availableWithCurrentFilter)
    {
        if (orderId.HasValue)
        {
            var availableWithoutOrder = store.GetAvailableKmCodeIds(batchId, null, item.Id, gtin14, required).Count;
            if (availableWithoutOrder >= required)
            {
                return $"Недостаточно кодов для {item.Name}. Нужно {required}, доступно {availableWithCurrentFilter}. " +
                       "Коды есть в пуле, но не привязаны к выбранному заказу. " +
                       "Откройте пакет КМ и задайте ему нужный заказ (Маркировка -> Редактировать).";
            }
        }

        return $"Недостаточно кодов для {item.Name}. Нужно {required}, доступно {availableWithCurrentFilter}.";
    }

    private static string? NormalizeGtin(string? value, out bool invalid)
    {
        invalid = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!trimmed.All(char.IsDigit))
        {
            invalid = true;
            return null;
        }

        if (trimmed.Length == 14)
        {
            return trimmed;
        }

        if (trimmed.Length == 13)
        {
            return "0" + trimmed;
        }

        invalid = true;
        return null;
    }

    private static string NormalizeCodeRaw(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.Length >= 2
            && trimmed[0] == '"'
            && trimmed[^1] == '"')
        {
            trimmed = trimmed.Substring(1, trimmed.Length - 2).Trim();
        }

        return trimmed;
    }

    private static char DetectDelimiter(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return '\t';
        }

        var tab = line.Count(ch => ch == '\t');
        var semicolon = line.Count(ch => ch == ';');
        var comma = line.Count(ch => ch == ',');
        var max = Math.Max(tab, Math.Max(semicolon, comma));
        if (max == 0)
        {
            return '\t';
        }
        if (tab == max)
        {
            return '\t';
        }
        if (semicolon == max)
        {
            return ';';
        }
        return ',';
    }

    private static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    private static string BuildBatchStatusDisplay(int inPool, int onHand, int shipped, int blocked)
    {
        if (inPool > 0 && onHand == 0 && shipped == 0 && blocked == 0)
        {
            return "В пуле";
        }

        if (onHand > 0 && inPool == 0 && shipped == 0 && blocked == 0)
        {
            return "На складе";
        }

        if (shipped > 0 && inPool == 0 && onHand == 0 && blocked == 0)
        {
            return "Отгружен";
        }

        if (blocked > 0 && inPool == 0 && onHand == 0 && shipped == 0)
        {
            return "Заблокирован";
        }

        var parts = new List<string>();
        if (inPool > 0)
        {
            parts.Add($"Пул {inPool}");
        }
        if (onHand > 0)
        {
            parts.Add($"Склад {onHand}");
        }
        if (shipped > 0)
        {
            parts.Add($"Отгр {shipped}");
        }
        if (blocked > 0)
        {
            parts.Add($"Блок {blocked}");
        }

        return parts.Count == 0 ? "Пусто" : string.Join(" · ", parts);
    }
}
