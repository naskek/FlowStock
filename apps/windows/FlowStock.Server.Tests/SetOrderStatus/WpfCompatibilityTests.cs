using FlowStock.App;
using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.SetOrderStatus.Infrastructure;

namespace FlowStock.Server.Tests.SetOrderStatus;

[Collection("SetOrderStatus")]
public sealed class WpfCompatibilityTests
{
    [Fact]
    public async Task WpfSetOrderStatus_FeatureFlagReportsManualStatusDisabled()
    {
        var (harness, apiStore, orderId) = SetOrderStatusHttpScenario.CreateDraftCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var temp = new TempSettingsScope(host.Client.BaseAddress!, useServerSetOrderStatus: true);
        var service = new WpfSetOrderStatusService(new SettingsService(temp.SettingsPath), new FileLogger(temp.LogPath));

        var result = await service.SetStatusAsync(orderId, OrderStatus.Accepted);

        Assert.False(result.IsSuccess);
        Assert.Equal(WpfSetOrderStatusResultKind.ValidationFailed, result.Kind);
        Assert.Equal(OrderStatus.Draft, harness.GetOrder(orderId).Status);
    }

    [Fact]
    public async Task WpfSetOrderStatus_ForbiddenTransitionReturnsValidationError()
    {
        var (harness, apiStore, orderId) = SetOrderStatusHttpScenario.CreateShippedCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var temp = new TempSettingsScope(host.Client.BaseAddress!, useServerSetOrderStatus: true);
        var service = new WpfSetOrderStatusService(new SettingsService(temp.SettingsPath), new FileLogger(temp.LogPath));

        var result = await service.SetStatusAsync(orderId, OrderStatus.Accepted);

        Assert.False(result.IsSuccess);
        Assert.Equal(WpfSetOrderStatusResultKind.ValidationFailed, result.Kind);
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(orderId).Status);
    }

    [Fact]
    public async Task WpfSetOrderStatus_IgnoresLegacyFlag_AndStillReportsManualStatusDisabled()
    {
        var (harness, apiStore, orderId) = SetOrderStatusHttpScenario.CreateDraftCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var temp = new TempSettingsScope(host.Client.BaseAddress!, useServerSetOrderStatus: false);
        var service = new WpfSetOrderStatusService(new SettingsService(temp.SettingsPath), new FileLogger(temp.LogPath));

        var result = await service.SetStatusAsync(orderId, OrderStatus.Accepted);

        Assert.False(result.IsSuccess);
        Assert.Equal(WpfSetOrderStatusResultKind.ValidationFailed, result.Kind);
        Assert.Equal(OrderStatus.Draft, harness.GetOrder(orderId).Status);
    }

    private sealed class TempSettingsScope : IDisposable
    {
        private readonly string _dir;

        public TempSettingsScope(Uri baseAddress, bool useServerSetOrderStatus)
        {
            _dir = Path.Combine(Path.GetTempPath(), "FlowStock.Server.Tests", "SetOrderStatusWpf", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            SettingsPath = Path.Combine(_dir, "settings.json");
            LogPath = Path.Combine(_dir, "app.log");

            var settings = new BackupSettings
            {
                Server = new ServerSettings
                {
                    UseServerSetOrderStatus = useServerSetOrderStatus,
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
