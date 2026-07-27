using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class PartnerItemSalePriceService
{
    private readonly IDataStore _data;

    public PartnerItemSalePriceService(IDataStore data)
    {
        _data = data;
    }

    public PartnerItemSalePricePage Get(
        long? partnerId,
        long? itemId,
        bool? isActive,
        string? search,
        int limit,
        int offset)
    {
        var normalizedLimit = Math.Clamp(limit, 1, 500);
        var normalizedOffset = Math.Max(0, offset);
        return _data.GetPartnerItemSalePrices(
            partnerId,
            itemId,
            isActive,
            search,
            normalizedLimit,
            normalizedOffset);
    }

    public long Create(long partnerId, long itemId, decimal unitPriceGross, bool isActive)
    {
        ValidateReferences(_data, partnerId, itemId);
        var price = CommercialTermsResolver.ValidateManualPrice(unitPriceGross);
        return _data.AddPartnerItemSalePrice(new PartnerItemSalePrice
        {
            PartnerId = partnerId,
            ItemId = itemId,
            UnitPriceGross = price,
            IsActive = isActive
        });
    }

    public void Update(long id, long partnerId, long itemId, decimal unitPriceGross, bool isActive)
    {
        _data.ExecuteInTransaction(store =>
        {
            if (!store.LockPartnerItemSalePriceForUpdate(id))
            {
                throw new CommercialTermsException(
                    "PARTNER_ITEM_SALE_PRICE_NOT_FOUND",
                    "Цена клиента не найдена.");
            }

            ValidateReferences(store, partnerId, itemId);
            var price = CommercialTermsResolver.ValidateManualPrice(unitPriceGross);
            store.UpdatePartnerItemSalePrice(new PartnerItemSalePrice
            {
                Id = id,
                PartnerId = partnerId,
                ItemId = itemId,
                UnitPriceGross = price,
                IsActive = isActive
            });
        });
    }

    public void Delete(long id)
    {
        _data.DeletePartnerItemSalePrice(id);
    }

    private static void ValidateReferences(IDataStore store, long partnerId, long itemId)
    {
        if (store.GetPartner(partnerId) == null)
        {
            throw new CommercialTermsException("PARTNER_NOT_FOUND", "Контрагент не найден.");
        }

        if (store.FindItemById(itemId) == null)
        {
            throw new CommercialTermsException("ITEM_NOT_FOUND", "Товар не найден.");
        }
    }
}
