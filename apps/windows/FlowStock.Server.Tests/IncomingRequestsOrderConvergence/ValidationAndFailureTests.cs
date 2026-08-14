using FlowStock.App;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.IncomingRequestsOrderConvergence.Infrastructure;
using System.Net.Http.Json;

namespace FlowStock.Server.Tests.IncomingRequestsOrderConvergence;

[Collection("IncomingRequestsOrderConvergence")]
public sealed class ValidationAndFailureTests
{
    [Fact]
    public async Task LegacyResolveRoute_IsClosed()
    {
        var (harness, apiStore, request) = IncomingRequestsOrderConvergenceScenario.CreateCreateOrderApprovalScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync(
            $"/api/orders/requests/{request.Id}/resolve",
            new { status = "APPROVED", applied_order_id = 123 });

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(OrderRequestStatus.Pending, harness.GetOrderRequest(request.Id)!.Status);
        Assert.Equal(0, harness.OrderCount);
    }

    [Fact]
    public async Task UnsupportedRequestType_ReturnsUnprocessableEntity_AndRemainsPending()
    {
        var (harness, apiStore, request) = IncomingRequestsOrderConvergenceScenario.CreateCreateOrderApprovalScenario();
        var unsupported = new OrderRequest
        {
            Id = request.Id,
            RequestType = "UNKNOWN_ORDER_ACTION",
            PayloadJson = "{}",
            Status = OrderRequestStatus.Pending,
            CreatedAt = request.CreatedAt
        };
        harness.SeedOrderRequest(unsupported);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsync($"/api/orders/requests/{unsupported.Id}/confirm", null);

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(OrderRequestStatus.Pending, harness.GetOrderRequest(unsupported.Id)!.Status);
        Assert.Equal(0, harness.OrderCount);
    }

    [Fact]
    public async Task UnsupportedRequestType_RejectAlsoReturnsUnprocessableEntity_AndRemainsPending()
    {
        var (harness, apiStore, request) = IncomingRequestsOrderConvergenceScenario.CreateCreateOrderApprovalScenario();
        var unsupported = new OrderRequest
        {
            Id = request.Id,
            RequestType = "UNKNOWN_ORDER_ACTION",
            PayloadJson = "{}",
            Status = OrderRequestStatus.Pending,
            CreatedAt = request.CreatedAt
        };
        harness.SeedOrderRequest(unsupported);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsync($"/api/orders/requests/{unsupported.Id}/reject", null);

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(OrderRequestStatus.Pending, harness.GetOrderRequest(unsupported.Id)!.Status);
        Assert.Equal(0, harness.OrderCount);
    }

    [Fact]
    public async Task CanonicalValidationFailure_DoesNotMarkRequestApproved()
    {
        var (harness, apiStore, request) = IncomingRequestsOrderConvergenceScenario.CreateInvalidCreateOrderApprovalScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var temp = new TempSettingsScope(host.Client.BaseAddress!, useServerIncomingRequestOrderApproval: true);
        var settingsService = new SettingsService(temp.SettingsPath);
        var logger = new FileLogger(temp.LogPath);
        var requestsApi = new WpfIncomingRequestsApiService(settingsService, logger);
        var service = new IncomingRequestOrderApiBridgeService(settingsService, logger, requestsApi);

        var result = await service.ApproveAsync(request, "wpf-operator");

        Assert.False(result.IsSuccess);
        Assert.Equal(IncomingRequestOrderApprovalResultKind.ValidationFailed, result.Kind);
        Assert.Equal(0, harness.OrderCount);
        Assert.Equal(0, harness.TotalOrderLineCount);

        var storedRequest = harness.GetOrderRequest(request.Id);
        Assert.NotNull(storedRequest);
        Assert.Equal(OrderRequestStatus.Pending, storedRequest!.Status);
        Assert.Null(storedRequest.AppliedOrderId);
        Assert.Null(storedRequest.ResolvedAt);
    }

    [Fact]
    public async Task Approval_IgnoresLegacyFlagAndStillUsesCanonicalApi()
    {
        var (harness, apiStore, request) = IncomingRequestsOrderConvergenceScenario.CreateCreateOrderApprovalScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var temp = new TempSettingsScope(host.Client.BaseAddress!, useServerIncomingRequestOrderApproval: false);
        var settingsService = new SettingsService(temp.SettingsPath);
        var logger = new FileLogger(temp.LogPath);
        var requestsApi = new WpfIncomingRequestsApiService(settingsService, logger);
        var service = new IncomingRequestOrderApiBridgeService(settingsService, logger, requestsApi);

        var result = await service.ApproveAsync(request, "wpf-operator");

        Assert.True(result.IsSuccess);
        Assert.Equal(IncomingRequestOrderApprovalResultKind.Approved, result.Kind);
        Assert.Equal(1, harness.OrderCount);

        var storedRequest = harness.GetOrderRequest(request.Id);
        Assert.NotNull(storedRequest);
        Assert.Equal(OrderRequestStatus.Approved, storedRequest!.Status);
        Assert.NotNull(storedRequest.ResolvedAt);
    }

    [Fact]
    public async Task ItemDeactivatedAfterRequestCreation_ApprovalFailsAndRequestRemainsPending()
    {
        var (harness, apiStore, request) = IncomingRequestsOrderConvergenceScenario.CreateCreateOrderApprovalScenario();
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Горчица",
            IsActive = false,
            DefaultSalePriceGross = 100m,
            DefaultSaleVatRateId = 1,
            DefaultSaleVatRate = 22m,
            DefaultSaleVatRateIsActive = true
        });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var temp = new TempSettingsScope(host.Client.BaseAddress!, useServerIncomingRequestOrderApproval: true);
        var settingsService = new SettingsService(temp.SettingsPath);
        var logger = new FileLogger(temp.LogPath);
        var requestsApi = new WpfIncomingRequestsApiService(settingsService, logger);
        var service = new IncomingRequestOrderApiBridgeService(settingsService, logger, requestsApi);

        var result = await service.ApproveAsync(request, "wpf-operator");

        Assert.False(result.IsSuccess);
        Assert.Equal(IncomingRequestOrderApprovalResultKind.ValidationFailed, result.Kind);
        Assert.Equal("Сервер отклонил подтверждение заявки.", result.Message);
        Assert.Equal(0, harness.OrderCount);
        Assert.Equal(0, harness.TotalOrderLineCount);
        var storedRequest = Assert.IsType<OrderRequest>(harness.GetOrderRequest(request.Id));
        Assert.Equal(OrderRequestStatus.Pending, storedRequest.Status);
        Assert.Null(storedRequest.AppliedOrderId);
        Assert.Null(storedRequest.ResolvedAt);
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
                    AllowInvalidTls = false,
                    WpfAdminApiKey = CloseDocumentHttpHost.WpfAdminApiKey
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
