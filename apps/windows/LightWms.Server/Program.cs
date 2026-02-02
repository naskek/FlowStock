using System.Diagnostics;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LightWms.Core.Abstractions;
using LightWms.Core.Models;
using LightWms.Core.Services;
using LightWms.Data;
using LightWms.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var dbProvider = ResolveDbProvider(builder.Configuration);
string? sqlitePath = null;
string? postgresConnectionString = null;

if (dbProvider == DbProvider.Postgres)
{
    postgresConnectionString = BuildPostgresConnectionString(builder.Configuration);
}
else
{
    sqlitePath = ResolveSqlitePath(builder.Configuration);
}

if (dbProvider == DbProvider.Postgres)
{
    builder.Services.AddSingleton<PostgresDataStore>(sp =>
    {
        var store = new PostgresDataStore(postgresConnectionString!);
        store.Initialize();
        return store;
    });
    builder.Services.AddSingleton<LightWms.Core.Abstractions.IDataStore>(sp => sp.GetRequiredService<PostgresDataStore>());
    builder.Services.AddSingleton<IApiDocStore>(new PostgresApiDocStore(postgresConnectionString!));
}
else
{
    builder.Services.AddSingleton<SqliteDataStore>(sp =>
    {
        var directory = Path.GetDirectoryName(sqlitePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var store = new SqliteDataStore(sqlitePath!);
        store.Initialize();
        return store;
    });
    builder.Services.AddSingleton<LightWms.Core.Abstractions.IDataStore>(sp => sp.GetRequiredService<SqliteDataStore>());
    builder.Services.AddSingleton<IApiDocStore>(new ApiDocStore(sqlitePath!));
}
builder.Services.AddSingleton<DocumentService>();

var app = builder.Build();

app.UseHttpsRedirection();

LogDbInfo(app.Logger, dbProvider, sqlitePath, postgresConnectionString);

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

app.MapGet("/api/diag/db", () =>
{
    var info = BuildDbInfo(dbProvider, sqlitePath, postgresConnectionString);
    return Results.Ok(info);
});

app.MapGet("/api/diag/version", (EndpointDataSource dataSource) =>
{
    return Results.Ok(BuildVersionInfo(dataSource));
});

app.MapGet("/api/diag/routes", (EndpointDataSource dataSource) =>
{
    return Results.Ok(BuildRouteList(dataSource));
});

app.MapGet("/api/diag/counts", () =>
{
    using var connection = OpenConnection(dbProvider, sqlitePath, postgresConnectionString);
    return Results.Ok(new
    {
        items = CountTable(connection, "items"),
        locations = CountTable(connection, "locations"),
        partners = CountTable(connection, "partners"),
        orders = CountTable(connection, "orders"),
        docs = CountTable(connection, "docs"),
        ledger = CountTable(connection, "ledger")
    });
});

app.MapGet("/api/locations", (IDataStore store) =>
{
    var locations = store.GetLocations()
        .Select(location => new { id = location.Id, code = location.Code, name = location.Name })
        .ToList();
    return Results.Ok(locations);
});

app.MapGet("/api/items/by-barcode/{barcode}", (string barcode, IDataStore store) =>
{
    if (string.IsNullOrWhiteSpace(barcode))
    {
        return Results.BadRequest(new ApiResult(false, "MISSING_BARCODE"));
    }

    var trimmed = barcode.Trim();
    var item = store.FindItemByBarcode(trimmed) ?? FindItemByBarcodeVariant(store, trimmed);
    if (item == null)
    {
        return Results.NotFound();
    }

    return Results.Ok(new
    {
        id = item.Id,
        name = item.Name,
        barcode = item.Barcode,
        gtin = item.Gtin,
        base_uom_code = item.BaseUom
    });
});

app.MapGet("/api/items", (HttpRequest request) =>
{
    var query = request.Query["q"].ToString();
    var search = string.IsNullOrWhiteSpace(query) ? null : $"%{query.Trim()}%";

    using var connection = OpenConnection(dbProvider, sqlitePath, postgresConnectionString);
    using var command = connection.CreateCommand();
    command.CommandText = dbProvider == DbProvider.Postgres
        ? @"
SELECT id, name, barcode, gtin, base_uom, uom
FROM items
WHERE @search::text IS NULL
   OR name ILIKE @search::text
   OR barcode ILIKE @search::text
   OR gtin ILIKE @search::text
ORDER BY name;"
        : @"
SELECT id, name, barcode, gtin, base_uom, uom
FROM items
WHERE @search IS NULL
   OR name LIKE @search COLLATE NOCASE
   OR barcode LIKE @search COLLATE NOCASE
   OR gtin LIKE @search COLLATE NOCASE
ORDER BY name;";
    AddParam(command, "@search", search ?? (object)DBNull.Value);
    using var reader = command.ExecuteReader();
    var list = new List<object>();
    while (reader.Read())
    {
        var baseUom = reader.IsDBNull(4) ? null : reader.GetString(4);
        if (string.IsNullOrWhiteSpace(baseUom) && !reader.IsDBNull(5))
        {
            baseUom = reader.GetString(5);
        }

        list.Add(new
        {
            id = reader.GetInt64(0),
            name = reader.GetString(1),
            barcode = reader.IsDBNull(2) ? null : reader.GetString(2),
            gtin = reader.IsDBNull(3) ? null : reader.GetString(3),
            base_uom_code = string.IsNullOrWhiteSpace(baseUom) ? "шт" : baseUom
        });
    }

    return Results.Ok(list);
});

app.MapGet("/api/partners", (HttpRequest request, IDataStore store) =>
{
    var role = request.Query["role"].ToString();
    var filter = ParsePartnerRole(role);
    if (filter == PartnerRoleFilter.Unknown)
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_ROLE"));
    }

    var statusMap = LoadPartnerStatuses();
    var partners = store.GetPartners();
    var list = new List<object>();
    foreach (var partner in partners)
    {
        var status = statusMap.TryGetValue(partner.Id, out var stored) ? stored : PartnerRole.Both;
        if (!ShouldIncludePartner(status, filter))
        {
            continue;
        }

        list.Add(new
        {
            id = partner.Id,
            name = partner.Name,
            code = partner.Code
        });
    }

    return Results.Ok(list);
});

