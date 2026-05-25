using FlowStock.Core.Abstractions;

namespace FlowStock.Core.Commercial;

public sealed class CommercialPricingService
{
    private readonly ICommercialDataStore _commercial;
    private readonly IDataStore _data;

    public CommercialPricingService(ICommercialDataStore commercial, IDataStore data)
    {
        _commercial = commercial;
        _data = data;
    }

    public PricingQuoteResult Quote(PricingQuoteRequest request)
    {
        if (request.ItemId <= 0)
        {
            return PricingQuoteResult.Failure("INVALID_ITEM_ID");
        }

        if (request.PartnerId <= 0)
        {
            return PricingQuoteResult.Failure("INVALID_PARTNER_ID");
        }

        if (request.Qty <= 0)
        {
            return PricingQuoteResult.Failure("INVALID_QTY");
        }

        var item = _data.FindItemById(request.ItemId);
        if (item == null)
        {
            return PricingQuoteResult.Failure("ITEM_NOT_FOUND");
        }

        var partner = _data.GetPartner(request.PartnerId);
        if (partner == null)
        {
            return PricingQuoteResult.Failure("PARTNER_NOT_FOUND");
        }

        var baseGroup = RequireSystemBaseGroup();
        var priceGroupId = ResolvePriceGroupId(request.PartnerId, request.AsOfDate, request.PriceGroupOverrideId);
        if (!priceGroupId.HasValue)
        {
            return PricingQuoteResult.Failure("PRICE_GROUP_NOT_FOUND");
        }

        var priceGroup = _commercial.GetPriceGroup(priceGroupId.Value);
        if (priceGroup == null || !priceGroup.IsActive)
        {
            return PricingQuoteResult.Failure("PRICE_GROUP_NOT_FOUND");
        }

        var baseItemPrice = _commercial.GetActiveItemPrice(request.ItemId, baseGroup.Id, request.AsOfDate);
        if (baseItemPrice == null || baseItemPrice.Price <= 0m)
        {
            return PricingQuoteResult.Failure("PRICE_NOT_FOUND");
        }

        var catalogBasePrice = baseItemPrice.Price;
        var groupOverride = priceGroup.IsSystem
            ? null
            : _commercial.GetActiveItemPrice(request.ItemId, priceGroupId.Value, request.AsOfDate);

        var (groupPrice, priceSource) = CalculateGroupPrice(catalogBasePrice, priceGroup, groupOverride);
        if (groupPrice <= 0m)
        {
            return PricingQuoteResult.Failure("PRICE_IS_ZERO");
        }

        var partnerDiscount = ResolvePartnerDiscount(request.PartnerId, request.AsOfDate);
        var manualDiscount = ClampPercent(request.ManualDiscountPercent);

        var priceAfterPartner = ApplyDiscount(groupPrice, partnerDiscount);
        var finalPrice = ApplyDiscount(priceAfterPartner, manualDiscount);

        if (finalPrice <= 0m)
        {
            return PricingQuoteResult.Failure("PRICE_IS_ZERO");
        }

        var finalDiscountPercent = CombineSequentialPercent(partnerDiscount, manualDiscount);
        var qty = (decimal)request.Qty;
        var lineTotal = RoundMoney(finalPrice * qty);
        var currency = groupOverride?.Currency ?? baseItemPrice.Currency;

        return new PricingQuoteResult
        {
            IsSuccess = true,
            PriceGroupId = priceGroupId,
            CatalogBasePrice = RoundUnitPrice(catalogBasePrice),
            GroupPrice = RoundUnitPrice(groupPrice),
            BasePrice = RoundUnitPrice(groupPrice),
            VolumeDiscountPercent = 0m,
            PartnerDiscountPercent = partnerDiscount,
            ManualDiscountPercent = manualDiscount,
            FinalDiscountPercent = finalDiscountPercent,
            FinalPrice = RoundUnitPrice(finalPrice),
            LineTotal = lineTotal,
            Currency = currency,
            PriceSource = PriceSourceKindMapper.ToCode(priceSource)
        };
    }

