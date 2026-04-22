using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowStock.Core.Models;

namespace FlowStock.App;

public sealed class WpfReadApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsService _settings;
    private readonly FileLogger _logger;

    public WpfReadApiService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool TryGetLocations(out IReadOnlyList<Location> locations)
    {
        locations = Array.Empty<Location>();
        return TryRead(
            "/api/locations",
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                    .Select(MapLocation)
                    .ToList()
                : new List<Location>(),
            "locations",
            out locations);
    }

    public bool TryGetPartners(out IReadOnlyList<Partner> partners)
    {
        partners = Array.Empty<Partner>();
        return TryRead(
            "/api/partners",
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                    .Select(MapPartner)
                    .ToList()
                : new List<Partner>(),
            "partners",
            out partners);
    }

    public bool TryGetItems(string? search, out IReadOnlyList<Item> items)
    {
        items = Array.Empty<Item>();
        var path = "/api/items";
        if (!string.IsNullOrWhiteSpace(search))
        {
            path += "?q=" + Uri.EscapeDataString(search.Trim());
        }

        return TryRead(
            path,
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                    .Select(MapItem)
                    .ToList()
                : new List<Item>(),
            "items",
            out items);
    }

    public bool TryGetDocs(DocType? typeFilter, DocStatus? statusFilter, out IReadOnlyList<Doc> docs)
    {
        docs = Array.Empty<Doc>();
        var query = new List<string>();
        if (typeFilter.HasValue)
        {
            query.Add($"op={Uri.EscapeDataString(DocTypeMapper.ToOpString(typeFilter.Value))}");
        }

        if (statusFilter.HasValue)
        {
            query.Add($"status={Uri.EscapeDataString(DocTypeMapper.StatusToString(statusFilter.Value))}");
        }

        var path = "/api/docs";
        if (query.Count > 0)
        {
            path += "?" + string.Join("&", query);
        }

        return TryRead(
            path,
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                    .Select(MapDoc)
                    .OrderByDescending(doc => doc.CreatedAt)
                    .ToList()
                : new List<Doc>(),
            "docs",
            out docs);
    }

    public bool TryGetOrders(bool includeInternal, string? search, out IReadOnlyList<Order> orders)
    {
        orders = Array.Empty<Order>();
        var query = new List<string>();
        if (includeInternal)
        {
            query.Add("include_internal=1");
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query.Add($"q={Uri.EscapeDataString(search.Trim())}");
        }

        var path = "/api/orders";
        if (query.Count > 0)
        {
            path += "?" + string.Join("&", query);
        }

        return TryRead(
            path,
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                    .Select(MapOrder)
                    .ToList()
                : new List<Order>(),
            "orders",
            out orders);
    }

    public bool TryGetOrder(long orderId, out Order? order)
    {
        order = null;
        return TryRead(
            $"/api/orders/{orderId}",
            MapOrder,
            "order-detail",
            out order);
    }

    public bool TryGetOrderLines(long orderId, out IReadOnlyList<OrderLineView> lines)
    {
        lines = Array.Empty<OrderLineView>();
        return TryRead(
            $"/api/orders/{orderId}/lines",
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                    .Select(MapOrderLineView)
                    .ToList()
                : new List<OrderLineView>(),
            "order-lines",
            out lines);
    }

    public bool TryGetOrderShipmentRemaining(long orderId, out IReadOnlyList<OrderShipmentLine> lines)
    {
        lines = Array.Empty<OrderShipmentLine>();
        return TryRead(
            $"/api/orders/{orderId}/shipment-remaining",
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                    .Select(MapOrderShipmentLine)
                    .ToList()
                : new List<OrderShipmentLine>(),
            "order-shipment-remaining",
            out lines);
    }

    public bool TryGetOrderReceiptRemaining(long orderId, out IReadOnlyList<OrderReceiptLine> lines)
    {
        lines = Array.Empty<OrderReceiptLine>();
        return TryRead(
            $"/api/orders/{orderId}/receipt-remaining",
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                    .Select(MapOrderReceiptLine)
                    .ToList()
                : new List<OrderReceiptLine>(),
            "order-receipt-remaining",
            out lines);
    }

    public bool TryGetStockRows(string? search, out IReadOnlyList<StockRow> rows)
    {
        rows = Array.Empty<StockRow>();
        var path = "/api/stock/rows";
        if (!string.IsNullOrWhiteSpace(search))
        {
            path += "?q=" + Uri.EscapeDataString(search.Trim());
        }

        return TryRead(
            path,
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                    .Select(MapStockRow)
                    .ToList()
                : new List<StockRow>(),
            "stock-rows",
            out rows);
    }

    public bool TryGetItemAvailability(out IReadOnlyDictionary<long, double> availability)
    {
        availability = new Dictionary<long, double>();
        if (!TryGetStockRows(null, out var rows))
        {
            return false;
        }

        availability = rows
            .GroupBy(row => row.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Qty));
        return true;
    }

    public bool TryGetDoc(long docId, out Doc? doc)
    {
        doc = null;
        return TryRead(
            $"/api/docs/{docId}",
            MapDoc,
            "doc-detail",
            out doc);
    }

    public bool TryGetDocLines(long docId, out IReadOnlyList<DocLineView> lines)
    {
        lines = Array.Empty<DocLineView>();
        return TryRead(
            $"/api/docs/{docId}/lines",
            root => root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                    .Select(MapDocLineView)
                    .ToList()
                : new List<DocLineView>(),
            "doc-lines",
            out lines);
    }

    public bool TryGenerateNextDocRef(DocType type, out string docRef)
    {
        docRef = string.Empty;
        var path = $"/api/docs/next-ref?type={Uri.EscapeDataString(DocTypeMapper.ToOpString(type))}";
        return TryRead(
            path,
            root => root.TryGetProperty("doc_ref", out var docRefElement)
                ? (docRefElement.GetString() ?? string.Empty)
                : string.Empty,
            "docs-next-ref",
            out docRef)
            && !string.IsNullOrWhiteSpace(docRef);
    }

    public bool TryGenerateNextOrderRef(out string orderRef)
    {
        orderRef = string.Empty;
        return TryRead(
            "/api/orders/next-ref",
            root => root.TryGetProperty("order_ref", out var orderRefElement)
                ? (orderRefElement.GetString() ?? string.Empty)
                : string.Empty,
            "orders-next-ref",
            out orderRef)
            && !string.IsNullOrWhiteSpace(orderRef);
    }

    private bool TryRead<T>(
        string relativePath,
        Func<JsonElement, T> map,
        string operationName,
        out T value)
    {
        value = default!;

        try
        {
            var configuration = LoadConfiguration();
            if (!configuration.IsConfigured)
            {
                _logger.Info($"WPF read API skipped for {operationName}: server base URL is not configured.");
                return false;
            }

            var payload = SendRequest(relativePath, configuration);
            if (payload == null)
            {
                return false;
            }

            value = map(payload.RootElement);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"WPF read API failed for {operationName}", ex);
            return false;
        }
    }

    private JsonDocument? SendRequest(string relativePath, WpfReadApiConfiguration configuration)
    {
        using var handler = CreateHandler(configuration);
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(configuration.BaseUrl!, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds)
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        using var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

        if (!response.IsSuccessStatusCode)
        {
            _logger.Warn($"WPF read API request failed: {relativePath} -> {(int)response.StatusCode} {response.ReasonPhrase}");
            return null;
        }

        var json = response.Content.ReadAsStringAsync()
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

        return JsonDocument.Parse(json);
    }

    private WpfReadApiConfiguration LoadConfiguration()
    {
        var settings = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = ReadEnvOrSettings("FLOWSTOCK_SERVER_BASE_URL", settings.BaseUrl);
        var timeoutSeconds = ReadEnvInt("FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS") ?? settings.CloseTimeoutSeconds;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = WpfCloseDocumentService.DefaultCloseTimeoutSeconds;
        }

        return new WpfReadApiConfiguration(
            NormalizeBaseUrl(baseUrl),
            timeoutSeconds,
            ReadEnvBool("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS") ?? settings.AllowInvalidTls);
    }

    private static HttpMessageHandler CreateHandler(WpfReadApiConfiguration configuration)
    {
        var handler = new HttpClientHandler();
        if (configuration.AllowInvalidTls)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
    }

    private static string? NormalizeBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }

        return trimmed.TrimEnd('/');
    }

    private static string? ReadEnvOrSettings(string envKey, string? settingsValue)
    {
        var env = Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        return string.IsNullOrWhiteSpace(settingsValue) ? null : settingsValue.Trim();
    }

    private static bool? ReadEnvBool(string envKey)
    {
        var env = Environment.GetEnvironmentVariable(envKey);
        if (string.IsNullOrWhiteSpace(env))
        {
            return null;
        }

        return env.Trim().ToLowerInvariant() switch
        {
            "1" => true,
            "true" => true,
            "yes" => true,
            "on" => true,
            "0" => false,
            "false" => false,
            "no" => false,
            "off" => false,
            _ => null
        };
    }

    private static int? ReadEnvInt(string envKey)
    {
        var env = Environment.GetEnvironmentVariable(envKey);
        if (string.IsNullOrWhiteSpace(env))
        {
            return null;
        }

        return int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static Location MapLocation(JsonElement element)
    {
        var autoHuDistributionEnabled = true;
        if (element.TryGetProperty("auto_hu_distribution_enabled", out var autoProperty))
        {
            if (autoProperty.ValueKind == JsonValueKind.True || autoProperty.ValueKind == JsonValueKind.False)
            {
                autoHuDistributionEnabled = autoProperty.GetBoolean();
            }
            else if (!bool.TryParse(autoProperty.ToString(), out autoHuDistributionEnabled))
            {
                autoHuDistributionEnabled = true;
            }
        }

        return new Location
        {
            Id = ReadInt64(element, "id"),
            Code = ReadString(element, "code") ?? string.Empty,
            Name = ReadString(element, "name") ?? string.Empty,
            MaxHuSlots = ReadNullableInt32(element, "max_hu_slots"),
            AutoHuDistributionEnabled = autoHuDistributionEnabled
        };
    }

    private static Partner MapPartner(JsonElement element)
    {
        return new Partner
        {
            Id = ReadInt64(element, "id"),
            Name = ReadString(element, "name") ?? string.Empty,
            Code = ReadString(element, "code"),
            CreatedAt = DateTime.MinValue
        };
    }

    private static Doc MapDoc(JsonElement element)
    {
        var type = DocTypeMapper.FromOpString(ReadString(element, "op")) ?? DocType.Inbound;
        var status = DocTypeMapper.StatusFromString(ReadString(element, "status")) ?? DocStatus.Draft;

        return new Doc
        {
            Id = ReadInt64(element, "id"),
            DocRef = ReadString(element, "doc_ref") ?? string.Empty,
            ApiDocUid = ReadString(element, "doc_uid"),
            Type = type,
            Status = status,
            CreatedAt = ReadDateTime(element, "created_at") ?? DateTime.MinValue,
            ClosedAt = ReadDateTime(element, "closed_at"),
            PartnerId = ReadNullableInt64(element, "partner_id"),
            PartnerName = ReadString(element, "partner_name"),
            PartnerCode = ReadString(element, "partner_code"),
            OrderId = ReadNullableInt64(element, "order_id"),
            OrderRef = ReadString(element, "order_ref"),
            ShippingRef = ReadString(element, "shipping_ref"),
            ReasonCode = ReadString(element, "reason_code"),
            Comment = ReadString(element, "comment"),
            ProductionBatchNo = ReadString(element, "production_batch_no"),
            SourceDeviceId = ReadString(element, "source_device_id"),
            LineCount = ReadInt32(element, "line_count")
        };
    }

    private static Order MapOrder(JsonElement element)
    {
        var type = OrderStatusMapper.TypeFromString(ReadString(element, "order_type")) ?? OrderType.Customer;
        var status = ParseOrderStatus(ReadString(element, "status"), type);

        return new Order
        {
            Id = ReadInt64(element, "id"),
            OrderRef = ReadString(element, "order_ref") ?? string.Empty,
            Type = type,
            PartnerId = ReadNullableInt64(element, "partner_id"),
            PartnerName = ReadString(element, "partner_name"),
            PartnerCode = ReadString(element, "partner_code"),
            DueDate = ReadDateOnly(element, "due_date"),
            Status = status,
            Comment = ReadString(element, "comment"),
            CreatedAt = ReadDateTime(element, "created_at") ?? DateTime.MinValue,
            ShippedAt = ReadDateTime(element, "shipped_at")
        };
    }

    private static OrderLineView MapOrderLineView(JsonElement element)
    {
        return new OrderLineView
        {
            Id = ReadInt64(element, "id"),
            OrderId = ReadInt64(element, "order_id"),
            ItemId = ReadInt64(element, "item_id"),
            ItemName = ReadString(element, "item_name") ?? string.Empty,
            Barcode = ReadString(element, "barcode"),
            Gtin = ReadString(element, "gtin"),
            QtyOrdered = ReadDouble(element, "qty_ordered"),
            QtyShipped = ReadDouble(element, "qty_shipped"),
            QtyProduced = ReadDouble(element, "qty_produced"),
            QtyRemaining = ReadDouble(element, "qty_left"),
            QtyAvailable = ReadDouble(element, "qty_available"),
            CanShipNow = ReadDouble(element, "can_ship_now"),
            Shortage = ReadDouble(element, "shortage")
        };
    }

    private static Item MapItem(JsonElement element)
    {
        return new Item
        {
            Id = ReadInt64(element, "id"),
            Name = ReadString(element, "name") ?? string.Empty,
            Barcode = ReadString(element, "barcode"),
            Gtin = ReadString(element, "gtin"),
            BaseUom = ReadString(element, "base_uom_code") ?? ReadString(element, "base_uom") ?? "шт",
            DefaultPackagingId = ReadNullableInt64(element, "default_packaging_id"),
            Brand = ReadString(element, "brand"),
            Volume = ReadString(element, "volume"),
            ShelfLifeMonths = ReadNullableInt32(element, "shelf_life_months"),
            MaxQtyPerHu = ReadNullableDouble(element, "max_qty_per_hu"),
            TaraId = ReadNullableInt64(element, "tara_id"),
            TaraName = ReadString(element, "tara_name"),
            IsMarked = ReadBool(element, "is_marked"),
            ItemTypeId = ReadNullableInt64(element, "item_type_id"),
            ItemTypeName = ReadString(element, "item_type_name"),
            ItemTypeIsVisibleInProductCatalog = ReadBool(element, "item_type_is_visible_in_product_catalog"),
            ItemTypeEnableMinStockControl = ReadBool(element, "item_type_enable_min_stock_control"),
            MinStockQty = ReadNullableDouble(element, "min_stock_qty")
        };
    }

    private static StockRow MapStockRow(JsonElement element)
    {
        return new StockRow
        {
            ItemId = ReadInt64(element, "item_id"),
            ItemName = ReadString(element, "item_name") ?? string.Empty,
            Barcode = ReadString(element, "barcode"),
            LocationCode = ReadString(element, "location_code") ?? string.Empty,
            Hu = ReadString(element, "hu"),
            Qty = ReadDouble(element, "qty"),
            BaseUom = ReadString(element, "base_uom") ?? "шт",
            ItemTypeId = ReadNullableInt64(element, "item_type_id"),
            ItemTypeName = ReadString(element, "item_type_name"),
            ItemTypeEnableMinStockControl = ReadBool(element, "item_type_enable_min_stock_control"),
            MinStockQty = ReadNullableDouble(element, "min_stock_qty")
        };
    }

    private static DocLineView MapDocLineView(JsonElement element)
    {
        return new DocLineView
        {
            Id = ReadInt64(element, "id"),
            OrderLineId = ReadNullableInt64(element, "order_line_id"),
            ItemId = ReadInt64(element, "item_id"),
            ItemName = ReadString(element, "item_name") ?? string.Empty,
            Barcode = ReadString(element, "barcode"),
            Qty = ReadDouble(element, "qty"),
            QtyInput = ReadNullableDouble(element, "qty_input"),
            UomCode = ReadString(element, "uom_code"),
            BaseUom = ReadString(element, "base_uom") ?? "шт",
            FromLocation = ReadString(element, "from_location"),
            ToLocation = ReadString(element, "to_location"),
            FromHu = ReadString(element, "from_hu"),
            ToHu = ReadString(element, "to_hu"),
            PackSingleHu = ReadBool(element, "pack_single_hu")
        };
    }

    private static OrderShipmentLine MapOrderShipmentLine(JsonElement element)
    {
        return new OrderShipmentLine
        {
            OrderLineId = ReadInt64(element, "order_line_id"),
            OrderId = ReadInt64(element, "order_id"),
            ItemId = ReadInt64(element, "item_id"),
            ItemName = ReadString(element, "item_name") ?? string.Empty,
            QtyOrdered = ReadDouble(element, "qty_ordered"),
            QtyShipped = ReadDouble(element, "qty_shipped"),
            QtyRemaining = ReadDouble(element, "qty_remaining")
        };
    }

    private static OrderReceiptLine MapOrderReceiptLine(JsonElement element)
    {
        return new OrderReceiptLine
        {
            OrderLineId = ReadInt64(element, "order_line_id"),
            OrderId = ReadInt64(element, "order_id"),
            ItemId = ReadInt64(element, "item_id"),
            ItemName = ReadString(element, "item_name") ?? string.Empty,
            QtyOrdered = ReadDouble(element, "qty_ordered"),
            QtyReceived = ReadDouble(element, "qty_received"),
            QtyRemaining = ReadDouble(element, "qty_remaining")
        };
    }

    private static OrderStatus ParseOrderStatus(string? value, OrderType type)
    {
        var parsed = OrderStatusMapper.StatusFromString(value);
        if (parsed.HasValue)
        {
            return parsed.Value;
        }

        foreach (var candidate in new[] { OrderStatus.Draft, OrderStatus.Accepted, OrderStatus.InProgress, OrderStatus.Shipped })
        {
            if (string.Equals(value, OrderStatusMapper.StatusToDisplayName(candidate, type), StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return OrderStatus.Draft;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
    }

    private static long ReadInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : 0;
    }

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private static int? ReadNullableInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.TryGetInt32(out var value))
        {
            return value;
        }

        return int.TryParse(property.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            ? value
            : null;
    }

    private static long? ReadNullableInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.TryGetInt64(out var value))
        {
            return value;
        }

        return long.TryParse(property.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            ? value
            : null;
    }

    private static double ReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0d;
        }

        if (property.TryGetDouble(out var value))
        {
            return value;
        }

        return double.TryParse(property.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            ? value
            : 0d;
    }

    private static double? ReadNullableDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.TryGetDouble(out var value))
        {
            return value;
        }

        return double.TryParse(property.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            ? value
            : null;
    }

    private static bool ReadBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
        {
            return property.GetBoolean();
        }

        return bool.TryParse(property.ToString(), out var parsed) && parsed;
    }

    private static DateTime? ReadDateTime(JsonElement element, string propertyName)
    {
        var raw = ReadString(element, propertyName);
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;
    }

    private static DateTime? ReadDateOnly(JsonElement element, string propertyName)
    {
        var raw = ReadString(element, propertyName);
        return DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : null;
    }
}

public sealed record WpfReadApiConfiguration(
    string? BaseUrl,
    int TimeoutSeconds,
    bool AllowInvalidTls)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
}
