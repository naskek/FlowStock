using FlowStock.Core.Services;

namespace FlowStock.Server.Tests.DeleteDocLine;

public sealed class WpfPrdDeleteGuardSourceTests
{
    [Fact]
    public void OperationDetailsWindow_HidesAndBlocksProductionReceiptLineDelete()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "OperationDetailsWindow.xaml.cs");
        var deleteMethod = SliceMethod(source, "private async void DocDeleteLine_Click", "    private void KmCodes_Click");
        var buttonMethod = SliceMethod(source, "private void UpdateLineButtons", "    private void UpdateOutboundHuButton");

        Assert.Contains("if (_doc?.Type == DocType.ProductionReceipt)", deleteMethod, StringComparison.Ordinal);
        Assert.Contains("DocumentService.ProductionReceiptLineDeleteForbiddenMessage", deleteMethod, StringComparison.Ordinal);
        Assert.Contains("DeleteLineButton.Visibility", buttonMethod, StringComparison.Ordinal);
        Assert.Contains("? Visibility.Collapsed", buttonMethod, StringComparison.Ordinal);
        Assert.Contains("_doc?.Type != DocType.ProductionReceipt", buttonMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void WpfDeleteService_MapsProductionReceiptDeleteError()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfDeleteDocLineService.cs");

        Assert.Contains("DocumentService.ProductionReceiptLineDeleteForbiddenCode", source, StringComparison.Ordinal);
        Assert.Contains("DocumentService.ProductionReceiptLineDeleteForbiddenMessage", source, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] pathParts)
    {
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidateParts = new[]
            {
                current,
                string.Concat(Enumerable.Repeat("..\\", i))
            }.Concat(pathParts).ToArray();
            var candidate = Path.GetFullPath(Path.Combine(candidateParts));
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Не удалось найти файл: {Path.Combine(pathParts)}.");
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Не найден метод: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Не найдена граница метода: {endMarker}");
        return source[start..end];
    }
}
