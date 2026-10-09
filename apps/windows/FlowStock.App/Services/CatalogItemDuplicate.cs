using FlowStock.Core.Models;

namespace FlowStock.App.Services;

internal static class CatalogItemDuplicate
{
    public static bool CanDuplicate(int selectedCount) => selectedCount == 1;

    public static bool HasSynchronizedIdentifiers(Item source) =>
        !string.IsNullOrWhiteSpace(source.Gtin)
        && string.Equals(source.Barcode?.Trim(), source.Gtin.Trim(), StringComparison.OrdinalIgnoreCase);

    public static Item CreateDraft(Item source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new Item
        {
            Name = source.Name,
            IsActive = true,
            Barcode = source.Barcode,
            Gtin = source.Gtin,
            BaseUom = source.BaseUom,
            Brand = source.Brand,
            Volume = source.Volume,
            ShelfLifeMonths = source.ShelfLifeMonths,
            StorageConditions = source.StorageConditions,
            MaxQtyPerHu = source.MaxQtyPerHu,
            TaraId = source.TaraId,
            ItemTypeId = source.ItemTypeId,
            MinStockQty = source.MinStockQty,
            DefaultSalePriceGross = source.DefaultSalePriceGross,
            DefaultSaleVatRateId = source.DefaultSaleVatRateId
        };
    }
}
