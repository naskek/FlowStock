using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        app.MapPost("/api/docs/{docUid}/lines/update", HandleUpdateLineAsync);
        app.MapPost("/api/docs/{docUid}/lines/delete", HandleDeleteLineAsync);
    }

    public static async Task<IResult> HandleCreateAsync(
        HttpRequest request,
        IDataStore store,
        DocumentService docs,
        IApiDocStore apiStore,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("FlowStock.Server.DocumentLifecycle");
        var started = Stopwatch.StartNew();
        var path = request.Path.Value ?? "/api/docs";

        IResult LogCreateAndReturn(
            IResult result,
            LogLevel level,
            string outcome,
            string? docUid = null,
            long? docId = null,
            string? docRef = null,
            string? docType = null,
            string? docStatusBefore = null,
            string? docStatusAfter = null,
            int? lineCount = null,
            bool? apiEventWritten = null,
            bool? idempotentReplay = null,
            IEnumerable<string>? errors = null,
            string? eventId = null,
            string? deviceId = null)
        {
            started.Stop();
            ServerOperationLogging.LogDocumentLifecycleOperation(
                logger,
                level,
                operation: "CreateDocDraft",
                path: path,
                result: outcome,
                docUid: docUid,
                docId: docId,
                docRef: docRef,
                docType: docType,
                docStatusBefore: docStatusBefore,
                docStatusAfter: docStatusAfter,
                lineCount: lineCount,
                ledgerRowsWritten: 0,
                eventId: eventId,
                deviceId: deviceId,
                apiEventWritten: apiEventWritten,
                idempotentReplay: idempotentReplay,
                elapsedMs: started.ElapsedMilliseconds,
                errors: errors);
            return result;
        }

        var rawJson = await ReadBody(request);
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return LogCreateAndReturn(
                Results.BadRequest(new ApiResult(false, "EMPTY_BODY")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["EMPTY_BODY"]);
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
            return LogCreateAndReturn(
                Results.BadRequest(new ApiResult(false, "INVALID_JSON")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["INVALID_JSON"]);
        }

        if (createRequest == null)
        {
            return LogCreateAndReturn(
                Results.BadRequest(new ApiResult(false, "INVALID_JSON")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["INVALID_JSON"]);
        }

        var draftOnly = createRequest.DraftOnly;

        var docUid = string.IsNullOrWhiteSpace(createRequest.DocUid) ? null : createRequest.DocUid.Trim();
        if (string.IsNullOrWhiteSpace(docUid))
        {
            return LogCreateAndReturn(
                Results.BadRequest(new ApiResult(false, "MISSING_DOC_UID")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["MISSING_DOC_UID"],
                eventId: createRequest.EventId,
                deviceId: createRequest.DeviceId);
        }

        if (string.IsNullOrWhiteSpace(createRequest.EventId))
        {
            return LogCreateAndReturn(
                Results.BadRequest(new ApiResult(false, "MISSING_EVENT_ID")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docUid: docUid,
                errors: ["MISSING_EVENT_ID"],
                deviceId: createRequest.DeviceId);
        }

        var docType = ParseDocType(createRequest.Type);
        if (docType == null || docType is not (DocType.Inbound or DocType.Outbound or DocType.Move or DocType.Inventory or DocType.WriteOff or DocType.ProductionReceipt))
        {
            return LogCreateAndReturn(
                Results.BadRequest(new ApiResult(false, "INVALID_TYPE")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docUid: docUid,
                errors: ["INVALID_TYPE"],
                eventId: createRequest.EventId,
                deviceId: createRequest.DeviceId);
        }

        var docTypeValue = DocTypeMapper.ToOpString(docType.Value);

        var requestedOrderId = createRequest.OrderId;
        var requestedOrderRef = string.IsNullOrWhiteSpace(createRequest.OrderRef) ? null : createRequest.OrderRef.Trim();
        Order? requestedOrder = null;
        if (requestedOrderId.HasValue)
        {
            requestedOrder = store.GetOrder(requestedOrderId.Value);
            if (requestedOrder == null)
            {
                return LogCreateAndReturn(
                    Results.BadRequest(new ApiResult(false, "UNKNOWN_ORDER")),
                    LogLevel.Warning,
                    outcome: "VALIDATION_FAILED",
                    docUid: docUid,
                    docType: docTypeValue,
                    errors: ["UNKNOWN_ORDER"],
                    eventId: createRequest.EventId,
                    deviceId: createRequest.DeviceId);
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
            return LogCreateAndReturn(
                Results.BadRequest(new ApiResult(false, "INTERNAL_ORDER_NOT_ALLOWED_FOR_OUTBOUND")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docUid: docUid,
                docType: docTypeValue,
                errors: ["INTERNAL_ORDER_NOT_ALLOWED_FOR_OUTBOUND"],
                eventId: createRequest.EventId,
                deviceId: createRequest.DeviceId);
        }

        var existingEvent = apiStore.GetEvent(createRequest.EventId);
        if (existingEvent != null)
        {
            if (string.Equals(existingEvent.EventType, "DOC_CREATE", StringComparison.OrdinalIgnoreCase)
                && string.Equals(existingEvent.DocUid, docUid, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsEquivalentCreateReplay(existingEvent.RawJson, createRequest))
                {
                    return LogCreateAndReturn(
                        Results.BadRequest(new ApiResult(false, "EVENT_ID_CONFLICT")),
                        LogLevel.Warning,
                        outcome: "EVENT_ID_CONFLICT",
                        docUid: docUid,
                        docType: docTypeValue,
                        errors: ["EVENT_ID_CONFLICT"],
                        eventId: createRequest.EventId,
                        deviceId: createRequest.DeviceId);
                }

                var existingDoc = apiStore.GetApiDoc(docUid);
                if (existingDoc != null)
                {
                    var replayDoc = store.GetDoc(existingDoc.DocId);
                    return LogCreateAndReturn(
                        Results.Ok(new
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
                        }),
                        LogLevel.Information,
                        outcome: "IDEMPOTENT_REPLAY",
                        docUid: docUid,
                        docId: existingDoc.DocId,
                        docRef: existingDoc.DocRef,
                        docType: existingDoc.DocType,
                        docStatusBefore: replayDoc == null ? existingDoc.Status : DocTypeMapper.StatusToString(replayDoc.Status),
                        docStatusAfter: replayDoc == null ? existingDoc.Status : DocTypeMapper.StatusToString(replayDoc.Status),
                        lineCount: replayDoc?.LineCount,
                        apiEventWritten: false,
                        idempotentReplay: true,
                        eventId: createRequest.EventId,
                        deviceId: createRequest.DeviceId);
                }

                return LogCreateAndReturn(
                    Results.Ok(new ApiResult(true)),
                    LogLevel.Information,
                    outcome: "IDEMPOTENT_REPLAY",
                    docUid: docUid,
                    docType: docTypeValue,
                    apiEventWritten: false,
                    idempotentReplay: true,
                    eventId: createRequest.EventId,
                    deviceId: createRequest.DeviceId);
            }

            return LogCreateAndReturn(
                Results.BadRequest(new ApiResult(false, "EVENT_ID_CONFLICT")),
                LogLevel.Warning,
                outcome: "EVENT_ID_CONFLICT",
                docUid: docUid,
                docType: docTypeValue,
                errors: ["EVENT_ID_CONFLICT"],
                eventId: createRequest.EventId,
                deviceId: createRequest.DeviceId);
        }

        var existingDocInfo = apiStore.GetApiDoc(docUid);
        if (existingDocInfo != null)
        {
            var existingDoc = store.GetDoc(existingDocInfo.DocId);
            if (existingDoc == null)
            {
                return LogCreateAndReturn(
                    Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND")),
                    LogLevel.Warning,
                    outcome: "DOC_NOT_FOUND",
                    docUid: docUid,
                    docId: existingDocInfo.DocId,
                    docRef: existingDocInfo.DocRef,
                    docType: existingDocInfo.DocType,
                    docStatusBefore: existingDocInfo.Status,
                    errors: ["DOC_NOT_FOUND"],
                    eventId: createRequest.EventId,
                    deviceId: createRequest.DeviceId);
            }

            var expectedType = DocTypeMapper.ToOpString(docType.Value);
            if (!string.IsNullOrWhiteSpace(existingDocInfo.DocType)
                && !string.Equals(existingDocInfo.DocType, expectedType, StringComparison.OrdinalIgnoreCase))
            {
                return LogCreateAndReturn(
                    Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID")),
                    LogLevel.Warning,
                    outcome: "VALIDATION_FAILED",
                    docUid: docUid,
                    docId: existingDocInfo.DocId,
                    docRef: existingDocInfo.DocRef,
                    docType: existingDocInfo.DocType,
                    docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                    errors: ["DUPLICATE_DOC_UID"],
                    eventId: createRequest.EventId,
                    deviceId: createRequest.DeviceId);
            }

            var requestedRef = string.IsNullOrWhiteSpace(createRequest.DocRef) ? null : createRequest.DocRef.Trim();
            if (!string.IsNullOrWhiteSpace(requestedRef)
                && !string.Equals(existingDocInfo.DocRef, requestedRef, StringComparison.OrdinalIgnoreCase))
            {
                return LogCreateAndReturn(
                    Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID")),
                    LogLevel.Warning,
                    outcome: "VALIDATION_FAILED",
                    docUid: docUid,
                    docId: existingDocInfo.DocId,
                    docRef: existingDocInfo.DocRef,
                    docType: existingDocInfo.DocType,
                    docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                    errors: ["DUPLICATE_DOC_UID"],
                    eventId: createRequest.EventId,
                    deviceId: createRequest.DeviceId);
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
                    return LogCreateAndReturn(
                        Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID")),
                        LogLevel.Warning,
                        outcome: "VALIDATION_FAILED",
                        docUid: docUid,
                        docId: existingDocInfo.DocId,
                        docRef: existingDocInfo.DocRef,
                        docType: existingDocInfo.DocType,
                        docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                        errors: ["DUPLICATE_DOC_UID"],
                        eventId: createRequest.EventId,
                        deviceId: createRequest.DeviceId);
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
                    return LogCreateAndReturn(
                        Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID")),
                        LogLevel.Warning,
                        outcome: "VALIDATION_FAILED",
                        docUid: docUid,
                        docId: existingDocInfo.DocId,
                        docRef: existingDocInfo.DocRef,
                        docType: existingDocInfo.DocType,
                        docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                        errors: ["DUPLICATE_DOC_UID"],
                        eventId: createRequest.EventId,
                        deviceId: createRequest.DeviceId);
                }

                if (!partnerId.HasValue)
                {
                    if (store.GetPartner(requestedPartnerId) == null)
                    {
                        return LogCreateAndReturn(
                            Results.BadRequest(new ApiResult(false, "UNKNOWN_PARTNER")),
                            LogLevel.Warning,
                            outcome: "VALIDATION_FAILED",
                            docUid: docUid,
                            docId: existingDocInfo.DocId,
                            docRef: existingDocInfo.DocRef,
                            docType: existingDocInfo.DocType,
                            docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                            errors: ["UNKNOWN_PARTNER"],
                            eventId: createRequest.EventId,
                            deviceId: createRequest.DeviceId);
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
                    return LogCreateAndReturn(
                        Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID")),
                        LogLevel.Warning,
                        outcome: "VALIDATION_FAILED",
                        docUid: docUid,
                        docId: existingDocInfo.DocId,
                        docRef: existingDocInfo.DocRef,
                        docType: existingDocInfo.DocType,
                        docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                        errors: ["DUPLICATE_DOC_UID"],
                        eventId: createRequest.EventId,
                        deviceId: createRequest.DeviceId);
                }

                if (!fromLocationId.HasValue)
                {
                    if (store.FindLocationById(requestedFrom) == null)
                    {
                        return LogCreateAndReturn(
                            Results.BadRequest(new ApiResult(false, "UNKNOWN_LOCATION")),
                            LogLevel.Warning,
                            outcome: "VALIDATION_FAILED",
                            docUid: docUid,
                            docId: existingDocInfo.DocId,
                            docRef: existingDocInfo.DocRef,
                            docType: existingDocInfo.DocType,
                            docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                            errors: ["UNKNOWN_LOCATION"],
                            eventId: createRequest.EventId,
                            deviceId: createRequest.DeviceId);
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
                    return LogCreateAndReturn(
                        Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID")),
                        LogLevel.Warning,
                        outcome: "VALIDATION_FAILED",
                        docUid: docUid,
                        docId: existingDocInfo.DocId,
                        docRef: existingDocInfo.DocRef,
                        docType: existingDocInfo.DocType,
                        docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                        errors: ["DUPLICATE_DOC_UID"],
                        eventId: createRequest.EventId,
                        deviceId: createRequest.DeviceId);
                }

                if (!toLocationId.HasValue)
                {
                    if (store.FindLocationById(requestedTo) == null)
                    {
                        return LogCreateAndReturn(
                            Results.BadRequest(new ApiResult(false, "UNKNOWN_LOCATION")),
                            LogLevel.Warning,
                            outcome: "VALIDATION_FAILED",
                            docUid: docUid,
                            docId: existingDocInfo.DocId,
                            docRef: existingDocInfo.DocRef,
                            docType: existingDocInfo.DocType,
                            docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                            errors: ["UNKNOWN_LOCATION"],
                            eventId: createRequest.EventId,
                            deviceId: createRequest.DeviceId);
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
                    return LogCreateAndReturn(
                        Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID")),
                        LogLevel.Warning,
                        outcome: "VALIDATION_FAILED",
                        docUid: docUid,
                        docId: existingDocInfo.DocId,
                        docRef: existingDocInfo.DocRef,
                        docType: existingDocInfo.DocType,
                        docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                        errors: ["DUPLICATE_DOC_UID"],
                        eventId: createRequest.EventId,
                        deviceId: createRequest.DeviceId);
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
                    return LogCreateAndReturn(
                        Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID")),
                        LogLevel.Warning,
                        outcome: "VALIDATION_FAILED",
                        docUid: docUid,
                        docId: existingDocInfo.DocId,
                        docRef: existingDocInfo.DocRef,
                        docType: existingDocInfo.DocType,
                        docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                        errors: ["DUPLICATE_DOC_UID"],
                        eventId: createRequest.EventId,
                        deviceId: createRequest.DeviceId);
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
                return LogCreateAndReturn(
                    Results.BadRequest(new
                    {
                        ok = false,
                        error = "UNKNOWN_HU",
                        missing = missingHu
                    }),
                    LogLevel.Warning,
                    outcome: "VALIDATION_FAILED",
                    docUid: docUid,
                    docId: existingDocInfo.DocId,
                    docRef: existingDocInfo.DocRef,
                    docType: existingDocInfo.DocType,
                    docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                    errors: ["UNKNOWN_HU", $"missing={string.Join(",", missingHu)}"],
                    eventId: createRequest.EventId,
                    deviceId: createRequest.DeviceId);
            }

            if (!draftOnly)
            {
                if ((docType == DocType.Inbound || docType == DocType.Outbound) && !partnerId.HasValue)
                {
                    return LogCreateAndReturn(
                        Results.BadRequest(new ApiResult(false, "MISSING_PARTNER")),
                        LogLevel.Warning,
                        outcome: "VALIDATION_FAILED",
                        docUid: docUid,
                        docId: existingDocInfo.DocId,
                        docRef: existingDocInfo.DocRef,
                        docType: existingDocInfo.DocType,
                        docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                        errors: ["MISSING_PARTNER"],
                        eventId: createRequest.EventId,
                        deviceId: createRequest.DeviceId);
                }

                if (docType == DocType.Move || docType == DocType.Outbound || docType == DocType.WriteOff)
                {
                    if (!fromLocationId.HasValue)
                    {
                        return LogCreateAndReturn(
                            Results.BadRequest(new ApiResult(false, "MISSING_LOCATION")),
                            LogLevel.Warning,
                            outcome: "VALIDATION_FAILED",
                            docUid: docUid,
                            docId: existingDocInfo.DocId,
                            docRef: existingDocInfo.DocRef,
                            docType: existingDocInfo.DocType,
                            docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                            errors: ["MISSING_LOCATION"],
                            eventId: createRequest.EventId,
                            deviceId: createRequest.DeviceId);
                    }
                }

                if (docType == DocType.Move || docType == DocType.Inbound || docType == DocType.Inventory || docType == DocType.ProductionReceipt)
                {
                    if (!toLocationId.HasValue)
                    {
                        return LogCreateAndReturn(
                            Results.BadRequest(new ApiResult(false, "MISSING_LOCATION")),
                            LogLevel.Warning,
                            outcome: "VALIDATION_FAILED",
                            docUid: docUid,
                            docId: existingDocInfo.DocId,
                            docRef: existingDocInfo.DocRef,
                            docType: existingDocInfo.DocType,
                            docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                            errors: ["MISSING_LOCATION"],
                            eventId: createRequest.EventId,
                            deviceId: createRequest.DeviceId);
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

            apiStore.RecordEvent(createRequest.EventId, "DOC_CREATE", docUid, createRequest.DeviceId, rawJson);

            var existingDocAfter = store.GetDoc(existingDocInfo.DocId) ?? existingDoc;
            return LogCreateAndReturn(
                Results.Ok(new
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
                }),
                LogLevel.Information,
                outcome: "UPSERTED",
                docUid: docUid,
                docId: existingDocInfo.DocId,
                docRef: existingDocInfo.DocRef,
                docType: existingDocInfo.DocType,
                docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                docStatusAfter: DocTypeMapper.StatusToString(existingDocAfter.Status),
                lineCount: existingDocAfter.LineCount,
                apiEventWritten: true,
                idempotentReplay: false,
                eventId: createRequest.EventId,
                deviceId: createRequest.DeviceId);
        }

        var partnerIdValue = createRequest.PartnerId;
        if (requestedOrder != null)
        {
            if (partnerIdValue.HasValue && partnerIdValue.Value != requestedOrder.PartnerId)
            {
                return LogCreateAndReturn(
                    Results.BadRequest(new ApiResult(false, "ORDER_PARTNER_MISMATCH")),
                    LogLevel.Warning,
                    outcome: "VALIDATION_FAILED",
                    docUid: docUid,
                    docType: docTypeValue,
                    errors: ["ORDER_PARTNER_MISMATCH"],
                    eventId: createRequest.EventId,
                    deviceId: createRequest.DeviceId);
            }

            partnerIdValue = requestedOrder.PartnerId;
        }

        if (partnerIdValue.HasValue && store.GetPartner(partnerIdValue.Value) == null)
        {
            return LogCreateAndReturn(
                Results.BadRequest(new ApiResult(false, "UNKNOWN_PARTNER")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docUid: docUid,
                docType: docTypeValue,
                errors: ["UNKNOWN_PARTNER"],
                eventId: createRequest.EventId,
                deviceId: createRequest.DeviceId);
        }

        if (!draftOnly && (docType == DocType.Inbound || docType == DocType.Outbound))
        {
            if (!partnerIdValue.HasValue)
            {
                return LogCreateAndReturn(
                    Results.BadRequest(new ApiResult(false, "MISSING_PARTNER")),
                    LogLevel.Warning,
                    outcome: "VALIDATION_FAILED",
                    docUid: docUid,
                    docType: docTypeValue,
                    errors: ["MISSING_PARTNER"],
                    eventId: createRequest.EventId,
                    deviceId: createRequest.DeviceId);
            }
        }

        var fromLocationIdValue = createRequest.FromLocationId;
        var toLocationIdValue = createRequest.ToLocationId;
        if (fromLocationIdValue.HasValue && store.FindLocationById(fromLocationIdValue.Value) == null)
        {
            return LogCreateAndReturn(
                Results.BadRequest(new ApiResult(false, "UNKNOWN_LOCATION")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docUid: docUid,
                docType: docTypeValue,
                errors: ["UNKNOWN_LOCATION"],
                eventId: createRequest.EventId,
                deviceId: createRequest.DeviceId);
        }

        if (toLocationIdValue.HasValue && store.FindLocationById(toLocationIdValue.Value) == null)
        {
            return LogCreateAndReturn(
                Results.BadRequest(new ApiResult(false, "UNKNOWN_LOCATION")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docUid: docUid,
                docType: docTypeValue,
                errors: ["UNKNOWN_LOCATION"],
                eventId: createRequest.EventId,
                deviceId: createRequest.DeviceId);
        }

        if (!draftOnly && (docType == DocType.Move || docType == DocType.Outbound || docType == DocType.WriteOff))
        {
            if (!fromLocationIdValue.HasValue)
            {
                return LogCreateAndReturn(
                    Results.BadRequest(new ApiResult(false, "MISSING_LOCATION")),
                    LogLevel.Warning,
                    outcome: "VALIDATION_FAILED",
                    docUid: docUid,
                    docType: docTypeValue,
                    errors: ["MISSING_LOCATION"],
                    eventId: createRequest.EventId,
                    deviceId: createRequest.DeviceId);
            }
        }

        if (!draftOnly && (docType == DocType.Move || docType == DocType.Inbound || docType == DocType.Inventory || docType == DocType.ProductionReceipt))
        {
            if (!toLocationIdValue.HasValue)
            {
                return LogCreateAndReturn(
                    Results.BadRequest(new ApiResult(false, "MISSING_LOCATION")),
                    LogLevel.Warning,
                    outcome: "VALIDATION_FAILED",
                    docUid: docUid,
                    docType: docTypeValue,
                    errors: ["MISSING_LOCATION"],
                    eventId: createRequest.EventId,
                    deviceId: createRequest.DeviceId);
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
            return LogCreateAndReturn(
                Results.BadRequest(new
                {
                    ok = false,
                    error = "UNKNOWN_HU",
                    missing = missingHuNew
                }),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docUid: docUid,
                docType: docTypeValue,
                errors: ["UNKNOWN_HU", $"missing={string.Join(",", missingHuNew)}"],
                eventId: createRequest.EventId,
                deviceId: createRequest.DeviceId);
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
                return LogCreateAndReturn(
                    Results.BadRequest(new ApiResult(false, "DOC_REF_EXISTS")),
                    LogLevel.Warning,
                    outcome: "VALIDATION_FAILED",
                    docUid: docUid,
                    docRef: docRef,
                    docType: docTypeValue,
                    errors: ["DOC_REF_EXISTS"],
                    eventId: createRequest.EventId,
                    deviceId: createRequest.DeviceId);
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

        var createdDoc = store.GetDoc(docId);
        return LogCreateAndReturn(
            Results.Ok(new
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
            }),
            LogLevel.Information,
            outcome: "CREATED",
            docUid: docUid,
            docId: docId,
            docRef: docRef,
            docType: docTypeValue,
            docStatusAfter: createdDoc == null ? "DRAFT" : DocTypeMapper.StatusToString(createdDoc.Status),
            lineCount: createdDoc?.LineCount ?? 0,
            apiEventWritten: true,
            idempotentReplay: false,
            eventId: createRequest.EventId,
            deviceId: createRequest.DeviceId);
    }

    public static async Task<IResult> HandleAddLineAsync(
        string docUid,
        HttpRequest request,
        IDataStore store,
        DocumentService docs,
        IApiDocStore apiStore,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("FlowStock.Server.DocumentLifecycle");
        var started = Stopwatch.StartNew();
        var path = request.Path.Value ?? "/api/docs/{docUid}/lines";

        IResult LogLineAndReturn(
            IResult result,
            LogLevel level,
            string outcome,
            long? docId = null,
            string? docRef = null,
            string? docType = null,
            string? docStatusBefore = null,
            string? docStatusAfter = null,
            int? lineCount = null,
            long? lineId = null,
            bool? apiEventWritten = null,
            bool? appended = null,
            bool? idempotentReplay = null,
            IEnumerable<string>? errors = null,
            string? eventId = null,
            string? deviceId = null)
        {
            started.Stop();
            ServerOperationLogging.LogDocumentLifecycleOperation(
                logger,
                level,
                operation: "AddDocLine",
                path: path,
                result: outcome,
                docUid: docUid,
                docId: docId,
                docRef: docRef,
                docType: docType,
                docStatusBefore: docStatusBefore,
                docStatusAfter: docStatusAfter,
                lineCount: lineCount,
                lineId: lineId,
                ledgerRowsWritten: 0,
                eventId: eventId,
                deviceId: deviceId,
                apiEventWritten: apiEventWritten,
                appended: appended,
                idempotentReplay: idempotentReplay,
                elapsedMs: started.ElapsedMilliseconds,
                errors: errors);
            return result;
        }

        var rawJson = await ReadBody(request);
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return LogLineAndReturn(
                Results.BadRequest(new ApiResult(false, "EMPTY_BODY")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["EMPTY_BODY"]);
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
            return LogLineAndReturn(
                Results.BadRequest(new ApiResult(false, "INVALID_JSON")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["INVALID_JSON"]);
        }

        if (lineRequest == null)
        {
            return LogLineAndReturn(
                Results.BadRequest(new ApiResult(false, "INVALID_JSON")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["INVALID_JSON"]);
        }

        if (string.IsNullOrWhiteSpace(lineRequest.EventId))
        {
            return LogLineAndReturn(
                Results.BadRequest(new ApiResult(false, "MISSING_EVENT_ID")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["MISSING_EVENT_ID"],
                deviceId: lineRequest.DeviceId);
        }

        var normalizedIncoming = NormalizeLineReplayPayload(docUid, lineRequest);
        var existingEvent = apiStore.GetEvent(lineRequest.EventId);
        if (existingEvent != null)
        {
            if (string.Equals(existingEvent.EventType, "DOC_LINE", StringComparison.OrdinalIgnoreCase)
                && string.Equals(existingEvent.DocUid, docUid, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadStoredLineReplay(existingEvent.RawJson, out var storedReplay))
                {
                    if (!TryReadLegacyLineReplay(existingEvent.RawJson, out var legacyRequest)
                        || !IsEquivalentLineReplay(NormalizeLineReplayPayload(docUid, legacyRequest), normalizedIncoming))
                    {
                        return LogLineAndReturn(
                            Results.BadRequest(new ApiResult(false, "EVENT_ID_CONFLICT")),
                            LogLevel.Warning,
                            outcome: "EVENT_ID_CONFLICT",
                            errors: ["EVENT_ID_CONFLICT"],
                            eventId: lineRequest.EventId,
                            deviceId: lineRequest.DeviceId);
                    }

                    var legacyDocInfo = apiStore.GetApiDoc(docUid);
                    var legacyDoc = legacyDocInfo == null ? null : store.GetDoc(legacyDocInfo.DocId);
                    var legacyDocStatus = ResolveCurrentDocStatus(store, legacyDocInfo);
                    return LogLineAndReturn(
                        Results.Ok(new
                        {
                            ok = true,
                            result = "IDEMPOTENT_REPLAY",
                            doc_uid = docUid,
                            doc_status = legacyDocStatus,
                            appended = false,
                            idempotent_replay = true,
                            line = (object?)null
                        }),
                        LogLevel.Information,
                        outcome: "IDEMPOTENT_REPLAY",
                        docId: legacyDocInfo?.DocId,
                        docRef: legacyDocInfo?.DocRef,
                        docType: legacyDocInfo?.DocType,
                        docStatusBefore: legacyDocStatus,
                        docStatusAfter: legacyDocStatus,
                        lineCount: legacyDoc?.LineCount,
                        apiEventWritten: false,
                        appended: false,
                        idempotentReplay: true,
                        eventId: lineRequest.EventId,
                        deviceId: lineRequest.DeviceId);
                }

                if (!IsEquivalentLineReplay(storedReplay.Normalized, normalizedIncoming))
                {
                    return LogLineAndReturn(
                        Results.BadRequest(new ApiResult(false, "EVENT_ID_CONFLICT")),
                        LogLevel.Warning,
                        outcome: "EVENT_ID_CONFLICT",
                        errors: ["EVENT_ID_CONFLICT"],
                        eventId: lineRequest.EventId,
                        deviceId: lineRequest.DeviceId);
                }

                var replayDocInfo = apiStore.GetApiDoc(docUid);
                var replayDoc = replayDocInfo == null ? null : store.GetDoc(replayDocInfo.DocId);
                var replayDocStatus = storedReplay.DocStatus ?? ResolveCurrentDocStatus(store, replayDocInfo);
                return LogLineAndReturn(
                    Results.Ok(new
                    {
                        ok = true,
                        result = "IDEMPOTENT_REPLAY",
                        doc_uid = docUid,
                        doc_status = replayDocStatus,
                        appended = false,
                        idempotent_replay = true,
                        line = storedReplay.Line
                    }),
                    LogLevel.Information,
                    outcome: "IDEMPOTENT_REPLAY",
                    docId: replayDocInfo?.DocId,
                    docRef: replayDocInfo?.DocRef,
                    docType: replayDocInfo?.DocType,
                    docStatusBefore: replayDocStatus,
                    docStatusAfter: replayDocStatus,
                    lineCount: replayDoc?.LineCount,
                    lineId: storedReplay.Line?.Id,
                    apiEventWritten: false,
                    appended: false,
                    idempotentReplay: true,
                    eventId: lineRequest.EventId,
                    deviceId: lineRequest.DeviceId);
            }

            return LogLineAndReturn(
                Results.BadRequest(new ApiResult(false, "EVENT_ID_CONFLICT")),
                LogLevel.Warning,
                outcome: "EVENT_ID_CONFLICT",
                errors: ["EVENT_ID_CONFLICT"],
                eventId: lineRequest.EventId,
                deviceId: lineRequest.DeviceId);
        }

        var docInfo = apiStore.GetApiDoc(docUid);
        if (docInfo == null)
        {
            return LogLineAndReturn(
                Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND")),
                LogLevel.Warning,
                outcome: "DOC_NOT_FOUND",
                errors: ["DOC_NOT_FOUND"],
                eventId: lineRequest.EventId,
                deviceId: lineRequest.DeviceId);
        }

        var existingDoc = store.GetDoc(docInfo.DocId);
        if (existingDoc == null)
        {
            return LogLineAndReturn(
                Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND")),
                LogLevel.Warning,
                outcome: "DOC_NOT_FOUND",
                docId: docInfo.DocId,
                docRef: docInfo.DocRef,
                docType: docInfo.DocType,
                docStatusBefore: docInfo.Status,
                errors: ["DOC_NOT_FOUND"],
                eventId: lineRequest.EventId,
                deviceId: lineRequest.DeviceId);
        }

        if (IsRecountComment(existingDoc.Comment))
        {
            if (existingDoc.Status != DocStatus.Draft)
            {
                return LogLineAndReturn(
                    Results.BadRequest(new ApiResult(false, "DOC_NOT_DRAFT")),
                    LogLevel.Warning,
                    outcome: "VALIDATION_FAILED",
                    docId: docInfo.DocId,
                    docRef: docInfo.DocRef,
                    docType: docInfo.DocType,
                    docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                    errors: ["DOC_NOT_DRAFT"],
                    eventId: lineRequest.EventId,
                    deviceId: lineRequest.DeviceId);
            }

            store.DeleteDocLines(docInfo.DocId);
            store.UpdateDocComment(docInfo.DocId, "TSD");
        }

        if (!string.Equals(docInfo.Status, "DRAFT", StringComparison.OrdinalIgnoreCase))
        {
            return LogLineAndReturn(
                Results.BadRequest(new ApiResult(false, "DOC_NOT_DRAFT")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docId: docInfo.DocId,
                docRef: docInfo.DocRef,
                docType: docInfo.DocType,
                docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                errors: ["DOC_NOT_DRAFT"],
                eventId: lineRequest.EventId,
                deviceId: lineRequest.DeviceId);
        }

        if (lineRequest.Qty <= 0)
        {
            return LogLineAndReturn(
                Results.BadRequest(new ApiResult(false, "INVALID_QTY")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docId: docInfo.DocId,
                docRef: docInfo.DocRef,
                docType: docInfo.DocType,
                docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                errors: ["INVALID_QTY"],
                eventId: lineRequest.EventId,
                deviceId: lineRequest.DeviceId);
        }

        var docType = ParseDocType(docInfo.DocType);
        if (docType == null)
        {
            return LogLineAndReturn(
                Results.BadRequest(new ApiResult(false, "INVALID_TYPE")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docId: docInfo.DocId,
                docRef: docInfo.DocRef,
                docType: docInfo.DocType,
                docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                errors: ["INVALID_TYPE"],
                eventId: lineRequest.EventId,
                deviceId: lineRequest.DeviceId);
        }

        var docTypeValue = DocTypeMapper.ToOpString(docType.Value);

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
            return LogLineAndReturn(
                Results.BadRequest(new
                {
                    ok = false,
                    error = "UNKNOWN_HU",
                    missing = missingHu
                }),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docId: docInfo.DocId,
                docRef: docInfo.DocRef,
                docType: docTypeValue,
                docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                errors: ["UNKNOWN_HU", $"missing={string.Join(",", missingHu)}"],
                eventId: lineRequest.EventId,
                deviceId: lineRequest.DeviceId);
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
            return LogLineAndReturn(
                Results.BadRequest(new ApiResult(false, "UNKNOWN_ITEM")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docId: docInfo.DocId,
                docRef: docInfo.DocRef,
                docType: docTypeValue,
                docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                errors: ["UNKNOWN_ITEM"],
                eventId: lineRequest.EventId,
                deviceId: lineRequest.DeviceId);
        }

        if (!item.IsActive)
        {
            return LogLineAndReturn(
                Results.BadRequest(new ApiResult(false, "ITEM_INACTIVE")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docId: docInfo.DocId,
                docRef: docInfo.DocRef,
                docType: docTypeValue,
                docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                errors: ["ITEM_INACTIVE"],
                eventId: lineRequest.EventId,
                deviceId: lineRequest.DeviceId);
        }

        long? fromLocationId = null;
        long? toLocationId = null;
        string? fromHu = null;
        string? toHu = null;

        switch (docType.Value)
        {
            case DocType.Inbound:
                if (lineRequest.ToLocationId.HasValue && store.FindLocationById(lineRequest.ToLocationId.Value) == null)
                {
                    return LogLineAndReturn(
                        Results.BadRequest(new ApiResult(false, "UNKNOWN_LOCATION")),
                        LogLevel.Warning,
                        outcome: "VALIDATION_FAILED",
                        docId: docInfo.DocId,
                        docRef: docInfo.DocRef,
                        docType: docTypeValue,
                        docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                        errors: ["UNKNOWN_LOCATION"],
                        eventId: lineRequest.EventId,
                        deviceId: lineRequest.DeviceId);
                }

                // Allow sending location per-line (WPF may not have persisted header yet).
                toLocationId = lineRequest.ToLocationId ?? docInfo.ToLocationId;
                toHu = NormalizeHu(docInfo.ToHu);
                if (!toLocationId.HasValue)
                {
                    return LogLineAndReturn(
                        Results.BadRequest(new ApiResult(false, "MISSING_LOCATION")),
                        LogLevel.Warning,
                        outcome: "VALIDATION_FAILED",
                        docId: docInfo.DocId,
                        docRef: docInfo.DocRef,
                        docType: docTypeValue,
                        docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                        errors: ["MISSING_LOCATION"],
                        eventId: lineRequest.EventId,
                        deviceId: lineRequest.DeviceId);
                }

                // If header was missing but line provided the location, persist it for subsequent lines.
                if (!docInfo.ToLocationId.HasValue && toLocationId.HasValue)
                {
                    apiStore.UpdateApiDocHeader(docUid, docInfo.PartnerId, docInfo.FromLocationId, toLocationId, docInfo.FromHu, docInfo.ToHu);
                }
                break;
            case DocType.ProductionReceipt:
                if (lineRequest.ToLocationId.HasValue && store.FindLocationById(lineRequest.ToLocationId.Value) == null)
                {
                    return LogLineAndReturn(
                        Results.BadRequest(new ApiResult(false, "UNKNOWN_LOCATION")),
                        LogLevel.Warning,
                        outcome: "VALIDATION_FAILED",
                        docId: docInfo.DocId,
                        docRef: docInfo.DocRef,
                        docType: docTypeValue,
                        docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                        errors: ["UNKNOWN_LOCATION"],
                        eventId: lineRequest.EventId,
                        deviceId: lineRequest.DeviceId);
                }

                toLocationId = lineRequest.ToLocationId ?? docInfo.ToLocationId;
                toHu = NormalizeHu(docInfo.ToHu);
                if (!toLocationId.HasValue)
                {
                    return LogLineAndReturn(
                        Results.BadRequest(new ApiResult(false, "MISSING_LOCATION")),
                        LogLevel.Warning,
                        outcome: "VALIDATION_FAILED",
                        docId: docInfo.DocId,
                        docRef: docInfo.DocRef,
                        docType: docTypeValue,
                        docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                        errors: ["MISSING_LOCATION"],
                        eventId: lineRequest.EventId,
                        deviceId: lineRequest.DeviceId);
                }

                if (!docInfo.ToLocationId.HasValue && toLocationId.HasValue)
                {
                    apiStore.UpdateApiDocHeader(docUid, docInfo.PartnerId, docInfo.FromLocationId, toLocationId, docInfo.FromHu, docInfo.ToHu);
                }
                break;
            case DocType.Outbound:
                if (lineRequest.FromLocationId.HasValue && store.FindLocationById(lineRequest.FromLocationId.Value) == null)
                {
                    return LogLineAndReturn(
                        Results.BadRequest(new ApiResult(false, "UNKNOWN_LOCATION")),
                        LogLevel.Warning,
                        outcome: "VALIDATION_FAILED",
                        docId: docInfo.DocId,
                        docRef: docInfo.DocRef,
                        docType: docTypeValue,
                        docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                        errors: ["UNKNOWN_LOCATION"],
                        eventId: lineRequest.EventId,
                        deviceId: lineRequest.DeviceId);
                }

                fromLocationId = lineRequest.FromLocationId ?? docInfo.FromLocationId;
                fromHu = !string.IsNullOrWhiteSpace(requestedFromHu)
                    ? requestedFromHu
                    : NormalizeHu(docInfo.FromHu);
                if (!fromLocationId.HasValue)
                {
                    return LogLineAndReturn(
                        Results.BadRequest(new ApiResult(false, "MISSING_LOCATION")),
                        LogLevel.Warning,
                        outcome: "VALIDATION_FAILED",
                        docId: docInfo.DocId,
                        docRef: docInfo.DocRef,
                        docType: docTypeValue,
                        docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                        errors: ["MISSING_LOCATION"],
                        eventId: lineRequest.EventId,
                        deviceId: lineRequest.DeviceId);
                }
                break;
            case DocType.Move:
                fromLocationId = docInfo.FromLocationId;
                toLocationId = docInfo.ToLocationId;
                fromHu = NormalizeHu(docInfo.FromHu);
                toHu = NormalizeHu(docInfo.ToHu);
                if (!fromLocationId.HasValue || !toLocationId.HasValue)
                {
                    return LogLineAndReturn(
                        Results.BadRequest(new ApiResult(false, "MISSING_LOCATION")),
                        LogLevel.Warning,
                        outcome: "VALIDATION_FAILED",
                        docId: docInfo.DocId,
                        docRef: docInfo.DocRef,
                        docType: docTypeValue,
                        docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                        errors: ["MISSING_LOCATION"],
                        eventId: lineRequest.EventId,
                        deviceId: lineRequest.DeviceId);
                }
                break;
            case DocType.WriteOff:
                fromLocationId = docInfo.FromLocationId;
                fromHu = !string.IsNullOrWhiteSpace(requestedFromHu)
                    ? requestedFromHu
                    : NormalizeHu(docInfo.FromHu);
                if (!fromLocationId.HasValue)
                {
                    return LogLineAndReturn(
                        Results.BadRequest(new ApiResult(false, "MISSING_LOCATION")),
                        LogLevel.Warning,
                        outcome: "VALIDATION_FAILED",
                        docId: docInfo.DocId,
                        docRef: docInfo.DocRef,
                        docType: docTypeValue,
                        docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                        errors: ["MISSING_LOCATION"],
                        eventId: lineRequest.EventId,
                        deviceId: lineRequest.DeviceId);
                }
                break;
            case DocType.Inventory:
                if (lineRequest.ToLocationId.HasValue && store.FindLocationById(lineRequest.ToLocationId.Value) == null)
                {
                    return LogLineAndReturn(
                        Results.BadRequest(new ApiResult(false, "UNKNOWN_LOCATION")),
                        LogLevel.Warning,
                        outcome: "VALIDATION_FAILED",
                        docId: docInfo.DocId,
                        docRef: docInfo.DocRef,
                        docType: docTypeValue,
                        docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                        errors: ["UNKNOWN_LOCATION"],
                        eventId: lineRequest.EventId,
                        deviceId: lineRequest.DeviceId);
                }

                toLocationId = lineRequest.ToLocationId ?? docInfo.ToLocationId;
                toHu = !string.IsNullOrWhiteSpace(requestedToHu)
                    ? requestedToHu
                    : NormalizeHu(docInfo.ToHu);
                if (!toLocationId.HasValue)
                {
                    return LogLineAndReturn(
                        Results.BadRequest(new ApiResult(false, "MISSING_LOCATION")),
                        LogLevel.Warning,
                        outcome: "VALIDATION_FAILED",
                        docId: docInfo.DocId,
                        docRef: docInfo.DocRef,
                        docType: docTypeValue,
                        docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                        errors: ["MISSING_LOCATION"],
                        eventId: lineRequest.EventId,
                        deviceId: lineRequest.DeviceId);
                }

                if (!docInfo.ToLocationId.HasValue && toLocationId.HasValue)
                {
                    apiStore.UpdateApiDocHeader(docUid, docInfo.PartnerId, docInfo.FromLocationId, toLocationId, docInfo.FromHu, docInfo.ToHu);
                }
                break;
        }

        long newLineId;
        try
        {
            newLineId = docs.AddDocLine(
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
            return LogLineAndReturn(
                Results.BadRequest(new ApiResult(false, ex.Message)),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docId: docInfo.DocId,
                docRef: docInfo.DocRef,
                docType: docTypeValue,
                docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                errors: [ex.Message],
                eventId: lineRequest.EventId,
                deviceId: lineRequest.DeviceId);
        }

        var lastLine = store.GetDocLines(docInfo.DocId)
            .FirstOrDefault(line => line.Id == newLineId);

        var lineResponse = lastLine == null
            ? null
            : new DocLineReplayResponse
            {
                Id = lastLine.Id,
                ReplacesLineId = lastLine.ReplacesLineId,
                ItemId = lastLine.ItemId,
                Qty = lastLine.Qty,
                UomCode = lastLine.UomCode,
                OrderLineId = lastLine.OrderLineId,
                FromLocationId = lastLine.FromLocationId,
                ToLocationId = lastLine.ToLocationId,
                FromHu = NormalizeHu(lastLine.FromHu),
                ToHu = NormalizeHu(lastLine.ToHu)
            };

        var replayRecord = new StoredDocLineReplay
        {
            Normalized = normalizedIncoming,
            DocStatus = "DRAFT",
            Line = lineResponse
        };

        apiStore.RecordEvent(
            lineRequest.EventId,
            "DOC_LINE",
            docUid,
            lineRequest.DeviceId,
            JsonSerializer.Serialize(replayRecord));

        var updatedDoc = store.GetDoc(docInfo.DocId) ?? existingDoc;
        return LogLineAndReturn(
            Results.Ok(new
            {
                ok = true,
                result = "APPENDED",
                doc_uid = docUid,
                doc_status = "DRAFT",
                appended = true,
                idempotent_replay = false,
                line = lineResponse
            }),
            LogLevel.Information,
            outcome: "CREATED",
            docId: docInfo.DocId,
            docRef: docInfo.DocRef,
            docType: docTypeValue,
            docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
            docStatusAfter: DocTypeMapper.StatusToString(updatedDoc.Status),
            lineCount: updatedDoc.LineCount,
            lineId: lineResponse?.Id,
            apiEventWritten: true,
            appended: true,
            idempotentReplay: false,
            eventId: lineRequest.EventId,
            deviceId: lineRequest.DeviceId);
    }

    public static async Task<IResult> HandleUpdateLineAsync(
        string docUid,
        HttpRequest request,
        IDataStore store,
        DocumentService docs,
        IApiDocStore apiStore,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("FlowStock.Server.DocumentLifecycle");
        var started = Stopwatch.StartNew();
        var path = request.Path.Value ?? $"/api/docs/{docUid}/lines/update";

        IResult LogUpdateAndReturn(
            IResult result,
            LogLevel level,
            string outcome,
            long? docId = null,
            string? docRef = null,
            string? docType = null,
            string? docStatusBefore = null,
            string? docStatusAfter = null,
            int? lineCount = null,
            long? lineId = null,
            long? replacesLineId = null,
            bool? apiEventWritten = null,
            bool? appended = null,
            bool? idempotentReplay = null,
            IEnumerable<string>? errors = null,
            string? eventId = null,
            string? deviceId = null)
        {
            started.Stop();
            ServerOperationLogging.LogDocumentLifecycleOperation(
                logger,
                level,
                operation: "UpdateDocLine",
                path: path,
                result: outcome,
                docUid: docUid,
                docId: docId,
                docRef: docRef,
                docType: docType,
                docStatusBefore: docStatusBefore,
                docStatusAfter: docStatusAfter,
                lineCount: lineCount,
                lineId: lineId,
                replacesLineId: replacesLineId,
                ledgerRowsWritten: 0,
                eventId: eventId,
                deviceId: deviceId,
                apiEventWritten: apiEventWritten,
                appended: appended,
                idempotentReplay: idempotentReplay,
                elapsedMs: started.ElapsedMilliseconds,
                errors: errors);
            return result;
        }

        var rawJson = await ReadBody(request);
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return LogUpdateAndReturn(
                Results.BadRequest(new ApiResult(false, "EMPTY_BODY")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["EMPTY_BODY"]);
        }

        UpdateDocLineRequest? updateRequest;
        try
        {
            updateRequest = JsonSerializer.Deserialize<UpdateDocLineRequest>(
                rawJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return LogUpdateAndReturn(
                Results.BadRequest(new ApiResult(false, "INVALID_JSON")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["INVALID_JSON"]);
        }

        if (updateRequest == null)
        {
            return LogUpdateAndReturn(
                Results.BadRequest(new ApiResult(false, "INVALID_JSON")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["INVALID_JSON"]);
        }

        if (string.IsNullOrWhiteSpace(updateRequest.EventId))
        {
            return LogUpdateAndReturn(
                Results.BadRequest(new ApiResult(false, "MISSING_EVENT_ID")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["MISSING_EVENT_ID"],
                deviceId: updateRequest.DeviceId);
        }

        if (!updateRequest.LineId.HasValue || updateRequest.LineId.Value <= 0)
        {
            return LogUpdateAndReturn(
                Results.BadRequest(new ApiResult(false, "MISSING_LINE_ID")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["MISSING_LINE_ID"],
                eventId: updateRequest.EventId,
                deviceId: updateRequest.DeviceId);
        }

        if (updateRequest.Qty <= 0)
        {
            return LogUpdateAndReturn(
                Results.BadRequest(new ApiResult(false, "INVALID_QTY")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["INVALID_QTY"],
                eventId: updateRequest.EventId,
                deviceId: updateRequest.DeviceId);
        }

        var normalizedIncoming = NormalizeUpdatedLineReplayPayload(docUid, updateRequest);
        var existingEvent = apiStore.GetEvent(updateRequest.EventId);
        if (existingEvent != null)
        {
            if (string.Equals(existingEvent.EventType, "DOC_LINE_UPDATE", StringComparison.OrdinalIgnoreCase)
                && string.Equals(existingEvent.DocUid, docUid, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadStoredLineReplay(existingEvent.RawJson, out var storedReplay)
                    || !IsEquivalentLineReplay(storedReplay.Normalized, normalizedIncoming))
                {
                    return LogUpdateAndReturn(
                        Results.BadRequest(new ApiResult(false, "EVENT_ID_CONFLICT")),
                        LogLevel.Warning,
                        outcome: "EVENT_ID_CONFLICT",
                        errors: ["EVENT_ID_CONFLICT"],
                        eventId: updateRequest.EventId,
                        deviceId: updateRequest.DeviceId);
                }

                var replayDocInfo = apiStore.GetApiDoc(docUid);
                var replayDoc = replayDocInfo == null ? null : store.GetDoc(replayDocInfo.DocId);
                var replayDocStatus = storedReplay.DocStatus ?? ResolveCurrentDocStatus(store, replayDocInfo);
                return LogUpdateAndReturn(
                    Results.Ok(new
                    {
                        ok = true,
                        result = "IDEMPOTENT_REPLAY",
                        doc_uid = docUid,
                        doc_status = replayDocStatus,
                        appended = false,
                        idempotent_replay = true,
                        line = storedReplay.Line
                    }),
                    LogLevel.Information,
                    outcome: "IDEMPOTENT_REPLAY",
                    docId: replayDocInfo?.DocId,
                    docRef: replayDocInfo?.DocRef,
                    docType: replayDocInfo?.DocType,
                    docStatusBefore: replayDocStatus,
                    docStatusAfter: replayDocStatus,
                    lineCount: replayDoc?.LineCount,
                    lineId: storedReplay.Line?.Id,
                    replacesLineId: storedReplay.Line?.ReplacesLineId,
                    apiEventWritten: false,
                    appended: false,
                    idempotentReplay: true,
                    eventId: updateRequest.EventId,
                    deviceId: updateRequest.DeviceId);
            }

            return LogUpdateAndReturn(
                Results.BadRequest(new ApiResult(false, "EVENT_ID_CONFLICT")),
                LogLevel.Warning,
                outcome: "EVENT_ID_CONFLICT",
                errors: ["EVENT_ID_CONFLICT"],
                eventId: updateRequest.EventId,
                deviceId: updateRequest.DeviceId);
        }

        var docInfo = apiStore.GetApiDoc(docUid);
        if (docInfo == null)
        {
            return LogUpdateAndReturn(
                Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND")),
                LogLevel.Warning,
                outcome: "DOC_NOT_FOUND",
                errors: ["DOC_NOT_FOUND"],
                eventId: updateRequest.EventId,
                deviceId: updateRequest.DeviceId);
        }

        var existingDoc = store.GetDoc(docInfo.DocId);
        if (existingDoc == null)
        {
            return LogUpdateAndReturn(
                Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND")),
                LogLevel.Warning,
                outcome: "DOC_NOT_FOUND",
                docId: docInfo.DocId,
                docRef: docInfo.DocRef,
                docType: docInfo.DocType,
                docStatusBefore: docInfo.Status,
                errors: ["DOC_NOT_FOUND"],
                eventId: updateRequest.EventId,
                deviceId: updateRequest.DeviceId);
        }

        if (!string.Equals(docInfo.Status, "DRAFT", StringComparison.OrdinalIgnoreCase) || existingDoc.Status != DocStatus.Draft)
        {
            return LogUpdateAndReturn(
                Results.BadRequest(new ApiResult(false, "DOC_NOT_DRAFT")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docId: docInfo.DocId,
                docRef: docInfo.DocRef,
                docType: docInfo.DocType,
                docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                errors: ["DOC_NOT_DRAFT"],
                eventId: updateRequest.EventId,
                deviceId: updateRequest.DeviceId);
        }

        var docType = ParseDocType(docInfo.DocType);
        if (docType == null)
        {
            return LogUpdateAndReturn(
                Results.BadRequest(new ApiResult(false, "INVALID_TYPE")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docId: docInfo.DocId,
                docRef: docInfo.DocRef,
                docType: docInfo.DocType,
                docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                errors: ["INVALID_TYPE"],
                eventId: updateRequest.EventId,
                deviceId: updateRequest.DeviceId);
        }

        var docTypeValue = DocTypeMapper.ToOpString(docType.Value);
        var existingLine = store.GetDocLines(docInfo.DocId)
            .FirstOrDefault(line => line.Id == updateRequest.LineId.Value);
        if (existingLine == null)
        {
            return LogUpdateAndReturn(
                Results.BadRequest(new ApiResult(false, "UNKNOWN_LINE")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docId: docInfo.DocId,
                docRef: docInfo.DocRef,
                docType: docTypeValue,
                docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                errors: ["UNKNOWN_LINE"],
                eventId: updateRequest.EventId,
                deviceId: updateRequest.DeviceId);
        }

        if (!TryResolveUpdatedLineState(
                store,
                docType.Value,
                docInfo,
                existingLine,
                updateRequest,
                out var fromLocationId,
                out var toLocationId,
                out var fromHu,
                out var toHu,
                out var validationError))
        {
            return LogUpdateAndReturn(
                Results.BadRequest(new ApiResult(false, validationError)),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docId: docInfo.DocId,
                docRef: docInfo.DocRef,
                docType: docTypeValue,
                docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                errors: [validationError ?? "VALIDATION_FAILED"],
                eventId: updateRequest.EventId,
                deviceId: updateRequest.DeviceId);
        }

        long newLineId;
        try
        {
            newLineId = docs.AddDocLine(
                docInfo.DocId,
                existingLine.ItemId,
                updateRequest.Qty,
                fromLocationId,
                toLocationId,
                existingLine.QtyInput,
                updateRequest.UomCode,
                fromHu,
                toHu,
                existingLine.OrderLineId,
                existingLine.Id);
        }
        catch (Exception ex)
        {
            return LogUpdateAndReturn(
                Results.BadRequest(new ApiResult(false, ex.Message)),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docId: docInfo.DocId,
                docRef: docInfo.DocRef,
                docType: docTypeValue,
                docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                errors: [ex.Message],
                eventId: updateRequest.EventId,
                deviceId: updateRequest.DeviceId);
        }

        var updatedLine = store.GetDocLines(docInfo.DocId)
            .FirstOrDefault(line => line.Id == newLineId);

        var lineResponse = updatedLine == null
            ? null
            : new DocLineReplayResponse
            {
                Id = updatedLine.Id,
                ReplacesLineId = updatedLine.ReplacesLineId,
                ItemId = updatedLine.ItemId,
                Qty = updatedLine.Qty,
                UomCode = updatedLine.UomCode,
                OrderLineId = updatedLine.OrderLineId,
                FromLocationId = updatedLine.FromLocationId,
                ToLocationId = updatedLine.ToLocationId,
                FromHu = NormalizeHu(updatedLine.FromHu),
                ToHu = NormalizeHu(updatedLine.ToHu)
            };

        var replayRecord = new StoredDocLineReplay
        {
            Normalized = normalizedIncoming,
            DocStatus = "DRAFT",
            Line = lineResponse
        };

        apiStore.RecordEvent(
            updateRequest.EventId,
            "DOC_LINE_UPDATE",
            docUid,
            updateRequest.DeviceId,
            JsonSerializer.Serialize(replayRecord));

        var updatedDoc = store.GetDoc(docInfo.DocId) ?? existingDoc;
        return LogUpdateAndReturn(
            Results.Ok(new
            {
                ok = true,
                result = "UPDATED",
                doc_uid = docUid,
                doc_status = "DRAFT",
                appended = true,
                idempotent_replay = false,
                line = lineResponse
            }),
            LogLevel.Information,
            outcome: "UPDATED",
            docId: docInfo.DocId,
            docRef: docInfo.DocRef,
            docType: docTypeValue,
            docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
            docStatusAfter: DocTypeMapper.StatusToString(updatedDoc.Status),
            lineCount: updatedDoc.LineCount,
            lineId: lineResponse?.Id,
            replacesLineId: existingLine.Id,
            apiEventWritten: true,
            appended: true,
            idempotentReplay: false,
            eventId: updateRequest.EventId,
            deviceId: updateRequest.DeviceId);
    }

    public static async Task<IResult> HandleDeleteLineAsync(
        string docUid,
        HttpRequest request,
        IDataStore store,
        IApiDocStore apiStore,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("FlowStock.Server.DocumentLifecycle");
        var started = Stopwatch.StartNew();
        var path = request.Path.Value ?? $"/api/docs/{docUid}/lines/delete";

        IResult LogDeleteAndReturn(
            IResult result,
            LogLevel level,
            string outcome,
            long? docId = null,
            string? docRef = null,
            string? docType = null,
            string? docStatusBefore = null,
            string? docStatusAfter = null,
            int? lineCount = null,
            long? lineId = null,
            long? replacesLineId = null,
            bool? apiEventWritten = null,
            bool? appended = null,
            bool? idempotentReplay = null,
            IEnumerable<string>? errors = null,
            string? eventId = null,
            string? deviceId = null)
        {
            started.Stop();
            ServerOperationLogging.LogDocumentLifecycleOperation(
                logger,
                level,
                operation: "DeleteDocLine",
                path: path,
                result: outcome,
                docUid: docUid,
                docId: docId,
                docRef: docRef,
                docType: docType,
                docStatusBefore: docStatusBefore,
                docStatusAfter: docStatusAfter,
                lineCount: lineCount,
                lineId: lineId,
                replacesLineId: replacesLineId,
                ledgerRowsWritten: 0,
                eventId: eventId,
                deviceId: deviceId,
                apiEventWritten: apiEventWritten,
                appended: appended,
                idempotentReplay: idempotentReplay,
                elapsedMs: started.ElapsedMilliseconds,
                errors: errors);
            return result;
        }

        var rawJson = await ReadBody(request);
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return LogDeleteAndReturn(
                Results.BadRequest(new ApiResult(false, "EMPTY_BODY")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["EMPTY_BODY"]);
        }

        DeleteDocLineRequest? deleteRequest;
        try
        {
            deleteRequest = JsonSerializer.Deserialize<DeleteDocLineRequest>(
                rawJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return LogDeleteAndReturn(
                Results.BadRequest(new ApiResult(false, "INVALID_JSON")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["INVALID_JSON"]);
        }

        if (deleteRequest == null)
        {
            return LogDeleteAndReturn(
                Results.BadRequest(new ApiResult(false, "INVALID_JSON")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["INVALID_JSON"]);
        }

        if (string.IsNullOrWhiteSpace(deleteRequest.EventId))
        {
            return LogDeleteAndReturn(
                Results.BadRequest(new ApiResult(false, "MISSING_EVENT_ID")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["MISSING_EVENT_ID"],
                deviceId: deleteRequest.DeviceId);
        }

        if (!deleteRequest.LineId.HasValue || deleteRequest.LineId.Value <= 0)
        {
            return LogDeleteAndReturn(
                Results.BadRequest(new ApiResult(false, "MISSING_LINE_ID")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["MISSING_LINE_ID"],
                eventId: deleteRequest.EventId,
                deviceId: deleteRequest.DeviceId);
        }

        var normalizedIncoming = NormalizeDeletedLineReplayPayload(docUid, deleteRequest);
        var existingEvent = apiStore.GetEvent(deleteRequest.EventId);
        if (existingEvent != null)
        {
            if (string.Equals(existingEvent.EventType, "DOC_LINE_DELETE", StringComparison.OrdinalIgnoreCase)
                && string.Equals(existingEvent.DocUid, docUid, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadStoredLineReplay(existingEvent.RawJson, out var storedReplay)
                    || !IsEquivalentLineReplay(storedReplay.Normalized, normalizedIncoming))
                {
                    return LogDeleteAndReturn(
                        Results.BadRequest(new ApiResult(false, "EVENT_ID_CONFLICT")),
                        LogLevel.Warning,
                        outcome: "EVENT_ID_CONFLICT",
                        errors: ["EVENT_ID_CONFLICT"],
                        eventId: deleteRequest.EventId,
                        deviceId: deleteRequest.DeviceId);
                }

                var replayDocInfo = apiStore.GetApiDoc(docUid);
                var replayDoc = replayDocInfo == null ? null : store.GetDoc(replayDocInfo.DocId);
                var replayDocStatus = storedReplay.DocStatus ?? ResolveCurrentDocStatus(store, replayDocInfo);
                return LogDeleteAndReturn(
                    Results.Ok(new
                    {
                        ok = true,
                        result = "IDEMPOTENT_REPLAY",
                        doc_uid = docUid,
                        doc_status = replayDocStatus,
                        appended = false,
                        idempotent_replay = true,
                        line = storedReplay.Line
                    }),
                    LogLevel.Information,
                    outcome: "IDEMPOTENT_REPLAY",
                    docId: replayDocInfo?.DocId,
                    docRef: replayDocInfo?.DocRef,
                    docType: replayDocInfo?.DocType,
                    docStatusBefore: replayDocStatus,
                    docStatusAfter: replayDocStatus,
                    lineCount: replayDoc?.LineCount,
                    lineId: storedReplay.Line?.Id,
                    replacesLineId: storedReplay.Line?.ReplacesLineId,
                    apiEventWritten: false,
                    appended: false,
                    idempotentReplay: true,
                    eventId: deleteRequest.EventId,
                    deviceId: deleteRequest.DeviceId);
            }

            return LogDeleteAndReturn(
                Results.BadRequest(new ApiResult(false, "EVENT_ID_CONFLICT")),
                LogLevel.Warning,
                outcome: "EVENT_ID_CONFLICT",
                errors: ["EVENT_ID_CONFLICT"],
                eventId: deleteRequest.EventId,
                deviceId: deleteRequest.DeviceId);
        }

        var docInfo = apiStore.GetApiDoc(docUid);
        if (docInfo == null)
        {
            return LogDeleteAndReturn(
                Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND")),
                LogLevel.Warning,
                outcome: "DOC_NOT_FOUND",
                errors: ["DOC_NOT_FOUND"],
                eventId: deleteRequest.EventId,
                deviceId: deleteRequest.DeviceId);
        }

        var existingDoc = store.GetDoc(docInfo.DocId);
        if (existingDoc == null)
        {
            return LogDeleteAndReturn(
                Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND")),
                LogLevel.Warning,
                outcome: "DOC_NOT_FOUND",
                docId: docInfo.DocId,
                docRef: docInfo.DocRef,
                docType: docInfo.DocType,
                docStatusBefore: docInfo.Status,
                errors: ["DOC_NOT_FOUND"],
                eventId: deleteRequest.EventId,
                deviceId: deleteRequest.DeviceId);
        }

        if (!string.Equals(docInfo.Status, "DRAFT", StringComparison.OrdinalIgnoreCase) || existingDoc.Status != DocStatus.Draft)
        {
            return LogDeleteAndReturn(
                Results.BadRequest(new ApiResult(false, "DOC_NOT_DRAFT")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docId: docInfo.DocId,
                docRef: docInfo.DocRef,
                docType: docInfo.DocType,
                docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                errors: ["DOC_NOT_DRAFT"],
                eventId: deleteRequest.EventId,
                deviceId: deleteRequest.DeviceId);
        }

        var docType = ParseDocType(docInfo.DocType);
        if (docType == null)
        {
            return LogDeleteAndReturn(
                Results.BadRequest(new ApiResult(false, "INVALID_TYPE")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docId: docInfo.DocId,
                docRef: docInfo.DocRef,
                docType: docInfo.DocType,
                docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                errors: ["INVALID_TYPE"],
                eventId: deleteRequest.EventId,
                deviceId: deleteRequest.DeviceId);
        }

        var docTypeValue = DocTypeMapper.ToOpString(docType.Value);
        var existingLine = store.GetDocLines(docInfo.DocId)
            .FirstOrDefault(line => line.Id == deleteRequest.LineId.Value);
        if (existingLine == null)
        {
            return LogDeleteAndReturn(
                Results.BadRequest(new ApiResult(false, "UNKNOWN_LINE")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docId: docInfo.DocId,
                docRef: docInfo.DocRef,
                docType: docTypeValue,
                docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                errors: ["UNKNOWN_LINE"],
                eventId: deleteRequest.EventId,
                deviceId: deleteRequest.DeviceId);
        }

        var tombstone = new DocLine
        {
            DocId = docInfo.DocId,
            ReplacesLineId = existingLine.Id,
            OrderLineId = existingLine.OrderLineId,
            ItemId = existingLine.ItemId,
            Qty = 0,
            QtyInput = null,
            UomCode = existingLine.UomCode,
            FromLocationId = existingLine.FromLocationId,
            ToLocationId = existingLine.ToLocationId,
            FromHu = NormalizeHu(existingLine.FromHu),
            ToHu = NormalizeHu(existingLine.ToHu)
        };

        long newLineId;
        try
        {
            newLineId = store.AddDocLine(tombstone);
        }
        catch (Exception ex)
        {
            return LogDeleteAndReturn(
                Results.BadRequest(new ApiResult(false, ex.Message)),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                docId: docInfo.DocId,
                docRef: docInfo.DocRef,
                docType: docTypeValue,
                docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
                errors: [ex.Message],
                eventId: deleteRequest.EventId,
                deviceId: deleteRequest.DeviceId);
        }

        var lineResponse = new DocLineReplayResponse
        {
            Id = newLineId,
            ReplacesLineId = existingLine.Id,
            ItemId = existingLine.ItemId,
            Qty = 0,
            UomCode = existingLine.UomCode,
            OrderLineId = existingLine.OrderLineId,
            FromLocationId = existingLine.FromLocationId,
            ToLocationId = existingLine.ToLocationId,
            FromHu = NormalizeHu(existingLine.FromHu),
            ToHu = NormalizeHu(existingLine.ToHu)
        };

        var replayRecord = new StoredDocLineReplay
        {
            Normalized = normalizedIncoming,
            DocStatus = "DRAFT",
            Line = lineResponse
        };

        apiStore.RecordEvent(
            deleteRequest.EventId,
            "DOC_LINE_DELETE",
            docUid,
            deleteRequest.DeviceId,
            JsonSerializer.Serialize(replayRecord));

        var updatedDoc = store.GetDoc(docInfo.DocId) ?? existingDoc;
        return LogDeleteAndReturn(
            Results.Ok(new
            {
                ok = true,
                result = "DELETED",
                doc_uid = docUid,
                doc_status = "DRAFT",
                appended = true,
                idempotent_replay = false,
                line = lineResponse
            }),
            LogLevel.Information,
            outcome: "DELETED",
            docId: docInfo.DocId,
            docRef: docInfo.DocRef,
            docType: docTypeValue,
            docStatusBefore: DocTypeMapper.StatusToString(existingDoc.Status),
            docStatusAfter: DocTypeMapper.StatusToString(updatedDoc.Status),
            lineCount: updatedDoc.LineCount,
            lineId: newLineId,
            replacesLineId: existingLine.Id,
            apiEventWritten: true,
            appended: true,
            idempotentReplay: false,
            eventId: deleteRequest.EventId,
            deviceId: deleteRequest.DeviceId);
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

    private static bool IsEquivalentCreateReplay(string? existingRawJson, CreateDocRequest incoming)
    {
        if (string.IsNullOrWhiteSpace(existingRawJson))
        {
            return false;
        }

        CreateDocRequest? existing;
        try
        {
            existing = JsonSerializer.Deserialize<CreateDocRequest>(
                existingRawJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return false;
        }

        if (existing == null)
        {
            return false;
        }

        return string.Equals(Normalize(existing.DocUid), Normalize(incoming.DocUid), StringComparison.OrdinalIgnoreCase)
               && string.Equals(Normalize(existing.EventId), Normalize(incoming.EventId), StringComparison.OrdinalIgnoreCase)
               && string.Equals(Normalize(existing.DeviceId), Normalize(incoming.DeviceId), StringComparison.OrdinalIgnoreCase)
               && string.Equals(Normalize(existing.Type), Normalize(incoming.Type), StringComparison.OrdinalIgnoreCase)
               && string.Equals(Normalize(existing.DocRef), Normalize(incoming.DocRef), StringComparison.OrdinalIgnoreCase)
               && string.Equals(Normalize(existing.Comment), Normalize(incoming.Comment), StringComparison.Ordinal)
               && string.Equals(Normalize(existing.ReasonCode), Normalize(incoming.ReasonCode), StringComparison.OrdinalIgnoreCase)
               && existing.PartnerId == incoming.PartnerId
               && existing.OrderId == incoming.OrderId
               && string.Equals(Normalize(existing.OrderRef), Normalize(incoming.OrderRef), StringComparison.OrdinalIgnoreCase)
               && existing.FromLocationId == incoming.FromLocationId
               && existing.ToLocationId == incoming.ToLocationId
               && string.Equals(Normalize(existing.FromHu), Normalize(incoming.FromHu), StringComparison.OrdinalIgnoreCase)
               && string.Equals(Normalize(existing.ToHu), Normalize(incoming.ToHu), StringComparison.OrdinalIgnoreCase)
               && existing.DraftOnly == incoming.DraftOnly;
    }

    private static NormalizedLineReplayPayload NormalizeLineReplayPayload(string docUid, AddDocLineRequest request)
    {
        return new NormalizedLineReplayPayload
        {
            DocUid = Normalize(docUid),
            EventId = Normalize(request.EventId),
            DeviceId = Normalize(request.DeviceId),
            LineId = null,
            Barcode = Normalize(request.Barcode),
            ItemId = request.ItemId,
            OrderLineId = request.OrderLineId,
            Qty = request.Qty,
            UomCode = Normalize(request.UomCode),
            FromLocationId = request.FromLocationId,
            ToLocationId = request.ToLocationId,
            FromHu = NormalizeHu(request.FromHu),
            ToHu = NormalizeHu(request.ToHu)
        };
    }

    private static NormalizedLineReplayPayload NormalizeUpdatedLineReplayPayload(string docUid, UpdateDocLineRequest request)
    {
        return new NormalizedLineReplayPayload
        {
            DocUid = Normalize(docUid),
            EventId = Normalize(request.EventId),
            DeviceId = Normalize(request.DeviceId),
            LineId = request.LineId,
            Qty = request.Qty,
            UomCode = Normalize(request.UomCode),
            FromLocationId = request.FromLocationId,
            ToLocationId = request.ToLocationId,
            FromHu = NormalizeHu(request.FromHu),
            ToHu = NormalizeHu(request.ToHu)
        };
    }

    private static NormalizedLineReplayPayload NormalizeDeletedLineReplayPayload(string docUid, DeleteDocLineRequest request)
    {
        return new NormalizedLineReplayPayload
        {
            DocUid = Normalize(docUid),
            EventId = Normalize(request.EventId),
            DeviceId = Normalize(request.DeviceId),
            LineId = request.LineId,
            Qty = 0
        };
    }

    private static bool TryReadStoredLineReplay(string? rawJson, out StoredDocLineReplay replay)
    {
        replay = new StoredDocLineReplay();
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<StoredDocLineReplay>(
                rawJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (parsed?.Normalized == null)
            {
                return false;
            }

            replay = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadLegacyLineReplay(string? rawJson, out AddDocLineRequest request)
    {
        request = new AddDocLineRequest();
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<AddDocLineRequest>(
                rawJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (parsed == null)
            {
                return false;
            }

            request = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsEquivalentLineReplay(NormalizedLineReplayPayload? existing, NormalizedLineReplayPayload incoming)
    {
        if (existing == null)
        {
            return false;
        }

        return string.Equals(existing.DocUid, incoming.DocUid, StringComparison.OrdinalIgnoreCase)
               && string.Equals(existing.EventId, incoming.EventId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(existing.DeviceId, incoming.DeviceId, StringComparison.OrdinalIgnoreCase)
               && existing.LineId == incoming.LineId
               && string.Equals(existing.Barcode, incoming.Barcode, StringComparison.OrdinalIgnoreCase)
               && existing.ItemId == incoming.ItemId
               && existing.OrderLineId == incoming.OrderLineId
               && existing.Qty == incoming.Qty
               && string.Equals(existing.UomCode, incoming.UomCode, StringComparison.OrdinalIgnoreCase)
               && existing.FromLocationId == incoming.FromLocationId
               && existing.ToLocationId == incoming.ToLocationId
               && string.Equals(existing.FromHu, incoming.FromHu, StringComparison.OrdinalIgnoreCase)
               && string.Equals(existing.ToHu, incoming.ToHu, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveUpdatedLineState(
        IDataStore store,
        DocType docType,
        ApiDocInfo docInfo,
        DocLine existingLine,
        UpdateDocLineRequest request,
        out long? fromLocationId,
        out long? toLocationId,
        out string? fromHu,
        out string? toHu,
        out string? validationError)
    {
        validationError = null;
        fromLocationId = null;
        toLocationId = null;
        fromHu = null;
        toHu = null;

        if (request.FromLocationId.HasValue && store.FindLocationById(request.FromLocationId.Value) == null)
        {
            validationError = "UNKNOWN_LOCATION";
            return false;
        }

        if (request.ToLocationId.HasValue && store.FindLocationById(request.ToLocationId.Value) == null)
        {
            validationError = "UNKNOWN_LOCATION";
            return false;
        }

        var requestedFromHu = NormalizeHu(request.FromHu);
        var requestedToHu = NormalizeHu(request.ToHu);
        if (!string.IsNullOrWhiteSpace(requestedFromHu))
        {
            var record = store.GetHuByCode(requestedFromHu);
            if (record == null || !IsHuAllowed(record))
            {
                validationError = "UNKNOWN_HU";
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(requestedToHu))
        {
            var record = store.GetHuByCode(requestedToHu);
            if (record == null || !IsHuAllowed(record))
            {
                validationError = "UNKNOWN_HU";
                return false;
            }
        }

        switch (docType)
        {
            case DocType.Inbound:
            case DocType.ProductionReceipt:
                toLocationId = request.ToLocationId ?? existingLine.ToLocationId ?? docInfo.ToLocationId;
                toHu = !string.IsNullOrWhiteSpace(requestedToHu)
                    ? requestedToHu
                    : NormalizeHu(existingLine.ToHu) ?? NormalizeHu(docInfo.ToHu);
                if (!toLocationId.HasValue)
                {
                    validationError = "MISSING_LOCATION";
                    return false;
                }
                break;
            case DocType.Outbound:
            case DocType.WriteOff:
                fromLocationId = request.FromLocationId ?? existingLine.FromLocationId ?? docInfo.FromLocationId;
                fromHu = !string.IsNullOrWhiteSpace(requestedFromHu)
                    ? requestedFromHu
                    : NormalizeHu(existingLine.FromHu) ?? NormalizeHu(docInfo.FromHu);
                if (!fromLocationId.HasValue)
                {
                    validationError = "MISSING_LOCATION";
                    return false;
                }
                break;
            case DocType.Move:
                fromLocationId = request.FromLocationId ?? existingLine.FromLocationId ?? docInfo.FromLocationId;
                toLocationId = request.ToLocationId ?? existingLine.ToLocationId ?? docInfo.ToLocationId;
                fromHu = !string.IsNullOrWhiteSpace(requestedFromHu)
                    ? requestedFromHu
                    : NormalizeHu(existingLine.FromHu) ?? NormalizeHu(docInfo.FromHu);
                toHu = !string.IsNullOrWhiteSpace(requestedToHu)
                    ? requestedToHu
                    : NormalizeHu(existingLine.ToHu) ?? NormalizeHu(docInfo.ToHu);
                if (!fromLocationId.HasValue || !toLocationId.HasValue)
                {
                    validationError = "MISSING_LOCATION";
                    return false;
                }
                break;
            case DocType.Inventory:
                toLocationId = request.ToLocationId ?? existingLine.ToLocationId ?? docInfo.ToLocationId;
                toHu = !string.IsNullOrWhiteSpace(requestedToHu)
                    ? requestedToHu
                    : NormalizeHu(existingLine.ToHu) ?? NormalizeHu(docInfo.ToHu);
                if (!toLocationId.HasValue)
                {
                    validationError = "MISSING_LOCATION";
                    return false;
                }
                break;
        }

        return true;
    }

    private static string ResolveCurrentDocStatus(IDataStore store, ApiDocInfo? docInfo)
    {
        if (docInfo != null)
        {
            var doc = store.GetDoc(docInfo.DocId);
            if (doc != null)
            {
                return doc.Status.ToString().ToUpperInvariant();
            }

            if (!string.IsNullOrWhiteSpace(docInfo.Status))
            {
                return docInfo.Status;
            }
        }

        return "DRAFT";
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed class StoredDocLineReplay
    {
        public NormalizedLineReplayPayload? Normalized { get; init; }
        public string? DocStatus { get; init; }
        public DocLineReplayResponse? Line { get; init; }
    }

    private sealed class NormalizedLineReplayPayload
    {
        public string? DocUid { get; init; }
        public string? EventId { get; init; }
        public string? DeviceId { get; init; }
        public long? LineId { get; init; }
        public string? Barcode { get; init; }
        public long? ItemId { get; init; }
        public long? OrderLineId { get; init; }
        public double Qty { get; init; }
        public string? UomCode { get; init; }
        public long? FromLocationId { get; init; }
        public long? ToLocationId { get; init; }
        public string? FromHu { get; init; }
        public string? ToHu { get; init; }
    }

    private sealed class DocLineReplayResponse
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("replaces_line_id")]
        public long? ReplacesLineId { get; init; }

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("qty")]
        public double Qty { get; init; }

        [JsonPropertyName("uom_code")]
        public string? UomCode { get; init; }

        [JsonPropertyName("order_line_id")]
        public long? OrderLineId { get; init; }

        [JsonPropertyName("from_location_id")]
        public long? FromLocationId { get; init; }

        [JsonPropertyName("to_location_id")]
        public long? ToLocationId { get; init; }

        [JsonPropertyName("from_hu")]
        public string? FromHu { get; init; }

        [JsonPropertyName("to_hu")]
        public string? ToHu { get; init; }
    }
}
