using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowStock.App.Services;

public sealed class WpfCommercialApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsService _settings;
    private readonly FileLogger _logger;

    public WpfCommercialApiService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool TryGetPriceGroups(out IReadOnlyList<CommercialPriceGroupRow> groups)
    {
        groups = Array.Empty<CommercialPriceGroupRow>();
        return TryRead("/api/price-groups", root =>
        {
            if (root.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<CommercialPriceGroupRow>();
            }

            return root.EnumerateArray().Select(MapPriceGroup).ToList();
        }, "commercial-price-groups", out groups);
    }

    public async Task<(bool IsSuccess, long? Id, string? Error)> TryCreatePriceGroupAsync(
        string name,
        string? description,
        string currency,
        string vatMode,
        bool isDefault,
        decimal defaultDiscountPercent = 0m,
        decimal defaultMarkupPercent = 0m,
        CancellationToken cancellationToken = default)
    {
        return await TryPostForIdAsync("/api/price-groups", new
        {
            name,
            description,
            currency,
            vat_mode = vatMode,
            is_default = isDefault,
            is_active = true,
            default_discount_percent = defaultDiscountPercent,
            default_markup_percent = defaultMarkupPercent
        }, "price_group_id", "commercial-create-price-group", cancellationToken).ConfigureAwait(false);
    }

    public bool TryGetItemPriceCatalog(
        long priceGroupId,
        out IReadOnlyList<CommercialItemPriceRow> rows,
        string? search = null,
        bool? hasPrice = null)
    {
        rows = Array.Empty<CommercialItemPriceRow>();
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(search))
        {
            query.Add($"search={Uri.EscapeDataString(search.Trim())}");
        }

        if (hasPrice.HasValue)
        {
            query.Add($"has_price={(hasPrice.Value ? "true" : "false")}");
        }

        var path = query.Count == 0
            ? $"/api/price-groups/{priceGroupId}/item-prices"
            : $"/api/price-groups/{priceGroupId}/item-prices?{string.Join("&", query)}";

        return TryRead(path, root =>
        {
            if (root.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<CommercialItemPriceRow>();
            }

            return root.EnumerateArray().Select(MapItemPriceCatalog).ToList();
        }, "commercial-item-prices", out rows);
    }

    public async Task<(bool IsSuccess, long? ItemPriceId, string? Error)> TryUpsertItemPriceAsync(
        long itemId,
        long priceGroupId,
        decimal price,
        string currency,
        string validFrom,
        string? validTo,
        string? comment,
        bool isActive = true,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        if (client == null)
        {
            return (false, null, "SERVER_NOT_CONFIGURED");
        }

        try
        {
            var response = await client.PostAsJsonAsync($"/api/items/{itemId}/prices", new
            {
                price_group_id = priceGroupId,
                price,
                currency,
                valid_from = validFrom,
                valid_to = validTo,
                comment,
                is_active = isActive
            }, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, null, ParseError(body));
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var itemPriceId = root.TryGetProperty("item_price_id", out var idEl) ? idEl.GetInt64() : (long?)null;
            return (true, itemPriceId, null);
        }
        catch (Exception ex)
        {
            _logger.Error("commercial-upsert-item-price failed", ex);
            return (false, null, ex.Message);
        }
    }

    public async Task<(bool IsSuccess, string? Error)> TryDeactivateItemPriceAsync(
        long itemPriceId,
        CancellationToken cancellationToken = default)
    {
        return await TryPostAsync($"/api/item-prices/{itemPriceId}/deactivate", new { }, "commercial-deactivate-item-price", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TryUpdatePriceGroupAsync(
        long id,
        string name,
        string? description,
        string currency,
        string vatMode,
        bool isDefault,
        bool isActive,
        decimal defaultDiscountPercent = 0m,
        decimal defaultMarkupPercent = 0m,
        CancellationToken cancellationToken = default)
    {
        return await TryPostAsync($"/api/price-groups/{id}", new
        {
            name,
            description,
            currency,
            vat_mode = vatMode,
            is_default = isDefault,
            is_active = isActive,
            default_discount_percent = defaultDiscountPercent,
            default_markup_percent = defaultMarkupPercent
        }, "commercial-update-price-group", cancellationToken).ConfigureAwait(false);
    }

    public bool TryGetItemPricingOverview(long itemId, out IReadOnlyList<CommercialItemPricingOverviewRow> rows)
    {
        rows = Array.Empty<CommercialItemPricingOverviewRow>();
        return TryRead($"/api/items/{itemId}/prices", root =>
        {
            if (root.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<CommercialItemPricingOverviewRow>();
            }

            return root.EnumerateArray().Select(MapItemPricingOverview).ToList();
        }, "commercial-item-pricing-overview", out rows);
    }

    public bool TryGetCommercialOffers(out IReadOnlyList<CommercialOfferRow> offers, string? status = null)
    {
        offers = Array.Empty<CommercialOfferRow>();
        var path = string.IsNullOrWhiteSpace(status)
            ? "/api/commercial/offers"
            : $"/api/commercial/offers?status={Uri.EscapeDataString(status)}";
        return TryRead(path, root =>
        {
            if (root.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<CommercialOfferRow>();
            }

            return root.EnumerateArray().Select(MapOfferRow).ToList();
        }, "commercial-offers", out offers);
    }

    public bool TryGetCommercialOffer(long id, out CommercialOfferDetails? details)
    {
        details = null;
        if (!TryRead($"/api/commercial/offers/{id}", root => MapOfferDetails(root), "commercial-offer-details", out CommercialOfferDetails mapped))
        {
            return false;
        }

        details = mapped;
        return true;
    }

    public async Task<(bool IsSuccess, long? OfferId, string? OfferRef, string? Error)> TryCreateCommercialOfferAsync(
        CommercialOfferCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        if (client == null)
        {
            return (false, null, null, "SERVER_NOT_CONFIGURED");
        }

        try
        {
            var response = await client.PostAsJsonAsync("/api/commercial/offers", new
            {
                partner_id = request.PartnerId,
                price_group_id = request.PriceGroupId,
                valid_until = request.ValidUntil,
                contact_person = request.ContactPerson,
                contact_phone = request.ContactPhone,
                contact_email = request.ContactEmail,
                payment_terms = request.PaymentTerms,
                delivery_terms = request.DeliveryTerms,
                comment = request.Comment,
                manager_name = request.ManagerName
            }, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, null, null, ParseError(body));
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var offerId = root.TryGetProperty("offer_id", out var idEl) ? idEl.GetInt64() : (long?)null;
            var offerRef = root.TryGetProperty("offer_ref", out var refEl) ? refEl.GetString() : null;
            return (true, offerId, offerRef, null);
        }
        catch (Exception ex)
        {
            _logger.Error("commercial-create-offer failed", ex);
            return (false, null, null, ex.Message);
        }
    }

    public async Task<(bool IsSuccess, string? Error)> TryAddOfferLineAsync(
        long offerId,
        long itemId,
        double qty,
        string? uomCode,
        decimal manualDiscountPercent = 0m,
        CancellationToken cancellationToken = default)
    {
        return await TryPostAsync($"/api/commercial/offers/{offerId}/lines", new
        {
            item_id = itemId,
            qty,
            uom_code = uomCode,
            manual_discount_percent = manualDiscountPercent
        }, "commercial-offer-add-line", cancellationToken).ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TryDeleteOfferLineAsync(
        long offerId,
        long lineId,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        if (client == null)
        {
            return (false, "SERVER_NOT_CONFIGURED");
        }

        try
        {
            var response = await client.DeleteAsync($"/api/commercial/offers/{offerId}/lines/{lineId}", cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return (true, null);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (false, ParseError(body));
        }
        catch (Exception ex)
        {
            _logger.Error("commercial-delete-offer-line failed", ex);
            return (false, ex.Message);
        }
    }

    public async Task<(bool IsSuccess, string? Error)> TryRecalculateOfferPricesAsync(
        long offerId,
        CancellationToken cancellationToken = default)
    {
        return await TryPostAsync($"/api/commercial/offers/{offerId}/recalculate-prices", new { }, "commercial-offer-recalculate", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TryUpdateCommercialOfferAsync(
        long offerId,
        long partnerId,
        long priceGroupId,
        CancellationToken cancellationToken = default)
    {
        return await TryPostAsync($"/api/commercial/offers/{offerId}", new
        {
            partner_id = partnerId,
            price_group_id = priceGroupId
        }, "commercial-offer-update", cancellationToken).ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TryDeleteCommercialOfferAsync(
        long offerId,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        if (client == null)
        {
            return (false, "SERVER_NOT_CONFIGURED");
        }

        try
        {
            var response = await client.DeleteAsync($"/api/commercial/offers/{offerId}", cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return (true, null);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (false, ParseError(body));
        }
        catch (Exception ex)
        {
            _logger.Error("commercial-delete-offer failed", ex);
            return (false, ex.Message);
        }
    }

    public async Task<(bool IsSuccess, string? Error)> TryChangeOfferStatusAsync(
        long offerId,
        string status,
        string? comment,
        CancellationToken cancellationToken = default)
    {
        return await TryPostAsync($"/api/commercial/offers/{offerId}/status", new
        {
            status,
            comment
        }, "commercial-offer-status", cancellationToken).ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? FilePath, string? Error)> TryGenerateOfferDocxAsync(
        long offerId,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        if (client == null)
        {
            return (false, null, "SERVER_NOT_CONFIGURED");
        }

        try
        {
            var response = await client.PostAsync($"/api/commercial/offers/{offerId}/generate-docx", null, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, null, ParseError(body));
            }

            using var doc = JsonDocument.Parse(body);
            var filePath = doc.RootElement.TryGetProperty("file_path", out var pathEl) ? pathEl.GetString() : null;
            return (true, filePath, null);
        }
        catch (Exception ex)
        {
            _logger.Error("commercial-generate-docx failed", ex);
            return (false, null, ex.Message);
        }
    }

    public async Task<(bool IsSuccess, string? Error)> TryUpsertPartnerCommercialSettingsAsync(
        long partnerId,
        long? priceGroupId,
        decimal defaultDiscountPercent,
        string? paymentTerms,
        string? deliveryTerms,
        CancellationToken cancellationToken = default)
    {
        return await TryPostAsync($"/api/partners/{partnerId}/commercial-settings", new
        {
            price_group_id = priceGroupId,
            default_discount_percent = defaultDiscountPercent,
            payment_terms = paymentTerms,
            delivery_terms = deliveryTerms
        }, "commercial-partner-settings", cancellationToken).ConfigureAwait(false);
    }

    public bool TryGetPartnerCommercialSettings(long partnerId, out CommercialPartnerSettingsRow? settings)
    {
        settings = null;
        if (!TryRead($"/api/partners/{partnerId}/commercial-settings", root =>
            {
                return new CommercialPartnerSettingsRow
                {
                    PartnerId = ReadLong(root, "partner_id") ?? partnerId,
                    PriceGroupId = ReadLong(root, "price_group_id"),
                    DefaultDiscountPercent = ReadDecimal(root, "default_discount_percent") ?? 0m,
                    PaymentTerms = ReadString(root, "payment_terms"),
                    DeliveryTerms = ReadString(root, "delivery_terms")
                };
            }, "commercial-partner-settings-read", out CommercialPartnerSettingsRow mapped))
        {
            return false;
        }

        settings = mapped;
        return true;
    }

    public async Task<(bool IsSuccess, long? OrderId, string? Error)> TryCreateOrderFromOfferAsync(
        long offerId,
        string orderRef,
        string? comment,
        CancellationToken cancellationToken = default)
    {
        return await TryPostForOrderIdAsync($"/api/commercial/offers/{offerId}/create-order", new
        {
            order_ref = orderRef,
            comment
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool IsSuccess, long? OrderId, string? Error)> TryPostForOrderIdAsync(
        string path,
        object payload,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        if (client == null)
        {
            return (false, null, "SERVER_NOT_CONFIGURED");
        }

        try
        {
            var response = await client.PostAsJsonAsync(path, payload, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, null, ParseError(body));
            }

            using var doc = JsonDocument.Parse(body);
            var orderId = doc.RootElement.TryGetProperty("order_id", out var idEl) ? idEl.GetInt64() : (long?)null;
            return (true, orderId, null);
        }
        catch (Exception ex)
        {
            _logger.Error("commercial-create-order failed", ex);
            return (false, null, ex.Message);
        }
    }

    public bool TryGetTemplateFields(out IReadOnlyList<CommercialTemplateFieldGroupRow> groups)
    {
        groups = Array.Empty<CommercialTemplateFieldGroupRow>();
        return TryRead("/api/commercial/template-fields", root =>
        {
            if (root.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<CommercialTemplateFieldGroupRow>();
            }

            return root.EnumerateArray().Select(item => new CommercialTemplateFieldGroupRow
            {
                Title = ReadString(item, "title") ?? string.Empty,
                Fields = item.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Array
                    ? fieldsEl.EnumerateArray().Select(f => f.GetString() ?? string.Empty).ToList()
                    : Array.Empty<string>()
            }).ToList();
        }, "commercial-template-fields", out groups);
    }

    private async Task<(bool IsSuccess, long? Id, string? Error)> TryPostForIdAsync(
        string path,
        object payload,
        string idProperty,
        string operation,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        if (client == null)
        {
            return (false, null, "SERVER_NOT_CONFIGURED");
        }

        try
        {
            var response = await client.PostAsJsonAsync(path, payload, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, null, ParseError(body));
            }

            using var doc = JsonDocument.Parse(body);
            var id = doc.RootElement.TryGetProperty(idProperty, out var idEl) ? idEl.GetInt64() : (long?)null;
            return (true, id, null);
        }
        catch (Exception ex)
        {
            _logger.Error($"{operation} failed", ex);
            return (false, null, ex.Message);
        }
    }

    private async Task<(bool IsSuccess, string? Error)> TryPostAsync(
        string path,
        object payload,
        string operation,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        if (client == null)
        {
            return (false, "SERVER_NOT_CONFIGURED");
        }

        try
        {
            var response = await client.PostAsJsonAsync(path, payload, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, ParseError(body));
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.Error($"{operation} failed", ex);
            return (false, ex.Message);
        }
    }

    private bool TryRead<T>(string path, Func<JsonElement, T> map, string operation, out T value)
    {
        value = default!;
        using var client = CreateClient();
        if (client == null)
        {
            return false;
        }

        try
        {
            using var response = client.GetAsync(path).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                _logger.Error($"{operation} failed: {body}");
                return false;
            }

            using var doc = JsonDocument.Parse(body);
            value = map(doc.RootElement);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"{operation} failed", ex);
            return false;
        }
    }

    private HttpClient? CreateClient()
    {
        var settings = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = Environment.GetEnvironmentVariable("FLOWSTOCK_SERVER_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = settings.BaseUrl;
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        var allowInvalidTls = bool.TryParse(Environment.GetEnvironmentVariable("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS"), out var parsedTls)
            ? parsedTls
            : settings.AllowInvalidTls;
        var timeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS"), out var parsedTimeout)
            ? parsedTimeout
            : settings.CloseTimeoutSeconds;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = 120;
        }

        var handler = new HttpClientHandler();
        if (allowInvalidTls)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
    }

    private static string? ParseError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var errorEl))
            {
                return errorEl.GetString();
            }
        }
        catch
        {
            // ignore
        }

        return body;
    }

    private static CommercialItemPriceRow MapItemPriceCatalog(JsonElement el) => new()
    {
        ItemId = ReadLong(el, "item_id") ?? 0,
        ItemName = ReadString(el, "item_name") ?? string.Empty,
        Barcode = ReadString(el, "barcode"),
        Gtin = ReadString(el, "gtin"),
        ItemTypeName = ReadString(el, "item_type"),
        ItemPriceId = ReadLong(el, "item_price_id"),
        PriceGroupId = ReadLong(el, "price_group_id") ?? 0,
        Price = ReadDecimal(el, "price") ?? ReadDecimal(el, "calculated_price"),
        BasePrice = ReadDecimal(el, "base_price"),
        GroupOverridePrice = ReadDecimal(el, "group_override_price"),
        CalculatedPrice = ReadDecimal(el, "calculated_price"),
        GroupDiscountPercent = ReadDecimal(el, "group_discount_percent"),
        GroupMarkupPercent = ReadDecimal(el, "group_markup_percent"),
        PriceSource = ReadString(el, "price_source"),
        PriceMissingReason = ReadString(el, "price_missing_reason"),
        Currency = ReadString(el, "currency"),
        ValidFrom = ReadString(el, "valid_from"),
        ValidTo = ReadString(el, "valid_to"),
        IsActive = el.TryGetProperty("is_active", out var activeEl) && activeEl.ValueKind == JsonValueKind.True,
        Comment = ReadString(el, "comment"),
        HasPrice = ReadBool(el, "has_price"),
        HasBasePrice = ReadBool(el, "has_base_price")
    };

    private static CommercialItemPricingOverviewRow MapItemPricingOverview(JsonElement el) => new()
    {
        PriceGroupId = ReadLong(el, "price_group_id") ?? 0,
        PriceGroupName = ReadString(el, "price_group_name") ?? string.Empty,
        IsSystem = ReadBool(el, "is_system"),
        DefaultDiscountPercent = ReadDecimal(el, "default_discount_percent") ?? 0m,
        DefaultMarkupPercent = ReadDecimal(el, "default_markup_percent") ?? 0m,
        BasePrice = ReadDecimal(el, "base_price"),
        OverridePrice = ReadDecimal(el, "override_price"),
        CalculatedPrice = ReadDecimal(el, "calculated_price"),
        PriceSource = ReadString(el, "price_source") ?? string.Empty,
        Currency = ReadString(el, "currency"),
        ItemPriceId = ReadLong(el, "item_price_id"),
        ValidFrom = ReadString(el, "valid_from"),
        ValidTo = ReadString(el, "valid_to"),
        Comment = ReadString(el, "comment")
    };

    private static CommercialPriceGroupRow MapPriceGroup(JsonElement el) => new()
    {
        Id = ReadLong(el, "id") ?? 0,
        Name = ReadString(el, "name") ?? string.Empty,
        Description = ReadString(el, "description"),
        Currency = ReadString(el, "currency") ?? "RUB",
        VatMode = ReadString(el, "vat_mode") ?? "INCLUDED",
        IsDefault = ReadBool(el, "is_default"),
        IsSystem = ReadBool(el, "is_system"),
        IsActive = ReadBool(el, "is_active"),
        DefaultDiscountPercent = ReadDecimal(el, "default_discount_percent") ?? 0m,
        DefaultMarkupPercent = ReadDecimal(el, "default_markup_percent") ?? 0m
    };

    private static CommercialOfferRow MapOfferRow(JsonElement el) => new()
    {
        Id = ReadLong(el, "id") ?? 0,
        OfferRef = ReadString(el, "offer_ref") ?? string.Empty,
        PartnerId = ReadLong(el, "partner_id") ?? 0,
        PartnerName = ReadString(el, "partner_name") ?? string.Empty,
        PriceGroupId = ReadLong(el, "price_group_id") ?? 0,
        Status = ReadString(el, "status") ?? "DRAFT",
        StatusDisplay = ReadString(el, "status_display") ?? string.Empty,
        Total = ReadDecimal(el, "total") ?? 0m,
        ValidUntil = ReadString(el, "valid_until"),
        ManagerName = ReadString(el, "manager_name"),
        CreatedAt = ReadDateTime(el, "created_at")
    };

    private static CommercialOfferDetails MapOfferDetails(JsonElement root)
    {
        var offerEl = root.TryGetProperty("offer", out var nested) ? nested : root;
        var lines = root.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array
            ? linesEl.EnumerateArray().Select(MapOfferLine).ToList()
            : new List<CommercialOfferLineRow>();

        return new CommercialOfferDetails
        {
            Offer = MapOfferRow(offerEl),
            Lines = lines
        };
    }

    private static CommercialOfferLineRow MapOfferLine(JsonElement el) => new()
    {
        Id = ReadLong(el, "id") ?? 0,
        LineNo = (int)(ReadLong(el, "line_no") ?? 0),
        ItemId = ReadLong(el, "item_id") ?? 0,
        ItemName = ReadString(el, "item_name") ?? string.Empty,
        Qty = ReadDouble(el, "qty") ?? 0d,
        BasePrice = ReadDecimal(el, "base_price") ?? 0m,
        FinalDiscountPercent = ReadDecimal(el, "final_discount_percent") ?? 0m,
        FinalPrice = ReadDecimal(el, "final_price") ?? 0m,
        LineTotal = ReadDecimal(el, "line_total") ?? 0m
    };

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? ReadLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : null;

    private static decimal? ReadDecimal(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var dec))
        {
            return dec;
        }

        return decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double? ReadDouble(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d))
        {
            return d;
        }

        return double.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool ReadBool(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false
        };
    }

    private static DateTime? ReadDateTime(JsonElement el, string name)
    {
        var text = ReadString(el, name);
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt : null;
    }
}

