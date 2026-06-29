using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace FlowStock.Server;

public static class ItemCatalogEndpoints
{
    public static void Map(WebApplication app, string? postgresConnectionString)
    {
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
       COALESCE(it.enable_marking, FALSE),
       i.min_stock_qty,
       i.storage_conditions
FROM items i
LEFT JOIN taras t ON t.id = i.tara_id
LEFT JOIN item_types it ON it.id = i.item_type_id
WHERE @search::text IS NULL
   OR i.name ILIKE @search::text
   OR i.barcode ILIKE @search::text
   OR i.gtin ILIKE @search::text
ORDER BY i.name;";
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
                    item_type_enable_marking = !reader.IsDBNull(19) && reader.GetBoolean(19),
                    min_stock_qty = reader.IsDBNull(20) ? (double?)null : Convert.ToDouble(reader.GetValue(20), CultureInfo.InvariantCulture),
                    storage_conditions = reader.IsDBNull(21) ? null : reader.GetString(21)
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
                    parsed.Value?.MinStockQty,
                    parsed.Value?.StorageConditions);
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
                    parsed.Value?.MinStockQty,
                    parsed.Value?.StorageConditions);
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
    }

    internal static Item? FindItemByBarcodeVariant(IDataStore store, string barcode)
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

    private static object MapItem(Item item)
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
            storage_conditions = item.StorageConditions,
            tara_id = item.TaraId,
            tara_name = item.TaraName,
            is_marked = item.IsMarked,
            item_type_id = item.ItemTypeId,
            item_type_name = item.ItemTypeName,
            item_type_is_visible_in_product_catalog = item.ItemTypeIsVisibleInProductCatalog,
            item_type_enable_min_stock_control = item.ItemTypeEnableMinStockControl,
            item_type_enable_marking = item.ItemTypeEnableMarking,
            cz_marking_required = item.IsChestnyZnakMarkingRequired,
            min_stock_qty = item.MinStockQty
        };
    }

    private static DbConnection OpenConnection(string? postgresConnectionString)
    {
        var connection = new NpgsqlConnection(postgresConnectionString);
        connection.Open();
        return connection;
    }

    private static void AddParam(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static async Task<(bool IsSuccess, T? Value, IResult? Error)> ParseJsonBody<T>(HttpRequest request)
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

    private static async Task<string> ReadBody(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        return await reader.ReadToEndAsync();
    }
}
