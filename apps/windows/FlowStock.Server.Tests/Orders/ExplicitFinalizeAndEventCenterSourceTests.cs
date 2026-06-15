namespace FlowStock.Server.Tests.Orders;

public sealed class ExplicitFinalizeAndEventCenterSourceTests
{
    [Fact]
    public void ServerAndClientsExposeExplicitFinalizeAndEventCenterContracts()
    {
        var root = FindRepoRoot();
        var production = File.ReadAllText(Path.Combine(root, "apps", "windows", "FlowStock.Server", "ProductionPalletEndpoints.cs"));
        var outbound = File.ReadAllText(Path.Combine(root, "apps", "windows", "FlowStock.Core", "Services", "OutboundPickingService.cs"));
        var fillingService = File.ReadAllText(Path.Combine(root, "apps", "windows", "FlowStock.Core", "Services", "ProductionPalletService.cs"));
        var app = File.ReadAllText(Path.Combine(root, "apps", "android", "tsd", "app.js"));
        var xaml = File.ReadAllText(Path.Combine(root, "apps", "windows", "FlowStock.App", "IncomingRequestsWindow.xaml"));
        var migration = File.ReadAllText(Path.Combine(root, "deploy", "postgres", "migrations", "V0025__tsd_explicit_finalize_and_business_notifications.sql"));

        Assert.Contains("/api/tsd/production/orders/{orderId:long}/complete", production);
        Assert.DoesNotContain("_options.OutboundAutoCloseOnComplete && details.IsComplete", outbound);
        Assert.Contains("store.AddProductionFillingCompletion", fillingService);
        Assert.Contains("BuildOperationFingerprint", fillingService);
        Assert.Contains("GetProductionFillingCompletion(orderId, fingerprint)", fillingService);
        Assert.Contains("buildClosePromptStateKey", app);
        Assert.Contains("operationFingerprint", app);
        Assert.Contains("Все паллеты отсканированы", app);
        Assert.Contains("Title=\"Центр событий\"", xaml);
        Assert.Contains("Header=\"Требуют действия\"", xaml);
        Assert.Contains("Header=\"Журнал событий\"", xaml);
        Assert.Contains("business_notification_reads", migration);
        Assert.Contains("dedupe_key TEXT NOT NULL UNIQUE", migration);
        Assert.DoesNotContain("is_read", migration);
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