    public IReadOnlyList<ItemPriceCatalogRow> GetItemPriceCatalogForGroup(
        long priceGroupId,
        string? search,
        long? itemTypeId,
        bool? hasPrice,
        DateOnly? asOfDate = null)
    {
        var asOf = asOfDate ?? DateOnly.FromDateTime(DateTime.Today);
        var selectedGroup = _commercial.GetPriceGroup(priceGroupId);
        if (selectedGroup == null)
        {
            return Array.Empty<ItemPriceCatalogRow>();
        }

        var baseGroup = RequireSystemBaseGroup();
        var rawRows = _commercial.GetItemPriceCatalogForGroup(priceGroupId, baseGroup.Id, search, itemTypeId, null);
        var result = new List<ItemPriceCatalogRow>(rawRows.Count);
        foreach (var row in rawRows)
        {
            var enriched = EnrichCatalogRow(row, selectedGroup, baseGroup, asOf);
            if (hasPrice == true && !enriched.HasPrice)
            {
                continue;
            }

            if (hasPrice == false && enriched.HasPrice)
            {
                continue;
            }

            result.Add(enriched);
        }

        return result;
    }

    public IReadOnlyList<ItemPricingOverviewRow> GetItemPricingOverview(long itemId, DateOnly? asOfDate = null)
    {
        if (_data.FindItemById(itemId) == null)
        {
            return Array.Empty<ItemPricingOverviewRow>();
        }

        var asOf = asOfDate ?? DateOnly.FromDateTime(DateTime.Today);
        var baseGroup = RequireSystemBaseGroup();
        var baseItemPrice = _commercial.GetActiveItemPrice(itemId, baseGroup.Id, asOf);
        var catalogBasePrice = baseItemPrice is { Price: > 0m } ? baseItemPrice.Price : (decimal?)null;

        var groups = _commercial.GetPriceGroups(includeInactive: false)
            .OrderByDescending(g => g.IsSystem)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new List<ItemPricingOverviewRow>(groups.Count);
        foreach (var group in groups)
        {
            ItemPrice? overridePrice = group.IsSystem
                ? baseItemPrice
                : _commercial.GetActiveItemPrice(itemId, group.Id, asOf);

            var (calculatedPrice, source) = CalculateGroupPrice(catalogBasePrice, group, group.IsSystem ? null : overridePrice);
            rows.Add(new ItemPricingOverviewRow
            {
                PriceGroupId = group.Id,
                PriceGroupName = group.Name,
                IsSystem = group.IsSystem,
                DefaultDiscountPercent = group.DefaultDiscountPercent,
                DefaultMarkupPercent = group.DefaultMarkupPercent,
                BasePrice = catalogBasePrice,
                OverridePrice = group.IsSystem ? null : overridePrice?.Price,
                CalculatedPrice = calculatedPrice,
                PriceSource = PriceSourceKindMapper.ToCode(source),
                Currency = overridePrice?.Currency ?? baseItemPrice?.Currency ?? group.Currency,
                ItemPriceId = overridePrice?.Id ?? (group.IsSystem ? baseItemPrice?.Id : overridePrice?.Id),
                ValidFrom = overridePrice?.ValidFrom ?? baseItemPrice?.ValidFrom,
                ValidTo = overridePrice?.ValidTo ?? baseItemPrice?.ValidTo,
                IsActive = overridePrice?.IsActive ?? baseItemPrice?.IsActive,
                Comment = overridePrice?.Comment ?? baseItemPrice?.Comment
            });
        }

        return rows;
    }

