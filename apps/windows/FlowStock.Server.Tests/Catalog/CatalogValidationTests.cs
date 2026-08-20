using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Catalog;

public sealed class CatalogValidationTests
{
    [Fact]
    public void CreateItem_WithZeroItemTypeId_RejectsBeforePersistence()
    {
        var (service, store) = CreateService();
        store.Setup(data => data.AddItem(It.IsAny<Item>())).Returns(1);

        var error = Assert.Throws<ArgumentException>(() =>
            service.CreateItem(
                "Товар",
                "SKU-ZERO-TYPE",
                null,
                "шт",
                null,
                null,
                null,
                null,
                false,
                itemTypeId: 0));

        Assert.Equal("itemTypeId", error.ParamName);
        store.Verify(data => data.AddItem(It.IsAny<Item>()), Times.Never);
    }

    [Fact]
    public void CreateItem_WithNegativeItemTypeId_RejectsBeforePersistence()
    {
        var (service, store) = CreateService();

        var error = Assert.Throws<ArgumentException>(() =>
            service.CreateItem(
                "Товар",
                "SKU-NEGATIVE-TYPE",
                null,
                "шт",
                null,
                null,
                null,
                null,
                false,
                itemTypeId: -1));

        Assert.Equal("itemTypeId", error.ParamName);
        store.Verify(data => data.AddItem(It.IsAny<Item>()), Times.Never);
    }

    [Fact]
    public void UpdateItem_WithZeroItemTypeId_RejectsBeforePersistence()
    {
        var (service, store) = CreateService();
        store.Setup(data => data.FindItemById(10)).Returns(new Item
        {
            Id = 10,
            Name = "Товар",
            Barcode = "SKU-UPDATE-TYPE",
            BaseUom = "шт",
            IsActive = true
        });

        var error = Assert.Throws<ArgumentException>(() =>
            service.UpdateItem(
                10,
                "Товар",
                "SKU-UPDATE-TYPE",
                null,
                "шт",
                null,
                null,
                null,
                null,
                false,
                itemTypeId: 0));

        Assert.Equal("itemTypeId", error.ParamName);
        store.Verify(data => data.UpdateItem(It.IsAny<Item>()), Times.Never);
    }

    [Fact]
    public void CreateItem_WithNullItemTypeId_PersistsWithoutType()
    {
        var (service, store) = CreateService();
        Item? captured = null;
        store.Setup(data => data.AddItem(It.IsAny<Item>()))
            .Callback<Item>(item => captured = item)
            .Returns(1);

        service.CreateItem(
            "Товар",
            "SKU-NO-TYPE",
            null,
            "шт",
            null,
            null,
            null,
            null,
            false,
            itemTypeId: null);

        Assert.NotNull(captured);
        Assert.Null(captured!.ItemTypeId);
    }

    [Fact]
    public void ShelfLife_IsOptional_ButSpecifiedValueMustBePositive()
    {
        var (service, store) = CreateService();
        Item? captured = null;
        store.Setup(data => data.AddItem(It.IsAny<Item>()))
            .Callback<Item>(item => captured = item)
            .Returns(1);

        service.CreateItem("Товар", "SKU-1", null, "шт", null, null, null, null, false);
        Assert.Null(captured?.ShelfLifeMonths);

        var error = Assert.Throws<ArgumentException>(() =>
            service.CreateItem("Товар", "SKU-2", null, "шт", null, null, 0, null, false));
        Assert.Contains("положительным целым", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyBarcode_FallsBackToTrimmedGtin()
    {
        var (service, store) = CreateService();
        Item? captured = null;
        store.Setup(data => data.AddItem(It.IsAny<Item>()))
            .Callback<Item>(item => captured = item)
            .Returns(1);

        service.CreateItem("Товар", " ", " 04600000000001 ", "шт", null, null, null, null, false);

        Assert.Equal("04600000000001", captured?.Barcode);
        Assert.Equal("04600000000001", captured?.Gtin);
    }

    [Fact]
    public void IdentifierConflict_IsCaseInsensitiveAndIdentifiesFieldAndExistingItem()
    {
        var (service, store) = CreateService();
        store.Setup(data => data.GetItems(null)).Returns([
            new Item { Id = 42, Name = "Существующий", Barcode = " AbC-1 ", Gtin = "04600000000001" }
        ]);

        var barcode = Assert.Throws<CatalogIdentifierConflictException>(() =>
            service.CreateItem("Новый", "abc-1", null, "шт", null, null, null, null, false));
        Assert.Equal("ITEM_BARCODE_DUPLICATE", barcode.ErrorCode);
        Assert.Equal(42, barcode.ExistingItemId);

        var gtin = Assert.Throws<CatalogIdentifierConflictException>(() =>
            service.CreateItem("Новый", "SKU-NEW", "04600000000001", "шт", null, null, null, null, false));
        Assert.Equal("ITEM_GTIN_DUPLICATE", gtin.ErrorCode);
    }

    [Fact]
    public void PartnerInn_IsNullableOrDigitsOnly_AndRoleIsMandatory()
    {
        var (service, store) = CreateService();
        Partner? captured = null;
        store.Setup(data => data.AddPartner(It.IsAny<Partner>()))
            .Callback<Partner>(partner => captured = partner)
            .Returns(1);

        service.CreatePartner("Клиент", " ", "Client");
        Assert.Null(captured?.Code);
        Assert.Equal("CLIENT", captured?.PartnerRole);

        Assert.Throws<ArgumentException>(() => service.CreatePartner("Клиент", "12A", "CLIENT"));
        Assert.Throws<ArgumentException>(() => service.CreatePartner("Клиент", "123", "UNKNOWN"));
    }

    private static (CatalogService Service, Mock<IDataStore> Store) CreateService()
    {
        var store = new Mock<IDataStore>();
        store.Setup(data => data.GetItems(null)).Returns(Array.Empty<Item>());
        store.Setup(data => data.GetUoms()).Returns([new Uom { Id = 1, Name = "шт" }]);
        store.Setup(data => data.GetTaras()).Returns(Array.Empty<Tara>());
        return (new CatalogService(store.Object), store);
    }
}
