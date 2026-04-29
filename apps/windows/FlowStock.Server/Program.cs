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
using FlowStock.Server.Maintenance;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var postgresConnectionString = BuildPostgresConnectionString(builder.Configuration);
if (OrderReservationBackfillCommand.TryRun(args, postgresConnectionString, out var maintenanceExitCode))
{
    Environment.ExitCode = maintenanceExitCode;
    return;
}

builder.Services.AddSingleton<PostgresDataStore>(_ =>
{
    return new PostgresDataStore(postgresConnectionString);
});
builder.Services.AddSingleton<FlowStock.Core.Abstractions.IDataStore>(sp => sp.GetRequiredService<PostgresDataStore>());
builder.Services.AddSingleton<IApiDocStore>(new PostgresApiDocStore(postgresConnectionString));
builder.Services.AddSingleton<DocumentService>();
builder.Services.AddSingleton<CatalogService>();
builder.Services.AddSingleton<ImportService>();
builder.Services.AddSingleton<ItemPackagingService>();
builder.Services.AddSingleton<LiveUpdateHub>();

var app = builder.Build();
var appVersion = ResolveAppVersion();

OrderCreateEndpoint.Map(app);
OrderUpdateEndpoint.Map(app);
OrderDeleteEndpoint.Map(app);
OrderStatusEndpoint.Map(app);
MaintenanceBackfillEndpoints.Map(app);
app.MapGet("/api/version", () => Results.Ok(new { version = appVersion }));