public sealed class CommercialItemPriceRow
{
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string? Barcode { get; init; }
    public string? Gtin { get; init; }
    public string? ItemTypeName { get; init; }
    public long? ItemPriceId { get; init; }
    public long PriceGroupId { get; init; }
    public decimal? Price { get; init; }
    public decimal? BasePrice { get; init; }
    public decimal? GroupOverridePrice { get; init; }
    public decimal? CalculatedPrice { get; init; }
    public decimal? GroupDiscountPercent { get; init; }
    public decimal? GroupMarkupPercent { get; init; }
    public string? PriceSource { get; init; }
    public string? PriceMissingReason { get; init; }
    public string? Currency { get; init; }
    public string? ValidFrom { get; init; }
    public string? ValidTo { get; init; }
    public bool IsActive { get; init; }
    public string? Comment { get; init; }
    public bool HasPrice { get; init; }
    public bool HasBasePrice { get; init; }

    public string SkuDisplay => !string.IsNullOrWhiteSpace(Barcode)
        ? Barcode!
        : !string.IsNullOrWhiteSpace(Gtin)
            ? Gtin!
            : string.Empty;

    public string PriceDisplay => Price.HasValue ? Price.Value.ToString("0.00", CultureInfo.InvariantCulture) : "—";

    public string BasePriceDisplay => BasePrice.HasValue ? BasePrice.Value.ToString("0.00", CultureInfo.InvariantCulture) : "—";

