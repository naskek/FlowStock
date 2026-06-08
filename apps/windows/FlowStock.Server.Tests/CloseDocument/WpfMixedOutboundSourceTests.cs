namespace FlowStock.Server.Tests.CloseDocument;

public sealed class WpfMixedOutboundSourceTests
{
    [Fact]
    public void OperationDetailsWindow_DeletesWholeMixedHuAfterConfirmation_AndBlocksQuantityEdit()
    {
        var source = ReadOperationDetailsSource();
        var deleteMethod = SliceMethod(source, "private async void DocDeleteLine_Click", "    private void KmCodes_Click");
        var editMethod = SliceMethod(source, "private async void DocEditLine_Click", "    private static MessageBoxImage ResolveServerUpdateLineMessageImage");

        Assert.Contains("Микс-паллета отгружается только целиком.", deleteMethod, StringComparison.Ordinal);
        Assert.Contains("MessageBoxButton.YesNo", deleteMethod, StringComparison.Ordinal);
        Assert.Contains("mixedHuCodes.Contains(line.FromHu", deleteMethod, StringComparison.Ordinal);
        Assert.Contains("Количество внутри микс-паллеты нельзя изменить отдельно.", editMethod, StringComparison.Ordinal);
    }

    private static string ReadOperationDetailsSource()
    {
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(
                current,
                string.Concat(Enumerable.Repeat("..\\", i)),
                "apps",
                "windows",
                "FlowStock.App",
                "OperationDetailsWindow.xaml.cs"));
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException("Не удалось найти OperationDetailsWindow.xaml.cs.");
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