app.MapGet("/api/stock", () =>
{
    using var connection = OpenConnection(dbProvider, sqlitePath, postgresConnectionString);
    using var command = connection.CreateCommand();
    command.CommandText = @"
SELECT item_id, location_id, COALESCE(SUM(qty_delta), 0) AS qty
FROM ledger
GROUP BY item_id, location_id
HAVING SUM(qty_delta) != 0
ORDER BY item_id, location_id;";
    using var reader = command.ExecuteReader();
    var rows = new List<object>();
    while (reader.Read())
    {
        rows.Add(new
        {
            item_id = reader.GetInt64(0),
            location_id = reader.GetInt64(1),
            qty = reader.GetDouble(2)
        });
    }

    return Results.Ok(rows);
});

app.MapGet("/api/stock/by-barcode/{barcode}", (string barcode, IDataStore store) =>
{
    if (string.IsNullOrWhiteSpace(barcode))
    {
        return Results.BadRequest(new ApiResult(false, "MISSING_BARCODE"));
    }

    var trimmed = barcode.Trim();
    var item = store.FindItemByBarcode(trimmed) ?? FindItemByBarcodeVariant(store, trimmed);
    if (item == null)
    {
        return Results.NotFound(new ApiResult(false, "UNKNOWN_BARCODE"));
    }

    using var connection = OpenConnection(dbProvider, sqlitePath, postgresConnectionString);
    using var totalsCommand = connection.CreateCommand();
    totalsCommand.CommandText = @"
SELECT l.id, l.code, COALESCE(SUM(led.qty_delta), 0) AS qty
FROM ledger led
INNER JOIN locations l ON l.id = led.location_id
WHERE led.item_id = @item_id
GROUP BY l.id, l.code
HAVING SUM(led.qty_delta) != 0
ORDER BY l.code;";
    AddParam(totalsCommand, "@item_id", item.Id);
    using var totalsReader = totalsCommand.ExecuteReader();
    var totals = new List<object>();
    while (totalsReader.Read())
    {
        totals.Add(new
        {
            location_id = totalsReader.GetInt64(0),
            location_code = totalsReader.GetString(1),
            qty = totalsReader.GetDouble(2)
        });
    }

    using var byHuCommand = connection.CreateCommand();
    byHuCommand.CommandText = @"
SELECT COALESCE(led.hu_code, led.hu) AS hu, l.id, l.code, COALESCE(SUM(led.qty_delta), 0) AS qty
FROM ledger led
INNER JOIN locations l ON l.id = led.location_id
WHERE led.item_id = @item_id
  AND COALESCE(led.hu_code, led.hu) IS NOT NULL
  AND COALESCE(led.hu_code, led.hu) <> ''
GROUP BY hu, l.id, l.code
HAVING SUM(led.qty_delta) != 0
ORDER BY hu, l.code;";
    AddParam(byHuCommand, "@item_id", item.Id);
    using var byHuReader = byHuCommand.ExecuteReader();
    var byHu = new List<object>();
    while (byHuReader.Read())
    {
        byHu.Add(new
        {
            hu = byHuReader.GetString(0),
            location_id = byHuReader.GetInt64(1),
            location_code = byHuReader.GetString(2),
            qty = byHuReader.GetDouble(3)
        });
    }

    return Results.Ok(new
    {
        totalsByLocation = totals,
        byHu
    });
});

