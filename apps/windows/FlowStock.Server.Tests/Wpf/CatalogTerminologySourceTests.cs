namespace FlowStock.Server.Tests.Wpf;

public sealed class CatalogTerminologySourceTests
{
    [Fact]
    public void ItemCard_HidesTechnicalIdAndUsesCatalogLabels()
    {
        var itemXaml = ReadAppFile("ItemEditWindow.xaml");
        var itemCode = ReadAppFile("ItemEditWindow.xaml.cs");
        var mainXaml = ReadAppFile("MainWindow.xaml");
        var itemsGrid = Slice(mainXaml, "<DataGrid x:Name=\"ItemsGrid\"", "</DataGrid>");

        Assert.DoesNotContain("IdBox", itemXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IdBox", itemCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Редактирование товара #", itemCode, StringComparison.Ordinal);
        Assert.Contains("Title = \"Редактирование товара\";", itemCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"ID\"", itemsGrid, StringComparison.Ordinal);

        Assert.Contains("Text=\"Фасовка / нетто\"", itemXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Потребительская тара\"", itemXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Единица складского учета\"", itemXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Макс. в 1 HU\"", itemXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ItemCard_TypeDrivenRowsHideLabelsAndControlsTogether()
    {
        var xaml = ReadAppFile("ItemEditWindow.xaml");
        var code = ReadAppFile("ItemEditWindow.xaml.cs");

        Assert.Contains("x:Name=\"MinStockQtyLabel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MaxQtyPerHuLabel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MarkingStatusLabel\"", xaml, StringComparison.Ordinal);

        Assert.Contains("MinStockQtyLabel.Visibility = minStockVisibility;", code, StringComparison.Ordinal);
        Assert.Contains("MinStockQtyBox.Visibility = minStockVisibility;", code, StringComparison.Ordinal);
        Assert.Contains("MaxQtyPerHuLabel.Visibility = maxQtyPerHuVisibility;", code, StringComparison.Ordinal);
        Assert.Contains("MaxQtyPerHuBox.Visibility = maxQtyPerHuVisibility;", code, StringComparison.Ordinal);
        Assert.Contains("MarkingStatusLabel.Visibility = visibility;", code, StringComparison.Ordinal);
        Assert.Contains("MarkingStatusText.Visibility = visibility;", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ItemCard_DisabledHuDistributionPreservesExistingValueWithoutValidation()
    {
        var code = ReadAppFile("ItemEditWindow.xaml.cs");
        var save = Slice(code, "private async void Save_Click", "private bool TryParseShelfLifeMonths");

        Assert.Contains("if (itemType?.EnableHuDistribution == true)", save, StringComparison.Ordinal);
        Assert.Contains("maxQtyPerHu = _item?.MaxQtyPerHu;", save, StringComparison.Ordinal);
        Assert.Equal(1, Count(save, "TryParseMaxQtyPerHu("));
        Assert.Contains("MaxQtyPerHu = maxQtyPerHu,", save, StringComparison.Ordinal);
        Assert.Contains("MaxQtyPerHu = candidate.MaxQtyPerHu,", save, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagingEditors_UseOperatorTerminologyWithoutChangingTechnicalBindings()
    {
        var mainXaml = ReadAppFile("MainWindow.xaml");
        var itemXaml = ReadAppFile("ItemPackagingWindow.xaml");
        var managerXaml = ReadAppFile("PackagingManagerWindow.xaml");
        var itemCode = ReadAppFile("ItemPackagingWindow.xaml.cs");
        var managerCode = ReadAppFile("PackagingManagerWindow.xaml.cs");

        Assert.Contains("Упаковочные единицы / кратности...", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Коэффициент к складской единице", itemXaml, StringComparison.Ordinal);
        Assert.Contains("Коэффициент к складской единице", managerXaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding FactorToBase}\"", itemXaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding FactorToBase}\"", managerXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Количество в упаковке", itemXaml + itemCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Количество в упаковке", managerXaml + managerCode, StringComparison.Ordinal);
    }

    private static string ReadAppFile(string fileName)
    {
        return File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.App", fileName));
    }

    private static string Slice(string value, string startMarker, string endMarker)
    {
        var start = value.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        var end = value.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker not found: {endMarker}");
        return value[start..end];
    }

    private static int Count(string value, string needle)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }

        return count;
    }

    private static string GetRepoFile(params string[] parts)
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
