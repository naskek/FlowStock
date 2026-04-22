using FlowStock.App;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.DeleteOrder.Infrastructure;

namespace FlowStock.Server.Tests.DeleteOrder;

[Collection("DeleteOrder")]
public sealed class WpfCompatibilityTests
{
    [Fact]
    public async Task WpfDeleteOrder_FeatureFlagRoutesToCanonicalDeleteApiOrders()
    {
        var (harness, apiStore, orderId) = DeleteOrderHttpScenario.CreateDraftCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var temp = new TempSettingsScope(host.Client.BaseAddress!, useServerDeleteOrder: true);
        var service = new WpfDeleteOrderService(new SettingsService(temp.SettingsPath), new FileLogger(temp.LogPath));

        var result = await service.DeleteOrderAsync(orderId);

        Assert.True(result.IsSuccess);
        Assert.Equal(WpfDeleteOrderResultKind.Deleted, result.Kind);
        Assert.Null(harness.Store.GetOrder(orderId));
    }

    [Fact]
    public async Task WpfDeleteOrder_ForbiddenDeleteReturnsValidationError()
    {
        var (harness, apiStore, orderId) = DeleteOrderHttpScenario.CreateCustomerWithOutboundDocsScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var temp = new TempSettingsScope(host.Client.BaseAddress!, useServerDeleteOrder: true);
        var service = new WpfDeleteOrderService(new SettingsService(temp.SettingsPath), new FileLogger(temp.LogPath));

        var result = await service.DeleteOrderAsync(orderId);

        Assert.False(result.IsSuccess);
        Assert.Equal(WpfDeleteOrderResultKind.ValidationFailed, result.Kind);
        Assert.NotNull(harness.Store.GetOrder(orderId));
    }

    [Fact]
    public async Task WpfDeleteOrder_IgnoresLegacyFlagAndStillUsesCanonicalApi()
    {
        var (harness, apiStore, orderId) = DeleteOrderHttpScenario.CreateDraftCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var temp = new TempSettingsScope(host.Client.BaseAddress!, useServerDeleteOrder: false);
        var service = new WpfDeleteOrderService(new SettingsService(temp.SettingsPath), new FileLogger(temp.LogPath));

        var result = await service.DeleteOrderAsync(orderId);

        Assert.True(result.IsSuccess);
        Assert.Equal(WpfDeleteOrderResultKind.Deleted, result.Kind);
        Assert.Null(harness.Store.GetOrder(orderId));
    }

    private sealed class TempSettingsScope : IDisposable
    {
        private readonly string _dir;

        public TempSettingsScope(Uri baseAddress, bool useServerDeleteOrder)
        {
            _dir = Path.Combine(Path.GetTempPath(), "FlowStock.Server.Tests", "DeleteOrderWpf", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            SettingsPath = Path.Combine(_dir, "settings.json");
            LogPath = Path.Combine(_dir, "app.log");

            var settings = new BackupSettings
            {
                Server = new ServerSettings
                {
                    UseServerDeleteOrder = useServerDeleteOrder,
                    BaseUrl = baseAddress.ToString().TrimEnd('/'),
                    CloseTimeoutSeconds = 10,
                    AllowInvalidTls = false
                }
            };

            new SettingsService(SettingsPath).Save(settings);
        }

        public string SettingsPath { get; }

        public string LogPath { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dir))
                {
                    Directory.Delete(_dir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
