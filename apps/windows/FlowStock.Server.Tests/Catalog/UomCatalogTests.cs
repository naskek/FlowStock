using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Catalog;

public sealed class UomCatalogTests
{
    [Fact]
    public void CreateAndRename_RejectReservedLegacyMasterName()
    {
        var store = new Mock<IDataStore>();
        var service = new CatalogService(store.Object);

        Assert.Throws<ArgumentException>(() => service.CreateUom(" шт "));
        Assert.Throws<ArgumentException>(() => service.RenameUom(1, "ШТ"));
        store.Verify(data => data.AddUom(It.IsAny<Uom>()), Times.Never);
        store.Verify(data => data.RenameUom(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void CreateItem_KeepsLegacyShtWithoutMasterRow()
    {
        var store = new Mock<IDataStore>();
        store.Setup(data => data.GetUoms()).Returns([]);
        store.Setup(data => data.GetItems(null)).Returns([]);
        store.Setup(data => data.GetTaras()).Returns([]);
        store.Setup(data => data.AddItem(It.IsAny<Item>())).Returns(42);
        var service = new CatalogService(store.Object);

        var id = service.CreateItem("Товар", "SKU-UOM", null, "шт", null, null, null, null, false);

        Assert.Equal(42, id);
        store.Verify(data => data.AddItem(It.Is<Item>(item => item.BaseUom == "шт")), Times.Once);
    }

    [Fact]
    public void Rename_DelegatesOnlyValidatedTrimmedName()
    {
        var store = new Mock<IDataStore>();
        var service = new CatalogService(store.Object);

        service.RenameUom(7, "  Коробка  ");

        store.Verify(data => data.RenameUom(7, "Коробка"), Times.Once);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(
                current,
                string.Concat(Enumerable.Repeat("..\\", i)),
                Path.Combine(parts)));
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException("Не удалось найти файл репозитория.", Path.Combine(parts));
    }
}
