using System.Globalization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class OpsEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/ops", HandleAsync);
    }

    public static async Task<IResult> HandleAsync(
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

        if (!OperationEventParser.TryParse(rawJson, out var opEvent, out var parseError))
        {
            return Results.BadRequest(new ApiResult(false, parseError ?? "INVALID_JSON"));
        }

        if (opEvent == null)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
        }

        if (string.IsNullOrWhiteSpace(opEvent.EventId))
        {
            return Results.BadRequest(new ApiResult(false, "MISSING_EVENT_ID"));
        }

        if (string.IsNullOrWhiteSpace(opEvent.DocRef))
        {
            return Results.BadRequest(new ApiResult(false, "MISSING_DOC_REF"));
        }

        if (string.IsNullOrWhiteSpace(opEvent.Barcode))
        {
            return Results.BadRequest(new ApiResult(false, "MISSING_BARCODE"));
        }

        if (opEvent.Qty <= 0)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_QTY"));
        }

        var docRef = opEvent.DocRef.Trim();
        var existingEvent = apiStore.GetEvent(opEvent.EventId);
        if (existingEvent != null)
        {
            if (string.Equals(existingEvent.EventType, "OP", StringComparison.OrdinalIgnoreCase))
            {
                var replayDoc = store.FindDocByRef(docRef);
                return Results.Ok(CanonicalCloseBehavior.BuildReplayResponse(
                    docUid: null,
                    docRefHint: docRef,
                    currentDoc: replayDoc,
                    result: CanonicalCloseBehavior.ResultClosed,
                    idempotentReplay: true,
                    alreadyClosed: false));
            }

            return Results.BadRequest(new ApiResult(false, "EVENT_ID_CONFLICT"));
        }

        var opNormalized = opEvent.Op?.Trim().ToUpperInvariant();
        var isMove = string.Equals(opNormalized, "MOVE", StringComparison.Ordinal);
        var isReceive = string.Equals(opNormalized, "RECEIVE", StringComparison.Ordinal)
                        || string.Equals(opNormalized, "IN", StringComparison.Ordinal)
                        || string.Equals(opNormalized, "INBOUND", StringComparison.Ordinal);
        var isAdjustPlus = string.Equals(opNormalized, "ADJUST_PLUS", StringComparison.Ordinal);

        if (!isMove && !isReceive && !isAdjustPlus)
        {
            return Results.BadRequest(new ApiResult(false, "UNSUPPORTED_OP"));
        }

        var barcode = opEvent.Barcode.Trim();
        var item = store.FindItemByBarcode(barcode) ?? FindItemByBarcodeVariant(store, barcode);
        if (item == null)
        {
            return Results.BadRequest(new ApiResult(false, "UNKNOWN_BARCODE"));
        }

        if (!item.IsActive)
        {
            return Results.BadRequest(new ApiResult(false, "ITEM_INACTIVE"));
        }

        Location? fromLocation = null;
        if (isMove)
        {
            var fromResult = ResolveLocationForEvent(store, opEvent.FromLoc, opEvent.FromLocationId);
            if (fromResult.Error != null)
            {
                return Results.BadRequest(BuildLocationErrorResult(fromResult, opEvent, store));
            }

            fromLocation = fromResult.Location!;
        }

        var toResult = ResolveLocationForEvent(store, opEvent.ToLoc, opEvent.ToLocationId);
        if (toResult.Error != null)
        {
            return Results.BadRequest(BuildLocationErrorResult(toResult, opEvent, store));
        }

        var toLocation = toResult.Location!;
        var docType = isMove ? DocType.Move : DocType.Inbound;
        var existingDoc = store.FindDocByRef(docRef);

        if (existingDoc != null)
        {
            if (existingDoc.Type != docType)
            {
                return Results.BadRequest(new ApiResult(false, "DOC_REF_EXISTS"));
            }

            if (existingDoc.Status == DocStatus.Closed)
            {
                apiStore.RecordOpEvent(opEvent.EventId, "OP", null, opEvent.DeviceId, rawJson);
                return Results.Ok(CanonicalCloseBehavior.BuildReplayResponse(
                    docUid: null,
                    docRefHint: docRef,
                    currentDoc: existingDoc,
                    result: CanonicalCloseBehavior.ResultAlreadyClosed,
                    idempotentReplay: false,
                    alreadyClosed: true));
            }
        }

        var fromHu = isMove ? NormalizeHu(opEvent.FromHu) : null;
        var toHu = NormalizeHu(opEvent.ToHu);
        if (string.IsNullOrWhiteSpace(toHu) && !string.IsNullOrWhiteSpace(opEvent.HuCode))
        {
            toHu = NormalizeHu(opEvent.HuCode);
        }

        var missingHu = new List<string>();
        if (isMove && !string.IsNullOrWhiteSpace(fromHu) && store.GetHuByCode(fromHu) == null)
        {
            missingHu.Add("from_hu");
        }

        if (!string.IsNullOrWhiteSpace(toHu) && store.GetHuByCode(toHu) == null)
        {
            missingHu.Add("to_hu");
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

        var docId = existingDoc?.Id ?? docs.CreateDoc(docType, docRef, null, null, null, null);

        try
        {
            docs.AddDocLine(
                docId,
                item.Id,
                opEvent.Qty,
                fromLocation?.Id,
                toLocation.Id,
                null,
                null,
                fromHu,
                toHu);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new ApiResult(false, ex.Message));
        }

        var response = CanonicalCloseBehavior.Execute(
            docId,
            docUid: null,
            docRefHint: docRef,
            store,
            docs,
            () => apiStore.RecordOpEvent(opEvent.EventId, "OP", null, opEvent.DeviceId, rawJson));

        return Results.Ok(response);
    }

    private static async Task<string> ReadBody(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        return await reader.ReadToEndAsync();
    }

    private static string? NormalizeHu(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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

    private static LocationResolution ResolveLocationForEvent(IDataStore store, string? code, int? id)
    {
        if (id.HasValue)
        {
            var byId = store.FindLocationById(id.Value);
            return byId != null
                ? new LocationResolution { Location = byId }
                : new LocationResolution { Error = "UNKNOWN_LOCATION" };
        }

        var trimmed = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return new LocationResolution { Error = "MISSING_LOCATION" };
        }

        var locations = store.GetLocations();
        var byCode = locations
            .Where(location => string.Equals(location.Code, trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byCode.Count == 1)
        {
            return new LocationResolution { Location = byCode[0] };
        }

        if (byCode.Count > 1)
        {
            return new LocationResolution { Error = "AMBIGUOUS_LOCATION", Matches = byCode };
        }

        var byName = locations
            .Where(location => string.Equals(location.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byName.Count == 1)
        {
            return new LocationResolution { Location = byName[0] };
        }

        if (byName.Count > 1)
        {
            return new LocationResolution { Error = "AMBIGUOUS_LOCATION", Matches = byName };
        }

        return new LocationResolution { Error = "UNKNOWN_LOCATION" };
    }

    private static object BuildLocationErrorResult(LocationResolution resolution, OperationEventParser.OperationEventData request, IDataStore store)
    {
        var sampleCodes = store.GetLocations()
            .Select(location => location.Code)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        object? matches = null;
        if (resolution.Matches != null && resolution.Matches.Count > 0)
        {
            matches = resolution.Matches
                .Select(location => new { id = location.Id, code = location.Code, name = location.Name })
                .ToList();
        }

        return new
        {
            ok = false,
            error = resolution.Error,
            details = new
            {
                parsed = new
                {
                    from_loc = request.FromLoc,
                    to_loc = request.ToLoc,
                    from_location_id = request.FromLocationId,
                    to_location_id = request.ToLocationId
                },
                matches,
                sample_codes = sampleCodes
            }
        };
    }

    private sealed class LocationResolution
    {
        public Location? Location { get; init; }
        public string? Error { get; init; }
        public IReadOnlyList<Location>? Matches { get; init; }
    }
}
