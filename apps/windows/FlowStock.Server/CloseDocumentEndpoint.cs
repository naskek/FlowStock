using System.Diagnostics;
using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class CloseDocumentEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/docs/{docUid}/close", HandleAsync);
    }

    public static async Task<IResult> HandleAsync(
        string docUid,
        HttpRequest request,
        IDataStore store,
        DocumentService docs,
        IApiDocStore apiStore,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("FlowStock.Server.DocumentLifecycle");
        var started = Stopwatch.StartNew();
        var path = request.Path.Value ?? "/api/docs/{docUid}/close";

        IResult LogAndReturn(
            IResult result,
            LogLevel level,
            string outcome,
            string? docRef = null,
            long? docId = null,
            string? docType = null,
            string? docStatusBefore = null,
            string? docStatusAfter = null,
            int? lineCount = null,
            int? ledgerRowsWritten = null,
            bool? apiEventWritten = null,
            bool? idempotentReplay = null,
            bool? alreadyClosed = null,
            IEnumerable<string>? errors = null,
            string? eventId = null,
            string? deviceId = null)
        {
            started.Stop();
            ServerOperationLogging.LogDocumentLifecycleOperation(
                logger,
                level,
                operation: "CloseDocument",
                path: path,
                result: outcome,
                docUid: docUid,
                docId: docId,
                docRef: docRef,
                docType: docType,
                docStatusBefore: docStatusBefore,
                docStatusAfter: docStatusAfter,
                lineCount: lineCount,
                ledgerRowsWritten: ledgerRowsWritten,
                eventId: eventId,
                deviceId: deviceId,
                apiEventWritten: apiEventWritten,
                idempotentReplay: idempotentReplay,
                alreadyClosed: alreadyClosed,
                elapsedMs: started.ElapsedMilliseconds,
                errors: errors);
            return result;
        }

        var rawJson = await ReadBody(request);
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return LogAndReturn(
                Results.BadRequest(new ApiResult(false, "EMPTY_BODY")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["EMPTY_BODY"]);
        }

        CloseDocRequest? closeRequest;
        try
        {
            closeRequest = JsonSerializer.Deserialize<CloseDocRequest>(
                rawJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return LogAndReturn(
                Results.BadRequest(new ApiResult(false, "INVALID_JSON")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["INVALID_JSON"]);
        }

        if (closeRequest == null)
        {
            return LogAndReturn(
                Results.BadRequest(new ApiResult(false, "INVALID_JSON")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["INVALID_JSON"]);
        }

        if (string.IsNullOrWhiteSpace(closeRequest.EventId))
        {
            return LogAndReturn(
                Results.BadRequest(new ApiResult(false, "MISSING_EVENT_ID")),
                LogLevel.Warning,
                outcome: "VALIDATION_FAILED",
                errors: ["MISSING_EVENT_ID"],
                deviceId: closeRequest.DeviceId);
        }

        var existingEvent = apiStore.GetEvent(closeRequest.EventId);
        if (existingEvent != null)
        {
            if (string.Equals(existingEvent.EventType, "DOC_CLOSE", StringComparison.OrdinalIgnoreCase)
                && string.Equals(existingEvent.DocUid, docUid, StringComparison.OrdinalIgnoreCase))
            {
                var replayDocInfo = apiStore.GetApiDoc(docUid);
                var replayDoc = replayDocInfo == null ? null : store.GetDoc(replayDocInfo.DocId);
                if (replayDocInfo != null)
                {
                    apiStore.UpdateApiDocStatus(docUid, "CLOSED");
                }

                var replayResponse = CanonicalCloseBehavior.BuildReplayResponse(
                    docUid,
                    replayDocInfo?.DocRef,
                    replayDoc,
                    CanonicalCloseBehavior.ResultClosed,
                    idempotentReplay: true,
                    alreadyClosed: false);

                return LogAndReturn(
                    Results.Ok(replayResponse),
                    LogLevel.Information,
                    outcome: "IDEMPOTENT_REPLAY",
                    docRef: replayResponse.DocRef,
                    docId: replayDocInfo?.DocId,
                    docType: replayDoc == null ? null : DocTypeMapper.ToOpString(replayDoc.Type),
                    docStatusBefore: replayDoc == null ? replayDocInfo?.Status : DocTypeMapper.StatusToString(replayDoc.Status),
                    docStatusAfter: replayResponse.DocStatus,
                    lineCount: replayDoc?.LineCount,
                    ledgerRowsWritten: 0,
                    eventId: closeRequest.EventId,
                    deviceId: closeRequest.DeviceId,
                    apiEventWritten: false,
                    idempotentReplay: true,
                    alreadyClosed: false);
            }

            return LogAndReturn(
                Results.BadRequest(new ApiResult(false, "EVENT_ID_CONFLICT")),
                LogLevel.Warning,
                outcome: "EVENT_ID_CONFLICT",
                eventId: closeRequest.EventId,
                deviceId: closeRequest.DeviceId,
                errors: ["EVENT_ID_CONFLICT"]);
        }

        var docInfo = apiStore.GetApiDoc(docUid);
        if (docInfo == null)
        {
            return LogAndReturn(
                Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND")),
                LogLevel.Warning,
                outcome: "DOC_NOT_FOUND",
                eventId: closeRequest.EventId,
                deviceId: closeRequest.DeviceId,
                errors: ["DOC_NOT_FOUND"]);
        }

        var currentDoc = store.GetDoc(docInfo.DocId);
        var docType = currentDoc == null ? null : DocTypeMapper.ToOpString(currentDoc.Type);
        var docStatusBefore = currentDoc == null ? docInfo.Status : DocTypeMapper.StatusToString(currentDoc.Status);
        var lineCountBefore = currentDoc?.LineCount;
        var ledgerCountBefore = store is PostgresDataStore postgresStore
            ? postgresStore.CountLedgerEntriesByDocId(docInfo.DocId)
            : (int?)null;

        var response = CanonicalCloseBehavior.Execute(
            docInfo.DocId,
            docUid,
            docInfo.DocRef,
            store,
            docs,
            () =>
            {
                apiStore.UpdateApiDocStatus(docUid, "CLOSED");
                apiStore.RecordEvent(closeRequest.EventId, "DOC_CLOSE", docUid, closeRequest.DeviceId, rawJson);
            });

        var currentDocAfter = store.GetDoc(docInfo.DocId);
        var lineCountAfter = currentDocAfter?.LineCount ?? lineCountBefore;
        var ledgerRowsWritten = store is PostgresDataStore postgresStoreAfter && ledgerCountBefore.HasValue
            ? postgresStoreAfter.CountLedgerEntriesByDocId(docInfo.DocId) - ledgerCountBefore.Value
            : (int?)null;

        var responseErrors = response.Errors?.Count > 0 ? response.Errors : Array.Empty<string>();
        var responseLevel = response.Ok ? LogLevel.Information : LogLevel.Warning;
        var responseOutcome = response.Ok
            ? response.IdempotentReplay ? "IDEMPOTENT_REPLAY" : response.Result
            : (responseErrors.Contains("DOC_NOT_FOUND", StringComparer.OrdinalIgnoreCase) ? "DOC_NOT_FOUND" : response.Result);

        return LogAndReturn(
            Results.Ok(response),
            responseLevel,
            outcome: responseOutcome,
            docRef: response.DocRef ?? docInfo.DocRef,
            docId: docInfo.DocId,
            docType: currentDocAfter == null ? docType : DocTypeMapper.ToOpString(currentDocAfter.Type),
            docStatusBefore: docStatusBefore,
            docStatusAfter: response.DocStatus,
            lineCount: lineCountAfter,
            ledgerRowsWritten: response.Ok ? Math.Max(0, ledgerRowsWritten ?? 0) : 0,
            eventId: closeRequest.EventId,
            deviceId: closeRequest.DeviceId,
            apiEventWritten: response.Ok && !response.IdempotentReplay,
            idempotentReplay: response.IdempotentReplay,
            alreadyClosed: response.AlreadyClosed,
            errors: response.Ok ? null : responseErrors);
    }

    private static async Task<string> ReadBody(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        return await reader.ReadToEndAsync();
    }
}
