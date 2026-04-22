using FlowStock.App;
using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.CreateOrder.Infrastructure;
using FlowStock.Server.Tests.UpdateOrder.Infrastructure;

namespace FlowStock.Server.Tests.UpdateOrder;

[Collection("UpdateOrder")]
public sealed class WpfCompatibilityTests
{
    [Fact]
    public async Task WpfUpdateOrder_FeatureFlagRoutesToCanonicalPutApiOrders()
    {
        var (harness, apiStore, orderId) = UpdateOrderHttpScenario.CreateCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var temp = new TempSettingsScope(host.Client.BaseAddress!, useServerUpdateOrder: true);
        var service = new WpfUpdateOrderService(new SettingsService(temp.SettingsPath), new FileLogger(temp.LogPath));

        var result = await service.UpdateOrderAsync(
            new WpfUpdateOrderContext(
                orderId,
                "002",
                OrderType.Customer,
                202,
                new DateTime(2026, 3, 30),
                OrderStatus.InProgress,
                "WPF update bridge",
                new[]
                {
                    new OrderLineView { ItemId = 1002, ItemName = "Кетчуп", QtyOrdered = 9 }
                }));

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Response);
        Assert.Equal(WpfUpdateOrderResultKind.Updated, result.Kind);

        var order = harness.GetOrder(orderId);
        Assert.Equal("002", order.OrderRef);
        Assert.Equal(202, order.PartnerId);
        Assert.Equal(OrderStatus.InProgress, order.Status);
        Assert.Equal(new DateTime(2026, 3, 30), order.DueDate);
    }

    [Fact]
    public async Task WpfUpdateOrder_AcceptsServerAssignedOrderRef()
    {
        var (harness, apiStore, orderId) = UpdateOrderHttpScenario.CreateOrderRefCollisionScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var temp = new TempSettingsScope(host.Client.BaseAddress!, useServerUpdateOrder: true);
        var service = new WpfUpdateOrderService(new SettingsService(temp.SettingsPath), new FileLogger(temp.LogPath));

        var result = await service.UpdateOrderAsync(
            new WpfUpdateOrderContext(
                orderId,
                "777",
                OrderType.Customer,
                200,
                null,
                OrderStatus.Accepted,
                null,
                new[]
                {
                    new OrderLineView { ItemId = 1001, ItemName = "Горчица", QtyOrdered = 10 }
                }));

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Response);
        Assert.True(result.Response!.OrderRefChanged);
        Assert.False(string.IsNullOrWhiteSpace(result.Response.OrderRef));
        Assert.NotEqual("777", result.Response.OrderRef);
        Assert.Contains("Сервер заменил номер заказа", result.Message);
    }

    [Fact]
    public async Task WpfUpdateOrder_IgnoresLegacyFlagAndStillUsesCanonicalApi()
    {
        var (harness, apiStore, orderId) = UpdateOrderHttpScenario.CreateCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var temp = new TempSettingsScope(host.Client.BaseAddress!, useServerUpdateOrder: false);
        var service = new WpfUpdateOrderService(new SettingsService(temp.SettingsPath), new FileLogger(temp.LogPath));

        var result = await service.UpdateOrderAsync(
            new WpfUpdateOrderContext(
                orderId,
                "002",
                OrderType.Customer,
                200,
                null,
                OrderStatus.Accepted,
                null,
                new[]
                {
                    new OrderLineView { ItemId = 1001, ItemName = "Горчица", QtyOrdered = 10 }
                }));

        Assert.True(result.IsSuccess);
        Assert.Equal(WpfUpdateOrderResultKind.Updated, result.Kind);
        Assert.Equal("002", harness.GetOrder(orderId).OrderRef);
    }

    private sealed class TempSettingsScope : IDisposable
    {
        private readonly string _dir;

        public TempSettingsScope(Uri baseAddress, bool useServerUpdateOrder)
        {
            _dir = Path.Combine(Path.GetTempPath(), "FlowStock.Server.Tests", "UpdateOrderWpf", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            SettingsPath = Path.Combine(_dir, "settings.json");
            LogPath = Path.Combine(_dir, "app.log");

            var settings = new BackupSettings
            {
                Server = new ServerSettings
                {
                    UseServerUpdateOrder = useServerUpdateOrder,
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
