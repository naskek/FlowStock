using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Catalog;

public sealed class ItemTypeMinStockTests
{
    [Fact]
    public void CreateItem_WhenTypeDoesNotControlMinStock_ClearsMinStockQty()
    {
        var store = new Mock<IDataStore>();
        store.Setup(x => x.GetItemType(5)).Returns(new ItemType
        {
            Id = 5,
            Name = "Без контроля",
            IsActive = true,
            EnableMinStockControl = false
        });

        Item? captured = null;
        store.Setup(x => x.AddItem(It.IsAny<Item>()))
            .Callback<Item>(item => captured = item)
            .Returns(10);

        var service = new CatalogService(store.Object);
        service.CreateItem(
            name: "Тест",
            barcode: "SKU-1",
            gtin: null,
            baseUom: "шт",
            brand: null,
            volume: null,
            shelfLifeMonths: null,
            taraId: null,
            isMarked: false,
            maxQtyPerHu: null,
            itemTypeId: 5,
            minStockQty: 25);

        Assert.NotNull(captured);
        Assert.Null(captured!.MinStockQty);
    }

    [Fact]
    public void CreateItem_WhenMinStockIsNegative_Throws()
    {
        var store = new Mock<IDataStore>();
        store.Setup(x => x.GetItemType(7)).Returns(new ItemType
        {
            Id = 7,
            Name = "С контролем",
            IsActive = true,
            EnableMinStockControl = true
        });

        var service = new CatalogService(store.Object);

        var ex = Assert.Throws<ArgumentException>(() => service.CreateItem(
            name: "Тест",
            barcode: "SKU-2",
            gtin: null,
            baseUom: "шт",
            brand: null,
            volume: null,
            shelfLifeMonths: null,
            taraId: null,
            isMarked: false,
            maxQtyPerHu: null,
            itemTypeId: 7,
            minStockQty: -1));

        Assert.Contains("Минимальный остаток", ex.Message);
    }

    [Fact]
    public void DeleteItemType_WhenUsed_DeactivatesInsteadOfDelete()
    {
        var store = new Mock<IDataStore>();
        store.Setup(x => x.GetItemType(3)).Returns(new ItemType { Id = 3, Name = "Тип", IsActive = true });
        store.Setup(x => x.IsItemTypeUsed(3)).Returns(true);

        var service = new CatalogService(store.Object);
        service.DeleteItemType(3);

        store.Verify(x => x.DeactivateItemType(3), Times.Once);
        store.Verify(x => x.DeleteItemType(3), Times.Never);
    }

    [Fact]
    public void DeleteItemType_WhenUnused_Deletes()
    {
        var store = new Mock<IDataStore>();
        store.Setup(x => x.GetItemType(4)).Returns(new ItemType { Id = 4, Name = "Тип", IsActive = true });
        store.Setup(x => x.IsItemTypeUsed(4)).Returns(false);

        var service = new CatalogService(store.Object);
        service.DeleteItemType(4);

        store.Verify(x => x.DeleteItemType(4), Times.Once);
        store.Verify(x => x.DeactivateItemType(4), Times.Never);
    }

    [Fact]
    public void CreateItemType_PersistsMinStockUsesOrderBindingFlag()
    {
        var store = new Mock<IDataStore>();
        ItemType? captured = null;
        store.Setup(x => x.AddItemType(It.IsAny<ItemType>()))
            .Callback<ItemType>(itemType => captured = itemType)
            .Returns(11);

        var service = new CatalogService(store.Object);
        service.CreateItemType(
            name: "Тип",
            code: "T1",
            sortOrder: 1,
            isActive: true,
            isVisibleInProductCatalog: true,
            enableMinStockControl: true,
            minStockUsesOrderBinding: true,
            enableOrderReservation: true,
            enableHuDistribution: false);

        Assert.NotNull(captured);
        Assert.True(captured!.MinStockUsesOrderBinding);
    }

    [Fact]
    public void UpdateItemType_PersistsMinStockUsesOrderBindingFlag()
    {
        var store = new Mock<IDataStore>();
        store.Setup(x => x.GetItemType(5)).Returns(new ItemType { Id = 5, Name = "Тип", IsActive = true });
        ItemType? captured = null;
        store.Setup(x => x.UpdateItemType(It.IsAny<ItemType>()))
            .Callback<ItemType>(itemType => captured = itemType);

        var service = new CatalogService(store.Object);
        service.UpdateItemType(
            itemTypeId: 5,
            name: "Тип",
            code: "T1",
            sortOrder: 1,
            isActive: true,
            isVisibleInProductCatalog: true,
            enableMinStockControl: true,
            minStockUsesOrderBinding: true,
            enableOrderReservation: true,
            enableHuDistribution: false);

        Assert.NotNull(captured);
        Assert.True(captured!.MinStockUsesOrderBinding);
    }
}
