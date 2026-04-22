using FlowStock.App;
using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.IncomingRequestsOrderConvergence.Infrastructure;

namespace FlowStock.Server.Tests.IncomingRequestsOrderConvergence;

[Collection("IncomingRequestsOrderConvergence")]
public sealed class ApprovalFlowTests
{
    [Fact]
    public async Task ApproveCreateOrderRequest_RoutesToCanonicalPostApiOrders_AndMarksRequestApproved()
    {
        var (harness, apiStore, request) = IncomingRequestsOrderConvergenceScenario.CreateCreateOrderApprovalScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var temp = new TempSettingsScope(host.Client.BaseAddress!, useServerIncomingRequestOrderApproval: true);
        var settingsService = new SettingsService(temp.SettingsPath);
        var logger = new FileLogger(temp.LogPath);
        var requestsApi = new WpfIncomingRequestsApiService(settingsService, logger);
        var service = new IncomingRequestOrderApiBridgeService(settingsService, logger, requestsApi);

        var result = await service.ApproveAsync(request, "wpf-operator");

        Assert.True(result.IsSuccess);
        Assert.Equal(IncomingRequestOrderApprovalResultKind.Approved, result.Kind);
        Assert.Equal(1, harness.OrderCount);
        Assert.Equal(2, harness.TotalOrderLineCount);

        var createdOrderId = Assert.IsType<long>(result.AppliedOrderId);
        var order = harness.GetOrder(createdOrderId);
        Assert.Equal(OrderStatus.Accepted, order.Status);
        Assert.Equal(200, order.PartnerId);

        var storedRequest = harness.GetOrderRequest(request.Id);
        Assert.NotNull(storedRequest);
        Assert.Equal(OrderRequestStatus.Approved, storedRequest!.Status);
        Assert.Equal(createdOrderId, storedRequest.AppliedOrderId);
        Assert.Contains("Создан заказ ID=", storedRequest.ResolutionNote);
    }

    [Fact]
    public async Task ApproveSetOrderStatusRequest_RoutesToCanonicalPostApiOrdersStatus_AndMarksRequestApproved()
    {
        var (harness, apiStore, request, orderId) = IncomingRequestsOrderConvergenceScenario.CreateSetStatusApprovalScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var temp = new TempSettingsScope(host.Client.BaseAddress!, useServerIncomingRequestOrderApproval: true);
        var settingsService = new SettingsService(temp.SettingsPath);
        var logger = new FileLogger(temp.LogPath);
        var requestsApi = new WpfIncomingRequestsApiService(settingsService, logger);
        var service = new IncomingRequestOrderApiBridgeService(settingsService, logger, requestsApi);

        var result = await service.ApproveAsync(request, "wpf-operator");

        Assert.True(result.IsSuccess);
        Assert.Equal(IncomingRequestOrderApprovalResultKind.Approved, result.Kind);
        Assert.Equal(OrderStatus.Accepted, harness.GetOrder(orderId).Status);

        var storedRequest = harness.GetOrderRequest(request.Id);
        Assert.NotNull(storedRequest);
        Assert.Equal(OrderRequestStatus.Approved, storedRequest!.Status);
        Assert.Equal(orderId, storedRequest.AppliedOrderId);
        Assert.Contains("Статус изменен", storedRequest.ResolutionNote);
    }

    private sealed class TempSettingsScope : IDisposable
    {
        private readonly string _dir;

        public TempSettingsScope(Uri baseAddress, bool useServerIncomingRequestOrderApproval)
        {
            _dir = Path.Combine(Path.GetTempPath(), "FlowStock.Server.Tests", "IncomingRequestsOrderConvergence", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            SettingsPath = Path.Combine(_dir, "settings.json");
            LogPath = Path.Combine(_dir, "app.log");

            var settings = new BackupSettings
            {
                Server = new ServerSettings
                {
                    UseServerIncomingRequestOrderApproval = useServerIncomingRequestOrderApproval,
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