    public (bool IsSuccess, long? ItemPriceId, string? ErrorCode) UpsertItemPrice(UpsertItemPriceCommand command)
    {
        if (command.ItemId <= 0 || command.PriceGroupId <= 0)
        {
            return (false, null, "INVALID_BODY");
        }

        if (command.Price < 0m)
        {
            return (false, null, "INVALID_PRICE");
        }

        if (command.Price == 0m)
        {
            return (false, null, "PRICE_IS_ZERO");
        }

        if (string.IsNullOrWhiteSpace(command.Currency))
        {
            return (false, null, "INVALID_CURRENCY");
        }

        if (command.ValidTo.HasValue && command.ValidTo.Value < command.ValidFrom)
        {
            return (false, null, "INVALID_VALID_TO");
        }

        if (_data.FindItemById(command.ItemId) == null)
        {
            return (false, null, "ITEM_NOT_FOUND");
        }

        var priceGroup = _commercial.GetPriceGroup(command.PriceGroupId);
        if (priceGroup == null)
        {
            return (false, null, "PRICE_GROUP_NOT_FOUND");
        }

        if (priceGroup.IsSystem && !priceGroup.IsDefault)
        {
            // still allow saving to system base group
        }

        if (!priceGroup.IsSystem)
        {
            var baseGroup = RequireSystemBaseGroup();
            var basePrice = _commercial.GetActiveItemPrice(command.ItemId, baseGroup.Id, command.ValidFrom);
            if (basePrice == null || basePrice.Price <= 0m)
            {
                return (false, null, "PRICE_NOT_FOUND");
            }
        }

        _commercial.CloseOverlappingActiveItemPrices(
            command.ItemId,
            command.PriceGroupId,
            command.ValidFrom,
            command.ValidTo);

        var id = _commercial.AddItemPrice(new ItemPrice
        {
            ItemId = command.ItemId,
            PriceGroupId = command.PriceGroupId,
            Price = command.Price,
            Currency = command.Currency.Trim(),
            VatRate = command.VatRate,
            VatIncluded = command.VatIncluded,
            UomCode = command.UomCode,
            ValidFrom = command.ValidFrom,
            ValidTo = command.ValidTo,
            IsActive = command.IsActive,
            Comment = command.Comment,
            CreatedAt = DateTime.UtcNow
        });

        return (true, id, null);
    }

    public long? ResolvePriceGroupId(long partnerId, DateOnly asOfDate, long? overrideId)
    {
        if (overrideId.HasValue && overrideId.Value > 0)
        {
            return overrideId.Value;
        }

        var settings = _commercial.GetPartnerCommercialSettings(partnerId);
        if (settings?.PriceGroupId is > 0 && IsPartnerSettingsActive(settings, asOfDate))
        {
            return settings.PriceGroupId;
        }

        return RequireSystemBaseGroup().Id;
    }

    internal static (decimal GroupPrice, PriceSourceKind Source) CalculateGroupPrice(
        decimal? catalogBasePrice,
        PriceGroup selectedGroup,
        ItemPrice? groupOverride)
    {
        if (catalogBasePrice is not > 0m)
        {
            return (0m, PriceSourceKind.Base);
        }

        if (selectedGroup.IsSystem)
        {
            return (catalogBasePrice.Value, PriceSourceKind.Base);
        }

        if (groupOverride is { Price: > 0m })
        {
            return (groupOverride.Price, PriceSourceKind.GroupOverride);
        }

        var afterDiscount = ApplyDiscount(catalogBasePrice.Value, selectedGroup.DefaultDiscountPercent);
        var afterMarkup = ApplyMarkup(afterDiscount, selectedGroup.DefaultMarkupPercent);
        return (afterMarkup, PriceSourceKind.GroupRule);
    }

