using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Catalog;

public sealed class CatalogMarkingApplicabilityTests
{
    [Fact]
    public void CreateApplicableWithoutGtinRejects_ButExplicitExemptionIsAllowed()
    {
        var (catalog, store) = CreateCatalog();

        var error = Assert.Throws<InvalidOperationException>(() => Create(catalog, exempt: false));
        Assert.Equal("MARKING_GTIN_REQUIRED", error.Message);

        Create(catalog, exempt: true);
        store.Verify(candidate => candidate.AddItem(It.Is<Item>(item => item.ChzMarkingExempt)), Times.Once);
    }

    [Fact]
    public void LegacyInvalidMetadataUpdateIsAllowed_ButApplicabilityTransitionRequiresGtin()
    {
        var (catalog, store) = CreateCatalog();
        var legacyInvalid = Existing(gtin: null, exempt: false, applicable: true);
        store.Setup(candidate => candidate.FindItemById(10)).Returns(legacyInvalid);

        Update(catalog, gtin: null, exempt: false);
        store.Verify(candidate => candidate.UpdateItem(It.IsAny<Item>()), Times.Once);

        store.Setup(candidate => candidate.FindItemById(10))
            .Returns(Existing(gtin: null, exempt: true, applicable: false));
        var error = Assert.Throws<InvalidOperationException>(() =>
            Update(catalog, gtin: null, exempt: false));
        Assert.Equal("MARKING_GTIN_REQUIRED", error.Message);
    }

    [Fact]
    public void ClearingGtinOnApplicableItemRejects_SettingExemptionAllowsBlankGtin()
    {
        var (catalog, store) = CreateCatalog();
        store.Setup(candidate => candidate.FindItemById(10))
            .Returns(Existing(gtin: "04600000000001", exempt: false, applicable: true));

        var error = Assert.Throws<InvalidOperationException>(() =>
            Update(catalog, gtin: null, exempt: false));
        Assert.Equal("MARKING_GTIN_REQUIRED", error.Message);

        Update(catalog, gtin: null, exempt: true);
        store.Verify(candidate => candidate.UpdateItem(It.Is<Item>(item => item.ChzMarkingExempt)), Times.Once);
    }

    private static (CatalogService Catalog, Mock<IDataStore> Store) CreateCatalog()
    {
        var store = new Mock<IDataStore>();
        store.Setup(candidate => candidate.GetItems(null)).Returns([]);
        store.Setup(candidate => candidate.GetUoms()).Returns([new Uom { Id = 1, Name = "шт" }]);
        store.Setup(candidate => candidate.GetTaras()).Returns([]);
        store.Setup(candidate => candidate.GetItemType(1)).Returns(new ItemType
        {
            Id = 1,
            Name = "Маркируемый",
            EnableMarking = true,
            IsActive = true
        });
        store.Setup(candidate => candidate.AddItem(It.IsAny<Item>())).Returns(10);
        return (new CatalogService(store.Object), store);
    }

    private static void Create(CatalogService catalog, bool exempt) => catalog.CreateItem(
        name: "Товар",
        barcode: "SKU-CREATE",
        gtin: null,
        baseUom: "шт",
        brand: null,
        volume: null,
        shelfLifeMonths: null,
        taraId: null,
        isMarked: false,
        itemTypeId: 1,
        chzMarkingExempt: exempt);

    private static void Update(CatalogService catalog, string? gtin, bool exempt) => catalog.UpdateItem(
        itemId: 10,
        name: "Товар после изменения",
        barcode: "SKU-UPDATE",
        gtin: gtin,
        baseUom: "шт",
        brand: null,
        volume: null,
        shelfLifeMonths: null,
        taraId: null,
        isMarked: false,
        itemTypeId: 1,
        chzMarkingExempt: exempt);

    private static Item Existing(string? gtin, bool exempt, bool applicable) => new()
    {
        Id = 10,
        Name = "Товар",
        Barcode = "SKU-UPDATE",
        Gtin = gtin,
        BaseUom = "шт",
        IsActive = true,
        ItemTypeId = 1,
        ItemTypeEnableMarking = applicable || exempt,
        ChzMarkingExempt = exempt
    };
}