app.MapGet("/api/hu-stock", (IDataStore store) =>
{
    var rows = store.GetHuStockRows()
        .Select(row => new
        {
            hu = row.HuCode,
            item_id = row.ItemId,
            location_id = row.LocationId,
            qty = row.Qty
        })
        .ToList();

    return Results.Ok(rows);
});

app.MapGet("/api/hus", (HttpRequest request) =>
{
    var takeText = request.Query["take"].ToString();
    var take = 200;
    if (!string.IsNullOrWhiteSpace(takeText) && int.TryParse(takeText, out var parsed))
    {
        take = parsed;
    }

    if (take < 1)
    {
        take = 1;
    }
    if (take > 1000)
    {
        take = 1000;
    }

    using var connection = OpenConnection(dbProvider, sqlitePath, postgresConnectionString);
    using var command = connection.CreateCommand();
    command.CommandText = @"
SELECT id, hu_code, status, created_at, created_by, closed_at, note
FROM hus
ORDER BY id DESC
LIMIT @take;";
    AddParam(command, "@take", take);
    using var reader = command.ExecuteReader();
    var list = new List<object>();
    while (reader.Read())
    {
        list.Add(new
        {
            id = reader.GetInt64(0),
            hu_code = reader.GetString(1),
            status = reader.GetString(2),
            created_at = reader.GetString(3),
            created_by = reader.IsDBNull(4) ? null : reader.GetString(4),
            closed_at = reader.IsDBNull(5) ? null : reader.GetString(5),
            note = reader.IsDBNull(6) ? null : reader.GetString(6)
        });
    }

    return Results.Ok(list);
});