    public string PriceSourceDisplay => PriceSource switch
    {
        "BASE" => "Базовая",
        "GROUP_RULE" => "По правилу",
        "GROUP_OVERRIDE" => "Индивидуальная",
        _ => PriceMissingReason ?? "—"
    };
}

public sealed class CommercialItemPricingOverviewRow
{
    public long PriceGroupId { get; init; }
    public string PriceGroupName { get; init; } = string.Empty;
    public bool IsSystem { get; init; }
    public decimal DefaultDiscountPercent { get; init; }
    public decimal DefaultMarkupPercent { get; init; }
    public decimal? BasePrice { get; init; }
    public decimal? OverridePrice { get; init; }
    public decimal? CalculatedPrice { get; init; }
    public string PriceSource { get; init; } = string.Empty;
    public string? Currency { get; init; }
    public long? ItemPriceId { get; init; }
    public string? ValidFrom { get; init; }
    public string? ValidTo { get; init; }
    public string? Comment { get; init; }
}

public sealed class CommercialPriceGroupRow
{
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Currency { get; init; } = "RUB";
    public string VatMode { get; init; } = "INCLUDED";
    public bool IsDefault { get; init; }
    public bool IsSystem { get; init; }
    public bool IsActive { get; init; }
    public decimal DefaultDiscountPercent { get; init; }
    public decimal DefaultMarkupPercent { get; init; }

