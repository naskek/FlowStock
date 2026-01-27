using System.Diagnostics;
using System.Globalization;
using System.IO;
using LightWms.Core.Models;
using LightWms.Core.Services;
using LightWms.Data;
using LightWms.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<SqliteDataStore>(sp =>
{
    Directory.CreateDirectory(ServerPaths.BaseDir);
    var store = new SqliteDataStore(ServerPaths.DatabasePath);
    store.Initialize();
    return store;
});
builder.Services.AddSingleton<LightWms.Core.Abstractions.IDataStore>(sp => sp.GetRequiredService<SqliteDataStore>());
builder.Services.AddSingleton<DocumentService>();
builder.Services.AddSingleton(new ApiDocStore(ServerPaths.DatabasePath));

var app = builder.Build();

app.UseHttpsRedirection();

app.Use(async (context, next) =>
{
    var sw = Stopwatch.StartNew();
    await next();
    sw.Stop();

    var path = context.Request.Path.Value ?? "/";
    if (!string.IsNullOrWhiteSpace(context.Request.QueryString.Value))
    {
        path += context.Request.QueryString.Value;
    }

    app.Logger.LogInformation(
        "{Method} {Path} => {StatusCode} in {Elapsed}ms",
        context.Request.Method,
        path,
        context.Response.StatusCode,
        sw.ElapsedMilliseconds);
});

var tsdRoot = ServerPaths.TsdRoot;
var tsdIndexPath = Path.Combine(tsdRoot, "index.html");
if (Directory.Exists(tsdRoot))
{
    var fileProvider = new PhysicalFileProvider(tsdRoot);
    var contentTypeProvider = new FileExtensionContentTypeProvider();
    contentTypeProvider.Mappings[".jsonl"] = "application/x-ndjson";
    contentTypeProvider.Mappings[".webmanifest"] = "application/manifest+json";

    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = fileProvider
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = fileProvider,
        ContentTypeProvider = contentTypeProvider
    });
}

app.MapGet("/api/ping", () =>
{
    return Results.Ok(new
    {
        ok = true,
        server_time = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
        version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown"
    });
});

app.MapPost("/api/docs", (CreateDocRequest request, SqliteDataStore store, DocumentService docs, ApiDocStore apiStore) =>
{
    if (!string.Equals(request.Op, "MOVE", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new ApiResult(false, "UNSUPPORTED_OP"));
    }

    var docRef = docs.GenerateDocRef(DocType.Move, DateTime.Now);
    var docId = docs.CreateDoc(DocType.Move, docRef, null, null, null, null);
    var docUid = Guid.NewGuid().ToString("N");
    apiStore.AddApiDoc(docUid, docId, "DRAFT");

    return Results.Ok(new CreateDocResponse
    {
        DocUid = docUid,
        DocRef = docRef,
        Status = "DRAFT"
    });
});

app.MapPost("/api/docs/{docUid}/lines", (string docUid, AddMoveLineRequest request, SqliteDataStore store, DocumentService docs, ApiDocStore apiStore) =>
{
    if (string.IsNullOrWhiteSpace(request.EventId))
    {
        return Results.BadRequest(new ApiResult(false, "MISSING_EVENT_ID"));
    }

    if (apiStore.IsEventProcessed(request.EventId))
    {
        return Results.Ok(new ApiResult(true));
    }

    var docInfo = apiStore.GetApiDoc(docUid);
    if (docInfo == null)
    {
        return Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND"));
    }

    if (!string.Equals(docInfo.Value.Status, "DRAFT", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new ApiResult(false, "DOC_CLOSED"));
    }

    if (string.IsNullOrWhiteSpace(request.Barcode))
    {
        return Results.BadRequest(new ApiResult(false, "MISSING_BARCODE"));
    }

    if (request.Qty <= 0)
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_QTY"));
    }

    var item = store.FindItemByBarcode(request.Barcode.Trim())
               ?? FindItemByBarcodeVariant(store, request.Barcode.Trim());
    if (item == null)
    {
        return Results.BadRequest(new ApiResult(false, "UNKNOWN_BARCODE"));
    }

    var fromLocation = ResolveLocation(store, request.FromLocCode);
    var toLocation = ResolveLocation(store, request.ToLocCode);
    if (fromLocation == null || toLocation == null)
    {
        return Results.BadRequest(new ApiResult(false, "UNKNOWN_LOCATION"));
    }

    var fromHu = NormalizeHu(request.FromHu);
    var toHu = NormalizeHu(request.ToHu);
    var available = store.GetLedgerBalance(item.Id, fromLocation.Id, fromHu);
    var reserved = apiStore.GetReservedQty(item.Id, fromLocation.Id, docUid);
    var remaining = available - reserved;

    if (remaining < request.Qty)
    {
        return Results.Ok(new ApiResult(false, "INSUFFICIENT_STOCK"));
    }

    docs.AddDocLine(docInfo.Value.DocId, item.Id, request.Qty, fromLocation.Id, toLocation.Id, null, null, fromHu, toHu);
    apiStore.AddReservationLine(docUid, item.Id, fromLocation.Id, request.Qty);
    apiStore.RecordEvent(request.EventId, "LINE", docUid);

    return Results.Ok(new ApiResult(true));
});

app.MapPost("/api/docs/{docUid}/close", (string docUid, CloseDocRequest request, SqliteDataStore store, DocumentService docs, ApiDocStore apiStore) =>
{
    if (string.IsNullOrWhiteSpace(request.EventId))
    {
        return Results.BadRequest(new ApiResult(false, "MISSING_EVENT_ID"));
    }

    if (apiStore.IsEventProcessed(request.EventId))
    {
        return Results.Ok(new ApiResult(true));
    }

    var docInfo = apiStore.GetApiDoc(docUid);
    if (docInfo == null)
    {
        return Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND"));
    }

    var result = docs.TryCloseDoc(docInfo.Value.DocId, allowNegative: false);
    if (!result.Success)
    {
        var error = result.Errors.Count > 0
            ? string.Join("; ", result.Errors)
            : "CLOSE_FAILED";
        return Results.Ok(new ApiResult(false, error));
    }

    apiStore.ClearReservations(docUid);
    apiStore.UpdateApiDocStatus(docUid, "CLOSED");
    apiStore.RecordEvent(request.EventId, "CLOSE", docUid);

    return Results.Ok(new ApiResult(true));
});

if (Directory.Exists(tsdRoot) && File.Exists(tsdIndexPath))
{
    app.MapFallback(async context =>
    {
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(tsdIndexPath);
    });
}

app.Run();

static string? NormalizeHu(string? value)
{
    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

static Location? ResolveLocation(SqliteDataStore store, string? code)
{
    if (string.IsNullOrWhiteSpace(code))
    {
        return null;
    }

    return store.FindLocationByCode(code.Trim());
}

static Item? FindItemByBarcodeVariant(SqliteDataStore store, string barcode)
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