app.MapGet("/api/hus/{huCode}", (string huCode, IDataStore store) =>
{
    if (string.IsNullOrWhiteSpace(huCode))
    {
        return Results.BadRequest(new ApiResult(false, "MISSING_HU"));
    }

    var normalized = NormalizeHu(huCode);
    if (string.IsNullOrWhiteSpace(normalized))
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_HU"));
    }

    var record = store.GetHuByCode(normalized);
    if (record == null)
    {
        return Results.Ok(new ApiResult(false, "UNKNOWN_HU"));
    }

    return Results.Ok(new
    {
        ok = true,
        hu = new
        {
            id = record.Id,
            hu_code = record.Code,
            status = record.Status,
            created_at = record.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            created_by = record.CreatedBy,
            closed_at = record.ClosedAt?.ToString("O", CultureInfo.InvariantCulture),
            note = record.Note
        }
    });
});

app.MapPost("/api/hus/generate", (HuGenerateRequest request) =>
{
    var count = request.Count;
    if (count < 1 || count > 1000)
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_COUNT"));
    }

    var createdBy = string.IsNullOrWhiteSpace(request.CreatedBy) ? null : request.CreatedBy.Trim();
    var createdAt = DateTime.Now.ToString("s", CultureInfo.InvariantCulture);
    var codes = new List<string>(count);

    using var connection = OpenConnection(dbProvider, sqlitePath, postgresConnectionString);
    using var transaction = connection.BeginTransaction();
    try
    {
        for (var i = 0; i < count; i++)
        {
            var tmpCode = "TMP-" + Guid.NewGuid().ToString("N");
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = dbProvider == DbProvider.Postgres
                ? @"
INSERT INTO hus(hu_code, status, created_at, created_by)
VALUES(@hu_code, 'OPEN', @created_at, @created_by)
RETURNING id;
"
                : @"
INSERT INTO hus(hu_code, status, created_at, created_by)
VALUES(@hu_code, 'OPEN', @created_at, @created_by);
SELECT last_insert_rowid();
";
            AddParam(insert, "@hu_code", tmpCode);
            AddParam(insert, "@created_at", createdAt);
            AddParam(insert, "@created_by", createdBy ?? (object)DBNull.Value);
            var id = (long)(insert.ExecuteScalar() ?? 0L);
            var huCode = $"HU-{id:000000}";

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE hus SET hu_code = @hu_code WHERE id = @id;";
            AddParam(update, "@hu_code", huCode);
            AddParam(update, "@id", id);
            update.ExecuteNonQuery();

            codes.Add(huCode);
        }

        transaction.Commit();
    }
    catch
    {
        transaction.Rollback();
        throw;
    }

    return Results.Ok(new { ok = true, hus = codes });
});

