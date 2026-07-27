using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class CommercialTermsResolver
{
    public const string ItemSaleVatRateRequired = "ITEM_SALE_VAT_RATE_REQUIRED";
    public const string VatRateInactive = "VAT_RATE_INACTIVE_FOR_NEW_ORDER_LINE";
    public const string VatRateReferenceInvalid = "ITEM_SALE_VAT_RATE_REFERENCE_INVALID";
    public const string ItemSalePriceRequired = "ITEM_SALE_PRICE_REQUIRED";
    public const string InvalidUnitPrice = "INVALID_UNIT_PRICE_GROSS";
    public const string UnitPriceRequired = "UNIT_PRICE_GROSS_REQUIRED";
    public const string UnitPriceIntentRequired = "UNIT_PRICE_OVERRIDE_INTENT_REQUIRED";
    public const string CommercialTermsNotAllowedForInternal = "COMMERCIAL_TERMS_NOT_ALLOWED_FOR_INTERNAL";
    public const string OrderLinePriceLockedByShipment = "ORDER_LINE_PRICE_LOCKED_BY_SHIPMENT";

    private readonly IDataStore _data;

    public CommercialTermsResolver(IDataStore data)
    {
        _data = data;
    }

    public CommercialTermsResolution ResolveForNewCustomerLine(
        long partnerId,
        long itemId,
        bool changeUnitPriceGross,
        decimal? requestedUnitPriceGross)
    {
        if (_data.GetPartner(partnerId) == null)
        {
            throw new CommercialTermsException("PARTNER_NOT_FOUND", "Контрагент не найден.");
        }

        var item = _data.FindItemById(itemId)
            ?? throw new CommercialTermsException("ITEM_NOT_FOUND", "Товар не найден.");
        var vatRate = ResolveVatRate(item);

        if (changeUnitPriceGross)
        {
            var manualPrice = ValidateManualPrice(requestedUnitPriceGross);
            return new CommercialTermsResolution(manualPrice, vatRate, SalePriceSource.Manual);
        }

        if (requestedUnitPriceGross.HasValue)
        {
            throw new CommercialTermsException(
                UnitPriceIntentRequired,
                "Для ручной цены необходимо явно указать намерение изменить цену.");
        }

        var partnerPrice = _data.GetActivePartnerItemSalePrice(partnerId, itemId);
        if (partnerPrice != null)
        {
            return new CommercialTermsResolution(
                partnerPrice.UnitPriceGross,
                vatRate,
                SalePriceSource.PartnerItem);
        }

        if (item.DefaultSalePriceGross.HasValue)
        {
            return new CommercialTermsResolution(
                item.DefaultSalePriceGross.Value,
                vatRate,
                SalePriceSource.ItemDefault);
        }

        throw new CommercialTermsException(
            ItemSalePriceRequired,
            "Для товара не задана цена продажи. Укажите цену клиента, базовую цену товара или ручную цену строки.");
    }

    public CommercialTermsPreview Preview(long partnerId, long itemId)
    {
        if (_data.GetPartner(partnerId) == null)
        {
            throw new CommercialTermsException("PARTNER_NOT_FOUND", "Контрагент не найден.");
        }

        var item = _data.FindItemById(itemId)
            ?? throw new CommercialTermsException("ITEM_NOT_FOUND", "Товар не найден.");
        var partnerPrice = _data.GetActivePartnerItemSalePrice(partnerId, itemId);
        var automaticPrice = partnerPrice?.UnitPriceGross ?? item.DefaultSalePriceGross;
        var priceSource = partnerPrice != null
            ? SalePriceSource.PartnerItem
            : item.DefaultSalePriceGross.HasValue
                ? SalePriceSource.ItemDefault
                : SalePriceSource.None;

        if (!item.DefaultSaleVatRateId.HasValue)
        {
            return new CommercialTermsPreview(
                partnerId,
                itemId,
                automaticPrice,
                priceSource,
                null,
                ItemSaleVatRateRequired,
                "Для товара не выбрана ставка НДС продажи. Укажите ставку в карточке товара.");
        }

        if (!item.DefaultSaleVatRate.HasValue)
        {
            return new CommercialTermsPreview(
                partnerId,
                itemId,
                automaticPrice,
                priceSource,
                null,
                VatRateReferenceInvalid,
                "Ссылка товара на ставку НДС повреждена.");
        }

        if (item.DefaultSaleVatRateIsActive != true)
        {
            return new CommercialTermsPreview(
                partnerId,
                itemId,
                automaticPrice,
                priceSource,
                item.DefaultSaleVatRate,
                VatRateInactive,
                "Для товара выбрана неактивная ставка НДС продажи. Укажите активную ставку в карточке товара.");
        }

        return new CommercialTermsPreview(
            partnerId,
            itemId,
            automaticPrice,
            priceSource,
            item.DefaultSaleVatRate,
            automaticPrice.HasValue ? null : ItemSalePriceRequired,
            automaticPrice.HasValue
                ? null
                : "Для товара не задана автоматическая цена продажи. Можно явно указать ручную цену строки.");
    }

    public static decimal ValidateManualPrice(decimal? price)
    {
        if (!price.HasValue)
        {
            throw new CommercialTermsException(UnitPriceRequired, "Укажите ручную цену продажи.");
        }

        if (price.Value < 0m
            || decimal.Round(price.Value, 4, MidpointRounding.AwayFromZero) != price.Value
            || price.Value > 999_999_999_999_999.9999m)
        {
            throw new CommercialTermsException(
                InvalidUnitPrice,
                "Цена должна быть неотрицательным числом с точностью не более четырёх знаков после запятой.");
        }

        return price.Value;
    }

    private decimal ResolveVatRate(Item item)
    {
        if (!item.DefaultSaleVatRateId.HasValue)
        {
            throw new CommercialTermsException(
                ItemSaleVatRateRequired,
                "Для товара не выбрана ставка НДС продажи. Укажите ставку в карточке товара.");
        }

        if (!item.DefaultSaleVatRate.HasValue)
        {
            throw new CommercialTermsException(
                VatRateReferenceInvalid,
                "Ссылка товара на ставку НДС повреждена.");
        }

        if (item.DefaultSaleVatRateIsActive != true)
        {
            throw new CommercialTermsException(
                VatRateInactive,
                "Для товара выбрана неактивная ставка НДС продажи. Укажите активную ставку в карточке товара.");
        }

        return item.DefaultSaleVatRate.Value;
    }
}
