using FlowStock.App;
using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.CreateOrder.Infrastructure;

namespace FlowStock.Server.Tests.CreateOrder;

[Collection("CreateOrder")]
public sealed class WpfCompatibilityTests
{
    [Fact]
    public async Task WpfCreateOrder_FeatureFlagRoutesToCanonicalPostApiOrders()
    {
        var (harness, apiStore) = CreateOrderHttpScenario.CreateCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var temp = new TempSettingsScope(host.Client.BaseAddress!, useServerCreateOrder: true);
        var service = new WpfCreateOrderService(new SettingsService(temp.SettingsPath), new FileLogger(temp.LogPath));

        var result = await service.CreateOrderAsync(
            new WpfCreateOrderContext(
                "001",
                OrderType.Customer,
                200,
                new DateTime(2026, 3, 15),
                OrderStatus.Draft,
                "Через WPF bridge",
                new[]
                {
                    new OrderLineView { ItemId = 1001, ItemName = "Горчица", QtyOrdered = 12 }
                }));

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Response);
        Assert.Equal(WpfCreateOrderResultKind.Created, result.Kind);
        Assert.Equal(1, harness.OrderCount);
        Assert.Equal(1, harness.TotalOrderLineCount);

        var order = harness.GetOrder(result.Response!.OrderId);
        Assert.Equal("001", order.OrderRef);
        Assert.Equal(OrderType.Customer, order.Type);
        Assert.Equal(OrderStatus.InProgress, order.Status);
        Assert.Equal(200, order.PartnerId);
    }

    [Fact]
    public async Task WpfCreateOrder_AcceptsServerAssignedOrderRef()
    {
        var (harness, apiStore) = CreateOrderHttpScenario.CreateOrderRefCollisionScenario("001");
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var temp = new TempSettingsScope(host.Client.BaseAddress!, useServerCreateOrder: true);
        var service = new WpfCreateOrderService(new SettingsService(temp.SettingsPath), new FileLogger(temp.LogPath));

        var result = await service.CreateOrderAsync(
            new WpfCreateOrderContext(
                "001",
                OrderType.Customer,
                200,
                null,
                OrderStatus.Accepted,
                null,
                new[]
                {
                    new OrderLineView { ItemId = 1002, ItemName = "Кетчуп", QtyOrdered = 5 }
                }));

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Response);
        Assert.True(result.Response!.OrderRefChanged);
        Assert.False(string.IsNullOrWhiteSpace(result.Response.OrderRef));
        Assert.NotEqual("001", result.Response.OrderRef);
        Assert.Contains("Сервер назначил номер заказа", result.Message);

        var order = harness.GetOrder(result.Response.OrderId);
        Assert.Equal(result.Response.OrderRef, order.OrderRef);
    }

    [Fact]
    public async Task WpfCreateOrder_IgnoresLegacyFlagAndStillUsesCanonicalApi()
    {
        var (harness, apiStore) = CreateOrderHttpScenario.CreateCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var temp = new TempSettingsScope(host.Client.BaseAddress!, useServerCreateOrder: false);
        var service = new WpfCreateOrderService(new SettingsService(temp.SettingsPath), new FileLogger(temp.LogPath));

        var result = await service.CreateOrderAsync(
            new WpfCreateOrderContext(
                "001",
                OrderType.Customer,
                200,
                null,
                OrderStatus.Draft,
                null,
                new[]
                {
                    new OrderLineView { ItemId = 1001, ItemName = "Горчица", QtyOrdered = 1 }
                }));

        Assert.True(result.IsSuccess);
        Assert.Equal(WpfCreateOrderResultKind.Created, result.Kind);
        Assert.Equal(1, harness.OrderCount);
        Assert.Equal(1, harness.TotalOrderLineCount);
    }

    private sealed class TempSettingsScope : IDisposable
    {
        private readonly string _dir;

        public TempSettingsScope(Uri baseAddress, bool useServerCreateOrder)
        {
            _dir = Path.Combine(Path.GetTempPath(), "FlowStock.Server.Tests", "CreateOrderWpf", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            SettingsPath = Path.Combine(_dir, "settings.json");
            LogPath = Path.Combine(_dir, "app.log");

            var settings = new BackupSettings
            {
                Server = new ServerSettings
                {
                    UseServerCreateOrder = useServerCreateOrder,
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