    public string SystemBadge => IsSystem ? " (системная)" : string.Empty;
}

public sealed class CommercialOfferRow
{
    public long Id { get; init; }
    public string OfferRef { get; init; } = string.Empty;
    public long PartnerId { get; init; }
    public string PartnerName { get; init; } = string.Empty;
    public long PriceGroupId { get; init; }
    public string Status { get; init; } = "DRAFT";
    public string StatusDisplay { get; init; } = string.Empty;
    public decimal Total { get; init; }
    public string? ValidUntil { get; init; }
    public string? ManagerName { get; init; }
    public DateTime? CreatedAt { get; init; }
}

public sealed class CommercialOfferDetails
{
    public CommercialOfferRow Offer { get; init; } = new();
    public IReadOnlyList<CommercialOfferLineRow> Lines { get; init; } = Array.Empty<CommercialOfferLineRow>();
}

public sealed class CommercialOfferCreateRequest
{
    public long PartnerId { get; init; }
    public long? PriceGroupId { get; init; }
    public string? ValidUntil { get; init; }
    public string? ContactPerson { get; init; }
    public string? ContactPhone { get; init; }
    public string? ContactEmail { get; init; }
    public string? PaymentTerms { get; init; }
    public string? DeliveryTerms { get; init; }
    public string? Comment { get; init; }
    public string? ManagerName { get; init; }
}

public sealed class CommercialOfferLineRow
{
    public long Id { get; init; }
    public int LineNo { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public double Qty { get; init; }
    public decimal BasePrice { get; init; }
    public decimal FinalDiscountPercent { get; init; }
    public decimal FinalPrice { get; init; }
    public decimal LineTotal { get; init; }
}

public sealed class CommercialPartnerSettingsRow
{
    public long PartnerId { get; init; }
    public long? PriceGroupId { get; init; }
    public decimal DefaultDiscountPercent { get; init; }
    public string? PaymentTerms { get; init; }
    public string? DeliveryTerms { get; init; }
}

public sealed class CommercialTemplateFieldGroupRow
{
    public string Title { get; init; } = string.Empty;
    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();
}
