using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Catalog;

public sealed class ItemStorageConditionsTests
{
    [Fact]
    public void CreateItem_StorageConditions_TrimsEdgesAndPreservesInternalText()
    {
        var store = new Mock<IDataStore>();
        Item? captured = null;
        store.Setup(data => data.AddItem(It.IsAny<Item>()))
            .Callback<Item>(item => captured = item)
            .Returns(10);
        var service = new CatalogService(store.Object);
        var value = " \r\nот 0С до +10С,\nвлажность не более 75%  + сохранять\tтаб\r\n ";

        service.CreateItem(
            name: "Товар",
            barcode: "SKU-1",
            gtin: null,
            baseUom: "шт",
            brand: null,
            volume: null,
            shelfLifeMonths: null,
            taraId: null,
            isMarked: false,
            storageConditions: value);

        Assert.NotNull(captured);
        Assert.Equal("от 0С до +10С,\nвлажность не более 75%  + сохранять\tтаб", captured!.StorageConditions);
    }

    [Fact]
    public void CreateItem_WhitespaceStorageConditions_BecomesNull()
    {
        var store = new Mock<IDataStore>();
        Item? captured = null;
        store.Setup(data => data.AddItem(It.IsAny<Item>()))
            .Callback<Item>(item => captured = item)
            .Returns(10);
        var service = new CatalogService(store.Object);

        service.CreateItem(
            name: "Товар",
            barcode: "SKU-2",
            gtin: null,
            baseUom: "шт",
            brand: null,
            volume: null,
            shelfLifeMonths: null,
            taraId: null,
            isMarked: false,
            storageConditions: " \r\n\t ");

        Assert.NotNull(captured);
        Assert.Null(captured!.StorageConditions);
    }

    [Fact]
    public void UpdateItem_StorageConditions_CanBeChangedAndCleared()
    {
        var store = new Mock<IDataStore>();
        store.Setup(data => data.FindItemById(10)).Returns(new Item
        {
            Id = 10,
            Name = "Старый товар",
            BaseUom = "шт",
            IsActive = true,
            StorageConditions = "старое значение"
        });
        var captured = new List<Item>();
        store.Setup(data => data.UpdateItem(It.IsAny<Item>()))
            .Callback<Item>(item => captured.Add(item));
        var service = new CatalogService(store.Object);

        service.UpdateItem(
            itemId: 10,
            name: "Товар",
            barcode: "SKU-3",
            gtin: null,
            baseUom: "шт",
            brand: null,
            volume: null,
            shelfLifeMonths: null,
            taraId: null,
            isMarked: false,
            storageConditions: "  хранить при +5С\nне замораживать  ");
        service.UpdateItem(
            itemId: 10,
            name: "Товар",
            barcode: "SKU-3",
            gtin: null,
            baseUom: "шт",
            brand: null,
            volume: null,
            shelfLifeMonths: null,
            taraId: null,
            isMarked: false,
            storageConditions: " \r\n ");

        Assert.Equal("хранить при +5С\nне замораживать", captured[0].StorageConditions);
        Assert.Null(captured[1].StorageConditions);
    }

    [Fact]
    public void PostgresSchemaGuard_RequiresStorageConditionsMigrationWithoutRuntimeEnsureColumn()
    {
        var migration = ReadRepoFile("deploy", "postgres", "migrations", "V0028__item_storage_conditions.sql");
        var storeSource = ReadRepoFile("apps", "windows", "FlowStock.Data", "PostgresDataStore.cs");

        Assert.Contains("ADD COLUMN IF NOT EXISTS storage_conditions TEXT NULL", migration, StringComparison.Ordinal);
        Assert.Contains("ColumnExists(connection, \"items\", \"storage_conditions\")", storeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureColumn(connection, \"items\", \"storage_conditions\"", storeSource, StringComparison.Ordinal);
    }

    [Fact]
    public void WpfItemContracts_MapStorageConditions()
    {
        var readApi = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfReadApiService.cs");
        var catalogApi = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfCatalogApiService.cs");

        Assert.Contains("StorageConditions = ReadString(element, \"storage_conditions\")", readApi, StringComparison.Ordinal);
        Assert.Contains("storage_conditions = item.StorageConditions", catalogApi, StringComparison.Ordinal);
    }

    [Fact]
    public void NonCatalogItemUpdatePaths_PreserveExistingStorageConditions()
    {
        var importService = ReadRepoFile("apps", "windows", "FlowStock.Core", "Services", "ImportService.cs");
        var kmService = ReadRepoFile("apps", "windows", "FlowStock.Core", "Services", "KmService.cs");

        Assert.Contains("StorageConditions = existing.StorageConditions", importService, StringComparison.Ordinal);
        Assert.Contains("StorageConditions = item.StorageConditions", kmService, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(current, string.Concat(Enumerable.Repeat("..\\", i)), Path.Combine(parts)));
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException("Не удалось найти файл в репозитории.", Path.Combine(parts));
    }
}
