namespace FlowStock.Server.Tests.Orders;

public sealed class OrderItemActivitySourceTests
{
    [Fact]
    public void PcRequestIntakeRejectsInactiveItemBeforePersistingRequest()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.Server", "Program.cs");
        var start = source.IndexOf(
            "app.MapPost(\"/api/orders/requests/create\"",
            StringComparison.Ordinal);
        var end = source.IndexOf("app.MapPost(\"/api/orders/requests/", start + 1, StringComparison.Ordinal);
        var endpoint = source[start..end];

        var activityCheck = endpoint.IndexOf("if (!item.IsActive)", StringComparison.Ordinal);
        var errorCode = endpoint.IndexOf("OrderItemActivityGuard.ItemInactiveForOrder", StringComparison.Ordinal);
        var persistence = endpoint.IndexOf("AddOrderRequest", StringComparison.Ordinal);

        Assert.True(activityCheck >= 0);
        Assert.True(errorCode > activityCheck);
        Assert.True(persistence > errorCode);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
