namespace FlowStock.Server.Tests.Wpf;

public sealed class WpfOrderControlApiSourceTests
{
    [Fact]
    public void ListAsync_AlwaysSendsActiveOnlyQueryParameter()
    {
        var code = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfOrderControlApiService.cs"));

        Assert.Contains("\"/api/order-control/tasks?activeOnly={activeOnly.ToString().ToLowerInvariant()}\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("activeOnly ? \"/api/order-control/tasks?activeOnly=true\" : \"/api/order-control/tasks\"", code, StringComparison.Ordinal);
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
