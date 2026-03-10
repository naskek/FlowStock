using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class DocumentDraftEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/docs", HandleCreateAsync);
        app.MapPost("/api/docs/{docUid}/lines", HandleAddLineAsync);
    }

    public static async Task<IResult> HandleCreateAsync(
        HttpRequest request,
        IDataStore store,
        DocumentService docs,
        IApiDocStore apiStore)
    {
        var rawJson = await ReadBody(request);
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return Results.BadRequest(new ApiResult(false, "EMPTY_BODY"));
        }

        CreateDocRequest? createRequest;
        try
        {
            createRequest = JsonSerializer.Deserialize<CreateDocRequest>(
                rawJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
        }

        if (createRequest == null)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
        }

        var draftOnly = createRequest.DraftOnly;

        var docUid = string.IsNullOrWhiteSpace(createRequest.DocUid) ? null : createRequest.DocUid.Trim();
        if (string.IsNullOrWhiteSpace(docUid))
        {
            return Results.BadRequest(new ApiResult(false, "MISSING_DOC_UID"));
        }

        if (string.IsNullOrWhiteSpace(createRequest.EventId))
        {
            return Results.BadRequest(new ApiResult(false, "MISSING_EVENT_ID"));
        }

        var docType = ParseDocType(createRequest.Type);
        if (docType == null || docType is not (DocType.Inbound or DocType.Outbound or DocType.Move or DocType.Inventory or DocType.WriteOff or DocType.ProductionReceipt))
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_TYPE"));
        }

        var requestedOrderId = createRequest.OrderId;
        var requestedOrderRef = string.IsNullOrWhiteSpace(createRequest.OrderRef) ? null : createRequest.OrderRef.Trim();
        Order? requestedOrder = null;
        if (requestedOrderId.HasValue)
        {
            requestedOrder = store.GetOrder(requestedOrderId.Value);
            if (requestedOrder == null)
            {
                return Results.BadRequest(new ApiResult(false, "UNKNOWN_ORDER"));
            }
        }
        else if (!string.IsNullOrWhiteSpace(requestedOrderRef))
        {
            requestedOrder = store.GetOrders()
                .FirstOrDefault(order => string.Equals(order.OrderRef, requestedOrderRef, StringComparison.OrdinalIgnoreCase));
            if (requestedOrder != null)
            {
                requestedOrderId = requestedOrder.Id;
                requestedOrderRef = requestedOrder.OrderRef;
            }
        }

        if (requestedOrder != null
            && docType == DocType.Outbound
            && requestedOrder.Type != OrderType.Customer)
        {
            return Results.BadRequest(new ApiResult(false, "INTERNAL_ORDER_NOT_ALLOWED_FOR_OUTBOUND"));
        }

        var existingEvent = apiStore.GetEvent(createRequest.EventId);
        if (existingEvent != null)
        {
            if (string.Equals(existingEvent.EventType, "DOC_CREATE", StringComparison.OrdinalIgnoreCase)
                && string.Equals(existingEvent.DocUid, docUid, StringComparison.OrdinalIgnoreCase))
            {
                var existingDoc = apiStore.GetApiDoc(docUid);
                if (existingDoc != null)
                {
                    return Results.Ok(new
                    {
                        ok = true,
                        doc = new
                        {
                            id = existingDoc.DocId,
                            doc_uid = docUid,
                            doc_ref = existingDoc.DocRef,
                            status = existingDoc.Status,
                            type = existingDoc.DocType
                        }
                    });
                }

                return Results.Ok(new ApiResult(true));
            }

            return Results.BadRequest(new ApiResult(false, "EVENT_ID_CONFLICT"));
        }

        var existingDocInfo = apiStore.GetApiDoc(docUid);
        if (existingDocInfo != null)
        {
            var existingDoc = store.GetDoc(existingDocInfo.DocId);
            if (existingDoc == null)
            {
                return Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND"));
            }

            var expectedType = DocTypeMapper.ToOpString(docType.Value);
            if (!string.IsNullOrWhiteSpace(existingDocInfo.DocType)
                && !string.Equals(existingDocInfo.DocType, expectedType, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID"));
            }

            var requestedRef = string.IsNullOrWhiteSpace(createRequest.DocRef) ? null : createRequest.DocRef.Trim();
            if (!string.IsNullOrWhiteSpace(requestedRef)
                && !string.Equals(existingDocInfo.DocRef, requestedRef, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID"));
            }

            var partnerId = existingDocInfo.PartnerId;
            var fromLocationId = existingDocInfo.FromLocationId;
            var toLocationId = existingDocInfo.ToLocationId;
            var fromHu = existingDocInfo.FromHu;
            var toHu = existingDocInfo.ToHu;
            var updated = false;

            if (requestedOrderId.HasValue)
            {
                if (existingDoc.OrderId.HasValue && existingDoc.OrderId.Value != requestedOrderId.Value)
                {
                    return Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID"));
                }

                if (!existingDoc.OrderId.HasValue)
                {
                    store.UpdateDocOrder(existingDocInfo.DocId, requestedOrderId.Value, requestedOrder?.OrderRef);
                    updated = true;
                }

                if (requestedOrder != null && !partnerId.HasValue)
                {
                    partnerId = requestedOrder.PartnerId;
                    updated = true;
                }
            }

            if (createRequest.PartnerId.HasValue)
            {
                var requestedPartnerId = createRequest.PartnerId.Value;
                if (partnerId.HasValue && partnerId.Value != requestedPartnerId)
                {
                    return Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID"));
                }

                if (!partnerId.HasValue)
                {
                    if (store.GetPartner(requestedPartnerId) == null)
                    {
                        return Results.BadRequest(new ApiResult(false, "UNKNOWN_PARTNER"));
                    }

                    partnerId = requestedPartnerId;
                    updated = true;
                }
            }

            if (createRequest.FromLocationId.HasValue)
            {
                var requestedFrom = createRequest.FromLocationId.Value;
                if (fromLocationId.HasValue && fromLocationId.Value != requestedFrom)
                {
                    return Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID"));
                }

                if (!fromLocationId.HasValue)
                {
                    if (store.FindLocationById(requestedFrom) == null)
                    {
                        return Results.BadRequest(new ApiResult(false, "UNKNOWN_LOCATION"));
                    }

                    fromLocationId = requestedFrom;
                    updated = true;
                }
            }

            if (createRequest.ToLocationId.HasValue)
            {
                var requestedTo = createRequest.ToLocationId.Value;
                if (toLocationId.HasValue && toLocationId.Value != requestedTo)
                {
                    return Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID"));
                }

                if (!toLocationId.HasValue)
                {
                    if (store.FindLocationById(requestedTo) == null)
                    {
                        return Results.BadRequest(new ApiResult(false, "UNKNOWN_LOCATION"));
                    }

                    toLocationId = requestedTo;
                    updated = true;
                }
            }

            var requestedFromHu = NormalizeHu(createRequest.FromHu);
            var requestedToHu = NormalizeHu(createRequest.ToHu);
            var missingHu = new List<string>();

            if (!string.IsNullOrWhiteSpace(requestedFromHu))
            {
                if (!string.IsNullOrWhiteSpace(fromHu)
                    && !string.Equals(fromHu, requestedFromHu, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID"));
                }

                if (string.IsNullOrWhiteSpace(fromHu))
                {
                    var record = store.GetHuByCode(requestedFromHu);
                    if (record == null || !IsHuAllowed(record))
                    {
                        missingHu.Add("from_hu");
                    }
                    else
                    {
                        fromHu = requestedFromHu;
                        updated = true;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(requestedToHu))
            {
                if (!string.IsNullOrWhiteSpace(toHu)
                    && !string.Equals(toHu, requestedToHu, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID"));
                }

                if (string.IsNullOrWhiteSpace(toHu))
                {
                    var record = store.GetHuByCode(requestedToHu);
                    if (record == null || !IsHuAllowed(record))
                    {
                        missingHu.Add("to_hu");
                    }
                    else
                    {
                        toHu = requestedToHu;
                        updated = true;
                    }
                }
            }

            if (missingHu.Count > 0)
            {
                return Results.BadRequest(new
                {
                    ok = false,
                    error = "UNKNOWN_HU",
                    missing = missingHu
                });
            }

            if (!draftOnly)
            {
                if ((docType == DocType.Inbound || docType == DocType.Outbound) && !partnerId.HasValue)
                {
                    return Results.BadRequest(new ApiResult(false, "MISSING_PARTNER"));
                }

                if (docType == DocType.Move || docType == DocType.Outbound || docType == DocType.WriteOff)
                {
                    if (!fromLocationId.HasValue)
                    {
                        return Results.BadRequest(new ApiResult(false, "MISSING_LOCATION"));
                    }
                }

                if (docType == DocType.Move || docType == DocType.Inbound || docType == DocType.Inventory || docType == DocType.ProductionReceipt)
                {
                    if (!toLocationId.HasValue)
                    {
                        return Results.BadRequest(new ApiResult(false, "MISSING_LOCATION"));
                    }
                }
            }

            if (updated)
            {
                if (partnerId.HasValue && existingDocInfo.PartnerId != partnerId)
                {
                    var doc = store.GetDoc(existingDocInfo.DocId);
                    if (doc != null && doc.Status == DocStatus.Draft)
                    {
                        store.UpdateDocHeader(existingDocInfo.DocId, partnerId, doc.OrderRef, doc.ShippingRef);
                    }
                }

                apiStore.UpdateApiDocHeader(docUid, partnerId, fromLocationId, toLocationId, fromHu, toHu);
            }

            var resolvedShippingRef = ResolveDocShippingRef(docType.Value, fromHu, toHu);
            UpdateDocShippingRefIfNeeded(store, existingDocInfo.DocId, resolvedShippingRef);

            if (!string.IsNullOrWhiteSpace(createRequest.Comment))
            {
                var cleanedComment = createRequest.Comment.Trim();
                var existingRecount = IsRecountComment(existingDoc.Comment);
                var incomingRecount = IsRecountComment(cleanedComment);
                if (existingDoc.Status == DocStatus.Draft
                    && !existingRecount
                    && !string.Equals(existingDoc.Comment ?? string.Empty, cleanedComment, StringComparison.Ordinal))
                {
                    store.UpdateDocComment(existingDocInfo.DocId, cleanedComment);
                }
                else if (existingDoc.Status == DocStatus.Draft
                         && existingRecount
                         && incomingRecount
                         && !string.Equals(existingDoc.Comment ?? string.Empty, cleanedComment, StringComparison.Ordinal))
                {
                    store.UpdateDocComment(existingDocInfo.DocId, cleanedComment);
                }
            }

            if (docType == DocType.WriteOff && !string.IsNullOrWhiteSpace(createRequest.ReasonCode))
            {
                var cleanedReason = createRequest.ReasonCode.Trim();
                if (existingDoc.Status == DocStatus.Draft
                    && !string.Equals(existingDoc.ReasonCode ?? string.Empty, cleanedReason, StringComparison.OrdinalIgnoreCase))
                {
                    store.UpdateDocReason(existingDocInfo.DocId, cleanedReason);
                }
            }

            return Results.Ok(new
            {
                ok = true,
                doc = new
                {
                    id = existingDocInfo.DocId,
                    doc_uid = docUid,
                    doc_ref = existingDocInfo.DocRef,
                    status = existingDocInfo.Status,
                    type = existingDocInfo.DocType
                }
            });
        }

        var partnerIdValue = createRequest.PartnerId;
        if (requestedOrder != null)
        {
            if (partnerIdValue.HasValue && partnerIdValue.Value != requestedOrder.PartnerId)
            {
                return Results.BadRequest(new ApiResult(false, "ORDER_PARTNER_MISMATCH"));
            }

            partnerIdValue = requestedOrder.PartnerId;
        }

        if (partnerIdValue.HasValue && store.GetPartner(partnerIdValue.Value) == null)
        {
            return Results.BadRequest(new ApiResult(false, "UNKNOWN_PARTNER"));
        }

        if (!draftOnly && (docType == DocType.Inbound || docType == DocType.Outbound))
        {
            if (!partnerIdValue.HasValue)
            {
                return Results.BadRequest(new ApiResult(false, "MISSING_PARTNER"));
            }
        }

        var fromLocationIdValue = createRequest.FromLocationId;
        var toLocationIdValue = createRequest.ToLocationId;
        if (fromLocationIdValue.HasValue && store.FindLocationById(fromLocationIdValue.Value) == null)
        {
            return Results.BadRequest(new ApiResult(false, "UNKNOWN_LOCATION"));
        }

        if (toLocationIdValue.HasValue && store.FindLocationById(toLocationIdValue.Value) == null)
        {
            return Results.BadRequest(new ApiResult(false, "UNKNOWN_LOCATION"));
        }

        if (!draftOnly && (docType == DocType.Move || docType == DocType.Outbound || docType == DocType.WriteOff))
        {
            if (!fromLocationIdValue.HasValue)
            {
                return Results.BadRequest(new ApiResult(false, "MISSING_LOCATION"));
            }
        }

        if (!draftOnly && (docType == DocType.Move || docType == DocType.Inbound || docType == DocType.Inventory || docType == DocType.ProductionReceipt))
        {
            if (!toLocationIdValue.HasValue)
            {
                return Results.BadRequest(new ApiResult(false, "MISSING_LOCATION"));
            }
        }

        var normalizedFromHu = NormalizeHu(createRequest.FromHu);
        var normalizedToHu = NormalizeHu(createRequest.ToHu);

        var missingHuNew = new List<string>();
        if (!string.IsNullOrWhiteSpace(normalizedFromHu))
        {
            var record = store.GetHuByCode(normalizedFromHu);
            if (record == null || !IsHuAllowed(record))
            {
                missingHuNew.Add("from_hu");
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedToHu))
        {
            var record = store.GetHuByCode(normalizedToHu);
            if (record == null || !IsHuAllowed(record))
            {
                missingHuNew.Add("to_hu");
            }
        }

        if (missingHuNew.Count > 0)
        {
            return Results.BadRequest(new
            {
                ok = false,
                error = "UNKNOWN_HU",
                missing = missingHuNew
            });
        }

        var resolvedShippingRefNew = ResolveDocShippingRef(docType.Value, normalizedFromHu, normalizedToHu);
        var requestedRefValue = string.IsNullOrWhiteSpace(createRequest.DocRef) ? null : createRequest.DocRef.Trim();
        var docRef = requestedRefValue;
        if (string.IsNullOrWhiteSpace(docRef))
        {
            docRef = docs.GenerateDocRef(docType.Value, DateTime.Now);
        }
        else if (store.FindDocByRef(docRef) != null)
        {
            docRef = docs.GenerateDocRef(docType.Value, DateTime.Now);
        }

        var comment = string.IsNullOrWhiteSpace(createRequest.Comment) ? null : createRequest.Comment.Trim();
        var reasonCode = string.IsNullOrWhiteSpace(createRequest.ReasonCode) ? null : createRequest.ReasonCode.Trim();

        long docId;
        try
        {
            docId = docs.CreateDoc(docType.Value, docRef, comment, partnerIdValue, requestedOrderRef, resolvedShippingRefNew, requestedOrderId);
        }
        catch (ArgumentException ex) when (string.Equals(ex.ParamName, "docRef", StringComparison.Ordinal))
        {
            docRef = docs.GenerateDocRef(docType.Value, DateTime.Now);
            try
            {
                docId = docs.CreateDoc(docType.Value, docRef, comment, partnerIdValue, requestedOrderRef, resolvedShippingRefNew, requestedOrderId);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new ApiResult(false, "DOC_REF_EXISTS"));
            }
        }

        if (docType == DocType.WriteOff && !string.IsNullOrWhiteSpace(reasonCode))
        {
            docs.UpdateDocReason(docId, reasonCode);
        }

        apiStore.AddApiDoc(
            docUid,
            docId,
            "DRAFT",
            DocTypeMapper.ToOpString(docType.Value),
            docRef,
            partnerIdValue,
            fromLocationIdValue,
            toLocationIdValue,
            normalizedFromHu,
            normalizedToHu,
            createRequest.DeviceId);

        apiStore.RecordEvent(createRequest.EventId, "DOC_CREATE", docUid, createRequest.DeviceId, rawJson);

        var docRefChanged = !string.IsNullOrWhiteSpace(requestedRefValue)
                            && !string.Equals(requestedRefValue, docRef, StringComparison.OrdinalIgnoreCase);

        return Results.Ok(new
        {
            ok = true,
            doc = new
            {
                id = docId,
                doc_uid = docUid,
                doc_ref = docRef,
                status = "DRAFT",
                type = DocTypeMapper.ToOpString(docType.Value),
                doc_ref_changed = docRefChanged
            }
        });
    }

    public static async Task<IResult> HandleAddLineAsync(
        string docUid,
        HttpRequest request,
        IDataStore store,
        DocumentService docs,
        IApiDocStore apiStore)
    {
        var rawJson = await ReadBody(request);
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return Results.BadRequest(new ApiResult(false, "EMPTY_BODY"));
        }

        AddDocLineRequest? lineRequest;
        try
        {
            lineRequest = JsonSerializer.Deserialize<AddDocLineRequest>(
                rawJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
        }

        if (lineRequest == null)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
        }

        if (string.IsNullOrWhiteSpace(lineRequest.EventId))
        {
            return Results.BadRequest(new ApiResult(false, "MISSING_EVENT_ID"));
        }

        var existingEvent = apiStore.GetEvent(lineRequest.EventId);
        if (existingEvent != null)
        {
            if (string.Equals(existingEvent.EventType, "DOC_LINE", StringComparison.OrdinalIgnoreCase)
                && string.Equals(existingEvent.DocUid, docUid, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Ok(new ApiResult(true));
            }

            return Results.BadRequest(new ApiResult(false, "EVENT_ID_CONFLICT"));
        }

        var docInfo = apiStore.GetApiDoc(docUid);
        if (docInfo == null)
        {
            return Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND"));
        }

        var existingDoc = store.GetDoc(docInfo.DocId);
        if (existingDoc == null)
        {
            return Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND"));
        }

        if (IsRecountComment(existingDoc.Comment))
        {
            if (existingDoc.Status != DocStatus.Draft)
            {
                return Results.BadRequest(new ApiResult(false, "DOC_NOT_DRAFT"));
            }

            store.DeleteDocLines(docInfo.DocId);
            store.UpdateDocComment(docInfo.DocId, "TSD");
        }

        if (!string.Equals(docInfo.Status, "DRAFT", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new ApiResult(false, "DOC_NOT_DRAFT"));
        }

        if (lineRequest.Qty <= 0)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_QTY"));
        }

        var docType = ParseDocType(docInfo.DocType);
        if (docType == null)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_TYPE"));
        }

        var requestedFromHu = NormalizeHu(lineRequest.FromHu);
        var requestedToHu = NormalizeHu(lineRequest.ToHu);
        var missingHu = new List<string>();
        if (!string.IsNullOrWhiteSpace(requestedFromHu))
        {
            var record = store.GetHuByCode(requestedFromHu);
            if (record == null || !IsHuAllowed(record))
            {
                missingHu.Add("from_hu");
            }
        }

        if (!string.IsNullOrWhiteSpace(requestedToHu))
        {
            var record = store.GetHuByCode(requestedToHu);
            if (record == null || !IsHuAllowed(record))
            {
                missingHu.Add("to_hu");
            }
        }

        if (missingHu.Count > 0)
        {
            return Results.BadRequest(new
            {
                ok = false,
                error = "UNKNOWN_HU",
                missing = missingHu
            });
        }

        Item? item = null;
        if (lineRequest.ItemId.HasValue)
        {
            item = store.FindItemById(lineRequest.ItemId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(lineRequest.Barcode))
        {
            var barcode = lineRequest.Barcode.Trim();
            item = store.FindItemByBarcode(barcode) ?? FindItemByBarcodeVariant(store, barcode);
        }

        if (item == null)
        {
            return Results.BadRequest(new ApiResult(false, "UNKNOWN_ITEM"));
        }

        long? fromLocationId = null;
        long? toLocationId = null;
        string? fromHu = null;
        string? toHu = null;

        switch (docType.Value)
        {
            case DocType.Inbound:
                toLocationId = docInfo.ToLocationId;
                toHu = NormalizeHu(docInfo.ToHu);
                if (!toLocationId.HasValue)
                {
                    return Results.BadRequest(new ApiResult(false, "MISSING_LOCATION"));
                }
                break;
            case DocType.ProductionReceipt:
                toLocationId = docInfo.ToLocationId;
                toHu = NormalizeHu(docInfo.ToHu);
                if (!toLocationId.HasValue)
                {
                    return Results.BadRequest(new ApiResult(false, "MISSING_LOCATION"));
                }
                break;
            case DocType.Outbound:
                if (lineRequest.FromLocationId.HasValue && store.FindLocationById(lineRequest.FromLocationId.Value) == null)
                {
                    return Results.BadRequest(new ApiResult(false, "UNKNOWN_LOCATION"));
                }

                fromLocationId = lineRequest.FromLocationId ?? docInfo.FromLocationId;
                fromHu = !string.IsNullOrWhiteSpace(requestedFromHu)
                    ? requestedFromHu
                    : NormalizeHu(docInfo.FromHu);
                if (!fromLocationId.HasValue)
                {
                    return Results.BadRequest(new ApiResult(false, "MISSING_LOCATION"));
                }
                break;
            case DocType.Move:
                fromLocationId = docInfo.FromLocationId;
                toLocationId = docInfo.ToLocationId;
                fromHu = NormalizeHu(docInfo.FromHu);
                toHu = NormalizeHu(docInfo.ToHu);
                if (!fromLocationId.HasValue || !toLocationId.HasValue)
                {
                    return Results.BadRequest(new ApiResult(false, "MISSING_LOCATION"));
                }
                break;
            case DocType.WriteOff:
                fromLocationId = docInfo.FromLocationId;
                fromHu = !string.IsNullOrWhiteSpace(requestedFromHu)
                    ? requestedFromHu
                    : NormalizeHu(docInfo.FromHu);
                if (!fromLocationId.HasValue)
                {
                    return Results.BadRequest(new ApiResult(false, "MISSING_LOCATION"));
                }
                break;
            case DocType.Inventory:
                toLocationId = docInfo.ToLocationId;
                toHu = !string.IsNullOrWhiteSpace(requestedToHu)
                    ? requestedToHu
                    : NormalizeHu(docInfo.ToHu);
                if (!toLocationId.HasValue)
                {
                    return Results.BadRequest(new ApiResult(false, "MISSING_LOCATION"));
                }
                break;
        }

        try
        {
            docs.AddDocLine(
                docInfo.DocId,
                item.Id,
                lineRequest.Qty,
                fromLocationId,
                toLocationId,
                null,
                lineRequest.UomCode,
                fromHu,
                toHu,
                lineRequest.OrderLineId);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new ApiResult(false, ex.Message));
        }

        apiStore.RecordEvent(lineRequest.EventId, "DOC_LINE", docUid, lineRequest.DeviceId, rawJson);

        var lastLine = store.GetDocLines(docInfo.DocId)
            .Where(line => line.ItemId == item.Id)
            .OrderByDescending(line => line.Id)
            .FirstOrDefault();

        return Results.Ok(new
        {
            ok = true,
            line = lastLine == null
                ? null
                : new
                {
                    id = lastLine.Id,
                    item_id = lastLine.ItemId,
                    qty = lastLine.Qty,
                    uom_code = lastLine.UomCode
                }
        });
    }

    private static async Task<string> ReadBody(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        return await reader.ReadToEndAsync();
    }

    private static DocType? ParseDocType(string? value)
    {
        return DocTypeMapper.FromOpString(value);
    }

    private static bool IsRecountComment(string? comment)
    {
        return !string.IsNullOrWhiteSpace(comment)
               && comment.IndexOf("RECOUNT", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsHuAllowed(HuRecord record)
    {
        return string.Equals(record.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase)
               || string.Equals(record.Status, "OPEN", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeHu(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? ResolveDocShippingRef(DocType type, string? fromHu, string? toHu)
    {
        return type switch
        {
            DocType.Inbound => NormalizeHu(toHu),
            DocType.Inventory => NormalizeHu(toHu),
            DocType.ProductionReceipt => NormalizeHu(toHu),
            DocType.Outbound => NormalizeHu(fromHu),
            DocType.WriteOff => NormalizeHu(fromHu),
            DocType.Move => NormalizeHu(toHu),
            _ => null
        };
    }

    private static void UpdateDocShippingRefIfNeeded(IDataStore store, long docId, string? shippingRef)
    {
        var doc = store.GetDoc(docId);
        if (doc == null)
        {
            return;
        }

        var normalizedCurrent = NormalizeHu(doc.ShippingRef);
        var normalizedTarget = NormalizeHu(shippingRef);
        if (string.Equals(normalizedCurrent, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (doc.Status != DocStatus.Draft)
        {
            return;
        }

        store.UpdateDocHeader(docId, doc.PartnerId, doc.OrderRef, normalizedTarget);
    }

    private static Item? FindItemByBarcodeVariant(IDataStore store, string barcode)
    {
        if (barcode.Length == 13)
        {
            return store.FindItemByBarcode("0" + barcode);
        }

        if (barcode.Length == 14 && barcode.StartsWith("0", StringComparison.Ordinal))
        {
            return store.FindItemByBarcode(barcode.Substring(1));
        }

        return null;
    }
}