// Example (RECEIVE):
// curl.exe -k -X POST "https://localhost:7153/api/ops" -H "Content-Type: application/json" ^
//   -d "{\"schema_version\":1,\"event_id\":\"...\",\"ts\":\"2026-01-27T18:45:00Z\",\"device_id\":\"CT48-01\",\"op\":\"RECEIVE\",\"doc_ref\":\"IN-ONLINE-0001\",\"barcode\":\"4660011933641\",\"qty\":10,\"to_loc\":\"A1\"}"
app.MapPost("/api/ops", async (HttpRequest request, IDataStore store, DocumentService docs, IApiDocStore apiStore) =>
{
    string rawJson;
    using (var reader = new StreamReader(request.Body))
    {
        rawJson = await reader.ReadToEndAsync();
    }

    if (string.IsNullOrWhiteSpace(rawJson))
    {
        return Results.BadRequest(new ApiResult(false, "EMPTY_BODY"));
    }

    if (!OperationEventParser.TryParse(rawJson, out var opEvent, out var parseError))
    {
        return Results.BadRequest(new ApiResult(false, parseError ?? "INVALID_JSON"));
    }

    if (string.IsNullOrWhiteSpace(opEvent.EventId))
    {
        return Results.BadRequest(new ApiResult(false, "MISSING_EVENT_ID"));
    }

    if (apiStore.IsEventProcessed(opEvent.EventId))
    {
        return Results.Ok(new ApiResult(true));
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

    var barcode = opEvent.Barcode.Trim();
    var item = store.FindItemByBarcode(barcode) ?? FindItemByBarcodeVariant(store, barcode);
    if (item == null)
    {
        return Results.BadRequest(new ApiResult(false, "UNKNOWN_BARCODE"));
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

    var docRef = opEvent.DocRef.Trim();
    var docType = isMove ? DocType.Move : DocType.Inbound;
    var existingDoc = store.FindDocByRef(docRef, docType);
    long docId;

    if (existingDoc != null)
    {
        if (existingDoc.Status == DocStatus.Closed)
        {
            return Results.BadRequest(new ApiResult(false, "DOC_ALREADY_CLOSED"));
        }

        docId = existingDoc.Id;
    }
    else
    {
        docId = docs.CreateDoc(docType, docRef, null, null, null, null);
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

    // Online ops close immediately to keep server state authoritative.
    var closeResult = docs.TryCloseDoc(docId, allowNegative: false);
    if (!closeResult.Success)
    {
        var error = closeResult.Errors.Count > 0
            ? string.Join("; ", closeResult.Errors)
            : "CLOSE_FAILED";
        return Results.Ok(new ApiResult(false, error));
    }

    apiStore.RecordOpEvent(opEvent.EventId, "OP", null, opEvent.DeviceId, rawJson);
    return Results.Ok(new ApiResult(true));
});

app.MapPost("/api/docs", async (HttpRequest request, IDataStore store, DocumentService docs, IApiDocStore apiStore) =>
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
    if (docType == null || docType is not (DocType.Inbound or DocType.Outbound or DocType.Move))
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_TYPE"));
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
        var expectedType = DocTypeMapper.ToOpString(docType.Value);
        if (!string.IsNullOrWhiteSpace(existingDocInfo.DocType)
            && !string.Equals(existingDocInfo.DocType, expectedType, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID"));
        }

        if (createRequest.PartnerId.HasValue && existingDocInfo.PartnerId != createRequest.PartnerId)
        {
            return Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID"));
        }

        if (createRequest.FromLocationId.HasValue && existingDocInfo.FromLocationId != createRequest.FromLocationId)
        {
            return Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID"));
        }

        if (createRequest.ToLocationId.HasValue && existingDocInfo.ToLocationId != createRequest.ToLocationId)
        {
            return Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID"));
        }

        var fromHu = NormalizeHu(createRequest.FromHu);
        var toHu = NormalizeHu(createRequest.ToHu);
        if (!string.IsNullOrWhiteSpace(fromHu) && !string.Equals(existingDocInfo.FromHu, fromHu, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID"));
        }

        if (!string.IsNullOrWhiteSpace(toHu) && !string.Equals(existingDocInfo.ToHu, toHu, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID"));
        }

        if (!string.IsNullOrWhiteSpace(createRequest.DocRef)
            && !string.Equals(existingDocInfo.DocRef, createRequest.DocRef.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new ApiResult(false, "DUPLICATE_DOC_UID"));
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

    var partnerId = createRequest.PartnerId;
    if (docType == DocType.Inbound || docType == DocType.Outbound)
    {
        if (!partnerId.HasValue)
        {
            return Results.BadRequest(new ApiResult(false, "MISSING_PARTNER"));
        }

        if (store.GetPartner(partnerId.Value) == null)
        {
            return Results.BadRequest(new ApiResult(false, "UNKNOWN_PARTNER"));
        }
    }

    var fromLocationId = createRequest.FromLocationId;
    var toLocationId = createRequest.ToLocationId;
    if (docType == DocType.Move || docType == DocType.Outbound)
    {
        if (!fromLocationId.HasValue)
        {
            return Results.BadRequest(new ApiResult(false, "MISSING_LOCATION"));
        }

        if (store.FindLocationById(fromLocationId.Value) == null)
        {
            return Results.BadRequest(new ApiResult(false, "UNKNOWN_LOCATION"));
        }
    }

    if (docType == DocType.Move || docType == DocType.Inbound)
    {
        if (!toLocationId.HasValue)
        {
            return Results.BadRequest(new ApiResult(false, "MISSING_LOCATION"));
        }

        if (store.FindLocationById(toLocationId.Value) == null)
        {
            return Results.BadRequest(new ApiResult(false, "UNKNOWN_LOCATION"));
        }
    }

    var normalizedFromHu = NormalizeHu(createRequest.FromHu);
    var normalizedToHu = NormalizeHu(createRequest.ToHu);

    var missingHu = new List<string>();
    if (!string.IsNullOrWhiteSpace(normalizedFromHu))
    {
        var record = store.GetHuByCode(normalizedFromHu);
        if (record == null || !IsHuAllowed(record))
        {
            missingHu.Add("from_hu");
        }
    }
    if (!string.IsNullOrWhiteSpace(normalizedToHu))
    {
        var record = store.GetHuByCode(normalizedToHu);
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

    var docRef = string.IsNullOrWhiteSpace(createRequest.DocRef)
        ? docs.GenerateDocRef(docType.Value, DateTime.Now)
        : createRequest.DocRef.Trim();

    long docId;
    try
    {
        docId = docs.CreateDoc(docType.Value, docRef, null, partnerId, null, null, null);
    }
    catch (ArgumentException)
    {
        return Results.BadRequest(new ApiResult(false, "DOC_REF_EXISTS"));
    }

    apiStore.AddApiDoc(
        docUid,
        docId,
        "DRAFT",
        DocTypeMapper.ToOpString(docType.Value),
        docRef,
        partnerId,
        fromLocationId,
        toLocationId,
        normalizedFromHu,
        normalizedToHu,
        createRequest.DeviceId);

    apiStore.RecordEvent(createRequest.EventId, "DOC_CREATE", docUid, createRequest.DeviceId, rawJson);

    return Results.Ok(new
    {
        ok = true,
        doc = new
        {
            id = docId,
            doc_uid = docUid,
            doc_ref = docRef,
            status = "DRAFT",
            type = DocTypeMapper.ToOpString(docType.Value)
        }
    });
});

app.MapPost("/api/docs/{docUid}/lines", async (string docUid, HttpRequest request, IDataStore store, DocumentService docs, IApiDocStore apiStore) =>
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
        case DocType.Outbound:
            fromLocationId = docInfo.FromLocationId;
            fromHu = NormalizeHu(docInfo.FromHu);
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
            toHu);
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
});

app.MapPost("/api/docs/{docUid}/close", async (string docUid, HttpRequest request, IDataStore store, DocumentService docs, IApiDocStore apiStore) =>
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
            return Results.Ok(new { ok = true, closed = true });
        }

        return Results.BadRequest(new ApiResult(false, "EVENT_ID_CONFLICT"));
    }

    var docInfo = apiStore.GetApiDoc(docUid);
    if (docInfo == null)
    {
        return Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND"));
    }

    var result = docs.TryCloseDoc(docInfo.DocId, allowNegative: false);
    if (!result.Success)
    {
        return Results.Ok(new
        {
            ok = false,
            closed = false,
            errors = result.Errors.Count > 0 ? result.Errors : new List<string> { "CLOSE_FAILED" }
        });
    }

    apiStore.UpdateApiDocStatus(docUid, "CLOSED");
    apiStore.RecordEvent(closeRequest.EventId, "DOC_CLOSE", docUid, closeRequest.DeviceId, rawJson);

    return Results.Ok(new
    {
        ok = true,
        closed = true,
        doc_ref = docInfo.DocRef,
        warnings = result.Warnings
    });
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

static DbProvider ResolveDbProvider(IConfiguration configuration)
{
    var provider = configuration["LIGHTWMS_DB_PROVIDER"];
    if (string.IsNullOrWhiteSpace(provider))
    {
        return DbProvider.Sqlite;
    }

    return provider.Trim().Equals("postgres", StringComparison.OrdinalIgnoreCase)
        ? DbProvider.Postgres
        : DbProvider.Sqlite;
}

static string ResolveSqlitePath(IConfiguration configuration)
{
    var configured = configuration["DbPath"];
    var path = string.IsNullOrWhiteSpace(configured) ? ServerPaths.DatabasePath : configured;
    path = Environment.ExpandEnvironmentVariables(path);
    return Path.GetFullPath(path);
}

static string BuildPostgresConnectionString(IConfiguration configuration)
{
    var host = configuration["LIGHTWMS_PG_HOST"];
    var database = configuration["LIGHTWMS_PG_DB"];
    var user = configuration["LIGHTWMS_PG_USER"];
    var password = configuration["LIGHTWMS_PG_PASSWORD"];
    var portText = configuration["LIGHTWMS_PG_PORT"];

    if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(user))
    {
        throw new InvalidOperationException("Missing postgres connection settings. Set LIGHTWMS_PG_HOST/DB/USER/PASSWORD.");
    }

    var builder = new NpgsqlConnectionStringBuilder
    {
        Host = host,
        Database = database,
        Username = user,
        Password = password ?? string.Empty
    };

    if (!string.IsNullOrWhiteSpace(portText) && int.TryParse(portText, out var port))
    {
        builder.Port = port;
    }

    return builder.ConnectionString;
}

static void LogDbInfo(ILogger logger, DbProvider provider, string? sqlitePath, string? postgresConnectionString)
{
    if (provider == DbProvider.Postgres)
    {
        var info = BuildPostgresInfo(postgresConnectionString);
        logger.LogInformation(
            "DB: postgres host={Host} db={Database} port={Port} user={User}",
            info.Host,
            info.Database,
            info.Port,
            info.Username);
        return;
    }

    var fileInfo = new FileInfo(sqlitePath ?? string.Empty);
    var exists = fileInfo.Exists;
    var sizeBytes = exists ? fileInfo.Length : 0;
    var lastWriteUtc = exists
        ? fileInfo.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture)
        : null;

    logger.LogInformation(
        "DB: {Path} exists={Exists} size={Size} lastWriteUtc={LastWriteUtc}",
        sqlitePath,
        exists,
        sizeBytes,
        lastWriteUtc);
}

static object BuildDbInfo(DbProvider provider, string? sqlitePath, string? postgresConnectionString)
{
    if (provider == DbProvider.Postgres)
    {
        var info = BuildPostgresInfo(postgresConnectionString);
        return new
        {
            provider = "postgres",
            host = info.Host,
            port = info.Port,
            database = info.Database,
            user = info.Username
        };
    }

    var fileInfo = new FileInfo(sqlitePath ?? string.Empty);
    return new
    {
        provider = "sqlite",
        dbPath = sqlitePath,
        exists = fileInfo.Exists,
        sizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
        lastWriteUtc = fileInfo.Exists ? fileInfo.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture) : null
    };
}

static object BuildVersionInfo(EndpointDataSource dataSource)
{
    var assembly = typeof(Program).Assembly;
    var location = assembly.Location;
    var buildUtc = string.IsNullOrWhiteSpace(location)
        ? null
        : File.GetLastWriteTimeUtc(location).ToString("O", CultureInfo.InvariantCulture);
    var routesCount = BuildRouteList(dataSource).Count;
    return new
    {
        version = assembly.GetName().Version?.ToString() ?? "unknown",
        buildUtc,
        routesCount
    };
}

static List<string> BuildRouteList(EndpointDataSource dataSource)
{
    var list = new List<string>();
    foreach (var endpoint in dataSource.Endpoints)
    {
        if (endpoint is not RouteEndpoint routeEndpoint)
        {
            continue;
        }

        var pattern = "/" + (routeEndpoint.RoutePattern.RawText?.TrimStart('/') ?? string.Empty);
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        if (methods == null || methods.Count == 0)
        {
            list.Add($"ANY {pattern}");
            continue;
        }

        foreach (var method in methods)
        {
            list.Add($"{method} {pattern}");
        }
    }

    return list
        .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static DbConnection OpenConnection(DbProvider provider, string? sqlitePath, string? postgresConnectionString)
{
    DbConnection connection = provider == DbProvider.Postgres
        ? new NpgsqlConnection(postgresConnectionString)
        : new SqliteConnection($"Data Source={sqlitePath}");
    connection.Open();
    return connection;
}

static long CountTable(DbConnection connection, string table)
{
    using var command = connection.CreateCommand();
    command.CommandText = $"SELECT COUNT(*) FROM {table};";
    var result = command.ExecuteScalar();
    return result == null || result == DBNull.Value ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
}

static void AddParam(DbCommand command, string name, object? value)
{
    var parameter = command.CreateParameter();
    parameter.ParameterName = name;
    parameter.Value = value ?? DBNull.Value;
    command.Parameters.Add(parameter);
}

static (string Host, int Port, string Database, string Username) BuildPostgresInfo(string? connectionString)
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return (string.Empty, 0, string.Empty, string.Empty);
    }

    var builder = new NpgsqlConnectionStringBuilder(connectionString);
    return (builder.Host, builder.Port, builder.Database, builder.Username);
}

