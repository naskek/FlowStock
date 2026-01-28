using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LightWms.Core.Abstractions;
using LightWms.Core.Models;

namespace LightWms.Core.Services;

public sealed class ImportService
{
    private const string ReasonUnknownBarcode = "UNKNOWN_BARCODE";
    private const string ReasonInvalidJson = "INVALID_JSON";
    private const string ReasonUnknownOp = "UNKNOWN_OP";
    private const string ReasonMissingField = "MISSING_FIELD";
    private const string ReasonUnknownLocation = "UNKNOWN_LOCATION";
    private const string ReasonHuMismatch = "HU_MISMATCH";
    private const string ReasonMoveHuRequired = "MOVE внутри склада требует from_hu/to_hu. Обновите ТСД или пересоздайте документ.";

    private readonly IDataStore _data;
    private readonly JsonSerializerOptions _jsonOptions;

    public ImportService(IDataStore data)
    {
        _data = data;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    public ImportResult ImportJsonl(string filePath)
    {
        var result = new ImportResult();
        var deviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!TryParseLine(line, out var importEvent, out var itemUpsertEvent, out var errorReason))
            {
                _data.AddImportError(new ImportError
                {
                    EventId = importEvent?.EventId ?? itemUpsertEvent?.EventId,
                    Reason = errorReason ?? ReasonInvalidJson,
                    RawJson = line,
                    CreatedAt = DateTime.Now
                });
                result.Errors++;
                continue;
            }

            if (itemUpsertEvent != null)
            {
                if (!string.IsNullOrWhiteSpace(itemUpsertEvent.DeviceId))
                {
                    deviceIds.Add(itemUpsertEvent.DeviceId.Trim());
                }

                ImportOutcome outcome;
                try
                {
                    outcome = ProcessItemUpsert(itemUpsertEvent, line, filePath, allowErrorInsert: true);
                }
                catch
                {
                    _data.AddImportError(new ImportError
                    {
                        EventId = itemUpsertEvent.EventId,
                        Reason = ReasonInvalidJson,
                        RawJson = line,
                        CreatedAt = DateTime.Now
                    });
                    outcome = ImportOutcome.Error;
                }

                switch (outcome)
                {
                    case ImportOutcome.Imported:
                        result.Imported++;
                        result.ItemsUpserted++;
                        break;
                    case ImportOutcome.Duplicate:
                        result.Duplicates++;
                        break;
                    case ImportOutcome.Error:
                        result.Errors++;
                        break;
                }

                continue;
            }

            if (importEvent != null && !string.IsNullOrWhiteSpace(importEvent.DeviceId))
            {
                deviceIds.Add(importEvent.DeviceId.Trim());
            }

            try
            {
                var outcome = ProcessEvent(importEvent!, line, filePath, allowErrorInsert: true, out var docCreated);
                switch (outcome)
                {
                    case ImportOutcome.Imported:
                        result.Imported++;
                        result.OperationsImported++;
                        result.LinesImported++;
                        if (docCreated)
                        {
                            result.DocumentsCreated++;
                        }
                        break;
                    case ImportOutcome.Duplicate:
                        result.Duplicates++;
                        break;
                    case ImportOutcome.Error:
                        result.Errors++;
                        break;
                }
            }
            catch
            {
                _data.AddImportError(new ImportError
                {
                    EventId = importEvent?.EventId,
                    Reason = ReasonInvalidJson,
                    RawJson = line,
                    CreatedAt = DateTime.Now
                });
                result.Errors++;
            }
        }