    private ItemPriceCatalogRow EnrichCatalogRow(
        ItemPriceCatalogRow row,
        PriceGroup selectedGroup,
        PriceGroup baseGroup,
        DateOnly asOfDate)
    {
        var catalogBase = row.BasePrice is > 0m ? row.BasePrice : null;
        ItemPrice? overridePrice = null;
        if (!selectedGroup.IsSystem && row.GroupOverridePrice is > 0m && row.ItemPriceId is > 0)
        {
            overridePrice = new ItemPrice
            {
                Id = row.ItemPriceId.Value,
                ItemId = row.ItemId,
                PriceGroupId = selectedGroup.Id,
                Price = row.GroupOverridePrice.Value,
                Currency = row.Currency ?? selectedGroup.Currency,
                ValidFrom = row.ValidFrom ?? asOfDate,
                ValidTo = row.ValidTo,
                IsActive = row.IsActive ?? true,
                Comment = row.Comment
            };
        }

        var (calculatedPrice, source) = CalculateGroupPrice(catalogBase, selectedGroup, overridePrice);
        string? missingReason = null;
        if (catalogBase is not > 0m)
        {
            missingReason = "Нет базовой цены";
        }
        else if (calculatedPrice <= 0m)
        {
            missingReason = "Цена не рассчитана";
        }

        var isBaseGroup = selectedGroup.IsSystem;
        decimal? displayPrice = isBaseGroup ? catalogBase : calculatedPrice > 0m ? calculatedPrice : null;
        long? itemPriceId = isBaseGroup ? row.BaseItemPriceId : row.ItemPriceId;
        if (isBaseGroup && row.BaseItemPriceId is > 0)
        {
            displayPrice = catalogBase;
        }

        return new ItemPriceCatalogRow
        {
            ItemId = row.ItemId,
            ItemName = row.ItemName,
            Barcode = row.Barcode,
            Gtin = row.Gtin,
            ItemTypeName = row.ItemTypeName,
            ItemPriceId = itemPriceId,
            PriceGroupId = selectedGroup.Id,
            Price = displayPrice,
            BasePrice = catalogBase,
            GroupOverridePrice = selectedGroup.IsSystem ? null : row.GroupOverridePrice,
            CalculatedPrice = calculatedPrice > 0m ? calculatedPrice : null,
            GroupDiscountPercent = selectedGroup.DefaultDiscountPercent,
            GroupMarkupPercent = selectedGroup.DefaultMarkupPercent,
            PriceSource = PriceSourceKindMapper.ToCode(source),
            PriceMissingReason = missingReason,
            Currency = row.Currency ?? row.BaseCurrency ?? selectedGroup.Currency,
            ValidFrom = isBaseGroup ? row.BaseValidFrom ?? row.ValidFrom : row.ValidFrom,
            ValidTo = isBaseGroup ? row.BaseValidTo ?? row.ValidTo : row.ValidTo,
            IsActive = isBaseGroup ? row.BaseIsActive ?? row.IsActive : row.IsActive,
            Comment = isBaseGroup ? row.BaseComment ?? row.Comment : row.Comment
        };
    }

    private PriceGroup RequireSystemBaseGroup()
    {
        var group = _commercial.GetSystemBasePriceGroup() ?? _commercial.GetDefaultPriceGroup();
        if (group == null || !group.IsActive)
        {
            throw new InvalidOperationException("SYSTEM_BASE_PRICE_GROUP_NOT_FOUND");
        }

        return group;
    }

    private decimal ResolvePartnerDiscount(long partnerId, DateOnly asOfDate)
    {
        var settings = _commercial.GetPartnerCommercialSettings(partnerId);
        if (settings == null || !IsPartnerSettingsActive(settings, asOfDate))
        {
            return 0m;
        }

        return ClampPercent(settings.DefaultDiscountPercent);
    }

    private static bool IsPartnerSettingsActive(PartnerCommercialSettings settings, DateOnly asOfDate)
    {
        if (settings.ValidFrom.HasValue && asOfDate < settings.ValidFrom.Value)
        {
            return false;
        }

        if (settings.ValidTo.HasValue && asOfDate > settings.ValidTo.Value)
        {
            return false;
        }

        return true;
    }

    internal static decimal ApplyDiscount(decimal price, decimal discountPercent)
    {
        if (discountPercent <= 0m)
        {
            return price;
        }

        var factor = 1m - ClampPercent(discountPercent) / 100m;
        return RoundUnitPrice(price * factor);
    }

    internal static decimal ApplyMarkup(decimal price, decimal markupPercent)
    {
        if (markupPercent <= 0m)
        {
            return price;
        }

        var factor = 1m + ClampPercent(markupPercent) / 100m;
        return RoundUnitPrice(price * factor);
    }

    public static decimal CombineSequentialPercent(params decimal[] discounts)
    {
        decimal remaining = 100m;
        decimal combined = 0m;
        foreach (var discount in discounts)
        {
            var applied = Math.Min(ClampPercent(discount), remaining);
            combined += applied;
            remaining -= applied;
            if (remaining <= 0m)
            {
                break;
            }
        }

        return Math.Min(combined, 100m);
    }

    internal static decimal ClampPercent(decimal value) => value < 0m ? 0m : value > 100m ? 100m : value;

    internal static decimal RoundUnitPrice(decimal value) =>
        Math.Round(value, 4, MidpointRounding.AwayFromZero);

    public static decimal RoundMoney(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
