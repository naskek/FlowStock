namespace FlowStock.Server.Tests.Telegram;

public sealed class OrderRequestTelegramEndpointSourceTests
{
    [Fact]
    public void EnqueueOccursOnlyAfterOrderRequestWasPersisted()
    {
        var root = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "apps",
            "windows",
            "FlowStock.Server",
            "Program.cs"));
        var endpointStart = source.IndexOf(
            "app.MapPost(\"/api/orders/requests/create\"",
            StringComparison.Ordinal);
        var endpointEnd = source.IndexOf(
            "app.MapPost(\"/api/orders/requests/status\"",
            endpointStart,
            StringComparison.Ordinal);
        var endpoint = source[endpointStart..endpointEnd];

        var persist = endpoint.IndexOf("store.AddOrderRequest", StringComparison.Ordinal);
        var enqueue = endpoint.IndexOf("telegramNotifications.TryEnqueue();", StringComparison.Ordinal);
        var success = endpoint.IndexOf("return Results.Ok", StringComparison.Ordinal);

        Assert.True(persist >= 0, "AddOrderRequest must remain in the create-request endpoint.");
        Assert.True(enqueue > persist, "Telegram enqueue must follow successful persistence.");
        Assert.True(success > enqueue, "Telegram enqueue must precede the existing success response.");
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "AGENTS.md")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
