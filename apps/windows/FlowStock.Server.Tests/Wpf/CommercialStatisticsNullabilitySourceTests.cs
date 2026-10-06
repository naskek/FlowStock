namespace FlowStock.Server.Tests.Wpf;

public sealed class CommercialStatisticsNullabilitySourceTests
{
    private static readonly string Source = File.ReadAllText(FindRepoFile(
        "apps",
        "windows",
        "FlowStock.App",
        "MainWindow.CommercialStatisticsAdvancedFilters.cs"));

    [Fact]
    public void Advanced_period_handler_accepts_nullable_sender()
    {
        Assert.Contains(
            "private void StatisticsAdvancedPeriod_Changed(object? sender, EventArgs e)",
            Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_cs8622_suppression_is_scoped_to_xaml_handler_detach()
    {
        const string disable = "#pragma warning disable CS8622";
        const string restore = "#pragma warning restore CS8622";
        const string fromDetach = "StatisticsFromDate.SelectedDateChanged -= StatisticsPeriod_Changed;";
        const string toDetach = "StatisticsToDate.SelectedDateChanged -= StatisticsPeriod_Changed;";
        const string advancedAttach = "StatisticsFromDate.SelectedDateChanged += StatisticsAdvancedPeriod_Changed;";

        Assert.Equal(1, CountOccurrences(Source, disable));
        Assert.Equal(1, CountOccurrences(Source, restore));

        var disableIndex = Source.IndexOf(disable, StringComparison.Ordinal);
        var fromDetachIndex = Source.IndexOf(fromDetach, StringComparison.Ordinal);
        var toDetachIndex = Source.IndexOf(toDetach, StringComparison.Ordinal);
        var restoreIndex = Source.IndexOf(restore, StringComparison.Ordinal);
        var advancedAttachIndex = Source.IndexOf(advancedAttach, StringComparison.Ordinal);

        Assert.True(disableIndex >= 0);
        Assert.True(disableIndex < fromDetachIndex);
        Assert.True(fromDetachIndex < toDetachIndex);
        Assert.True(toDetachIndex < restoreIndex);
        Assert.True(restoreIndex < advancedAttachIndex);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string FindRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
