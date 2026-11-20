using System.IO.Compression;
using System.Xml.Linq;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Marking;

public sealed class ReadOnlyOrderExcelTests
{
    private static (Mock<IDataStore> Store, OrderMarkingExportService Service) Create()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.GetOrder(1)).Returns(new Order { Id = 1, OrderRef = "INT-1", Type = OrderType.Internal });
        store.Setup(s => s.GetOrderLines(1)).Returns(new[] {
            new OrderLine { Id = 10, OrderId = 1, ItemId = 2, QtyOrdered = 3.125678 },
            new OrderLine { Id = 11, OrderId = 1, ItemId = 2, QtyOrdered = 7 },
            new OrderLine { Id = 12, OrderId = 1, ItemId = 3, QtyOrdered = 8 },
            new OrderLine { Id = 13, OrderId = 1, ItemId = 4, QtyOrdered = 9 }
        });
        store.Setup(s => s.FindItemById(2)).Returns(new Item { Id = 2, Name = "Товар & <1>", Gtin = "00123456789012", ItemTypeEnableMarking = true });
        store.Setup(s => s.FindItemById(3)).Returns(new Item { Id = 3, Gtin = "00123456789013", ItemTypeEnableMarking = false });
        store.Setup(s => s.FindItemById(4)).Returns(new Item { Id = 4, Gtin = " ", ItemTypeEnableMarking = true });
        return (store, new OrderMarkingExportService(store.Object));
    }

    [Fact]
    public void PreviewSelectionRetry_UsesOnlyReadsAndPreservesExactThreeColumnWorkbook()
    {
        var (store, service) = Create();
        var preview = service.Preview(1);
        Assert.Equal(new long[] { 10, 11 }, preview.Lines.Select(line => line.OrderLineId));
        Assert.Equal(10.125678, preview.TotalQty);
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var result = service.Export(1, DateTime.UtcNow, new long[] { 10 }, preview.Snapshot);
            Assert.True(result.IsSuccess);
            Assert.Equal(3.125678, Assert.Single(result.Lines).ExportQty);
            using var zip = new ZipArchive(new MemoryStream(result.FileBytes!));
            Assert.Single(zip.Entries.Where(entry => entry.FullName.StartsWith("xl/worksheets/")));
            using var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var xml = XDocument.Load(sheet);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var row = Assert.Single(xml.Descendants(ns + "row"));
            var cells = row.Elements(ns + "c").ToArray();
            Assert.Equal(new[] { "A1", "B1", "C1" }, cells.Select(cell => cell.Attribute("r")!.Value));
            Assert.Equal("Товар & <1>", cells[0].Element(ns + "is")!.Element(ns + "t")!.Value);
            Assert.Equal("00123456789012", cells[1].Element(ns + "is")!.Element(ns + "t")!.Value);
            Assert.Equal("3.125678", cells[2].Element(ns + "v")!.Value);
            Assert.Null(cells[2].Attribute("t"));
        }
        Assert.All(store.Invocations, invocation => Assert.Contains(invocation.Method.Name,
            new[] { "GetOrder", "GetOrderLines", "FindItemById" }));
    }

    [Fact]
    public void Selection_RejectsEmptyForeignAndStalePreviewWithoutWrites()
    {
        var (_, service) = Create();
        var preview = service.Preview(1);
        Assert.False(service.Export(1, DateTime.UtcNow, Array.Empty<long>(), preview.Snapshot).IsSuccess);
        Assert.False(service.Export(1, DateTime.UtcNow, new long[] { 999 }, preview.Snapshot).IsSuccess);
        Assert.False(service.Export(1, DateTime.UtcNow, new long[] { 10 }, "old").IsSuccess);
        var all = service.Export(1, DateTime.UtcNow, new long[] { 10, 11, 10 }, preview.Snapshot);
        Assert.True(all.IsSuccess);
        Assert.Equal(preview.TotalQty, all.RequiredQty);
        Assert.Equal(2, all.LineCount);
    }
}
