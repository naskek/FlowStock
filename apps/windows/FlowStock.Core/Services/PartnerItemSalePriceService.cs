using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class PartnerItemSalePriceService
{
    private readonly IDataStore _data;
    private readonly IPartnerRoleResolver _partnerRoles;

    public PartnerItemSalePriceService(
        IDataStore data,
        IPartnerRoleResolver? partnerRoles = null)
    {
        _data = data;
        _partnerRoles = partnerRoles ?? PersistedPartnerRoleResolver.Instance;
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
        long id = 0;
        _data.ExecuteInTransaction(store =>
        {
            var partner = LockPartner(store, partnerId);
            ValidateCustomerRole(partner);
            ValidateItem(store, itemId);
            var price = CommercialTermsResolver.ValidateManualPrice(unitPriceGross);
            id = store.AddPartnerItemSalePrice(new PartnerItemSalePrice
            {
                PartnerId = partnerId,
                ItemId = itemId,
                UnitPriceGross = price,
                IsActive = isActive
            });
        });
        return id;
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

            var existing = store.GetPartnerItemSalePrice(id)
                           ?? throw new CommercialTermsException(
                               "PARTNER_ITEM_SALE_PRICE_NOT_FOUND",
                               "Цена клиента не найдена.");
            var lockedPartners = new Dictionary<long, Partner>();
            foreach (var lockedPartnerId in new[] { existing.PartnerId, partnerId }
                         .Distinct()
                         .OrderBy(value => value))
            {
                lockedPartners.Add(
                    lockedPartnerId,
                    LockPartner(store, lockedPartnerId));
            }

            ValidateCustomerRole(lockedPartners[partnerId]);
            ValidateItem(store, itemId);
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

    private static Partner LockPartner(IDataStore store, long partnerId)
    {
        return store.LockPartnerForUpdate(partnerId)
               ?? throw new CommercialTermsException(
                   "PARTNER_NOT_FOUND",
                   "Контрагент не найден.");
    }

    private void ValidateCustomerRole(Partner partner)
    {
        if (!_partnerRoles.IsCustomer(partner))
        {
            throw new CommercialTermsException(
                "PARTNER_IS_SUPPLIER",
                "Индивидуальная цена может быть задана только для клиента.");
        }
    }

    private static void ValidateItem(IDataStore store, long itemId)
    {
        if (store.FindItemById(itemId) == null)
        {
            throw new CommercialTermsException("ITEM_NOT_FOUND", "Товар не найден.");
        }
    }

    private sealed class PersistedPartnerRoleResolver : IPartnerRoleResolver
    {
        public static PersistedPartnerRoleResolver Instance { get; } = new();

        public bool IsCustomer(Partner partner)
        {
            return partner.PartnerRole?.Trim().ToUpperInvariant() switch
            {
                "SUPPLIER" => false,
                "CLIENT" or "BOTH" => true,
                null or "" => true,
                _ => false
            };
        }
    }
}
