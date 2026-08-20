namespace FlowStock.Server.Tests.Wpf;

public sealed class CatalogMachineHeadersSourceTests
{
    [Fact]
    public void CatalogClientsAttachMachineKeyAndAuditActor()
    {
        var helper = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfTrustedRequestHeaders.cs");
        Assert.Contains("X-FlowStock-WPF-Admin-Key", helper, StringComparison.Ordinal);
        Assert.Contains("X-FlowStock-WPF-Audit-Actor", helper, StringComparison.Ordinal);

        foreach (var file in new[]
                 {
                     "WpfCatalogApiService.cs",
                     "WpfPartnerApiService.cs",
                     "WpfPackagingApiService.cs",
                     "WpfCommercialCatalogApiService.cs",
                     "WpfReadApiService.cs"
                 })
        {
            var source = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", file);
            Assert.Contains("WpfTrustedRequestHeaders", source, StringComparison.Ordinal);
        }

        var catalogSource = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfCatalogApiService.cs");
        Assert.Contains("TryUpdateUomAsync", catalogSource, StringComparison.Ordinal);
        Assert.Contains("$\"/api/uoms/{id}\"", catalogSource, StringComparison.Ordinal);

        var uomWindowSource = ReadRepoFile("apps", "windows", "FlowStock.App", "UomWindow.xaml.cs");
        Assert.Contains("TryUpdateUomAsync", uomWindowSource, StringComparison.Ordinal);
        Assert.Contains("LoadUoms();", uomWindowSource, StringComparison.Ordinal);
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
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException("Не удалось найти файл репозитория.", Path.Combine(parts));
    }
}
