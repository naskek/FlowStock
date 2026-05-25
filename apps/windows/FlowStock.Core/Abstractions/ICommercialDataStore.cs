using FlowStock.Core.Commercial;

namespace FlowStock.Core.Abstractions;

public interface ICommercialDataStore
{
    IReadOnlyList<PriceGroup> GetPriceGroups(bool includeInactive);
    PriceGroup? GetPriceGroup(long id);
    PriceGroup? GetDefaultPriceGroup();
    PriceGroup? GetSystemBasePriceGroup();
    long EnsureSystemBasePriceGroup();
    long AddPriceGroup(PriceGroup group);
    void UpdatePriceGroup(PriceGroup group);
    void SetDefaultPriceGroup(long priceGroupId);

    PartnerCommercialSettings? GetPartnerCommercialSettings(long partnerId);
    void UpsertPartnerCommercialSettings(PartnerCommercialSettings settings);

    IReadOnlyList<ItemPrice> GetItemPrices(long itemId, long? priceGroupId);
    IReadOnlyList<ItemPrice> GetItemPricesForGroup(long priceGroupId, string? search, long? itemTypeId, bool? hasPrice);
    IReadOnlyList<ItemPriceCatalogRow> GetItemPriceCatalogForGroup(long priceGroupId, long basePriceGroupId, string? search, long? itemTypeId, bool? hasPrice);
    ItemPrice? GetItemPrice(long itemPriceId);
    ItemPrice? GetActiveItemPrice(long itemId, long priceGroupId, DateOnly asOfDate);
    long AddItemPrice(ItemPrice price);
    void CloseOverlappingActiveItemPrices(long itemId, long priceGroupId, DateOnly validFrom, DateOnly? validTo);
    void DeactivateItemPrice(long itemPriceId);

    IReadOnlyList<VolumeDiscountRule> GetVolumeDiscountRules(bool includeInactive);
    long AddVolumeDiscountRule(VolumeDiscountRule rule);
    void UpdateVolumeDiscountRule(VolumeDiscountRule rule);

    IReadOnlyList<CommercialOffer> GetCommercialOffers(string? status, long? partnerId, DateOnly? from, DateOnly? to);
    CommercialOffer? GetCommercialOffer(long id);
    CommercialOffer? GetCommercialOfferByRef(string offerRef);
    int GetMaxCommercialOfferRefSequenceByYear(int year);
    long AddCommercialOffer(CommercialOffer offer);
    void UpdateCommercialOffer(CommercialOffer offer);
    void DeleteCommercialOffer(long offerId);

    IReadOnlyList<CommercialOfferLine> GetCommercialOfferLines(long offerId);
    long AddCommercialOfferLine(CommercialOfferLine line);
    void UpdateCommercialOfferLine(CommercialOfferLine line);
    void DeleteCommercialOfferLine(long lineId);
    void DeleteCommercialOfferLines(long offerId);

    IReadOnlyList<CommercialOfferStatusHistoryEntry> GetCommercialOfferStatusHistory(long offerId);
    void AddCommercialOfferStatusHistory(CommercialOfferStatusHistoryEntry entry);

    IReadOnlyList<CommercialTemplate> GetCommercialTemplates(CommercialTemplateType? templateType, bool includeInactive);
    CommercialTemplate? GetCommercialTemplate(long id);
    CommercialTemplate? GetDefaultCommercialTemplate(CommercialTemplateType templateType);
    long AddCommercialTemplate(CommercialTemplate template);
    void UpdateCommercialTemplate(CommercialTemplate template);
    void SetDefaultCommercialTemplate(long templateId);

    long AddGeneratedDocument(GeneratedDocument document);
    IReadOnlyList<GeneratedDocument> GetGeneratedDocuments(string sourceType, long sourceId);

    long AddPriceTagBatch(PriceTagBatch batch);
    void AddPriceTagBatchLine(PriceTagBatchLine line);
    IReadOnlyList<PriceTagBatchLine> GetPriceTagBatchLines(long batchId);

    void SetOrderCommercialOfferId(long orderId, long commercialOfferId);
}