        result.DeviceIds = deviceIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
        return result;
    }

    public IReadOnlyList<ImportErrorView> GetImportErrors(string? reason)
    {
        var errors = _data.GetImportErrors(reason);
        var result = new List<ImportErrorView>(errors.Count);
        foreach (var err in errors)
        {
            result.Add(new ImportErrorView
            {
                Id = err.Id,
                EventId = err.EventId,
                Reason = err.Reason,
                RawJson = err.RawJson,
                CreatedAt = err.CreatedAt,
                Barcode = ExtractBarcode(err.RawJson)
            });
        }

        return result;
    }

    public bool ReapplyError(long errorId)
    {
        var error = _data.GetImportError(errorId);
        if (error == null)
        {
            return false;
        }

        if (!TryParseEvent(error.RawJson, out var importEvent, out _))
        {
            return false;
        }

        if (_data.FindItemByBarcode(importEvent!.Barcode) == null)
        {
            return false;
        }

        var outcome = ProcessEvent(importEvent, error.RawJson, "reapply", allowErrorInsert: false, out _);
        if (outcome == ImportOutcome.Imported || outcome == ImportOutcome.Duplicate)
        {
            _data.DeleteImportError(errorId);
            return true;
        }

        return false;
    }

    private ImportOutcome ProcessEvent(ImportEvent importEvent, string rawJson, string sourceFile, bool allowErrorInsert, out bool docCreated)
    {
        var outcome = ImportOutcome.Error;
        var created = false;
        var huCode = NormalizeHuCode(importEvent.HuCode);

        _data.ExecuteInTransaction(store =>
        {
            if (store.IsEventImported(importEvent.EventId))
            {
                outcome = ImportOutcome.Duplicate;
                return;
            }

            var item = store.FindItemByBarcode(importEvent.Barcode);
            if (item == null)
            {
                foreach (var variant in GetBarcodeVariants(importEvent.Barcode))
                {
                    item = store.FindItemByBarcode(variant);
                    if (item != null)
                    {
                        break;
                    }
                }
            }
            if (item == null)
            {
                item = TryCreateFallbackItem(store, importEvent.Barcode);
            }
            if (item == null)
            {
                if (allowErrorInsert)
                {
                    store.AddImportError(new ImportError
                    {
                        EventId = importEvent.EventId,
                        Reason = ReasonUnknownBarcode,
                        RawJson = rawJson,
                        CreatedAt = DateTime.Now
                    });
                }

                outcome = ImportOutcome.Error;
                return;
            }

            var (fromLocation, toLocation, locationValid) = ResolveLocations(store, importEvent);
            if (!locationValid)
            {
                if (allowErrorInsert)
                {
                    store.AddImportError(new ImportError
                    {
                        EventId = importEvent.EventId,
                        Reason = ReasonUnknownLocation,
                        RawJson = rawJson,
                        CreatedAt = DateTime.Now
                    });
                }

                outcome = ImportOutcome.Error;
                return;
            }

            var docRef = string.IsNullOrWhiteSpace(importEvent.DocRef)
                ? DocRefGenerator.Generate(store, importEvent.Type, importEvent.Timestamp.Date)
                : importEvent.DocRef;

            var doc = store.FindDocByRef(docRef, importEvent.Type);
            if (doc == null)
            {
                var partnerId = ResolvePartnerId(store, importEvent);
                var docId = store.AddDoc(new Doc
                {
                    DocRef = docRef,
                    Type = importEvent.Type,
                    Status = DocStatus.Draft,
                    CreatedAt = importEvent.Timestamp,
                    ClosedAt = null,
                    PartnerId = partnerId,
                    OrderRef = NormalizeOrderRef(importEvent.OrderRef),
                    ShippingRef = huCode
                });
                doc = store.GetDoc(docId);
                created = true;
            }
            else
            {
                if (HasHuMismatch(doc.ShippingRef, huCode))
                {
                    if (allowErrorInsert)
                    {
                        store.AddImportError(new ImportError
                        {
                            EventId = importEvent.EventId,
                            Reason = ReasonHuMismatch,
                            RawJson = rawJson,
                            CreatedAt = DateTime.Now
                        });
                    }

                    outcome = ImportOutcome.Error;
                    return;
                }

                TryUpdateDocHeader(store, doc, importEvent, huCode);
            }

            if (doc == null)
            {
                outcome = ImportOutcome.Error;
                return;
            }

            var (fromHu, toHu) = ResolveLineHu(importEvent, huCode);
            store.AddDocLine(new DocLine
            {
                DocId = doc.Id,
                ItemId = item.Id,
                Qty = importEvent.Qty,
                FromLocationId = fromLocation?.Id,
                ToLocationId = toLocation?.Id,
                FromHu = fromHu,
                ToHu = toHu
            });

            store.AddImportedEvent(new ImportedEvent
            {
                EventId = importEvent.EventId,
                ImportedAt = DateTime.Now,
                SourceFile = Path.GetFileName(sourceFile),
                DeviceId = importEvent.DeviceId
            });

            outcome = ImportOutcome.Imported;
        });

        docCreated = created;
        return outcome;
    }

    private ImportOutcome ProcessItemUpsert(ItemUpsertEvent itemEvent, string rawJson, string sourceFile, bool allowErrorInsert)
    {
        var outcome = ImportOutcome.Error;

        _data.ExecuteInTransaction(store =>
        {
            if (store.IsEventImported(itemEvent.EventId))
            {
                outcome = ImportOutcome.Duplicate;
                return;
            }

            var name = TrimToNull(itemEvent.Name);
            var barcode = TrimToNull(itemEvent.Barcode);
            var gtin = TrimToNull(itemEvent.Gtin);
            if (string.IsNullOrWhiteSpace(name) || (string.IsNullOrWhiteSpace(barcode) && string.IsNullOrWhiteSpace(gtin)))
            {
                if (allowErrorInsert)
                {
                    store.AddImportError(new ImportError
                    {
                        EventId = itemEvent.EventId,
                        Reason = ReasonMissingField,
                        RawJson = rawJson,
                        CreatedAt = DateTime.Now
                    });
                }

                outcome = ImportOutcome.Error;
                return;
            }

            var existing = FindItemByCodes(store, barcode, gtin);
            var baseUom = TrimToNull(itemEvent.BaseUom);
            if (string.IsNullOrWhiteSpace(baseUom))
            {
                baseUom = existing?.BaseUom ?? "шт";
            }

            if (existing == null)
            {
                var itemId = store.AddItem(new Item
                {
                    Name = name,
                    Barcode = barcode,
                    Gtin = gtin,
                    BaseUom = baseUom,
                    DefaultPackagingId = null
                });
                existing = store.FindItemById(itemId);
            }
            else
            {
                store.UpdateItem(new Item
                {
                    Id = existing.Id,
                    Name = name,
                    Barcode = barcode ?? existing.Barcode,
                    Gtin = gtin ?? existing.Gtin,
                    BaseUom = baseUom,
                    DefaultPackagingId = existing.DefaultPackagingId
                });
            }

            store.AddImportedEvent(new ImportedEvent
            {
                EventId = itemEvent.EventId,
                ImportedAt = DateTime.Now,
                SourceFile = Path.GetFileName(sourceFile),
                DeviceId = itemEvent.DeviceId
            });

            outcome = ImportOutcome.Imported;
        });

        return outcome;
    }

    private static Item? TryCreateFallbackItem(IDataStore store, string? barcode)
    {
        var code = TrimToNull(barcode);
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        try
        {
            var itemId = store.AddItem(new Item
            {
                Name = code,
                Barcode = code,
                Gtin = null,
                BaseUom = "шт",
                DefaultPackagingId = null
            });
            return store.FindItemById(itemId);
        }
        catch
        {
            return null;
        }
    }

    private static Item? FindItemByCodes(IDataStore store, string? barcode, string? gtin)
    {
        if (!string.IsNullOrWhiteSpace(barcode))
        {
            var item = store.FindItemByBarcode(barcode);
            if (item != null)
            {
                return item;
            }
        }

        if (!string.IsNullOrWhiteSpace(gtin))
        {
            return store.FindItemByBarcode(gtin);
        }

        return null;
    }

    private static long? ResolvePartnerId(IDataStore store, ImportEvent importEvent)
    {
        if (importEvent.PartnerId.HasValue)
        {
            var partner = store.GetPartner(importEvent.PartnerId.Value);
            if (partner != null)
            {
                return partner.Id;
            }
        }

        if (!string.IsNullOrWhiteSpace(importEvent.PartnerCode))
        {
            var partner = store.FindPartnerByCode(importEvent.PartnerCode.Trim());
            if (partner != null)
            {
                return partner.Id;
            }
        }

        return null;
    }

    private static string? NormalizeOrderRef(string? orderRef)
    {
        return string.IsNullOrWhiteSpace(orderRef) ? null : orderRef.Trim();
    }

    private static void TryUpdateDocHeader(IDataStore store, Doc doc, ImportEvent importEvent, string? huCode)
    {
        if (doc.Status != DocStatus.Draft)
        {
            return;
        }

        var partnerId = doc.PartnerId ?? ResolvePartnerId(store, importEvent);
        var orderRef = doc.OrderRef ?? NormalizeOrderRef(importEvent.OrderRef);
        var shippingRef = doc.ShippingRef;
        if (string.IsNullOrWhiteSpace(shippingRef) && !string.IsNullOrWhiteSpace(huCode))
        {
            shippingRef = huCode;
        }

        if (partnerId == doc.PartnerId
            && string.Equals(orderRef, doc.OrderRef, StringComparison.Ordinal)
            && string.Equals(shippingRef, doc.ShippingRef, StringComparison.Ordinal))
        {
            return;
        }

        store.UpdateDocHeader(doc.Id, partnerId, orderRef, shippingRef);
    }

    private (Location? from, Location? to, bool valid) ResolveLocations(IDataStore store, ImportEvent importEvent)
    {
        var fromCode = NormalizeLocationCode(importEvent.FromLocation);
        var toCode = NormalizeLocationCode(importEvent.ToLocation);

        Location? from = null;
        Location? to = null;

        if (!string.IsNullOrWhiteSpace(fromCode))
        {
            from = store.FindLocationByCode(fromCode);
            if (from == null)
            {
                return (null, null, false);
            }
        }

        if (!string.IsNullOrWhiteSpace(toCode))
        {
            to = store.FindLocationByCode(toCode);
            if (to == null)
            {
                return (null, null, false);
            }
        }

        switch (importEvent.Type)
        {
            case DocType.Inbound:
                return to != null ? (null, to, true) : (null, null, false);
            case DocType.WriteOff:
                return from != null ? (from, null, true) : (null, null, false);
            case DocType.Outbound:
                return from != null ? (from, null, true) : (null, null, false);
            case DocType.Move:
                return from != null && to != null ? (from, to, true) : (null, null, false);
            case DocType.Inventory:
                if (to != null || from != null)
                {
                    return (from, to, true);
                }
                return (null, null, false);
            default:
                return (null, null, false);
        }
    }

    private bool TryParseEvent(string rawJson, out ImportEvent? importEvent, out string? errorReason)
    {
        importEvent = null;
        errorReason = null;

        ImportEventDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ImportEventDto>(rawJson, _jsonOptions);
        }
        catch (JsonException)
        {
            errorReason = ReasonInvalidJson;
            return false;
        }

        if (dto == null)
        {
            errorReason = ReasonInvalidJson;
            return false;
        }

        if (!string.IsNullOrWhiteSpace(dto.Event)
            && !string.Equals(dto.Event.Trim(), "OP", StringComparison.OrdinalIgnoreCase))
        {
            errorReason = ReasonUnknownOp;
            return false;
        }

        return TryBuildImportEvent(dto, out importEvent, out errorReason);
    }

    private bool TryParseLine(string rawJson, out ImportEvent? importEvent, out ItemUpsertEvent? itemUpsertEvent, out string? errorReason)
    {
        importEvent = null;
        itemUpsertEvent = null;
        errorReason = null;

        ImportEventDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ImportEventDto>(rawJson, _jsonOptions);
        }
        catch (JsonException)
        {
            errorReason = ReasonInvalidJson;
            return false;
        }

        if (dto == null)
        {
            errorReason = ReasonInvalidJson;
            return false;
        }

        var eventType = dto.Event?.Trim();
        if (string.Equals(eventType, "ITEM_UPSERT", StringComparison.OrdinalIgnoreCase))
        {
            return TryBuildItemUpsertEvent(dto, rawJson, out itemUpsertEvent, out errorReason);
        }

        if (!string.IsNullOrWhiteSpace(eventType)
            && !string.Equals(eventType, "OP", StringComparison.OrdinalIgnoreCase))
        {
            errorReason = ReasonUnknownOp;
            return false;
        }

        return TryBuildImportEvent(dto, out importEvent, out errorReason);
    }

    private bool TryBuildImportEvent(ImportEventDto dto, out ImportEvent? importEvent, out string? errorReason)
    {
        importEvent = null;
        errorReason = null;

        if (string.IsNullOrWhiteSpace(dto.EventId) ||
            string.IsNullOrWhiteSpace(dto.Op) ||
            string.IsNullOrWhiteSpace(dto.Barcode) ||
            dto.Qty == null)
        {
            errorReason = ReasonMissingField;
            return false;
        }

        var docType = DocTypeMapper.FromOpString(dto.Op);
        if (docType == null)
        {
            errorReason = ReasonUnknownOp;
            return false;
        }

        var timestamp = ParseTimestamp(dto.Ts) ?? DateTime.Now;
        var partnerCode = NormalizePartnerCode(dto.PartnerCode, dto.PartnerInn);
        var fromLocation = NormalizeLocationCode(dto.FromLoc ?? dto.From);
        var toLocation = NormalizeLocationCode(dto.ToLoc ?? dto.To);
        var fromHu = NormalizeHuCode(dto.FromHu);
        var toHu = NormalizeHuCode(dto.ToHu);
        var huCode = NormalizeHuCode(dto.HuCode ?? dto.HandlingUnit);
        if (docType == DocType.Move && string.IsNullOrWhiteSpace(toHu) && !string.IsNullOrWhiteSpace(huCode))
        {
            toHu = huCode;
        }

        if (docType == DocType.Move
            && !string.IsNullOrWhiteSpace(fromLocation)
            && !string.IsNullOrWhiteSpace(toLocation)
            && string.Equals(fromLocation, toLocation, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(fromHu) && string.IsNullOrWhiteSpace(toHu))
            {
                errorReason = ReasonMoveHuRequired;
                return false;
            }
        }

        importEvent = new ImportEvent
        {
            EventId = dto.EventId.Trim(),
            Timestamp = timestamp,
            DeviceId = dto.DeviceId?.Trim() ?? string.Empty,
            Type = docType.Value,
            DocRef = dto.DocRef?.Trim() ?? string.Empty,
            Barcode = dto.Barcode.Trim(),
            Qty = dto.Qty.Value,
            FromLocation = fromLocation,
            ToLocation = toLocation,
            FromHu = fromHu,
            ToHu = toHu,
            PartnerId = dto.PartnerId,
            PartnerCode = partnerCode,
            OrderRef = dto.OrderRef?.Trim(),
            ReasonCode = dto.ReasonCode?.Trim(),
            HuCode = huCode
        };

        return true;
    }

    private bool TryBuildItemUpsertEvent(ImportEventDto dto, string rawJson, out ItemUpsertEvent? itemUpsertEvent, out string? errorReason)
    {
        itemUpsertEvent = null;
        errorReason = null;

        var item = dto.Item;
        if (item == null)
        {
            errorReason = ReasonMissingField;
            return false;
        }

        var name = TrimToNull(item.Name);
        var barcode = TrimToNull(item.Barcode);
        var gtin = TrimToNull(item.Gtin);
        var baseUom = TrimToNull(item.BaseUom);

        if (string.IsNullOrWhiteSpace(name) || (string.IsNullOrWhiteSpace(barcode) && string.IsNullOrWhiteSpace(gtin)))
        {
            errorReason = ReasonMissingField;
            return false;
        }

        var eventId = TrimToNull(dto.EventId);
        if (string.IsNullOrWhiteSpace(eventId))
        {
            eventId = BuildFallbackEventId(rawJson);
        }

        var timestamp = ParseTimestamp(dto.Ts) ?? DateTime.Now;

        itemUpsertEvent = new ItemUpsertEvent
        {
            EventId = eventId,
            Timestamp = timestamp,
            DeviceId = dto.DeviceId?.Trim() ?? string.Empty,
            Name = name,
            Barcode = barcode,
            Gtin = gtin,
            BaseUom = baseUom
        };

        return true;
    }

    private static string BuildFallbackEventId(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return Guid.NewGuid().ToString();
        }

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawJson));
        return "ITEM_UPSERT_" + Convert.ToHexString(hash);
    }

    private static string? TrimToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static DateTime? ParseTimestamp(string? ts)
    {
        if (string.IsNullOrWhiteSpace(ts))
        {
            return null;
        }

        if (DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? NormalizeLocationCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return code.Trim();
    }

    private static string? NormalizePartnerCode(string? partnerCode, string? partnerInn)
    {
        if (!string.IsNullOrWhiteSpace(partnerCode))
        {
            return partnerCode.Trim();
        }

        if (!string.IsNullOrWhiteSpace(partnerInn))
        {
            return partnerInn.Trim();
        }

        return null;
    }

    private static string? ExtractBarcode(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.TryGetProperty("barcode", out var barcodeElement))
            {
                return barcodeElement.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static bool HasHuMismatch(string? existingHu, string? incomingHu)
    {
        if (string.IsNullOrWhiteSpace(incomingHu))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(existingHu))
        {
            return false;
        }

        return !string.Equals(existingHu.Trim(), incomingHu.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static (string? fromHu, string? toHu) ResolveLineHu(ImportEvent importEvent, string? huCode)
    {
        if (importEvent.Type == DocType.Move)
        {
            var fromHu = NormalizeHuCode(importEvent.FromHu);
            var toHu = NormalizeHuCode(importEvent.ToHu);
            if (string.IsNullOrWhiteSpace(toHu))
            {
                toHu = NormalizeHuCode(huCode);
            }
            return (fromHu, toHu);
        }

        var normalized = NormalizeHuCode(huCode);
        return importEvent.Type switch
        {
            DocType.Inbound => (null, normalized),
            DocType.Inventory => (null, normalized),
            DocType.Outbound => (normalized, null),
            DocType.WriteOff => (normalized, null),
            _ => (null, null)
        };
    }

    private static string? NormalizeHuCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith("HU-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed.ToUpperInvariant();
    }

    private static IEnumerable<string> GetBarcodeVariants(string? barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            yield break;
        }

        var value = barcode.Trim();
        if (value.Length == 13)
        {
            yield return "0" + value;
            yield break;
        }

        if (value.Length == 14 && value.StartsWith("0", StringComparison.Ordinal))
        {
            yield return value.Substring(1);
        }
    }

    private enum ImportOutcome
    {
        Imported,
        Duplicate,
        Error
    }

    private sealed class ImportEventDto
    {
        [JsonPropertyName("schema_version")]
        public int? SchemaVersion { get; set; }

        [JsonPropertyName("event")]
        public string? Event { get; set; }

        [JsonPropertyName("event_id")]
        public string? EventId { get; set; }

        [JsonPropertyName("ts")]
        public string? Ts { get; set; }

        [JsonPropertyName("device_id")]
        public string? DeviceId { get; set; }

        [JsonPropertyName("op")]
        public string? Op { get; set; }

        [JsonPropertyName("doc_ref")]
        public string? DocRef { get; set; }

        [JsonPropertyName("barcode")]
        public string? Barcode { get; set; }

        [JsonPropertyName("qty")]
        public double? Qty { get; set; }

        [JsonPropertyName("from_loc")]
        public string? FromLoc { get; set; }

        [JsonPropertyName("to_loc")]
        public string? ToLoc { get; set; }

        [JsonPropertyName("from")]
        public string? From { get; set; }

        [JsonPropertyName("to")]
        public string? To { get; set; }

        [JsonPropertyName("from_hu")]
        public string? FromHu { get; set; }

        [JsonPropertyName("to_hu")]
        public string? ToHu { get; set; }

        [JsonPropertyName("partner_id")]
        public long? PartnerId { get; set; }

        [JsonPropertyName("partner_code")]
        public string? PartnerCode { get; set; }

        [JsonPropertyName("partner_inn")]
        public string? PartnerInn { get; set; }

        [JsonPropertyName("order_ref")]
        public string? OrderRef { get; set; }

        [JsonPropertyName("reason_code")]
        public string? ReasonCode { get; set; }

        [JsonPropertyName("hu_code")]
        public string? HuCode { get; set; }

        [JsonPropertyName("handling_unit")]
        public string? HandlingUnit { get; set; }

        [JsonPropertyName("item")]
        public ItemUpsertDto? Item { get; set; }
    }

    private sealed class ItemUpsertDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("barcode")]
        public string? Barcode { get; set; }

        [JsonPropertyName("gtin")]
        public string? Gtin { get; set; }

        [JsonPropertyName("base_uom")]
        public string? BaseUom { get; set; }
    }

    private sealed class ItemUpsertEvent
    {
        public string EventId { get; init; } = string.Empty;
        public DateTime Timestamp { get; init; }
        public string DeviceId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string? Barcode { get; init; }
        public string? Gtin { get; init; }
        public string? BaseUom { get; init; }
    }
}
