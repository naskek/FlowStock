namespace FlowStock.Server.Tests.Orders;

public sealed class OrderDetailsDeleteLineSourceTests
{
    [Fact]
    public void DeleteLine_Click_MarksDirtyBeforeRefreshingMetrics_AndForcesGridRefresh()
    {
        var source = File.ReadAllText(GetRepoFilePath("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs"));
        var methodBody = SliceMethod(source, "private void DeleteLine_Click(object sender, RoutedEventArgs e)", "private void OrderLinesGrid_KeyDown");

        AssertContainsInOrder(
            methodBody,
            "var targetLine = ResolveEditableOrderLine(_selectedLine);",
            "_lines.Remove(targetLine);",
            "MarkDirty();",
            "ForceOrderLinesGridRefresh();",
            "RefreshLineMetrics();");
    }

    [Fact]
    public void DeleteLine_Click_ResolvesPersistedLineFromEditableCollectionBeforeRemoval()
    {
        var source = File.ReadAllText(GetRepoFilePath("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs"));

        Assert.Contains("var targetLine = ResolveEditableOrderLine(_selectedLine);", source, StringComparison.Ordinal);
        Assert.Contains("return _lines.FirstOrDefault(line => line.Id == selectedLine.Id);", source, StringComparison.Ordinal);
        Assert.Contains("_lines.Remove(targetLine);", source, StringComparison.Ordinal);
    }

    private static void AssertContainsInOrder(string source, params string[] fragments)
    {
        var lastIndex = -1;
        foreach (var fragment in fragments)
        {
            var index = source.IndexOf(fragment, lastIndex + 1, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Не найден фрагмент: {fragment}");
            Assert.True(index > lastIndex, $"Ожидался порядок после предыдущего фрагмента: {fragment}");
            lastIndex = index;
        }
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Не найден метод: {startMarker}");

        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Не найдена граница метода: {endMarker}");

        return source[start..end];
    }

    private static string GetRepoFilePath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"File not found: {string.Join(Path.DirectorySeparatorChar, parts)}");
    }
}
