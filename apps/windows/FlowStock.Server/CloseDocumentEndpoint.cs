using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Services;
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
        IApiDocStore apiStore)
    {
        var rawJson = await ReadBody(request);
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return Results.BadRequest(new ApiResult(false, "EMPTY_BODY"));
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
            return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
        }

        if (closeRequest == null)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
        }

        if (string.IsNullOrWhiteSpace(closeRequest.EventId))
        {
            return Results.BadRequest(new ApiResult(false, "MISSING_EVENT_ID"));
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

                return Results.Ok(CanonicalCloseBehavior.BuildReplayResponse(
                    docUid,
                    replayDocInfo?.DocRef,
                    replayDoc,
                    CanonicalCloseBehavior.ResultClosed,
                    idempotentReplay: true,
                    alreadyClosed: false));
            }

            return Results.BadRequest(new ApiResult(false, "EVENT_ID_CONFLICT"));
        }

        var docInfo = apiStore.GetApiDoc(docUid);
        if (docInfo == null)
        {
            return Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND"));
        }

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

        return Results.Ok(response);
    }

    private static async Task<string> ReadBody(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        return await reader.ReadToEndAsync();
    }
}
