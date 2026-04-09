using System.Diagnostics;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;
using FlowStock.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var postgresConnectionString = BuildPostgresConnectionString(builder.Configuration);

builder.Services.AddSingleton<PostgresDataStore>(_ =>
{
    return new PostgresDataStore(postgresConnectionString);
});
builder.Services.AddSingleton<FlowStock.Core.Abstractions.IDataStore>(sp => sp.GetRequiredService<PostgresDataStore>());
builder.Services.AddSingleton<IApiDocStore>(new PostgresApiDocStore(postgresConnectionString));
builder.Services.AddSingleton<DocumentService>();

var app = builder.Build();

OrderCreateEndpoint.Map(app);
OrderUpdateEndpoint.Map(app);
OrderDeleteEndpoint.Map(app);
OrderStatusEndpoint.Map(app);

app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health/ready", async (CancellationToken cancellationToken) =>
{
    try
    {
        await using var connection = new NpgsqlConnection(postgresConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT COUNT(*)
FROM schema_migrations;";
        var appliedMigrations = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
        if (appliedMigrations <= 0)
        {
            return Results.Json(new { status = "not_ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new { status = "ready" });
    }
    catch
    {
        return Results.Json(new { status = "not_ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/health"),
    branch => branch.UseHttpsRedirection());

LogDbInfo(app.Logger, postgresConnectionString);

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
var pcRoot = ServerPaths.PcRoot;
var pcIndexPath = Path.Combine(pcRoot, "index.html");
var pcPort = ResolvePcPort(builder.Configuration);

app.Use(async (context, next) =>
{
    if (context.Connection.LocalPort != pcPort
        && context.Request.Path.StartsWithSegments("/pc", out var remaining))
    {
        var host = context.Request.Host.Host;
        var path = remaining.HasValue ? remaining.Value : "/";
        var query = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : string.Empty;
        var target = $"{context.Request.Scheme}://{host}:{pcPort}{path}{query}";
        context.Response.Redirect(target, false);
        return;
    }

    await next();
});

if (Directory.Exists(pcRoot) && File.Exists(pcIndexPath))
{
    var pcProvider = new PhysicalFileProvider(pcRoot);
    var pcContentTypes = new FileExtensionContentTypeProvider();
    pcContentTypes.Mappings[".webmanifest"] = "application/manifest+json";

    app.UseWhen(
        context => context.Connection.LocalPort == pcPort
                   && !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase),
        pcApp =>
        {
            pcApp.UseDefaultFiles(new DefaultFilesOptions { FileProvider = pcProvider });
            pcApp.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = pcProvider,
                ContentTypeProvider = pcContentTypes
            });
            pcApp.Use(async (context, next) =>
            {
                await next();
                if (context.Response.StatusCode != StatusCodes.Status404NotFound)
                {
                    return;
                }

                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(pcIndexPath);
            });
        });
}

if (Directory.Exists(tsdRoot) && File.Exists(tsdIndexPath))
{
    var tsdProvider = new PhysicalFileProvider(tsdRoot);
    var tsdContentTypes = new FileExtensionContentTypeProvider();
    tsdContentTypes.Mappings[".jsonl"] = "application/x-ndjson";
    tsdContentTypes.Mappings[".webmanifest"] = "application/manifest+json";

    app.UseWhen(
        context => context.Connection.LocalPort != pcPort
                   && !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase),
        tsdApp =>
        {
            tsdApp.UseDefaultFiles(new DefaultFilesOptions { FileProvider = tsdProvider });
            tsdApp.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = tsdProvider,
                ContentTypeProvider = tsdContentTypes
            });
            tsdApp.Use(async (context, next) =>
            {
                await next();
                if (context.Response.StatusCode != StatusCodes.Status404NotFound)
                {
                    return;
                }

                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(tsdIndexPath);
            });
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

app.MapGet("/api/client-blocks", (IDataStore store) =>
{
    return Results.Ok(new
    {
        ok = true,
        blocks = BuildClientBlockStates(store.GetClientBlockSettings())
    });
});

app.MapPost("/api/tsd/login", async (HttpRequest request, IDataStore store) =>
{
    var rawJson = await ReadBody(request);
    if (string.IsNullOrWhiteSpace(rawJson))
    {
        return Results.BadRequest(new ApiResult(false, "EMPTY_BODY"));
    }

    TsdLoginRequest? loginRequest;
    try
    {
        loginRequest = JsonSerializer.Deserialize<TsdLoginRequest>(
            rawJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (JsonException)
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
    }

    if (loginRequest == null
        || string.IsNullOrWhiteSpace(loginRequest.Login)
        || string.IsNullOrWhiteSpace(loginRequest.Password))
    {
        return Results.BadRequest(new ApiResult(false, "MISSING_CREDENTIALS"));
    }

    using var connection = OpenConnection(postgresConnectionString);
    using var command = connection.CreateCommand();
    command.CommandText = @"
SELECT id, device_id, password_salt, password_hash, password_iterations, is_active, platform
FROM tsd_devices
WHERE login = @login;";
    AddParam(command, "@login", loginRequest.Login.Trim());

    using var reader = command.ExecuteReader();
    if (!reader.Read())
    {
        return Results.Json(new ApiResult(false, "INVALID_CREDENTIALS"), statusCode: StatusCodes.Status401Unauthorized);
    }

    var id = reader.GetInt64(0);
    var deviceId = reader.GetString(1);
    var salt = reader.GetString(2);
    var hash = reader.GetString(3);
    var iterations = reader.GetInt32(4);
    var isActive = reader.GetBoolean(5);
    var platform = reader.IsDBNull(6) ? "TSD" : reader.GetString(6);
    var platformNormalized = NormalizeDevicePlatform(platform);

    if (!isActive)
    {
        return Results.Json(new ApiResult(false, "DEVICE_BLOCKED"), statusCode: StatusCodes.Status401Unauthorized);
    }

    if (!TryVerifyPassword(loginRequest.Password, salt, hash, iterations))
    {
        return Results.Json(new ApiResult(false, "INVALID_CREDENTIALS"), statusCode: StatusCodes.Status401Unauthorized);
    }

    reader.Close();
    using var update = connection.CreateCommand();
    update.CommandText = "UPDATE tsd_devices SET last_seen = @last_seen WHERE id = @id;";
    AddParam(update, "@last_seen", DateTime.Now.ToString("s", CultureInfo.InvariantCulture));
    AddParam(update, "@id", id);
    update.ExecuteNonQuery();

    return Results.Ok(new
    {
        ok = true,
        device_id = deviceId,
        platform = platformNormalized,
        pc_port = pcPort,
        blocks = BuildClientBlockStates(store.GetClientBlockSettings())
    });
});

app.MapGet("/api/diag/db", () =>
{
    var info = BuildDbInfo(postgresConnectionString);
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
    using var connection = OpenConnection(postgresConnectionString);
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
        base_uom_code = item.BaseUom,
        max_qty_per_hu = item.MaxQtyPerHu,
        brand = item.Brand,
        volume = item.Volume
    });
});

app.MapGet("/api/items", (HttpRequest request) =>
{
    var query = request.Query["q"].ToString();
    var search = string.IsNullOrWhiteSpace(query) ? null : $"%{query.Trim()}%";

    using var connection = OpenConnection(postgresConnectionString);
    using var command = connection.CreateCommand();
   command.CommandText = @"
SELECT id, name, barcode, gtin, base_uom, uom, max_qty_per_hu, brand, volume
FROM items
WHERE @search::text IS NULL
   OR name ILIKE @search::text
   OR barcode ILIKE @search::text
   OR gtin ILIKE @search::text
ORDER BY name;"
    ;
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
            base_uom_code = string.IsNullOrWhiteSpace(baseUom) ? "шт" : baseUom,
            max_qty_per_hu = reader.IsDBNull(6) ? (double?)null : Convert.ToDouble(reader.GetValue(6), CultureInfo.InvariantCulture),
            brand = reader.IsDBNull(7) ? null : reader.GetString(7),
            volume = reader.IsDBNull(8) ? null : reader.GetString(8)
        });
    }

    return Results.Ok(list);
});