app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapMethods("/health/ready", ["GET", "HEAD"], async (CancellationToken cancellationToken) =>
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

app.Use(async (context, next) =>
{
    var rejection = TryCreateClientBlockRejection(context);
    if (rejection != null)
    {
        await rejection.ExecuteAsync(context);
        return;
    }

    await next();
});

app.Use(async (context, next) =>
{
    await next();

    if (!ShouldPublishLiveEvent(context))
    {
        return;
    }

    var liveHub = context.RequestServices.GetRequiredService<LiveUpdateHub>();
    var path = context.Request.Path.HasValue ? context.Request.Path.Value! : "/api";
    liveHub.Publish("api_write", path);
});

var tsdRoot = ServerPaths.TsdRoot;
var tsdIndexPath = Path.Combine(tsdRoot, "index.html");
var pcRoot = ServerPaths.PcRoot;
var pcIndexPath = Path.Combine(pcRoot, "index.html");

if (Directory.Exists(tsdRoot) && File.Exists(tsdIndexPath))
{
    var tsdProvider = new PhysicalFileProvider(tsdRoot);
    var tsdContentTypes = new FileExtensionContentTypeProvider();
    tsdContentTypes.Mappings[".jsonl"] = "application/x-ndjson";
    tsdContentTypes.Mappings[".webmanifest"] = "application/manifest+json";

    app.Map("/tsd", tsdApp =>
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

if (Directory.Exists(pcRoot) && File.Exists(pcIndexPath))
{
    var pcProvider = new PhysicalFileProvider(pcRoot);
    var pcContentTypes = new FileExtensionContentTypeProvider();
    pcContentTypes.Mappings[".webmanifest"] = "application/manifest+json";

    app.UseWhen(
        context => !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                   && !context.Request.Path.StartsWithSegments("/tsd", StringComparison.OrdinalIgnoreCase),
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

app.MapGet("/api/ping", () =>
{
    return Results.Ok(new
    {
        ok = true,
        server_time = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
        version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown"
    });
});

app.MapGet("/api/live", async (HttpContext context, LiveUpdateHub liveHub) =>
{
    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var (subscriberId, reader) = liveHub.Subscribe();
    var cancellationToken = context.RequestAborted;

    try
    {
        await context.Response.WriteAsync("event: connected\ndata: {}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            var waitReadTask = reader.WaitToReadAsync(cancellationToken).AsTask();
            var keepAliveTask = Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
            var completed = await Task.WhenAny(waitReadTask, keepAliveTask);

            if (completed == keepAliveTask)
            {
                await context.Response.WriteAsync(": ping\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                continue;
            }

            bool canRead;
            try
            {
                canRead = await waitReadTask;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!canRead)
            {
                break;
            }

            while (reader.TryRead(out var evt))
            {
                var payload = JsonSerializer.Serialize(new
                {
                    event_id = evt.EventId,
                    ts_utc = evt.TsUtc,
                    reason = evt.Reason,
                    path = evt.Path
                }, jsonOptions);

                await context.Response.WriteAsync($"event: changed\ndata: {payload}\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
    }
    catch (OperationCanceledException)
    {
        // client disconnected
    }
    finally
    {
        liveHub.Unsubscribe(subscriberId);
    }
});

app.MapGet("/api/client-blocks", (IDataStore store) =>
{
    return Results.Ok(new
    {
        ok = true,
        blocks = BuildClientBlockStates(store.GetClientBlockSettings())
    });
});

app.MapPost("/api/client-blocks", async (HttpRequest request, IDataStore store) =>
{
    var rawJson = await ReadBody(request);
    if (string.IsNullOrWhiteSpace(rawJson))
    {
        return Results.BadRequest(new ApiResult(false, "EMPTY_BODY"));
    }

    SaveClientBlocksRequest? saveRequest;
    try
    {
        saveRequest = JsonSerializer.Deserialize<SaveClientBlocksRequest>(
            rawJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (JsonException)
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
    }

    var blocks = saveRequest?.Blocks ?? new List<ClientBlockSettingRequest>();
    var normalized = new List<ClientBlockSetting>();
    foreach (var block in blocks)
    {
        var key = block.Key?.Trim();
        if (!ClientBlockCatalog.IsKnownKey(key))
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_BLOCK_KEY"));
        }

        normalized.Add(new ClientBlockSetting(key!, block.IsEnabled));
    }

    store.SaveClientBlockSettings(normalized);
    return Results.Ok(new ApiResult(true));
});

app.MapGet("/api/import-errors", (HttpRequest request, ImportService importService) =>
{
    var reason = request.Query["reason"].ToString();
    var normalized = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    var errors = importService.GetImportErrors(normalized)
        .Select(MapImportErrorView)
        .ToList();
    return Results.Ok(errors);
});

app.MapPost("/api/import-errors/{errorId:long}/reapply", (long errorId, ImportService importService) =>
{
    if (errorId <= 0)
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_IMPORT_ERROR_ID"));
    }

    var applied = importService.ReapplyError(errorId);
    return applied
        ? Results.Ok(new ApiResult(true))
        : Results.Ok(new ApiResult(false, "REAPPLY_FAILED"));
});

app.MapPost("/api/imports/jsonl", async (HttpRequest request, ImportService importService) =>
{
    var parsed = await ParseJsonBody<ImportJsonlRequest>(request);
    if (!parsed.IsSuccess)
    {
        return parsed.Error!;
    }

    var content = parsed.Value?.Content;
    if (string.IsNullOrWhiteSpace(content))
    {
        return Results.BadRequest(new ApiResult(false, "EMPTY_CONTENT"));
    }

    var result = importService.ImportJsonlContent(content);
    return Results.Ok(new
    {
        imported = result.Imported,
        duplicates = result.Duplicates,
        errors = result.Errors,
        documents_created = result.DocumentsCreated,
        operations_imported = result.OperationsImported,
        lines_imported = result.LinesImported,
        items_upserted = result.ItemsUpserted,
        device_ids = result.DeviceIds
    });
});

app.MapGet("/api/admin/tsd-devices", () =>
{
    using var connection = OpenConnection(postgresConnectionString);
    using var command = connection.CreateCommand();
    command.CommandText = @"
SELECT id, device_id, login, platform, is_active, created_at, last_seen
FROM tsd_devices
ORDER BY login;";
    using var reader = command.ExecuteReader();
    var list = new List<object>();
    while (reader.Read())
    {
        var platform = reader.IsDBNull(3) ? "TSD" : reader.GetString(3);
        list.Add(new
        {
            id = reader.GetInt64(0),
            device_id = reader.GetString(1),
            login = reader.GetString(2),
            platform = NormalizeDevicePlatform(platform),
            is_active = reader.GetBoolean(4),
            created_at = reader.IsDBNull(5) ? null : reader.GetString(5),
            last_seen = reader.IsDBNull(6) ? null : reader.GetString(6)
        });
    }

    return Results.Ok(list);
});

app.MapPost("/api/admin/tsd-devices", async (HttpRequest request) =>
{
    var rawJson = await ReadBody(request);
    if (string.IsNullOrWhiteSpace(rawJson))
    {
        return Results.BadRequest(new ApiResult(false, "EMPTY_BODY"));
    }

    UpsertTsdDeviceRequest? upsertRequest;
    try
    {
        upsertRequest = JsonSerializer.Deserialize<UpsertTsdDeviceRequest>(
            rawJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (JsonException)
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
    }

    if (upsertRequest == null)
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
    }

    var login = upsertRequest.Login?.Trim();
    if (string.IsNullOrWhiteSpace(login))
    {
        return Results.BadRequest(new ApiResult(false, "MISSING_LOGIN"));
    }

    var password = upsertRequest.Password ?? string.Empty;
    if (string.IsNullOrWhiteSpace(password))
    {
        return Results.BadRequest(new ApiResult(false, "MISSING_PASSWORD"));
    }

    var normalizedPlatform = NormalizeDevicePlatform(upsertRequest.Platform);
    var salt = RandomNumberGenerator.GetBytes(16);
    var hash = HashPassword(password, salt, 100_000);

    using var connection = OpenConnection(postgresConnectionString);
    try
    {
        EnsureUniqueTsdDeviceLogin(connection, login, null);
    }
    catch (InvalidOperationException)
    {
        return Results.Conflict(new ApiResult(false, "LOGIN_ALREADY_EXISTS"));
    }

    var deviceId = GenerateTsdDeviceId(connection);
    using var command = connection.CreateCommand();
    command.CommandText = @"
INSERT INTO tsd_devices(device_id, login, password_salt, password_hash, password_iterations, platform, is_active, created_at)
VALUES(@device_id, @login, @salt, @hash, @iterations, @platform, @is_active, @created_at);";
    AddParam(command, "@device_id", deviceId);
    AddParam(command, "@login", login);
    AddParam(command, "@salt", Convert.ToBase64String(salt));
    AddParam(command, "@hash", Convert.ToBase64String(hash));
    AddParam(command, "@iterations", 100_000);
    AddParam(command, "@platform", normalizedPlatform);
    AddParam(command, "@is_active", upsertRequest.IsActive);
    AddParam(command, "@created_at", DateTime.Now.ToString("s", CultureInfo.InvariantCulture));

    try
    {
        command.ExecuteNonQuery();
    }
    catch (PostgresException ex) when (string.Equals(ex.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal))
    {
        return Results.Conflict(new ApiResult(false, "LOGIN_ALREADY_EXISTS"));
    }

    return Results.Ok(new
    {
        ok = true,
        device_id = deviceId
    });
});

app.MapPost("/api/admin/tsd-devices/{id:long}", async (long id, HttpRequest request) =>
{
    var rawJson = await ReadBody(request);
    if (string.IsNullOrWhiteSpace(rawJson))
    {
        return Results.BadRequest(new ApiResult(false, "EMPTY_BODY"));
    }

    UpsertTsdDeviceRequest? upsertRequest;
    try
    {
        upsertRequest = JsonSerializer.Deserialize<UpsertTsdDeviceRequest>(
            rawJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (JsonException)
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
    }

    if (upsertRequest == null)
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
    }

    var login = upsertRequest.Login?.Trim();
    if (string.IsNullOrWhiteSpace(login))
    {
        return Results.BadRequest(new ApiResult(false, "MISSING_LOGIN"));
    }

    var normalizedPlatform = NormalizeDevicePlatform(upsertRequest.Platform);

    using var connection = OpenConnection(postgresConnectionString);
    using (var exists = connection.CreateCommand())
    {
        exists.CommandText = "SELECT 1 FROM tsd_devices WHERE id = @id LIMIT 1;";
        AddParam(exists, "@id", id);
        if (exists.ExecuteScalar() == null)
        {
            return Results.NotFound(new ApiResult(false, "DEVICE_NOT_FOUND"));
        }
    }

    try
    {
        EnsureUniqueTsdDeviceLogin(connection, login, id);
    }
    catch (InvalidOperationException)
    {
        return Results.Conflict(new ApiResult(false, "LOGIN_ALREADY_EXISTS"));
    }

    if (!string.IsNullOrWhiteSpace(upsertRequest.Password))
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = HashPassword(upsertRequest.Password, salt, 100_000);
        using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE tsd_devices
SET login = @login,
    platform = @platform,
    is_active = @is_active,
    password_salt = @salt,
    password_hash = @hash,
    password_iterations = @iterations
WHERE id = @id;";
        AddParam(command, "@login", login);
        AddParam(command, "@platform", normalizedPlatform);
        AddParam(command, "@is_active", upsertRequest.IsActive);
        AddParam(command, "@salt", Convert.ToBase64String(salt));
        AddParam(command, "@hash", Convert.ToBase64String(hash));
        AddParam(command, "@iterations", 100_000);
        AddParam(command, "@id", id);

        try
        {
            command.ExecuteNonQuery();
        }
        catch (PostgresException ex) when (string.Equals(ex.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal))
        {
            return Results.Conflict(new ApiResult(false, "LOGIN_ALREADY_EXISTS"));
        }
    }
    else
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE tsd_devices
SET login = @login,
    platform = @platform,
    is_active = @is_active
WHERE id = @id;";
        AddParam(command, "@login", login);
        AddParam(command, "@platform", normalizedPlatform);
        AddParam(command, "@is_active", upsertRequest.IsActive);
        AddParam(command, "@id", id);

        try
        {
            command.ExecuteNonQuery();
        }
        catch (PostgresException ex) when (string.Equals(ex.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal))
        {
            return Results.Conflict(new ApiResult(false, "LOGIN_ALREADY_EXISTS"));
        }
    }

    return Results.Ok(new ApiResult(true));
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
        .Select(location => new
        {
            id = location.Id,
            code = location.Code,
            name = location.Name,
            max_hu_slots = location.MaxHuSlots,
            auto_hu_distribution_enabled = location.AutoHuDistributionEnabled
        })
        .ToList();
    return Results.Ok(locations);
});

app.MapPost("/api/locations", async (HttpRequest request, CatalogService catalog) =>
{
    var parsed = await ParseJsonBody<UpsertLocationRequest>(request);
    if (!parsed.IsSuccess)
    {
        return parsed.Error!;
    }

    try
    {
        var locationId = catalog.CreateLocation(
            parsed.Value?.Code ?? string.Empty,
            parsed.Value?.Name ?? string.Empty,
            parsed.Value?.MaxHuSlots,
            parsed.Value?.AutoHuDistributionEnabled);
        return Results.Ok(new { ok = true, location_id = locationId });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
});

app.MapPost("/api/locations/{locationId:long}", async (long locationId, HttpRequest request, CatalogService catalog) =>
{
    var parsed = await ParseJsonBody<UpsertLocationRequest>(request);
    if (!parsed.IsSuccess)
    {
        return parsed.Error!;
    }

    try
    {
        catalog.UpdateLocation(
            locationId,
            parsed.Value?.Code ?? string.Empty,
            parsed.Value?.Name ?? string.Empty,
            parsed.Value?.MaxHuSlots,
            parsed.Value?.AutoHuDistributionEnabled);
        return Results.Ok(new ApiResult(true));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
});

app.MapDelete("/api/locations/{locationId:long}", (long locationId, CatalogService catalog) =>
{
    try
    {
        catalog.DeleteLocation(locationId);
        return Results.Ok(new ApiResult(true));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
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

    return Results.Ok(MapItem(item));
});

app.MapGet("/api/items", (HttpRequest request) =>
{
    var query = request.Query["q"].ToString();
    var search = string.IsNullOrWhiteSpace(query) ? null : $"%{query.Trim()}%";

    using var connection = OpenConnection(postgresConnectionString);
    using var command = connection.CreateCommand();
   command.CommandText = @"
SELECT i.id,
       i.name,
       i.is_active,
       i.barcode,
       i.gtin,
       i.base_uom,
       i.uom,
       i.default_packaging_id,
       i.brand,
       i.volume,
       i.shelf_life_months,
       i.max_qty_per_hu,
       i.tara_id,
       t.name,
       i.is_marked,
       i.item_type_id,
       it.name,
       COALESCE(it.is_visible_in_product_catalog, FALSE),
       COALESCE(it.enable_min_stock_control, FALSE),
       i.min_stock_qty
FROM items i
LEFT JOIN taras t ON t.id = i.tara_id
LEFT JOIN item_types it ON it.id = i.item_type_id
WHERE @search::text IS NULL
   OR i.name ILIKE @search::text
   OR i.barcode ILIKE @search::text
   OR i.gtin ILIKE @search::text
ORDER BY i.name;"
    ;
    AddParam(command, "@search", search ?? (object)DBNull.Value);
    using var reader = command.ExecuteReader();
    var list = new List<object>();
    while (reader.Read())
    {
        // Compatibility: some databases have items.is_marked as integer (0/1) instead of boolean.
        var isActive = reader.IsDBNull(2) || reader.GetBoolean(2);

        bool isMarked = false;
        if (!reader.IsDBNull(14))
        {
            var raw = reader.GetValue(14);
            isMarked = raw switch
            {
                bool b => b,
                byte b => b != 0,
                short s => s != 0,
                int i => i != 0,
                long l => l != 0,
                _ => Convert.ToInt32(raw, CultureInfo.InvariantCulture) != 0
            };
        }

        var baseUom = reader.IsDBNull(5) ? null : reader.GetString(5);
        if (string.IsNullOrWhiteSpace(baseUom) && !reader.IsDBNull(6))
        {
            baseUom = reader.GetString(6);
        }

        list.Add(new
        {
            id = reader.GetInt64(0),
            name = reader.GetString(1),
            is_active = isActive,
            barcode = reader.IsDBNull(3) ? null : reader.GetString(3),
            gtin = reader.IsDBNull(4) ? null : reader.GetString(4),
            base_uom = string.IsNullOrWhiteSpace(baseUom) ? "шт" : baseUom,
            base_uom_code = string.IsNullOrWhiteSpace(baseUom) ? "шт" : baseUom,
            default_packaging_id = reader.IsDBNull(7) ? (long?)null : reader.GetInt64(7),
            brand = reader.IsDBNull(8) ? null : reader.GetString(8),
            volume = reader.IsDBNull(9) ? null : reader.GetString(9),
            shelf_life_months = reader.IsDBNull(10) ? (int?)null : reader.GetInt32(10),
            max_qty_per_hu = reader.IsDBNull(11) ? (double?)null : Convert.ToDouble(reader.GetValue(11), CultureInfo.InvariantCulture),
            tara_id = reader.IsDBNull(12) ? (long?)null : reader.GetInt64(12),
            tara_name = reader.IsDBNull(13) ? null : reader.GetString(13),
            is_marked = isMarked,
            item_type_id = reader.IsDBNull(15) ? (long?)null : reader.GetInt64(15),
            item_type_name = reader.IsDBNull(16) ? null : reader.GetString(16),
            item_type_is_visible_in_product_catalog = !reader.IsDBNull(17) && reader.GetBoolean(17),
            item_type_enable_min_stock_control = !reader.IsDBNull(18) && reader.GetBoolean(18),
            min_stock_qty = reader.IsDBNull(19) ? (double?)null : Convert.ToDouble(reader.GetValue(19), CultureInfo.InvariantCulture)
        });
    }

    return Results.Ok(list);
});

app.MapPost("/api/items", async (HttpRequest request, CatalogService catalog) =>
{
    var parsed = await ParseJsonBody<UpsertItemRequest>(request);
    if (!parsed.IsSuccess)
    {
        return parsed.Error!;
    }

    try
    {
        var itemId = catalog.CreateItem(
            parsed.Value?.Name ?? string.Empty,
            parsed.Value?.Barcode,
            parsed.Value?.Gtin,
            parsed.Value?.BaseUom,
            parsed.Value?.Brand,
            parsed.Value?.Volume,
            parsed.Value?.ShelfLifeMonths,
            parsed.Value?.TaraId,
            parsed.Value?.IsMarked == true,
            parsed.Value?.IsActive != false,
            parsed.Value?.MaxQtyPerHu,
            parsed.Value?.ItemTypeId,
            parsed.Value?.MinStockQty);
        return Results.Ok(new { ok = true, item_id = itemId });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
    catch (PostgresException ex) when (string.Equals(ex.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal))
    {
        return Results.Conflict(new ApiResult(false, "ITEM_ALREADY_EXISTS"));
    }
});

app.MapPost("/api/items/{itemId:long}", async (long itemId, HttpRequest request, CatalogService catalog) =>
{
    var parsed = await ParseJsonBody<UpsertItemRequest>(request);
    if (!parsed.IsSuccess)
    {
        return parsed.Error!;
    }

    try
    {
        catalog.UpdateItem(
            itemId,
            parsed.Value?.Name ?? string.Empty,
            parsed.Value?.Barcode,
            parsed.Value?.Gtin,
            parsed.Value?.BaseUom,
            parsed.Value?.Brand,
            parsed.Value?.Volume,
            parsed.Value?.ShelfLifeMonths,
            parsed.Value?.TaraId,
            parsed.Value?.IsMarked == true,
            parsed.Value?.IsActive,
            parsed.Value?.MaxQtyPerHu,
            parsed.Value?.ItemTypeId,
            parsed.Value?.MinStockQty);
        return Results.Ok(new ApiResult(true));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
    catch (PostgresException ex) when (string.Equals(ex.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal))
    {
        return Results.Conflict(new ApiResult(false, "ITEM_ALREADY_EXISTS"));
    }
});

app.MapDelete("/api/items/{itemId:long}", (long itemId, CatalogService catalog) =>
{
    try
    {
        catalog.DeleteItem(itemId);
        return Results.Ok(new ApiResult(true));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
});

app.MapGet("/api/packagings", (HttpRequest request, IDataStore store) =>
{
    var includeInactive = ParseIncludeInactive(request.Query["include_inactive"].ToString());
    var itemIdText = request.Query["item_id"].ToString();
    var itemIdFilter = long.TryParse(itemIdText, out var parsedItemId) && parsedItemId > 0
        ? parsedItemId
        : (long?)null;

    var items = itemIdFilter.HasValue
        ? new[] { itemIdFilter.Value }
        : store.GetItems(null).Select(item => item.Id).ToArray();

    var list = new List<object>();
    foreach (var itemId in items)
    {
        foreach (var packaging in store.GetItemPackagings(itemId, includeInactive))
        {
            list.Add(MapItemPackaging(packaging));
        }
    }

    return Results.Ok(list);
});

app.MapPost("/api/packagings", async (HttpRequest request, ItemPackagingService packagings) =>
{
    var parsed = await ParseJsonBody<UpsertPackagingRequest>(request);
    if (!parsed.IsSuccess)
    {
        return parsed.Error!;
    }

    try
    {
        var packagingId = packagings.CreatePackaging(
            parsed.Value!.ItemId,
            parsed.Value.Code ?? string.Empty,
            parsed.Value.Name ?? string.Empty,
            parsed.Value.FactorToBase,
            parsed.Value.SortOrder);
        return Results.Ok(new { ok = true, packaging_id = packagingId });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
});

app.MapPost("/api/packagings/{packagingId:long}", async (long packagingId, HttpRequest request, ItemPackagingService packagings) =>
{
    var parsed = await ParseJsonBody<UpsertPackagingRequest>(request);
    if (!parsed.IsSuccess)
    {
        return parsed.Error!;
    }

    try
    {
        packagings.UpdatePackaging(
            packagingId,
            parsed.Value!.ItemId,
            parsed.Value.Code ?? string.Empty,
            parsed.Value.Name ?? string.Empty,
            parsed.Value.FactorToBase,
            parsed.Value.SortOrder,
            parsed.Value.IsActive ?? true);
        return Results.Ok(new ApiResult(true));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
});

app.MapDelete("/api/packagings/{packagingId:long}", (long packagingId, ItemPackagingService packagings) =>
{
    try
    {
        packagings.DeactivatePackaging(packagingId);
        return Results.Ok(new ApiResult(true));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
});

app.MapPost("/api/items/{itemId:long}/default-packaging", async (long itemId, HttpRequest request, ItemPackagingService packagings) =>
{
    var parsed = await ParseJsonBody<SetDefaultPackagingRequest>(request);
    if (!parsed.IsSuccess)
    {
        return parsed.Error!;
    }

    try
    {
        packagings.SetDefaultPackaging(itemId, parsed.Value!.PackagingId);
        return Results.Ok(new ApiResult(true));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
});

app.MapGet("/api/uoms", (CatalogService catalog) =>
{
    var uoms = catalog.GetUoms()
        .Select(uom => new { id = uom.Id, name = uom.Name })
        .ToList();
    return Results.Ok(uoms);
});

app.MapPost("/api/uoms", async (HttpRequest request, CatalogService catalog) =>
{
    var parsed = await ParseJsonBody<CreateNamedEntityRequest>(request);
    if (!parsed.IsSuccess)
    {
        return parsed.Error!;
    }

    try
    {
        var uomId = catalog.CreateUom(parsed.Value?.Name ?? string.Empty);
        return Results.Ok(new { ok = true, uom_id = uomId });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
});

app.MapDelete("/api/uoms/{uomId:long}", (long uomId, CatalogService catalog) =>
{
    try
    {
        catalog.DeleteUom(uomId);
        return Results.Ok(new ApiResult(true));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
});

app.MapGet("/api/write-off-reasons", (CatalogService catalog) =>
{
    var reasons = catalog.GetWriteOffReasons()
        .Select(reason => new
        {
            id = reason.Id,
            code = reason.Code,
            name = reason.Name
        })
        .ToList();
    return Results.Ok(reasons);
});

app.MapPost("/api/write-off-reasons", async (HttpRequest request, CatalogService catalog) =>
{
    var parsed = await ParseJsonBody<CreateWriteOffReasonRequest>(request);
    if (!parsed.IsSuccess)
    {
        return parsed.Error!;
    }

    try
    {
        var reasonId = catalog.CreateWriteOffReason(parsed.Value?.Code ?? string.Empty, parsed.Value?.Name ?? string.Empty);
        return Results.Ok(new { ok = true, reason_id = reasonId });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
    catch (PostgresException ex) when (string.Equals(ex.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal))
    {
        return Results.Conflict(new ApiResult(false, "WRITE_OFF_REASON_ALREADY_EXISTS"));
    }
});

app.MapDelete("/api/write-off-reasons/{reasonId:long}", (long reasonId, CatalogService catalog) =>
{
    try
    {
        catalog.DeleteWriteOffReason(reasonId);
        return Results.Ok(new ApiResult(true));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
});

app.MapGet("/api/taras", (CatalogService catalog) =>
{
    var taras = catalog.GetTaras()
        .Select(tara => new { id = tara.Id, name = tara.Name })
        .ToList();
    return Results.Ok(taras);
});

app.MapPost("/api/taras", async (HttpRequest request, CatalogService catalog) =>
{
    var parsed = await ParseJsonBody<CreateNamedEntityRequest>(request);
    if (!parsed.IsSuccess)
    {
        return parsed.Error!;
    }

    try
    {
        var taraId = catalog.CreateTara(parsed.Value?.Name ?? string.Empty);
        return Results.Ok(new { ok = true, tara_id = taraId });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
    catch (PostgresException ex) when (string.Equals(ex.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal))
    {
        return Results.Conflict(new ApiResult(false, "TARA_ALREADY_EXISTS"));
    }
});

app.MapDelete("/api/taras/{taraId:long}", (long taraId, CatalogService catalog) =>
{
    try
    {
        catalog.DeleteTara(taraId);
        return Results.Ok(new ApiResult(true));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
});

app.MapGet("/api/item-types", (HttpRequest request, CatalogService catalog) =>
{
    var includeInactive = ParseIncludeInactive(request.Query["include_inactive"].ToString());
    var itemTypes = catalog.GetItemTypes(includeInactive)
        .Select(itemType => new
        {
            id = itemType.Id,
            name = itemType.Name,
            code = itemType.Code,
            sort_order = itemType.SortOrder,
            is_active = itemType.IsActive,
            is_visible_in_product_catalog = itemType.IsVisibleInProductCatalog,
            enable_min_stock_control = itemType.EnableMinStockControl,
            min_stock_uses_order_binding = itemType.MinStockUsesOrderBinding,
            enable_order_reservation = itemType.EnableOrderReservation,
            enable_hu_distribution = itemType.EnableHuDistribution
        })
        .ToList();
    return Results.Ok(itemTypes);
});

app.MapPost("/api/item-types", async (HttpRequest request, CatalogService catalog) =>
{
    var parsed = await ParseJsonBody<UpsertItemTypeRequest>(request);
    if (!parsed.IsSuccess)
    {
        return parsed.Error!;
    }

    try
    {
        var itemTypeId = catalog.CreateItemType(
            parsed.Value?.Name ?? string.Empty,
            parsed.Value?.Code,
            parsed.Value?.SortOrder ?? 0,
            parsed.Value?.IsActive ?? true,
            parsed.Value?.IsVisibleInProductCatalog ?? true,
            parsed.Value?.EnableMinStockControl ?? false,
            parsed.Value?.MinStockUsesOrderBinding ?? false,
            parsed.Value?.EnableOrderReservation ?? false,
            parsed.Value?.EnableHuDistribution ?? false);
        return Results.Ok(new { ok = true, item_type_id = itemTypeId });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
    catch (PostgresException ex) when (string.Equals(ex.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal))
    {
        return Results.Conflict(new ApiResult(false, "ITEM_TYPE_ALREADY_EXISTS"));
    }
});

app.MapPost("/api/item-types/{itemTypeId:long}", async (long itemTypeId, HttpRequest request, CatalogService catalog) =>
{
    var parsed = await ParseJsonBody<UpsertItemTypeRequest>(request);
    if (!parsed.IsSuccess)
    {
        return parsed.Error!;
    }

    try
    {
        catalog.UpdateItemType(
            itemTypeId,
            parsed.Value?.Name ?? string.Empty,
            parsed.Value?.Code,
            parsed.Value?.SortOrder ?? 0,
            parsed.Value?.IsActive ?? true,
            parsed.Value?.IsVisibleInProductCatalog ?? true,
            parsed.Value?.EnableMinStockControl ?? false,
            parsed.Value?.MinStockUsesOrderBinding ?? false,
            parsed.Value?.EnableOrderReservation ?? false,
            parsed.Value?.EnableHuDistribution ?? false);
        return Results.Ok(new ApiResult(true));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
    catch (PostgresException ex) when (string.Equals(ex.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal))
    {
        return Results.Conflict(new ApiResult(false, "ITEM_TYPE_ALREADY_EXISTS"));
    }
});

app.MapDelete("/api/item-types/{itemTypeId:long}", (long itemTypeId, CatalogService catalog) =>
{
    try
    {
        catalog.DeleteItemType(itemTypeId);
        return Results.Ok(new ApiResult(true));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
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

app.MapGet("/api/item-requests", (HttpRequest request, IDataStore store) =>
{
    var includeResolved = ParseIncludeResolved(request.Query["include_resolved"]);
    var list = store.GetItemRequests(includeResolved)
        .Select(MapItemRequest)
        .ToList();
    return Results.Ok(list);
});

app.MapPost("/api/item-requests/{requestId:long}/resolve", (long requestId, IDataStore store) =>
{
    var existing = store.GetItemRequests(true)
        .FirstOrDefault(itemRequest => itemRequest.Id == requestId);
    if (existing == null)
    {
        return Results.NotFound(new ApiResult(false, "ITEM_REQUEST_NOT_FOUND"));
    }

    if (!string.Equals(existing.Status, "RESOLVED", StringComparison.OrdinalIgnoreCase))
    {
        store.MarkItemRequestResolved(requestId);
    }

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
            code = partner.Code,
            status = status.ToString()
        });
    }

    return Results.Ok(list);
});

app.MapPost("/api/partners", async (HttpRequest request, CatalogService catalog) =>
{
    var parsed = await ParseJsonBody<UpsertPartnerRequest>(request);
    if (!parsed.IsSuccess)
    {
        return parsed.Error!;
    }

    var status = ParsePartnerStatusValue(parsed.Value?.Status);
    if (!status.HasValue)
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_PARTNER_STATUS"));
    }

    try
    {
        var partnerId = catalog.CreatePartner(parsed.Value?.Name ?? string.Empty, parsed.Value?.Code);
        SavePartnerStatus(partnerId, status.Value);
        return Results.Ok(new { ok = true, partner_id = partnerId });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
    catch (PostgresException ex) when (string.Equals(ex.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal))
    {
        return Results.Conflict(new ApiResult(false, "PARTNER_ALREADY_EXISTS"));
    }
});

app.MapPost("/api/partners/{partnerId:long}", async (long partnerId, HttpRequest request, CatalogService catalog) =>
{
    var parsed = await ParseJsonBody<UpsertPartnerRequest>(request);
    if (!parsed.IsSuccess)
    {
        return parsed.Error!;
    }

    var status = ParsePartnerStatusValue(parsed.Value?.Status);
    if (!status.HasValue)
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_PARTNER_STATUS"));
    }

    try
    {
        catalog.UpdatePartner(partnerId, parsed.Value?.Name ?? string.Empty, parsed.Value?.Code);
        SavePartnerStatus(partnerId, status.Value);
        return Results.Ok(new ApiResult(true));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
    catch (PostgresException ex) when (string.Equals(ex.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal))
    {
        return Results.Conflict(new ApiResult(false, "PARTNER_ALREADY_EXISTS"));
    }
});

app.MapDelete("/api/partners/{partnerId:long}", (long partnerId, CatalogService catalog) =>
{
    try
    {
        catalog.DeletePartner(partnerId);
        RemovePartnerStatus(partnerId);
        return Results.Ok(new ApiResult(true));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
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

app.MapPost("/api/docs/{docId:long}/recount", (long docId, IDataStore store, DocumentService docs) =>
{
    var doc = store.GetDoc(docId);
    if (doc == null)
    {
        return Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND"));
    }

    if (doc.Status != DocStatus.Draft)
    {
        return Results.BadRequest(new ApiResult(false, "DOC_NOT_DRAFT"));
    }

    if (doc.Type != DocType.Inventory)
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_DOC_TYPE"));
    }

    docs.MarkDocForRecount(docId);
    return Results.Ok(new ApiResult(true));
});

app.MapPost("/api/docs/{docId:long}/header", async (long docId, HttpRequest request, IDataStore store, DocumentService docs) =>
{
    var doc = store.GetDoc(docId);
    if (doc == null)
    {
        return Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND"));
    }

    if (doc.Status != DocStatus.Draft)
    {
        return Results.BadRequest(new ApiResult(false, "DOC_NOT_DRAFT"));
    }

    var parsed = await ParseJsonBody<SaveDocHeaderRequest>(request);
    if (!parsed.IsSuccess)
    {
        return parsed.Error!;
    }

    var saveRequest = parsed.Value!;
    var partnerId = saveRequest.PartnerId;
    if (partnerId.HasValue && store.GetPartner(partnerId.Value) == null)
    {
        return Results.BadRequest(new ApiResult(false, "PARTNER_NOT_FOUND"));
    }

    var shippingRef = NormalizeHu(saveRequest.ShippingRef);
    var reasonCode = string.IsNullOrWhiteSpace(saveRequest.ReasonCode) ? null : saveRequest.ReasonCode.Trim();
    var comment = string.IsNullOrWhiteSpace(saveRequest.Comment) ? null : saveRequest.Comment.Trim();
    var productionBatchNo = string.IsNullOrWhiteSpace(saveRequest.ProductionBatchNo) ? null : saveRequest.ProductionBatchNo.Trim();

    Order? order = null;
    if (saveRequest.OrderId.HasValue)
    {
        order = store.GetOrder(saveRequest.OrderId.Value);
        if (order == null)
        {
            return Results.BadRequest(new ApiResult(false, "ORDER_NOT_FOUND"));
        }
    }

    if (order != null)
    {
        var headerPartnerId = doc.Type == DocType.Outbound ? order.PartnerId : partnerId;
        docs.UpdateDocHeader(docId, headerPartnerId, order.OrderRef, shippingRef);
        docs.UpdateDocOrderBinding(docId, order.Id);
    }
    else
    {
        if (doc.Type == DocType.Outbound)
        {
            docs.ClearDocOrder(docId, partnerId);
        }
        else if (doc.Type == DocType.ProductionReceipt)
        {
            docs.UpdateDocOrderBinding(docId, null);
        }

        docs.UpdateDocHeader(docId, partnerId, null, shippingRef);
    }

    if (doc.Type == DocType.WriteOff)
    {
        docs.UpdateDocReason(docId, reasonCode);
    }
    else if (doc.Type == DocType.ProductionReceipt)
    {
        docs.UpdateDocProductionBatch(docId, productionBatchNo);
        docs.UpdateDocComment(docId, comment);
    }

    var updated = store.GetDoc(docId);
    return updated == null
        ? Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND"))
        : Results.Ok(MapDoc(updated));
});

app.MapPost("/api/docs/{docId:long}/lines/{lineId:long}/assign-hu", async (long docId, long lineId, HttpRequest request, IDataStore store, DocumentService docs) =>
{
    var doc = store.GetDoc(docId);
    if (doc == null)
    {
        return Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND"));
    }

    if (doc.Status != DocStatus.Draft)
    {
        return Results.BadRequest(new ApiResult(false, "DOC_NOT_DRAFT"));
    }

    if (store.GetDocLines(docId).All(line => line.Id != lineId))
    {
        return Results.BadRequest(new ApiResult(false, "UNKNOWN_LINE"));
    }

    var parsed = await ParseJsonBody<AssignDocLineHuRequest>(request);
    if (!parsed.IsSuccess)
    {
        return parsed.Error!;
    }

    var assignRequest = parsed.Value!;
    if (assignRequest.Qty <= 0)
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_QTY"));
    }

    try
    {
        docs.AssignDocLineHu(
            docId,
            lineId,
            assignRequest.Qty,
            NormalizeHu(assignRequest.FromHu),
            NormalizeHu(assignRequest.ToHu));
        return Results.Ok(new ApiResult(true));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
});

app.MapPost("/api/docs/{docId:long}/production-receipt/auto-distribute-hus", async (long docId, HttpRequest request, IDataStore store, DocumentService docs) =>
{
    var doc = store.GetDoc(docId);
    if (doc == null)
    {
        return Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND"));
    }

    if (doc.Status != DocStatus.Draft)
    {
        return Results.BadRequest(new ApiResult(false, "DOC_NOT_DRAFT"));
    }

    if (doc.Type is not (DocType.ProductionReceipt or DocType.Inbound))
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_DOC_TYPE"));
    }

    var parsed = await ParseJsonBody<AutoDistributeProductionReceiptHusRequest>(request);
    if (!parsed.IsSuccess)
    {
        return parsed.Error!;
    }

    var lineIds = (parsed.Value!.LineIds ?? new List<long>())
        .Where(id => id > 0)
        .Distinct()
        .ToList();

    int usedHuCount;
    try
    {
        usedHuCount = docs.AutoDistributeProductionReceiptHus(docId, lineIds.Count > 0 ? lineIds : null);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }

    return Results.Ok(new
    {
        ok = true,
        used_hu_count = usedHuCount
    });
});

app.MapPost("/api/docs/{docId:long}/lines/{lineId:long}/distribute-hu-capacity", async (long docId, long lineId, HttpRequest request, IDataStore store, DocumentService docs) =>
{
    var doc = store.GetDoc(docId);
    if (doc == null)
    {
        return Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND"));
    }

    if (doc.Status != DocStatus.Draft)
    {
        return Results.BadRequest(new ApiResult(false, "DOC_NOT_DRAFT"));
    }

    if (doc.Type is not (DocType.ProductionReceipt or DocType.Inbound))
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_DOC_TYPE"));
    }

    if (store.GetDocLines(docId).All(line => line.Id != lineId))
    {
        return Results.BadRequest(new ApiResult(false, "UNKNOWN_LINE"));
    }

    var parsed = await ParseJsonBody<DistributeProductionLineByHuCapacityRequest>(request);
    if (!parsed.IsSuccess)
    {
        return parsed.Error!;
    }

    var distributeRequest = parsed.Value!;
    if (distributeRequest.MaxQtyPerHu <= 0)
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_CAPACITY"));
    }

    var huCodes = (distributeRequest.HuCodes ?? new List<string>())
        .Where(code => !string.IsNullOrWhiteSpace(code))
        .Select(code => code.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    if (huCodes.Count == 0)
    {
        return Results.BadRequest(new ApiResult(false, "MISSING_HU"));
    }

    try
    {
        docs.DistributeProductionLineByHuCapacity(docId, lineId, distributeRequest.MaxQtyPerHu, huCodes);
        return Results.Ok(new ApiResult(true));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new ApiResult(false, ex.Message));
    }
});

app.MapPost("/api/docs/{docId:long}/lines/{lineId:long}/pack-single-hu", async (long docId, long lineId, HttpRequest request, IDataStore store, DocumentService docs) =>
{
    var doc = store.GetDoc(docId);
    if (doc == null)
    {
        return Results.NotFound(new ApiResult(false, "DOC_NOT_FOUND"));
    }

    if (doc.Status != DocStatus.Draft)
    {
        return Results.BadRequest(new ApiResult(false, "DOC_NOT_DRAFT"));
    }

    if (doc.Type is not (DocType.ProductionReceipt or DocType.Inbound))
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_DOC_TYPE"));
    }

    if (store.GetDocLines(docId).All(line => line.Id != lineId))
    {
        return Results.BadRequest(new ApiResult(false, "UNKNOWN_LINE"));
    }

    var parsed = await ParseJsonBody<SetPackSingleHuRequest>(request);
    if (!parsed.IsSuccess)
    {
        return parsed.Error!;
    }

    docs.UpdateProductionLinePackSingleHu(docId, lineId, parsed.Value!.PackSingleHu);
    return Results.Ok(new ApiResult(true));
});

app.MapGet("/api/orders", (HttpRequest request, IDataStore store) =>
{
    var query = request.Query["q"].ToString();
    var normalized = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
    var includeInternal = string.Equals(request.Query["include_internal"], "1", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(request.Query["include_internal"], "true", StringComparison.OrdinalIgnoreCase);
    var includePendingRequests = string.Equals(request.Query["include_pending_requests"], "1", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(request.Query["include_pending_requests"], "true", StringComparison.OrdinalIgnoreCase);

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
    if (includePendingRequests)
    {
        var pendingCreateOrders = GetPendingCreateOrderRows(store, normalized);
        list.AddRange(pendingCreateOrders);
    }
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

    var lines = orderService.GetOrderLineViews(orderId)
        .Select(line => new
        {
            id = line.Id,
            order_id = line.OrderId,
            item_id = line.ItemId,
            item_name = line.ItemName,
            barcode = line.Barcode,
            gtin = line.Gtin,
            qty_ordered = line.QtyOrdered,
            qty_shipped = line.QtyShipped,
            qty_produced = line.QtyProduced,
            qty_left = line.QtyRemaining,
            qty_available = line.QtyAvailable,
            can_ship_now = line.CanShipNow,
            shortage = line.Shortage
        })
        .ToList();

    return Results.Ok(lines);
});

app.MapGet("/api/orders/{orderId:long}/shipment-remaining", (long orderId, IDataStore store) =>
{
    var orderService = new OrderService(store);
    var order = orderService.GetOrder(orderId);
    if (order == null)
    {
        return Results.NotFound(new ApiResult(false, "ORDER_NOT_FOUND"));
    }

    var documentService = new DocumentService(store);
    var lines = documentService.GetOrderShipmentRemaining(orderId)
        .Select(MapOrderShipmentRemaining)
        .ToList();
    return Results.Ok(lines);
});

app.MapGet("/api/orders/{orderId:long}/receipt-remaining", (long orderId, HttpRequest request, IDataStore store) =>
{
    var orderService = new OrderService(store);
    var order = orderService.GetOrder(orderId);
    if (order == null)
    {
        return Results.NotFound(new ApiResult(false, "ORDER_NOT_FOUND"));
    }

    var documentService = new DocumentService(store);
    var detailed = string.Equals(request.Query["detailed"], "1", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(request.Query["detailed"], "true", StringComparison.OrdinalIgnoreCase);
    var includeReservedStock = !string.Equals(request.Query["include_reserved_stock"], "0", StringComparison.OrdinalIgnoreCase)
                               && !string.Equals(request.Query["include_reserved_stock"], "false", StringComparison.OrdinalIgnoreCase);
    var lines = (detailed
            ? orderService.GetOrderReceiptRemainingDetailed(orderId, includeReservedStock)
            : documentService.GetOrderReceiptRemaining(orderId, includeReservedStock))
        .Select(MapOrderReceiptRemaining)
        .ToList();
    return Results.Ok(lines);
});

app.MapGet("/api/orders/{orderId:long}/bound-hu", (long orderId, IDataStore store) =>
{
    var orderService = new OrderService(store);
    var order = orderService.GetOrder(orderId);
    if (order == null)
    {
        return Results.NotFound(new ApiResult(false, "ORDER_NOT_FOUND"));
    }

    var rows = orderService.GetOrderBoundHuByItem(orderId)
        .SelectMany(
            pair => pair.Value.Select(hu => new
            {
                item_id = pair.Key,
                hu
            }))
        .OrderBy(row => row.item_id)
        .ThenBy(row => row.hu, StringComparer.OrdinalIgnoreCase)
        .ToList();
    return Results.Ok(rows);
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

    var orderType = string.IsNullOrWhiteSpace(createRequest.OrderType)
        ? OrderType.Customer
        : (OrderStatusMapper.TypeFromString(createRequest.OrderType) ?? (OrderType?)null);
    if (!orderType.HasValue)
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_TYPE"));
    }

    var orderRef = GenerateNextOrderRef(store);

    if (orderType.Value == OrderType.Customer)
    {
        if (!createRequest.PartnerId.HasValue || createRequest.PartnerId.Value <= 0)
        {
            return Results.BadRequest(new ApiResult(false, "MISSING_PARTNER_ID"));
        }

        var partner = store.GetPartner(createRequest.PartnerId.Value);
        if (partner == null)
        {
            return Results.BadRequest(new ApiResult(false, "PARTNER_NOT_FOUND"));
        }
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
        order_type = OrderStatusMapper.TypeToString(orderType.Value),
        partner_id = orderType.Value == OrderType.Customer ? createRequest.PartnerId : null,
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

app.MapPost("/api/orders/requests/status", () =>
{
    return Results.BadRequest(new ApiResult(false, "ORDER_STATUS_MANUAL_DISABLED"));
});

app.MapGet("/api/orders/requests", (HttpRequest request, IDataStore store) =>
{
    var includeResolved = ParseIncludeResolved(request.Query["include_resolved"]);
    var list = store.GetOrderRequests(includeResolved)
        .Select(MapOrderRequest)
        .ToList();
    return Results.Ok(list);
});

app.MapGet("/api/requests/summary", (IDataStore store) =>
{
    var itemCount = store.GetItemRequests(false).Count;
    var orderCount = store.GetOrderRequests(false).Count;
    return Results.Ok(new
    {
        item_requests_pending = itemCount,
        order_requests_pending = orderCount,
        total_pending = itemCount + orderCount
    });
});

app.MapPost("/api/orders/requests/{requestId:long}/resolve", async (long requestId, HttpRequest request, IDataStore store) =>
{
    var rawJson = await ReadBody(request);
    if (string.IsNullOrWhiteSpace(rawJson))
    {
        return Results.BadRequest(new ApiResult(false, "EMPTY_BODY"));
    }

    ResolveOrderRequestRequest? resolveRequest;
    try
    {
        resolveRequest = JsonSerializer.Deserialize<ResolveOrderRequestRequest>(
            rawJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (JsonException)
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
    }

    if (resolveRequest == null)
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
    }

    var existing = store.GetOrderRequests(true)
        .FirstOrDefault(orderRequest => orderRequest.Id == requestId);
    if (existing == null)
    {
        return Results.NotFound(new ApiResult(false, "ORDER_REQUEST_NOT_FOUND"));
    }

    var status = NormalizeOrderRequestResolutionStatus(resolveRequest.Status);
    if (status == null)
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_STATUS"));
    }

    var resolvedBy = string.IsNullOrWhiteSpace(resolveRequest.ResolvedBy)
        ? "WPF"
        : resolveRequest.ResolvedBy.Trim();
    var note = string.IsNullOrWhiteSpace(resolveRequest.Note)
        ? null
        : resolveRequest.Note.Trim();

    store.ResolveOrderRequest(
        requestId,
        status,
        resolvedBy,
        note,
        resolveRequest.AppliedOrderId);

    return Results.Ok(new ApiResult(true));
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

app.MapGet("/api/stock/rows", (HttpRequest request, IDataStore store) =>
{
    var query = request.Query["q"].ToString();
    var normalized = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
    var rows = store.GetStock(normalized)
        .Select(MapStockRow)
        .ToList();
    return Results.Ok(rows);
});

app.MapGet("/api/reports/production-need", (HttpRequest request, IDataStore store) =>
{
    var includeZeroNeed = string.Equals(request.Query["include_zero"], "1", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(request.Query["include_zero"], "true", StringComparison.OrdinalIgnoreCase);
    var rows = new ProductionNeedService(store)
        .GetRows(includeZeroNeed)
        .Select(MapProductionNeedRow)
        .ToList();
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
    var contextByKey = HuStockReadModelMapper.BuildContextMap(store.GetHuOrderContextRows());

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

                list.Add(HuStockReadModelMapper.Map(
                    itemId,
                    reader.GetInt64(1),
                    reader.GetString(0),
                    reader.GetDouble(2),
                    contextByKey));
            }

            return Results.Ok(list);
        }

        var filtered = store.GetHuStockRows()
            .Where(row => row.ItemId == itemId)
            .Select(row => HuStockReadModelMapper.Map(row.ItemId, row.LocationId, row.HuCode, row.Qty, contextByKey))
            .ToList();

        return Results.Ok(filtered);
    }

    var rows = store.GetHuStockRows()
        .Select(row => HuStockReadModelMapper.Map(row.ItemId, row.LocationId, row.HuCode, row.Qty, contextByKey))
        .ToList();

    return Results.Ok(rows);
});

app.MapGet("/api/hus", (HttpRequest request, IDataStore store) =>
{
    var takeText = request.Query["take"].ToString();
    var searchText = request.Query["q"].ToString();
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

    var normalizedSearch = string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim();
    var list = store.GetHus(normalizedSearch, take)
        .Select(MapHuRecord)
        .ToList();
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
        hu = MapHuRecord(record)
    });
});

app.MapGet("/api/hus/{huCode}/ledger", (string huCode, IDataStore store) =>
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

    var rows = store.GetHuLedgerRows(normalized)
        .Select(MapHuLedgerRow)
        .ToList();
    return Results.Ok(rows);
});

app.MapPost("/api/hus", async (HttpRequest request, IDataStore store) =>
{
    var parsed = await ParseJsonBody<CreateHuRequest>(request);
    if (!parsed.IsSuccess)
    {
        return parsed.Error!;
    }

    var huCode = NormalizeHu(parsed.Value!.HuCode);
    if (string.IsNullOrWhiteSpace(huCode))
    {
        return Results.BadRequest(new ApiResult(false, "INVALID_HU"));
    }

    var existing = store.GetHuByCode(huCode);
    if (existing != null)
    {
        return Results.Ok(new
        {
            ok = true,
            created = false,
            hu = MapHuRecord(existing)
        });
    }

    var createdBy = string.IsNullOrWhiteSpace(parsed.Value.CreatedBy) ? null : parsed.Value.CreatedBy.Trim();
    var created = store.CreateHuRecord(huCode, createdBy);
    return Results.Ok(new
    {
        ok = true,
        created = true,
        hu = MapHuRecord(created)
    });
});

app.MapPost("/api/hus/{huCode}/close", async (string huCode, HttpRequest request, IDataStore store) =>
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

    var existing = store.GetHuByCode(normalized);
    if (existing == null)
    {
        return Results.NotFound(new ApiResult(false, "UNKNOWN_HU"));
    }

    var parsed = await ParseJsonBody<CloseHuRequest>(request);
    if (!parsed.IsSuccess)
    {
        return parsed.Error!;
    }

    var closedBy = string.IsNullOrWhiteSpace(parsed.Value!.ClosedBy) ? null : parsed.Value.ClosedBy.Trim();
    var note = string.IsNullOrWhiteSpace(parsed.Value.Note) ? null : parsed.Value.Note.Trim();
    store.CloseHu(normalized, closedBy, note);
    return Results.Ok(new ApiResult(true));
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
// curl.exe -k -X POST "https://localhost:7154/api/ops" -H "Content-Type: application/json" ^
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

static byte[] HashPassword(string password, byte[] salt, int iterations)
{
    using var derive = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
    return derive.GetBytes(32);
}

static void EnsureUniqueTsdDeviceLogin(DbConnection connection, string login, long? excludedId)
{
    using var command = connection.CreateCommand();
    command.CommandText = excludedId.HasValue
        ? @"
SELECT 1
FROM tsd_devices
WHERE UPPER(login) = UPPER(@login)
  AND id <> @excluded_id
LIMIT 1;"
        : @"
SELECT 1
FROM tsd_devices
WHERE UPPER(login) = UPPER(@login)
LIMIT 1;";
    AddParam(command, "@login", login);
    if (excludedId.HasValue)
    {
        AddParam(command, "@excluded_id", excludedId.Value);
    }

    if (command.ExecuteScalar() != null)
    {
        throw new InvalidOperationException("Логин уже используется другим аккаунтом ПК/ТСД.");
    }
}

static string GenerateTsdDeviceId(DbConnection connection)
{
    for (var attempt = 0; attempt < 5; attempt++)
    {
        var candidate = $"ACC-{Guid.NewGuid():N}".Substring(0, 12).ToUpperInvariant();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM tsd_devices WHERE device_id = @device_id LIMIT 1;";
        AddParam(command, "@device_id", candidate);
        if (command.ExecuteScalar() == null)
        {
            return candidate;
        }
    }

    throw new InvalidOperationException("Не удалось сгенерировать уникальный ID аккаунта.");
}

static IReadOnlyDictionary<string, bool> BuildClientBlockStates(IReadOnlyList<ClientBlockSetting> settings)
{
    return ClientBlockCatalog.MergeWithDefaults(settings);
}

static IResult? TryCreateClientBlockRejection(HttpContext context)
{
    if (!context.Request.Path.StartsWithSegments("/api"))
    {
        return null;
    }

    if (IsClientBlockBypassPath(context.Request.Path))
    {
        return null;
    }

    if (!TryGetRequestedClientBlockKey(context.Request, out var blockKey))
    {
        return null;
    }

    var store = context.RequestServices.GetRequiredService<IDataStore>();
    var states = BuildClientBlockStates(store.GetClientBlockSettings());
    foreach (var requiredKey in EnumerateRequiredClientBlockKeys(blockKey))
    {
        if (!states.TryGetValue(requiredKey, out var isEnabled) || isEnabled)
        {
            continue;
        }

        return Results.Json(
            new
            {
                ok = false,
                error = "BLOCK_DISABLED",
                block_key = requiredKey,
                requested_block_key = blockKey
            },
            statusCode: StatusCodes.Status403Forbidden);
    }

    return null;
}

static bool IsClientBlockBypassPath(PathString path)
{
    if (path.StartsWithSegments("/api/client-blocks")
        || path.StartsWithSegments("/api/tsd/login")
        || path.StartsWithSegments("/api/ping")
        || path.StartsWithSegments("/api/version"))
    {
        return true;
    }

    return path.StartsWithSegments("/api/diag");
}

static bool TryGetRequestedClientBlockKey(HttpRequest request, out string blockKey)
{
    blockKey = string.Empty;
    if (!request.Headers.TryGetValue("X-FlowStock-Block-Key", out var values))
    {
        return false;
    }

    var raw = values.ToString();
    if (string.IsNullOrWhiteSpace(raw))
    {
        return false;
    }

    var normalized = raw
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault();
    if (string.IsNullOrWhiteSpace(normalized) || !ClientBlockCatalog.IsKnownKey(normalized))
    {
        return false;
    }

    blockKey = normalized;
    return true;
}

static IEnumerable<string> EnumerateRequiredClientBlockKeys(string blockKey)
{
    if (string.IsNullOrWhiteSpace(blockKey))
    {
        yield break;
    }

    if (string.Equals(blockKey, ClientBlockCatalog.TsdInbound, StringComparison.OrdinalIgnoreCase)
        || string.Equals(blockKey, ClientBlockCatalog.TsdProductionReceipt, StringComparison.OrdinalIgnoreCase)
        || string.Equals(blockKey, ClientBlockCatalog.TsdOutbound, StringComparison.OrdinalIgnoreCase)
        || string.Equals(blockKey, ClientBlockCatalog.TsdMove, StringComparison.OrdinalIgnoreCase)
        || string.Equals(blockKey, ClientBlockCatalog.TsdWriteOff, StringComparison.OrdinalIgnoreCase)
        || string.Equals(blockKey, ClientBlockCatalog.TsdInventory, StringComparison.OrdinalIgnoreCase))
    {
        yield return ClientBlockCatalog.TsdOperations;
    }

    yield return blockKey;
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
        comment = order.Comment,
        bind_reserved_stock = order.UseReservedStock,
        created_at = order.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
        shipped_at = order.ShippedAt?.ToString("O", CultureInfo.InvariantCulture)
    };
}

static object MapItem(Item item)
{
    return new
    {
        id = item.Id,
        name = item.Name,
        is_active = item.IsActive,
        barcode = item.Barcode,
        gtin = item.Gtin,
        base_uom = string.IsNullOrWhiteSpace(item.BaseUom) ? "шт" : item.BaseUom,
        base_uom_code = string.IsNullOrWhiteSpace(item.BaseUom) ? "шт" : item.BaseUom,
        default_packaging_id = item.DefaultPackagingId,
        max_qty_per_hu = item.MaxQtyPerHu,
        brand = item.Brand,
        volume = item.Volume,
        shelf_life_months = item.ShelfLifeMonths,
        tara_id = item.TaraId,
        tara_name = item.TaraName,
        is_marked = item.IsMarked,
        item_type_id = item.ItemTypeId,
        item_type_name = item.ItemTypeName,
        item_type_is_visible_in_product_catalog = item.ItemTypeIsVisibleInProductCatalog,
        item_type_enable_min_stock_control = item.ItemTypeEnableMinStockControl,
        min_stock_qty = item.MinStockQty
    };
}

static object MapItemRequest(ItemRequest request)
{
    return new
    {
        id = request.Id,
        barcode = request.Barcode,
        comment = request.Comment,
        device_id = request.DeviceId,
        login = request.Login,
        created_at = request.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
        status = request.Status,
        resolved_at = request.ResolvedAt?.ToString("O", CultureInfo.InvariantCulture)
    };
}

static object MapOrderRequest(OrderRequest request)
{
    return new
    {
        id = request.Id,
        request_type = request.RequestType,
        payload_json = request.PayloadJson,
        status = request.Status,
        created_at = request.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
        created_by_login = request.CreatedByLogin,
        created_by_device_id = request.CreatedByDeviceId,
        resolved_at = request.ResolvedAt?.ToString("O", CultureInfo.InvariantCulture),
        resolved_by = request.ResolvedBy,
        resolution_note = request.ResolutionNote,
        applied_order_id = request.AppliedOrderId
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

    foreach (var request in store.GetOrderRequests(includeResolved: false))
    {
        if (!string.Equals(request.RequestType, OrderRequestType.CreateOrder, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var orderRef = TryReadJsonString(request.PayloadJson, "order_ref")?.Trim();
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

static List<object> GetPendingCreateOrderRows(IDataStore store, string? normalizedQuery)
{
    var rows = new List<object>();
    var pendingRequests = store.GetOrderRequests(includeResolved: false)
        .Where(request => string.Equals(request.RequestType, OrderRequestType.CreateOrder, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(request => request.CreatedAt)
        .ThenByDescending(request => request.Id);

    foreach (var request in pendingRequests)
    {
        var orderRef = TryReadJsonString(request.PayloadJson, "order_ref")?.Trim();
        var displayOrderRef = string.IsNullOrWhiteSpace(orderRef)
            ? $"Заявка #{request.Id}"
            : orderRef;
        var orderType = OrderStatusMapper.TypeFromString(TryReadJsonString(request.PayloadJson, "order_type"))
            ?? OrderType.Customer;

        var partnerId = orderType == OrderType.Customer
            ? TryReadJsonInt64(request.PayloadJson, "partner_id")
            : null;
        var partner = partnerId.HasValue ? store.GetPartner(partnerId.Value) : null;
        var dueDate = TryReadJsonString(request.PayloadJson, "due_date");
        var partnerName = partner?.Name ?? string.Empty;
        var partnerCode = partner?.Code ?? string.Empty;
        var lines = TryReadPendingCreateOrderLines(store, request.PayloadJson);

        if (!string.IsNullOrWhiteSpace(normalizedQuery)
            && !displayOrderRef.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
            && !partnerName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
            && !partnerCode.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        rows.Add(new
        {
            id = $"request:{request.Id}",
            request_id = request.Id,
            order_ref = displayOrderRef,
            order_type = OrderStatusMapper.TypeToString(orderType),
            partner_id = partnerId,
            partner_name = partnerName,
            partner_code = partnerCode,
            due_date = dueDate,
            status = "Ожидает подтверждения",
            status_code = "PENDING_CONFIRMATION",
            created_at = request.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            shipped_at = (string?)null,
            is_pending_confirmation = true,
            lines
        });
    }

    return rows;
}

static List<object> TryReadPendingCreateOrderLines(IDataStore store, string json)
{
    var result = new List<object>();
    try
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("lines", out var linesElement)
            || linesElement.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        var availableByItem = store.GetLedgerTotalsByItem();
        foreach (var lineElement in linesElement.EnumerateArray())
        {
            var itemId = TryReadJsonElementInt64(lineElement, "item_id");
            if (!itemId.HasValue || itemId.Value <= 0)
            {
                continue;
            }

            var item = store.FindItemById(itemId.Value);
            var available = availableByItem.TryGetValue(itemId.Value, out var availableQty) ? availableQty : 0d;
            result.Add(new
            {
                item_id = itemId.Value,
                item_name = item?.Name ?? $"ID={itemId.Value}",
                barcode = item?.Barcode,
                gtin = item?.Gtin,
                qty_ordered = TryReadJsonElementDouble(lineElement, "qty_ordered") ?? 0d,
                qty_shipped = 0d,
                qty_produced = 0d,
                qty_left = TryReadJsonElementDouble(lineElement, "qty_ordered") ?? 0d,
                qty_available = available
            });
        }
    }
    catch (JsonException)
    {
        return result;
    }

    return result;
}

static string? TryReadJsonString(string json, string propertyName)
{
    try
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }
    catch (JsonException)
    {
        return null;
    }
}

static long? TryReadJsonInt64(string json, string propertyName)
{
    try
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String
            && long.TryParse(property.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
    }
    catch (JsonException)
    {
        return null;
    }

    return null;
}

static long? TryReadJsonElementInt64(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var property))
    {
        return null;
    }

    if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
    {
        return number;
    }

    if (property.ValueKind == JsonValueKind.String
        && long.TryParse(property.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
    {
        return parsed;
    }

    return null;
}

static double? TryReadJsonElementDouble(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var property))
    {
        return null;
    }

    if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number))
    {
        return number;
    }

    if (property.ValueKind == JsonValueKind.String
        && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
    {
        return parsed;
    }

    return null;
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
        production_batch_no = doc.ProductionBatchNo,
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
        to_hu = line.ToHu,
        pack_single_hu = line.PackSingleHu
    };
}

static object MapOrderShipmentRemaining(OrderShipmentLine line)
{
    return new
    {
        order_line_id = line.OrderLineId,
        order_id = line.OrderId,
        item_id = line.ItemId,
        item_name = line.ItemName,
        qty_ordered = line.QtyOrdered,
        qty_shipped = line.QtyShipped,
        qty_remaining = line.QtyRemaining
    };
}

static object MapOrderReceiptRemaining(OrderReceiptLine line)
{
    return new
    {
        order_line_id = line.OrderLineId,
        order_id = line.OrderId,
        item_id = line.ItemId,
        item_name = line.ItemName,
        qty_ordered = line.QtyOrdered,
        qty_received = line.QtyReceived,
        qty_remaining = line.QtyRemaining,
        to_location_id = line.ToLocationId,
        to_location = line.ToLocation,
        to_hu = line.ToHu,
        sort_order = line.SortOrder
    };
}

static object MapStockRow(StockRow row)
{
    return new
    {
        item_id = row.ItemId,
        item_name = row.ItemName,
        barcode = row.Barcode,
        location_code = row.LocationCode,
        hu = row.Hu,
        qty = row.Qty,
        base_uom = string.IsNullOrWhiteSpace(row.BaseUom) ? "шт" : row.BaseUom,
        item_type_id = row.ItemTypeId,
        item_type_name = row.ItemTypeName,
        item_type_enable_min_stock_control = row.ItemTypeEnableMinStockControl,
        item_type_min_stock_uses_order_binding = row.ItemTypeMinStockUsesOrderBinding,
        item_type_enable_order_reservation = row.ItemTypeEnableOrderReservation,
        min_stock_qty = row.MinStockQty,
        reserved_customer_order_qty = row.ReservedCustomerOrderQty,
        available_for_min_stock_qty = row.AvailableForMinStockQty
    };
}

static object MapProductionNeedRow(ProductionNeedRow row)
{
    return new
    {
        item_id = row.ItemId,
        item_name = row.ItemName,
        item_type = row.ItemTypeName,
        physical_stock_qty = row.PhysicalStockQty,
        active_customer_order_open_qty = row.ActiveCustomerOrderOpenQty,
        reserved_customer_order_qty = row.ReservedCustomerOrderQty,
        free_stock_qty = row.FreeStockQty,
        min_stock_qty = row.MinStockQty,
        production_need_qty = row.ProductionNeedQty
    };
}

static object MapHuRecord(HuRecord record)
{
    return new
    {
        id = record.Id,
        hu_code = record.Code,
        status = record.Status,
        created_at = record.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
        created_by = record.CreatedBy,
        closed_at = record.ClosedAt?.ToString("O", CultureInfo.InvariantCulture),
        note = record.Note
    };
}

static object MapHuLedgerRow(HuLedgerRow row)
{
    return new
    {
        hu_code = row.HuCode,
        item_id = row.ItemId,
        item_name = row.ItemName,
        location_id = row.LocationId,
        location_code = row.LocationCode,
        qty = row.Qty,
        base_uom = string.IsNullOrWhiteSpace(row.BaseUom) ? "шт" : row.BaseUom
    };
}

static object MapImportErrorView(ImportErrorView error)
{
    return new
    {
        id = error.Id,
        event_id = error.EventId,
        reason = error.Reason,
        raw_json = error.RawJson,
        created_at = error.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
        barcode = error.Barcode
    };
}

static object MapItemPackaging(ItemPackaging packaging)
{
    return new
    {
        id = packaging.Id,
        item_id = packaging.ItemId,
        code = packaging.Code,
        name = packaging.Name,
        factor_to_base = packaging.FactorToBase,
        is_active = packaging.IsActive,
        sort_order = packaging.SortOrder
    };
}

static async Task<string> ReadBody(HttpRequest request)
{
    using var reader = new StreamReader(request.Body);
    return await reader.ReadToEndAsync();
}

static async Task<(bool IsSuccess, T? Value, IResult? Error)> ParseJsonBody<T>(HttpRequest request)
{
    var rawJson = await ReadBody(request);
    if (string.IsNullOrWhiteSpace(rawJson))
    {
        return (false, default, Results.BadRequest(new ApiResult(false, "EMPTY_BODY")));
    }

    try
    {
        var value = JsonSerializer.Deserialize<T>(
            rawJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (value == null)
        {
            return (false, default, Results.BadRequest(new ApiResult(false, "INVALID_JSON")));
        }

        return (true, value, null);
    }
    catch (JsonException)
    {
        return (false, default, Results.BadRequest(new ApiResult(false, "INVALID_JSON")));
    }
}

static bool ParseIncludeResolved(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    return value.Trim().ToLowerInvariant() switch
    {
        "1" => true,
        "true" => true,
        "yes" => true,
        "on" => true,
        _ => false
    };
}

static bool ParseIncludeInactive(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    return value.Trim().ToLowerInvariant() switch
    {
        "1" => true,
        "true" => true,
        "yes" => true,
        "on" => true,
        _ => false
    };
}

static string? NormalizeOrderRequestResolutionStatus(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    return value.Trim().ToUpperInvariant() switch
    {
        OrderRequestStatus.Approved => OrderRequestStatus.Approved,
        OrderRequestStatus.Rejected => OrderRequestStatus.Rejected,
        _ => null
    };
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

static void SavePartnerStatus(long partnerId, PartnerRole status)
{
    var path = GetPartnerStatusPath();
    Dictionary<long, PartnerRole> data;
    if (File.Exists(path))
    {
        try
        {
            var json = File.ReadAllText(path);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };
            data = JsonSerializer.Deserialize<Dictionary<long, PartnerRole>>(json, options)
                   ?? new Dictionary<long, PartnerRole>();
        }
        catch
        {
            data = new Dictionary<long, PartnerRole>();
        }
    }
    else
    {
        data = new Dictionary<long, PartnerRole>();
    }

    data[partnerId] = status;
    SavePartnerStatuses(path, data);
}

static void RemovePartnerStatus(long partnerId)
{
    var path = GetPartnerStatusPath();
    if (!File.Exists(path))
    {
        return;
    }

    try
    {
        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var data = JsonSerializer.Deserialize<Dictionary<long, PartnerRole>>(json, options)
                   ?? new Dictionary<long, PartnerRole>();
        if (!data.Remove(partnerId))
        {
            return;
        }

        SavePartnerStatuses(path, data);
    }
    catch
    {
    }
}

static void SavePartnerStatuses(string path, Dictionary<long, PartnerRole> data)
{
    var dir = Path.GetDirectoryName(path);
    if (!string.IsNullOrWhiteSpace(dir))
    {
        Directory.CreateDirectory(dir);
    }

    var options = new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    var json = JsonSerializer.Serialize(data, options);
    File.WriteAllText(path, json);
}

static string GetPartnerStatusPath()
{
    return Path.Combine(ServerPaths.BaseDir, "partner_statuses.json");
}

static PartnerRole? ParsePartnerStatusValue(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return PartnerRole.Both;
    }

    return value.Trim().ToUpperInvariant() switch
    {
        "SUPPLIER" => PartnerRole.Supplier,
        "CLIENT" => PartnerRole.Client,
        "CUSTOMER" => PartnerRole.Client,
        "BOTH" => PartnerRole.Both,
        _ => null
    };
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

static string ResolveAppVersion()
{
    var assembly = typeof(Program).Assembly;
    var assemblyVersion = assembly.GetName().Version?.ToString() ?? "0.0.0";
    var moduleVersionId = assembly.ManifestModule.ModuleVersionId.ToString("N");
    return $"{assemblyVersion}-{moduleVersionId}";
}

static bool ShouldPublishLiveEvent(HttpContext context)
{
    var path = context.Request.Path;
    if (!path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (path.StartsWithSegments("/api/live", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (HttpMethods.IsGet(context.Request.Method)
        || HttpMethods.IsHead(context.Request.Method)
        || HttpMethods.IsOptions(context.Request.Method))
    {
        return false;
    }

    return context.Response.StatusCode >= StatusCodes.Status200OK
           && context.Response.StatusCode < StatusCodes.Status400BadRequest;
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

