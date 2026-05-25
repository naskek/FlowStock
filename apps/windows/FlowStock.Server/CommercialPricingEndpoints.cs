using System.Globalization;

using System.Text.Json.Serialization;

using FlowStock.Core.Abstractions;

using FlowStock.Core.Commercial;

using Microsoft.AspNetCore.Builder;

using Microsoft.AspNetCore.Http;



namespace FlowStock.Server;



public static class CommercialPricingEndpoints

{

    public static void Map(WebApplication app)

    {

        app.MapGet("/api/price-groups", (ICommercialDataStore store, bool include_inactive = false) =>

        {

            var groups = store.GetPriceGroups(include_inactive);

            return Results.Ok(groups.Select(MapPriceGroup));

        });



        app.MapPost("/api/price-groups", async (HttpRequest request, ICommercialDataStore store) =>

        {

            var body = await ReadBody<UpsertPriceGroupRequest>(request);

            if (body == null || string.IsNullOrWhiteSpace(body.Name))

            {

                return Results.BadRequest(new ApiResult(false, "INVALID_NAME"));

            }



            if (body.IsDefault == true || body.IsSystem == true)

            {

                return Results.BadRequest(new ApiResult(false, "SYSTEM_GROUP_PROTECTED"));

            }



            var now = DateTime.UtcNow;

            var id = store.AddPriceGroup(new PriceGroup

            {

                Name = body.Name.Trim(),

                Description = body.Description,

                Currency = string.IsNullOrWhiteSpace(body.Currency) ? "RUB" : body.Currency.Trim(),

                VatMode = VatModeMapper.FromCode(body.VatMode),

                IsDefault = false,

                IsSystem = false,

                IsActive = body.IsActive != false,

                DefaultDiscountPercent = body.DefaultDiscountPercent ?? 0m,

                DefaultMarkupPercent = body.DefaultMarkupPercent ?? 0m,

                CreatedAt = now,

                UpdatedAt = now

            });

            return Results.Ok(new { ok = true, price_group_id = id });

        });



        app.MapPost("/api/price-groups/{id:long}", async (long id, HttpRequest request, ICommercialDataStore store) =>

        {

            var existing = store.GetPriceGroup(id);

            if (existing == null)

            {

                return Results.NotFound(new ApiResult(false, "PRICE_GROUP_NOT_FOUND"));

            }



            var body = await ReadBody<UpsertPriceGroupRequest>(request);

            if (body == null || string.IsNullOrWhiteSpace(body.Name))

            {

                return Results.BadRequest(new ApiResult(false, "INVALID_NAME"));

            }



            if (existing.IsSystem)

            {

                if (body.IsActive == false || body.IsDefault == false)

                {

                    return Results.BadRequest(new ApiResult(false, "SYSTEM_GROUP_PROTECTED"));

                }



                if (!string.Equals(body.Name.Trim(), CommercialPricingConstants.BasePriceGroupName, StringComparison.Ordinal))

                {

                    return Results.BadRequest(new ApiResult(false, "SYSTEM_GROUP_NAME_PROTECTED"));

                }

            }

            else if (body.IsDefault == true)

            {

                return Results.BadRequest(new ApiResult(false, "ONLY_SYSTEM_GROUP_CAN_BE_DEFAULT"));

            }



            store.UpdatePriceGroup(new PriceGroup

            {

                Id = id,

                Name = existing.IsSystem ? CommercialPricingConstants.BasePriceGroupName : body.Name.Trim(),

                Description = body.Description,

                Currency = string.IsNullOrWhiteSpace(body.Currency) ? existing.Currency : body.Currency.Trim(),

                VatMode = string.IsNullOrWhiteSpace(body.VatMode) ? existing.VatMode : VatModeMapper.FromCode(body.VatMode),

                IsDefault = existing.IsSystem || body.IsDefault == true,

                IsSystem = existing.IsSystem,

                IsActive = existing.IsSystem ? true : body.IsActive ?? existing.IsActive,

                DefaultDiscountPercent = existing.IsSystem ? 0m : body.DefaultDiscountPercent ?? existing.DefaultDiscountPercent,

                DefaultMarkupPercent = existing.IsSystem ? 0m : body.DefaultMarkupPercent ?? existing.DefaultMarkupPercent,

                CreatedAt = existing.CreatedAt,

                UpdatedAt = DateTime.UtcNow

            });

            return Results.Ok(new ApiResult(true));

        });



        app.MapPost("/api/price-groups/{id:long}/deactivate", (long id, ICommercialDataStore store) =>

        {

            var existing = store.GetPriceGroup(id);

            if (existing == null)

            {

                return Results.NotFound(new ApiResult(false, "PRICE_GROUP_NOT_FOUND"));

            }



            if (existing.IsSystem)

            {

                return Results.BadRequest(new ApiResult(false, "SYSTEM_GROUP_PROTECTED"));

            }



            store.UpdatePriceGroup(new PriceGroup

            {

                Id = existing.Id,

                Name = existing.Name,

                Description = existing.Description,

                Currency = existing.Currency,

                VatMode = existing.VatMode,

                IsDefault = false,

                IsSystem = false,

                IsActive = false,

                DefaultDiscountPercent = existing.DefaultDiscountPercent,

                DefaultMarkupPercent = existing.DefaultMarkupPercent,

                CreatedAt = existing.CreatedAt,

                UpdatedAt = DateTime.UtcNow

            });

            return Results.Ok(new ApiResult(true));

        });



        app.MapGet("/api/price-groups/{id:long}/item-prices", (long id, CommercialPricingService pricing, ICommercialDataStore store, string? search, long? item_type_id, bool? has_price) =>

        {

            if (store.GetPriceGroup(id) == null)

            {

                return Results.NotFound(new ApiResult(false, "PRICE_GROUP_NOT_FOUND"));

            }



            var rows = pricing.GetItemPriceCatalogForGroup(id, search, item_type_id, has_price);

            return Results.Ok(rows.Select(MapItemPriceCatalog));

        });



        app.MapGet("/api/items/{itemId:long}/prices", (long itemId, CommercialPricingService pricing, IDataStore data, long? price_group_id, string? as_of) =>

        {

            if (data.FindItemById(itemId) == null)

            {

                return Results.NotFound(new ApiResult(false, "ITEM_NOT_FOUND"));

            }



            DateOnly? asOfDate = null;

            if (!string.IsNullOrWhiteSpace(as_of) && DateOnly.TryParseExact(as_of, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))

            {

                asOfDate = parsed;

            }



            if (price_group_id.HasValue)

            {

                var overview = pricing.GetItemPricingOverview(itemId, asOfDate)

                    .FirstOrDefault(row => row.PriceGroupId == price_group_id.Value);

                if (overview == null)

                {

                    return Results.Ok(Array.Empty<object>());

                }



                return Results.Ok(new[] { MapItemPricingOverview(overview) });

            }



            return Results.Ok(pricing.GetItemPricingOverview(itemId, asOfDate).Select(MapItemPricingOverview));

        });



        app.MapPost("/api/items/{itemId:long}/prices", async (long itemId, HttpRequest request, CommercialPricingService pricing) =>

        {

            var body = await ReadBody<UpsertItemPriceRequest>(request);

            if (body?.PriceGroupId is not > 0 || body.Price is null)

            {

                return Results.BadRequest(new ApiResult(false, "INVALID_BODY"));

            }



            if (!TryParseDate(body.ValidFrom, out var validFrom))

            {

                return Results.BadRequest(new ApiResult(false, "INVALID_VALID_FROM"));

            }



            DateOnly? validTo = null;

            if (!string.IsNullOrWhiteSpace(body.ValidTo))

            {

                if (!TryParseDate(body.ValidTo, out var parsedValidTo))

                {

                    return Results.BadRequest(new ApiResult(false, "INVALID_VALID_TO"));

                }



                validTo = parsedValidTo;

            }



            var result = pricing.UpsertItemPrice(new UpsertItemPriceCommand

            {

                ItemId = itemId,

                PriceGroupId = body.PriceGroupId.Value,

                Price = body.Price.Value,

                Currency = string.IsNullOrWhiteSpace(body.Currency) ? "RUB" : body.Currency.Trim(),

                VatRate = body.VatRate,

                VatIncluded = body.VatIncluded,

                UomCode = body.UomCode,

                ValidFrom = validFrom,

                ValidTo = validTo,

                IsActive = body.IsActive != false,

                Comment = body.Comment

            });



            if (!result.IsSuccess)

            {

                return Results.BadRequest(new ApiResult(false, result.ErrorCode ?? "UPSERT_FAILED"));

            }



            return Results.Ok(new { ok = true, item_price_id = result.ItemPriceId });

        });



        app.MapPost("/api/item-prices/{id:long}/deactivate", (long id, ICommercialDataStore store) =>

        {

            var existing = store.GetItemPrice(id);

            if (existing == null)

            {

                return Results.NotFound(new ApiResult(false, "ITEM_PRICE_NOT_FOUND"));

            }



            store.DeactivateItemPrice(id);

            return Results.Ok(new ApiResult(true));

        });



        app.MapGet("/api/partners/{partnerId:long}/commercial-settings", (long partnerId, ICommercialDataStore store) =>

        {

            var settings = store.GetPartnerCommercialSettings(partnerId);

            return settings == null

                ? Results.Ok(new { partner_id = partnerId })

                : Results.Ok(MapPartnerCommercialSettings(settings));

        });



        app.MapPost("/api/partners/{partnerId:long}/commercial-settings", async (long partnerId, HttpRequest request, ICommercialDataStore store, IDataStore data) =>

        {

            if (data.GetPartner(partnerId) == null)

            {

                return Results.BadRequest(new ApiResult(false, "PARTNER_NOT_FOUND"));

            }



            var body = await ReadBody<UpsertPartnerCommercialSettingsRequest>(request);

            if (body == null)

            {

                return Results.BadRequest(new ApiResult(false, "INVALID_BODY"));

            }



            DateOnly? validFrom = null;

            DateOnly? validTo = null;

            if (!string.IsNullOrWhiteSpace(body.ValidFrom))

            {

                if (!TryParseDate(body.ValidFrom, out var parsedValidFrom))

                {

                    return Results.BadRequest(new ApiResult(false, "INVALID_VALID_FROM"));

                }



                validFrom = parsedValidFrom;

            }



            if (!string.IsNullOrWhiteSpace(body.ValidTo))

            {

                if (!TryParseDate(body.ValidTo, out var parsedValidTo))

                {

                    return Results.BadRequest(new ApiResult(false, "INVALID_VALID_TO"));

                }



                validTo = parsedValidTo;

            }



            store.UpsertPartnerCommercialSettings(new PartnerCommercialSettings

            {

                PartnerId = partnerId,

                PriceGroupId = body.PriceGroupId,

                DefaultDiscountPercent = body.DefaultDiscountPercent ?? 0m,

                PaymentTerms = body.PaymentTerms,

                DeliveryTerms = body.DeliveryTerms,

                ValidFrom = validFrom,

                ValidTo = validTo,

                UpdatedAt = DateTime.UtcNow

            });

            return Results.Ok(new ApiResult(true));

        });



        app.MapPost("/api/commercial/pricing/quote", async (HttpRequest request, CommercialPricingService pricing) =>

        {

            var body = await ReadBody<PricingQuoteApiRequest>(request);

            if (body?.ItemId is not > 0 || body.PartnerId is not > 0 || body.Qty is not > 0)

            {

                return Results.BadRequest(new ApiResult(false, "INVALID_BODY"));

            }



            var asOf = DateOnly.FromDateTime(DateTime.Today);

            if (!string.IsNullOrWhiteSpace(body.AsOf) && DateOnly.TryParseExact(body.AsOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))

            {

                asOf = parsed;

            }



            var result = pricing.Quote(new PricingQuoteRequest

            {

                ItemId = body.ItemId.Value,

                PartnerId = body.PartnerId.Value,

                Qty = body.Qty.Value,

                AsOfDate = asOf,

                ManualDiscountPercent = body.ManualDiscountPercent ?? 0m,

                PriceGroupOverrideId = body.PriceGroupId

            });



            if (!result.IsSuccess)

            {

                return Results.BadRequest(new ApiResult(false, result.ErrorCode ?? "QUOTE_FAILED"));

            }



            return Results.Ok(new

            {

                ok = true,

                price_group_id = result.PriceGroupId,

                catalog_base_price = result.CatalogBasePrice,

                group_price = result.GroupPrice,

                base_price = result.BasePrice,

                price_source = result.PriceSource,

                volume_discount_percent = result.VolumeDiscountPercent,

                partner_discount_percent = result.PartnerDiscountPercent,

                manual_discount_percent = result.ManualDiscountPercent,

                final_discount_percent = result.FinalDiscountPercent,

                final_price = result.FinalPrice,

                line_total = result.LineTotal,

                currency = result.Currency

            });

        });



        app.MapGet("/api/commercial/volume-discount-rules", (ICommercialDataStore store, bool include_inactive = false) =>

        {

            return Results.Ok(store.GetVolumeDiscountRules(include_inactive).Select(MapVolumeDiscountRule));

        });



        app.MapPost("/api/commercial/volume-discount-rules", async (HttpRequest request, ICommercialDataStore store) =>

        {

            var body = await ReadBody<UpsertVolumeDiscountRuleRequest>(request);

            if (body?.MinQty is not > 0 || body.DiscountPercent is null || string.IsNullOrWhiteSpace(body.ScopeType))

            {

                return Results.BadRequest(new ApiResult(false, "INVALID_BODY"));

            }



            var scope = VolumeDiscountScopeMapper.FromCode(body.ScopeType);

            if (!scope.HasValue)

            {

                return Results.BadRequest(new ApiResult(false, "INVALID_SCOPE_TYPE"));

            }



            DateOnly? validFrom = null;

            DateOnly? validTo = null;

            if (!string.IsNullOrWhiteSpace(body.ValidFrom))

            {

                if (!TryParseDate(body.ValidFrom, out var parsedValidFrom))

                {

                    return Results.BadRequest(new ApiResult(false, "INVALID_VALID_FROM"));

                }



                validFrom = parsedValidFrom;

            }



            if (!string.IsNullOrWhiteSpace(body.ValidTo))

            {

                if (!TryParseDate(body.ValidTo, out var parsedValidTo))

                {

                    return Results.BadRequest(new ApiResult(false, "INVALID_VALID_TO"));

                }



                validTo = parsedValidTo;

            }



            var id = store.AddVolumeDiscountRule(new VolumeDiscountRule

            {

                ScopeType = scope.Value,

                PriceGroupId = body.PriceGroupId,

                PartnerId = body.PartnerId,

                ItemTypeId = body.ItemTypeId,

                ItemId = body.ItemId,

                MinQty = body.MinQty.Value,

                DiscountPercent = body.DiscountPercent.Value,

                ValidFrom = validFrom,

                ValidTo = validTo,

                IsActive = body.IsActive != false,

                Comment = body.Comment

            });

            return Results.Ok(new { ok = true, rule_id = id });

        });

    }