app.MapPost("/api/item-requests", async (HttpRequest request, IDataStore store) =>
{
    var rawJson = await ReadBody(request);
    if (string.IsNullOrWhiteSpace(rawJson))
    {
        return Results.BadRequest(new ApiResult(false, "EMPTY_BODY"));
    }

    ItemRequestCreateRequest? createRequest;
    try
    {
        createRequest = JsonSerializer.Deserialize<ItemRequestCreateRequest>(
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

    var barcode = createRequest.Barcode?.Trim();
    var comment = createRequest.Comment?.Trim();
    if (string.IsNullOrWhiteSpace(barcode))
    {
        return Results.BadRequest(new ApiResult(false, "MISSING_BARCODE"));
    }

    if (string.IsNullOrWhiteSpace(comment))
    {
        return Results.BadRequest(new ApiResult(false, "MISSING_COMMENT"));
    }

    var itemRequest = new ItemRequest
    {
        Barcode = barcode,
        Comment = comment,
        DeviceId = string.IsNullOrWhiteSpace(createRequest.DeviceId) ? null : createRequest.DeviceId.Trim(),
        Login = string.IsNullOrWhiteSpace(createRequest.Login) ? null : createRequest.Login.Trim(),
        CreatedAt = DateTime.Now,
        Status = "NEW"
    };

    store.AddItemRequest(itemRequest);
    return Results.Ok(new ApiResult(true));
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

app.MapGet("/api/docs", (HttpRequest request, IDataStore store) =>
{
    var op = request.Query["op"].ToString();
    var status = request.Query["status"].ToString();
    var typeFilter = string.IsNullOrWhiteSpace(op) ? null : DocTypeMapper.FromOpString(op);
    var statusFilter = string.IsNullOrWhiteSpace(status) ? null : DocTypeMapper.StatusFromString(status);

    var docs = store.GetDocs();
    if (typeFilter.HasValue)
    {
        docs = docs.Where(doc => doc.Type == typeFilter.Value).ToList();
    }
    if (statusFilter.HasValue)
    {
        docs = docs.Where(doc => doc.Status == statusFilter.Value).ToList();
    }

    var list = docs
        .OrderByDescending(doc => doc.CreatedAt)
        .Select(MapDoc)
        .ToList();
    return Results.Ok(list);
});

app.MapGet("/api/docs/next-ref", (HttpRequest request, DocumentService docs) =>
{
    var type = request.Query["type"].ToString();
    var docType = ParseDocType(type);
    if (docType == null)
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_TYPE"));
    }

    var docRef = docs.GenerateDocRef(docType.Value, DateTime.Now);
    return Results.Ok(new
    {
        ok = true,
        doc_ref = docRef
    });
});

app.MapGet("/api/docs/{docId:long}", (long docId, IDataStore store) =>
{
    var doc = store.GetDoc(docId);
    if (doc == null)
    {
        return Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND"));
    }

    return Results.Ok(MapDoc(doc));
});

app.MapGet("/api/docs/{docId:long}/lines", (long docId, IDataStore store) =>
{
    var doc = store.GetDoc(docId);
    if (doc == null)
    {
        return Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND"));
    }

    var lines = store.GetDocLineViews(docId)
        .Select(MapDocLine)
        .ToList();
    return Results.Ok(lines);
});

app.MapGet("/api/orders", (HttpRequest request, IDataStore store) =>
{
    var query = request.Query["q"].ToString();
    var normalized = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
    var includeInternal = string.Equals(request.Query["include_internal"], "1", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(request.Query["include_internal"], "true", StringComparison.OrdinalIgnoreCase);

    var orderService = new OrderService(store);
    var orders = orderService.GetOrders();
    if (!includeInternal)
    {
        orders = orders.Where(order => order.Type == OrderType.Customer).ToList();
    }

    if (!string.IsNullOrWhiteSpace(normalized))
    {
        orders = orders
            .Where(order =>
                order.OrderRef.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(order.PartnerName)
                    && order.PartnerName.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(order.PartnerCode)
                    && order.PartnerCode.Contains(normalized, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    var list = orders.Select(MapOrder).ToList();
    return Results.Ok(list);
});

app.MapGet("/api/orders/next-ref", (IDataStore store) =>
{
    return Results.Ok(new
    {
        order_ref = GenerateNextOrderRef(store)
    });
});

app.MapGet("/api/orders/{orderId:long}", (long orderId, IDataStore store) =>
{
    var orderService = new OrderService(store);
    var order = orderService.GetOrder(orderId);
    if (order == null)
    {
        return Results.NotFound(new ApiResult(false, "ORDER_NOT_FOUND"));
    }

    return Results.Ok(MapOrder(order));
});

app.MapGet("/api/orders/{orderId:long}/lines", (long orderId, IDataStore store) =>
{
    var orderService = new OrderService(store);
    var order = orderService.GetOrder(orderId);
    if (order == null)
    {
        return Results.NotFound(new ApiResult(false, "ORDER_NOT_FOUND"));
    }

    var itemLookup = store.GetItems(null).ToDictionary(item => item.Id, item => item);
    var lines = orderService.GetOrderLineViews(orderId)
        .Select(line => new
        {
            id = line.Id,
            order_id = line.OrderId,
            item_id = line.ItemId,
            item_name = line.ItemName,
            barcode = itemLookup.TryGetValue(line.ItemId, out var item) ? item.Barcode : null,
            qty_ordered = line.QtyOrdered,
            qty_shipped = line.QtyShipped,
            qty_produced = line.QtyProduced,
            qty_left = line.QtyRemaining
        })
        .ToList();

    return Results.Ok(lines);
});

app.MapPost("/api/orders/requests/create", async (HttpRequest request, IDataStore store) =>
{
    var rawJson = await ReadBody(request);
    if (string.IsNullOrWhiteSpace(rawJson))
    {
        return Results.BadRequest(new ApiResult(false, "EMPTY_BODY"));
    }

    OrderCreateRequestCreateRequest? createRequest;
    try
    {
        createRequest = JsonSerializer.Deserialize<OrderCreateRequestCreateRequest>(
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

    var orderRef = createRequest.OrderRef?.Trim();
    if (string.IsNullOrWhiteSpace(orderRef))
    {
        return Results.BadRequest(new ApiResult(false, "MISSING_ORDER_REF"));
    }

    if (!createRequest.PartnerId.HasValue || createRequest.PartnerId.Value <= 0)
    {
        return Results.BadRequest(new ApiResult(false, "MISSING_PARTNER_ID"));
    }

    var partner = store.GetPartner(createRequest.PartnerId.Value);
    if (partner == null)
    {
        return Results.BadRequest(new ApiResult(false, "PARTNER_NOT_FOUND"));
    }

    DateTime? dueDate = null;
    if (!string.IsNullOrWhiteSpace(createRequest.DueDate))
    {
        if (!DateTime.TryParseExact(
                createRequest.DueDate.Trim(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDueDate))
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_DUE_DATE"));
        }

        dueDate = parsedDueDate.Date;
    }

    var lines = createRequest.Lines ?? new List<OrderRequestLineCreateRequest>();
    var normalizedLines = new List<object>();
    foreach (var line in lines)
    {
        if (!line.ItemId.HasValue || line.ItemId.Value <= 0)
        {
            continue;
        }

        if (line.QtyOrdered <= 0)
        {
            continue;
        }

        var item = store.FindItemById(line.ItemId.Value);
        if (item == null)
        {
            return Results.BadRequest(new ApiResult(false, "ITEM_NOT_FOUND"));
        }

        normalizedLines.Add(new
        {
            item_id = line.ItemId.Value,
            qty_ordered = line.QtyOrdered
        });
    }

    if (normalizedLines.Count == 0)
    {
        return Results.BadRequest(new ApiResult(false, "MISSING_LINES"));
    }

    var createdByLogin = string.IsNullOrWhiteSpace(createRequest.Login) ? null : createRequest.Login.Trim();
    var createdByDeviceId = string.IsNullOrWhiteSpace(createRequest.DeviceId) ? null : createRequest.DeviceId.Trim();
    if (string.IsNullOrWhiteSpace(createdByLogin) || string.IsNullOrWhiteSpace(createdByDeviceId))
    {
        return Results.Json(new ApiResult(false, "MISSING_ACCOUNT"), statusCode: StatusCodes.Status401Unauthorized);
    }

    if (!IsActivePcAccount(postgresConnectionString, createdByLogin, createdByDeviceId))
    {
        return Results.Json(new ApiResult(false, "INVALID_ACCOUNT"), statusCode: StatusCodes.Status401Unauthorized);
    }

    var payloadJson = JsonSerializer.Serialize(new
    {
        order_ref = orderRef,
        partner_id = createRequest.PartnerId.Value,
        due_date = dueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        comment = string.IsNullOrWhiteSpace(createRequest.Comment) ? null : createRequest.Comment.Trim(),
        lines = normalizedLines
    });

    var requestId = store.AddOrderRequest(new OrderRequest
    {
        RequestType = OrderRequestType.CreateOrder,
        PayloadJson = payloadJson,
        Status = OrderRequestStatus.Pending,
        CreatedAt = DateTime.Now,
        CreatedByLogin = createdByLogin,
        CreatedByDeviceId = createdByDeviceId
    });

    return Results.Ok(new
    {
        ok = true,
        request_id = requestId,
        status = OrderRequestStatus.Pending
    });
});

app.MapPost("/api/orders/requests/status", async (HttpRequest request, IDataStore store) =>
{
    var rawJson = await ReadBody(request);
    if (string.IsNullOrWhiteSpace(rawJson))
    {
        return Results.BadRequest(new ApiResult(false, "EMPTY_BODY"));
    }

    OrderStatusChangeRequestCreateRequest? statusRequest;
    try
    {
        statusRequest = JsonSerializer.Deserialize<OrderStatusChangeRequestCreateRequest>(
            rawJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (JsonException)
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
    }

    if (statusRequest == null)
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
    }

    if (!statusRequest.OrderId.HasValue || statusRequest.OrderId.Value <= 0)
    {
        return Results.BadRequest(new ApiResult(false, "MISSING_ORDER_ID"));
    }

    var order = store.GetOrder(statusRequest.OrderId.Value);
    if (order == null)
    {
        return Results.BadRequest(new ApiResult(false, "ORDER_NOT_FOUND"));
    }

    if (!TryParseManualOrderStatus(statusRequest.Status, out var nextStatus))
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_STATUS"));
    }

    var createdByLogin = string.IsNullOrWhiteSpace(statusRequest.Login) ? null : statusRequest.Login.Trim();
    var createdByDeviceId = string.IsNullOrWhiteSpace(statusRequest.DeviceId) ? null : statusRequest.DeviceId.Trim();
    if (string.IsNullOrWhiteSpace(createdByLogin) || string.IsNullOrWhiteSpace(createdByDeviceId))
    {
        return Results.Json(new ApiResult(false, "MISSING_ACCOUNT"), statusCode: StatusCodes.Status401Unauthorized);
    }

    if (!IsActivePcAccount(postgresConnectionString, createdByLogin, createdByDeviceId))
    {
        return Results.Json(new ApiResult(false, "INVALID_ACCOUNT"), statusCode: StatusCodes.Status401Unauthorized);
    }

    var payloadJson = JsonSerializer.Serialize(new
    {
        order_id = statusRequest.OrderId.Value,
        status = OrderStatusMapper.StatusToString(nextStatus)
    });

    var requestId = store.AddOrderRequest(new OrderRequest
    {
        RequestType = OrderRequestType.SetOrderStatus,
        PayloadJson = payloadJson,
        Status = OrderRequestStatus.Pending,
        CreatedAt = DateTime.Now,
        CreatedByLogin = createdByLogin,
        CreatedByDeviceId = createdByDeviceId
    });

    return Results.Ok(new
    {
        ok = true,
        request_id = requestId,
        status = OrderRequestStatus.Pending
    });
});

app.MapGet("/api/stock", () =>
{
    using var connection = OpenConnection(postgresConnectionString);
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

    using var connection = OpenConnection(postgresConnectionString);
    var totals = new List<object>();
    using (var totalsCommand = connection.CreateCommand())
    {
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
        while (totalsReader.Read())
        {
            totals.Add(new
            {
                location_id = totalsReader.GetInt64(0),
                location_code = totalsReader.GetString(1),
                qty = totalsReader.GetDouble(2)
            });
        }
    }

    var byHu = new List<object>();
    using (var byHuCommand = connection.CreateCommand())
    {
        byHuCommand.CommandText = @"
SELECT COALESCE(led.hu_code, led.hu) AS hu, l.id, l.code, COALESCE(SUM(led.qty_delta), 0) AS qty
FROM ledger led
INNER JOIN locations l ON l.id = led.location_id
WHERE led.item_id = @item_id
  AND COALESCE(led.hu_code, led.hu) IS NOT NULL
  AND COALESCE(led.hu_code, led.hu) <> ''
GROUP BY COALESCE(led.hu_code, led.hu), l.id, l.code
HAVING SUM(led.qty_delta) != 0
ORDER BY COALESCE(led.hu_code, led.hu), l.code;";
        AddParam(byHuCommand, "@item_id", item.Id);
        using var byHuReader = byHuCommand.ExecuteReader();
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
    }

    return Results.Ok(new
    {
        totalsByLocation = totals,
        byHu
    });
});

app.MapGet("/api/hu-stock", (HttpRequest request, IDataStore store) =>
{
    var orderIdText = request.Query["order_id"].ToString();
    var itemIdText = request.Query["item_id"].ToString();
    if (long.TryParse(orderIdText, out var orderId)
        && orderId > 0
        && long.TryParse(itemIdText, out var itemId)
        && itemId > 0)
    {
        var item = store.FindItemById(itemId);
        if (item == null)
        {
            return Results.Ok(new List<object>());
        }

        if (item.IsMarked)
        {
            using var connection = OpenConnection(postgresConnectionString);
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT h.hu_code, c.location_id, COUNT(*)::double precision AS qty
FROM km_code c
JOIN hus h ON h.id = c.hu_id
WHERE c.status = @status
  AND c.hu_id IS NOT NULL
  AND c.location_id IS NOT NULL
  AND h.hu_code IS NOT NULL
  AND (c.sku_id = @item_id OR (c.sku_id IS NULL AND @gtin14::text IS NOT NULL AND c.gtin14 = @gtin14::text))
  AND (
    c.order_id = @order_id::bigint
    OR EXISTS (
        SELECT 1
        FROM km_code_batch b
        WHERE b.id = c.batch_id AND b.order_id = @order_id::bigint
    )
  )
GROUP BY h.hu_code, c.location_id
ORDER BY h.hu_code;";
            AddParam(command, "@status", (short)KmCodeStatusMapper.ToInt(KmCodeStatus.OnHand));
            AddParam(command, "@item_id", itemId);
            AddParam(command, "@gtin14", string.IsNullOrWhiteSpace(item.Gtin) ? DBNull.Value : item.Gtin.Trim());
            AddParam(command, "@order_id", orderId);
            using var reader = command.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                {
                    continue;
                }

                list.Add(new
                {
                    hu = reader.GetString(0),
                    item_id = itemId,
                    location_id = reader.GetInt64(1),
                    qty = reader.GetDouble(2)
                });
            }

            return Results.Ok(list);
        }

        var filtered = store.GetHuStockRows()
            .Where(row => row.ItemId == itemId)
            .Select(row => new
            {
                hu = row.HuCode,
                item_id = row.ItemId,
                location_id = row.LocationId,
                qty = row.Qty
            })
            .ToList();

        return Results.Ok(filtered);
    }

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

    using var connection = OpenConnection(postgresConnectionString);
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

    using var connection = OpenConnection(postgresConnectionString);
    using var transaction = connection.BeginTransaction();
    try
    {
        for (var i = 0; i < count; i++)
        {
            var tmpCode = "TMP-" + Guid.NewGuid().ToString("N");
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"
INSERT INTO hus(hu_code, status, created_at, created_by)
VALUES(@hu_code, 'OPEN', @created_at, @created_by)
RETURNING id;
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
OpsEndpoint.Map(app);
DocumentDraftEndpoints.Map(app);
CloseDocumentEndpoint.Map(app);

app.Run();

static string BuildPostgresConnectionString(IConfiguration configuration)
{
    var host = configuration["FLOWSTOCK_PG_HOST"] ?? "127.0.0.1";
    var database = configuration["FLOWSTOCK_PG_DB"] ?? "flowstock";
    var user = configuration["FLOWSTOCK_PG_USER"] ?? "flowstock";
    var password = configuration["FLOWSTOCK_PG_PASSWORD"] ?? "flowstock";
    var portText = configuration["FLOWSTOCK_PG_PORT"] ?? "5432";

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

static int ResolvePcPort(IConfiguration configuration)
{
    var configured = configuration["FLOWSTOCK_PC_PORT"];
    if (!string.IsNullOrWhiteSpace(configured) && int.TryParse(configured, out var parsed))
    {
        return parsed;
    }

    var pcUrl = configuration["Kestrel:Endpoints:PcHttps:Url"];
    if (!string.IsNullOrWhiteSpace(pcUrl))
    {
        var urlValue = pcUrl.Contains("://", StringComparison.Ordinal) ? pcUrl : $"https://{pcUrl}";
        if (Uri.TryCreate(urlValue, UriKind.Absolute, out var uri) && uri.Port > 0)
        {
            return uri.Port;
        }
    }

    return 7154;
}

static void LogDbInfo(ILogger logger, string? postgresConnectionString)
{
    var info = BuildPostgresInfo(postgresConnectionString);
    logger.LogInformation(
        "DB: postgres host={Host} db={Database} port={Port} user={User}",
        info.Host,
        info.Database,
        info.Port,
        info.Username);
}

static object BuildDbInfo(string? postgresConnectionString)
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

static DbConnection OpenConnection(string? postgresConnectionString)
{
    var connection = new NpgsqlConnection(postgresConnectionString);
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
    var host = builder.Host ?? string.Empty;
    var database = builder.Database ?? string.Empty;
    var user = builder.Username ?? string.Empty;
    return (host, builder.Port, database, user);
}

static string? NormalizeHu(string? value)
{
    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

static string NormalizeDevicePlatform(string? value)
{
    var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
    return normalized switch
    {
        "PC" => "PC",
        "BOTH" => "BOTH",
        "PC+TSD" => "BOTH",
        "PC_TSD" => "BOTH",
        _ => "TSD"
    };
}

static IReadOnlyDictionary<string, bool> BuildClientBlockStates(IReadOnlyList<ClientBlockSetting> settings)
{
    return ClientBlockCatalog.MergeWithDefaults(settings);
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

static object MapOrder(Order order)
{
    return new
    {
        id = order.Id,
        order_ref = order.OrderRef,
        order_type = OrderStatusMapper.TypeToString(order.Type),
        partner_id = order.PartnerId,
        partner_name = order.PartnerName,
        partner_code = order.PartnerCode,
        due_date = order.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        status = OrderStatusMapper.StatusToDisplayName(order.Status, order.Type),
        created_at = order.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
        shipped_at = order.ShippedAt?.ToString("O", CultureInfo.InvariantCulture)
    };
}

static string GenerateNextOrderRef(IDataStore store)
{
    long max = 0;
    foreach (var order in store.GetOrders())
    {
        var orderRef = order.OrderRef?.Trim();
        if (string.IsNullOrWhiteSpace(orderRef) || !IsDigitsOnly(orderRef))
        {
            continue;
        }

        if (long.TryParse(orderRef, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value > max)
        {
            max = value;
        }
    }

    return (max + 1).ToString("D3", CultureInfo.InvariantCulture);
}

static bool IsDigitsOnly(string value)
{
    if (string.IsNullOrEmpty(value))
    {
        return false;
    }

    foreach (var ch in value)
    {
        if (!char.IsDigit(ch))
        {
            return false;
        }
    }

    return true;
}

static object MapDoc(Doc doc)
{
    return new
    {
        id = doc.Id,
        doc_ref = doc.DocRef,
        doc_uid = doc.ApiDocUid,
        op = DocTypeMapper.ToOpString(doc.Type),
        status = DocTypeMapper.StatusToString(doc.Status),
        created_at = doc.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
        closed_at = doc.ClosedAt?.ToString("O", CultureInfo.InvariantCulture),
        partner_id = doc.PartnerId,
        partner_name = doc.PartnerName,
        partner_code = doc.PartnerCode,
        order_id = doc.OrderId,
        order_ref = doc.OrderRef,
        shipping_ref = doc.ShippingRef,
        reason_code = doc.ReasonCode,
        comment = doc.Comment,
        source_device_id = doc.SourceDeviceId,
        line_count = doc.LineCount
    };
}

static object MapDocLine(DocLineView line)
{
    return new
    {
        id = line.Id,
        order_line_id = line.OrderLineId,
        item_id = line.ItemId,
        item_name = line.ItemName,
        barcode = line.Barcode,
        qty = line.Qty,
        qty_input = line.QtyInput,
        uom_code = line.UomCode,
        base_uom = line.BaseUom,
        from_location = line.FromLocation,
        to_location = line.ToLocation,
        from_hu = line.FromHu,
        to_hu = line.ToHu
    };
}

static async Task<string> ReadBody(HttpRequest request)
{
    using var reader = new StreamReader(request.Body);
    return await reader.ReadToEndAsync();
}

static bool TryParseManualOrderStatus(string? value, out OrderStatus status)
{
    status = OrderStatus.Accepted;
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    var normalized = value.Trim().ToUpperInvariant();
    switch (normalized)
    {
        case "ACCEPTED":
        case "ПРИНЯТ":
            status = OrderStatus.Accepted;
            return true;
        case "IN_PROGRESS":
        case "В ПРОЦЕССЕ":
            status = OrderStatus.InProgress;
            return true;
        default:
            return false;
    }
}

static DocType? ParseDocType(string? value)
{
    return DocTypeMapper.FromOpString(value);
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
    return Path.Combine(ServerPaths.BaseDir, "partner_statuses.json");
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

static bool TryVerifyPassword(string password, string saltBase64, string hashBase64, int iterations)
{
    if (string.IsNullOrWhiteSpace(password))
    {
        return false;
    }

    byte[] salt;
    byte[] expectedHash;
    try
    {
        salt = Convert.FromBase64String(saltBase64);
        expectedHash = Convert.FromBase64String(hashBase64);
    }
    catch (FormatException)
    {
        return false;
    }

    if (iterations <= 0)
    {
        return false;
    }

    using var derive = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
    var actual = derive.GetBytes(expectedHash.Length);
    return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
}

static bool IsActivePcAccount(string connectionString, string login, string deviceId)
{
    using var connection = OpenConnection(connectionString);
    using var command = connection.CreateCommand();
    command.CommandText = @"
SELECT 1
FROM tsd_devices
WHERE login = @login
  AND device_id = @device_id
  AND is_active = TRUE
  AND UPPER(COALESCE(platform, 'TSD')) IN ('PC', 'BOTH')
LIMIT 1;";
    AddParam(command, "@login", login);
    AddParam(command, "@device_id", deviceId);
    return command.ExecuteScalar() != null;
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