static string? NormalizeHu(string? value)
{
    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

static LocationResolution ResolveLocationForEvent(IDataStore store, string? code, int? id)
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

static object BuildLocationErrorResult(LocationResolution resolution, OperationEventParser.OperationEventData request, IDataStore store)
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

static Item? FindItemByBarcodeVariant(IDataStore store, string barcode)
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

static async Task<string> ReadBody(HttpRequest request)
{
    using var reader = new StreamReader(request.Body);
    return await reader.ReadToEndAsync();
}

static DocType? ParseDocType(string? value)
{
    return DocTypeMapper.FromOpString(value);
}

static bool IsHuAllowed(HuRecord record)
{
    return string.Equals(record.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase)
           || string.Equals(record.Status, "OPEN", StringComparison.OrdinalIgnoreCase);
}

static IReadOnlyDictionary<long, PartnerRole> LoadPartnerStatuses()
{
    var path = GetPartnerStatusPath();
    if (!File.Exists(path))
    {
        return new Dictionary<long, PartnerRole>();
    }

    try
    {
        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var data = JsonSerializer.Deserialize<Dictionary<long, PartnerRole>>(json, options);
        return data ?? new Dictionary<long, PartnerRole>();
    }
    catch
    {
        return new Dictionary<long, PartnerRole>();
    }
}

static string GetPartnerStatusPath()
{
    var baseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LightWMS");
    return Path.Combine(baseDir, "partner_statuses.json");
}

static PartnerRoleFilter ParsePartnerRole(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return PartnerRoleFilter.Both;
    }

    var normalized = value.Trim().ToLowerInvariant();
    return normalized switch
    {
        "customer" => PartnerRoleFilter.Customer,
        "client" => PartnerRoleFilter.Customer,
        "supplier" => PartnerRoleFilter.Supplier,
        "both" => PartnerRoleFilter.Both,
        _ => PartnerRoleFilter.Unknown
    };
}

static bool ShouldIncludePartner(PartnerRole status, PartnerRoleFilter filter)
{
    return filter switch
    {
        PartnerRoleFilter.Customer => status is PartnerRole.Client or PartnerRole.Both,
        PartnerRoleFilter.Supplier => status is PartnerRole.Supplier or PartnerRole.Both,
        _ => true
    };
}

sealed class LocationResolution
{
    public Location? Location { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<Location>? Matches { get; init; }
}

enum PartnerRole
{
    Supplier,
    Client,
    Both
}

enum PartnerRoleFilter
{
    Customer,
    Supplier,
    Both,
    Unknown
}

enum DbProvider
{
    Sqlite,
    Postgres
}