    private static bool TryParseDate(string? value, out DateOnly date)

    {

        date = default;

        return !string.IsNullOrWhiteSpace(value)

               && DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    }



    private static async Task<T?> ReadBody<T>(HttpRequest request)

    {

        try

        {

            return await request.ReadFromJsonAsync<T>();

        }

        catch

        {

            return default;

        }

    }



    private static object MapPriceGroup(PriceGroup group) => new

    {

        id = group.Id,

        name = group.Name,

        description = group.Description,

        currency = group.Currency,

        vat_mode = VatModeMapper.ToCode(group.VatMode),

        is_default = group.IsDefault,

        is_system = group.IsSystem,

        is_active = group.IsActive,

        default_discount_percent = group.DefaultDiscountPercent,

        default_markup_percent = group.DefaultMarkupPercent,

        created_at = group.CreatedAt,

        updated_at = group.UpdatedAt

    };



    private static object MapItemPriceCatalog(ItemPriceCatalogRow row) => new

    {

        item_id = row.ItemId,

        item_name = row.ItemName,

        barcode = row.Barcode,

        gtin = row.Gtin,

        item_type = row.ItemTypeName,

        item_price_id = row.ItemPriceId,

        price_group_id = row.PriceGroupId,

        price = row.Price,

        base_price = row.BasePrice,

