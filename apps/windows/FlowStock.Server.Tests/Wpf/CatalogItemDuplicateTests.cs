using FlowStock.App.Services;
using FlowStock.Core.Models;

namespace FlowStock.Server.Tests.Wpf;

public sealed class CatalogItemDuplicateTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void CanDuplicate_RequiresExactlyOneSelectedRow(int count, bool expected)
    {
        Assert.Equal(expected, CatalogItemDuplicate.CanDuplicate(count));
    }

    [Fact]
    public void CreateDraft_FromInactiveSource_CopiesEditableFieldsButNotIdentityOrRelations()
    {
        var source = new Item
        {
            Id = 42,
            Name = "Хрен",
            IsActive = false,
            Barcode = "OLD-SKU",
            Gtin = "04601234567890",
            BaseUom = "шт",
            DefaultPackagingId = 133,
            Brand = "Русские закуски",
            Volume = "200 г",
            ShelfLifeMonths = 12,
            StorageConditions = "от +2 до +25 °C",
            MaxQtyPerHu = 900,
            TaraId = 5,
            ItemTypeId = 2,
            MinStockQty = 24,
            DefaultSalePriceGross = 55.1234m,
            DefaultSaleVatRateId = 3,
            IsMarked = true
        };

        var draft = CatalogItemDuplicate.CreateDraft(source);

        Assert.NotSame(source, draft);
        Assert.Equal(0, draft.Id);
        Assert.True(draft.IsActive);
        Assert.False(draft.IsMarked);
        Assert.Null(draft.DefaultPackagingId);
        Assert.Equal(source.Name, draft.Name);
        Assert.Equal(source.Barcode, draft.Barcode);
        Assert.Equal(source.Gtin, draft.Gtin);
        Assert.Equal(source.BaseUom, draft.BaseUom);
        Assert.Equal(source.Brand, draft.Brand);
        Assert.Equal(source.Volume, draft.Volume);
        Assert.Equal(source.ShelfLifeMonths, draft.ShelfLifeMonths);
        Assert.Equal(source.StorageConditions, draft.StorageConditions);
        Assert.Equal(source.MaxQtyPerHu, draft.MaxQtyPerHu);
        Assert.Equal(source.TaraId, draft.TaraId);
        Assert.Equal(source.ItemTypeId, draft.ItemTypeId);
        Assert.Equal(source.MinStockQty, draft.MinStockQty);
        Assert.Equal(source.DefaultSalePriceGross, draft.DefaultSalePriceGross);
        Assert.Equal(source.DefaultSaleVatRateId, draft.DefaultSaleVatRateId);
        Assert.Equal(42, source.Id);
        Assert.False(source.IsActive);
    }

    [Theory]
    [InlineData("04601234567890", "04601234567890", true)]
    [InlineData(" 04601234567890 ", "04601234567890", true)]
    [InlineData("SKU-1", "04601234567890", false)]
    [InlineData("04601234567890", null, false)]
    public void HasSynchronizedIdentifiers_OnlyForSameNonemptyBarcodeAndGtin(
        string barcode, string? gtin, bool expected)
    {
        Assert.Equal(expected, CatalogItemDuplicate.HasSynchronizedIdentifiers(new Item
        {
            Barcode = barcode,
            Gtin = gtin
        }));
    }
}
