using System.Globalization;
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

        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!TryParseEvent(line, out var importEvent, out var errorReason))
            {
                _data.AddImportError(new ImportError
                {
                    EventId = importEvent?.EventId,
                    Reason = errorReason ?? ReasonInvalidJson,
                    RawJson = line,
                    CreatedAt = DateTime.Now
                });
                result.Errors++;
                continue;
            }

            var outcome = ProcessEvent(importEvent!, line, filePath, allowErrorInsert: true, out var docCreated);
            switch (outcome)
            {
                case ImportOutcome.Imported:
                    result.Imported++;
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
                    OrderRef = NormalizeOrderRef(importEvent.OrderRef)
                });
                doc = store.GetDoc(docId);
                created = true;
            }
            else
            {
                TryUpdateDocHeader(store, doc, importEvent);
            }

            if (doc == null)
            {
                outcome = ImportOutcome.Error;
                return;
            }

            store.AddDocLine(new DocLine
            {
                DocId = doc.Id,
                ItemId = item.Id,
                Qty = importEvent.Qty,
                FromLocationId = fromLocation?.Id,
                ToLocationId = toLocation?.Id
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

    private static void TryUpdateDocHeader(IDataStore store, Doc doc, ImportEvent importEvent)
    {
        if (doc.Status != DocStatus.Draft)
        {
            return;
        }

        var partnerId = doc.PartnerId ?? ResolvePartnerId(store, importEvent);
        var orderRef = doc.OrderRef ?? NormalizeOrderRef(importEvent.OrderRef);

        if (partnerId == doc.PartnerId && string.Equals(orderRef, doc.OrderRef, StringComparison.Ordinal))
        {
            return;
        }

        store.UpdateDocHeader(doc.Id, partnerId, orderRef, doc.ShippingRef);
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

        importEvent = new ImportEvent
        {
            EventId = dto.EventId.Trim(),
            Timestamp = timestamp,
            DeviceId = dto.DeviceId?.Trim() ?? string.Empty,
            Type = docType.Value,
            DocRef = dto.DocRef?.Trim() ?? string.Empty,
            Barcode = dto.Barcode.Trim(),
            Qty = dto.Qty.Value,
            FromLocation = NormalizeLocationCode(dto.From),
            ToLocation = NormalizeLocationCode(dto.To),
            PartnerId = dto.PartnerId,
            PartnerCode = partnerCode,
            OrderRef = dto.OrderRef?.Trim(),
            ReasonCode = dto.ReasonCode?.Trim()
        };

        return true;
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

    private enum ImportOutcome
    {
        Imported,
        Duplicate,
        Error
    }

    private sealed class ImportEventDto
    {
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

        [JsonPropertyName("from")]
        public string? From { get; set; }

        [JsonPropertyName("to")]
        public string? To { get; set; }

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
    }
}