        group_override_price = row.GroupOverridePrice,

        calculated_price = row.CalculatedPrice,

        group_discount_percent = row.GroupDiscountPercent,

        group_markup_percent = row.GroupMarkupPercent,

        price_source = row.PriceSource,

        price_missing_reason = row.PriceMissingReason,

        currency = row.Currency,

        valid_from = row.ValidFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),

        valid_to = row.ValidTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),

        is_active = row.IsActive,

        comment = row.Comment,

        has_price = row.HasPrice,

        has_base_price = row.HasBasePrice

    };



    private static object MapItemPricingOverview(ItemPricingOverviewRow row) => new

    {

        price_group_id = row.PriceGroupId,

        price_group_name = row.PriceGroupName,

        is_system = row.IsSystem,

        default_discount_percent = row.DefaultDiscountPercent,

        default_markup_percent = row.DefaultMarkupPercent,

        base_price = row.BasePrice,

        override_price = row.OverridePrice,

        calculated_price = row.CalculatedPrice,

        price_source = row.PriceSource,

        currency = row.Currency,

        item_price_id = row.ItemPriceId,

        valid_from = row.ValidFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),

        valid_to = row.ValidTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),

        is_active = row.IsActive,

        comment = row.Comment

    };



    private static object MapItemPrice(ItemPrice price) => new

    {

        id = price.Id,

        item_id = price.ItemId,

        price_group_id = price.PriceGroupId,

        price = price.Price,

        currency = price.Currency,

        vat_rate = price.VatRate,

        vat_included = price.VatIncluded,

        uom_code = price.UomCode,

        valid_from = price.ValidFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),

        valid_to = price.ValidTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),

        is_active = price.IsActive,

        comment = price.Comment,

        created_at = price.CreatedAt

    };



    private static object MapPartnerCommercialSettings(PartnerCommercialSettings settings) => new

    {

        partner_id = settings.PartnerId,

        price_group_id = settings.PriceGroupId,

        default_discount_percent = settings.DefaultDiscountPercent,

        payment_terms = settings.PaymentTerms,

        delivery_terms = settings.DeliveryTerms,

        valid_from = settings.ValidFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),

        valid_to = settings.ValidTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),

        updated_at = settings.UpdatedAt

    };



    private static object MapVolumeDiscountRule(VolumeDiscountRule rule) => new

    {

        id = rule.Id,

        scope_type = VolumeDiscountScopeMapper.ToCode(rule.ScopeType),

        price_group_id = rule.PriceGroupId,

        partner_id = rule.PartnerId,

        item_type_id = rule.ItemTypeId,

        item_id = rule.ItemId,

        min_qty = rule.MinQty,

        discount_percent = rule.DiscountPercent,

        valid_from = rule.ValidFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),

        valid_to = rule.ValidTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),

        is_active = rule.IsActive,

        comment = rule.Comment

    };



    private sealed class UpsertPriceGroupRequest

    {

        [JsonPropertyName("name")] public string? Name { get; init; }

        [JsonPropertyName("description")] public string? Description { get; init; }

        [JsonPropertyName("currency")] public string? Currency { get; init; }

        [JsonPropertyName("vat_mode")] public string? VatMode { get; init; }

        [JsonPropertyName("is_default")] public bool? IsDefault { get; init; }

        [JsonPropertyName("is_system")] public bool? IsSystem { get; init; }

        [JsonPropertyName("is_active")] public bool? IsActive { get; init; }

        [JsonPropertyName("default_discount_percent")] public decimal? DefaultDiscountPercent { get; init; }

        [JsonPropertyName("default_markup_percent")] public decimal? DefaultMarkupPercent { get; init; }

    }



    private sealed class UpsertItemPriceRequest

    {

        [JsonPropertyName("price_group_id")] public long? PriceGroupId { get; init; }

        [JsonPropertyName("price")] public decimal? Price { get; init; }

        [JsonPropertyName("currency")] public string? Currency { get; init; }

        [JsonPropertyName("vat_rate")] public decimal? VatRate { get; init; }

        [JsonPropertyName("vat_included")] public bool? VatIncluded { get; init; }

        [JsonPropertyName("uom_code")] public string? UomCode { get; init; }

        [JsonPropertyName("valid_from")] public string? ValidFrom { get; init; }

        [JsonPropertyName("valid_to")] public string? ValidTo { get; init; }

        [JsonPropertyName("is_active")] public bool? IsActive { get; init; }

        [JsonPropertyName("comment")] public string? Comment { get; init; }

    }



    private sealed class UpsertPartnerCommercialSettingsRequest

    {

        [JsonPropertyName("price_group_id")] public long? PriceGroupId { get; init; }

        [JsonPropertyName("default_discount_percent")] public decimal? DefaultDiscountPercent { get; init; }

        [JsonPropertyName("payment_terms")] public string? PaymentTerms { get; init; }

        [JsonPropertyName("delivery_terms")] public string? DeliveryTerms { get; init; }

        [JsonPropertyName("valid_from")] public string? ValidFrom { get; init; }

        [JsonPropertyName("valid_to")] public string? ValidTo { get; init; }

    }



    private sealed class PricingQuoteApiRequest

    {

        [JsonPropertyName("item_id")] public long? ItemId { get; init; }

        [JsonPropertyName("partner_id")] public long? PartnerId { get; init; }

        [JsonPropertyName("qty")] public double? Qty { get; init; }

        [JsonPropertyName("as_of")] public string? AsOf { get; init; }

        [JsonPropertyName("manual_discount_percent")] public decimal? ManualDiscountPercent { get; init; }

        [JsonPropertyName("price_group_id")] public long? PriceGroupId { get; init; }

    }



    private sealed class UpsertVolumeDiscountRuleRequest

    {

        [JsonPropertyName("scope_type")] public string? ScopeType { get; init; }

        [JsonPropertyName("price_group_id")] public long? PriceGroupId { get; init; }

        [JsonPropertyName("partner_id")] public long? PartnerId { get; init; }

        [JsonPropertyName("item_type_id")] public long? ItemTypeId { get; init; }

        [JsonPropertyName("item_id")] public long? ItemId { get; init; }

        [JsonPropertyName("min_qty")] public double? MinQty { get; init; }

        [JsonPropertyName("discount_percent")] public decimal? DiscountPercent { get; init; }

        [JsonPropertyName("valid_from")] public string? ValidFrom { get; init; }

        [JsonPropertyName("valid_to")] public string? ValidTo { get; init; }

        [JsonPropertyName("is_active")] public bool? IsActive { get; init; }

        [JsonPropertyName("comment")] public string? Comment { get; init; }

    }

}


