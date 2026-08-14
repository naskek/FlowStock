using FlowStock.App;
using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.IncomingRequestsOrderConvergence.Infrastructure;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;

namespace FlowStock.Server.Tests.IncomingRequestsOrderConvergence;

[Collection("IncomingRequestsOrderConvergence")]
public sealed class ApprovalFlowTests
{
    [Fact]
    public async Task PcAdmin_CanConfirmPendingRequest_WithoutWpfCredential()
    {
        var (harness, apiStore, request) = IncomingRequestsOrderConvergenceScenario.CreateCreateOrderApprovalScenario();
        var identity = new PcWebIdentity(42, "PC-42", "admin", "PC", PcAccessRole.Admin);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore, pcIdentity: identity);
        host.Client.DefaultRequestHeaders.Remove(WpfMachineAuthorization.KeyHeader);
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/requests/{request.Id}/confirm");

        using var response = await host.Client.SendAsync(message);

        response.EnsureSuccessStatusCode();
        var storedRequest = Assert.IsType<OrderRequest>(harness.GetOrderRequest(request.Id));
        Assert.Equal(OrderRequestStatus.Approved, storedRequest.Status);
        Assert.Equal("PC:admin", storedRequest.ResolvedBy);
        Assert.Equal(1, harness.OrderCount);
    }

    [Fact]
    public async Task PcAdmin_CanRejectPendingRequest_WithoutWpfCredential()
    {
        var (harness, apiStore, request) = IncomingRequestsOrderConvergenceScenario.CreateCreateOrderApprovalScenario();
        var identity = new PcWebIdentity(42, "PC-42", "admin", "PC", PcAccessRole.Admin);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore, pcIdentity: identity);
        host.Client.DefaultRequestHeaders.Remove(WpfMachineAuthorization.KeyHeader);
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/requests/{request.Id}/reject");

        using var response = await host.Client.SendAsync(message);

        response.EnsureSuccessStatusCode();
        var storedRequest = Assert.IsType<OrderRequest>(harness.GetOrderRequest(request.Id));
        Assert.Equal(OrderRequestStatus.Rejected, storedRequest.Status);
        Assert.Equal("PC:admin", storedRequest.ResolvedBy);
        Assert.Null(storedRequest.AppliedOrderId);
        Assert.Equal(0, harness.OrderCount);
    }

    [Fact]
    public async Task PcOperator_CannotConfirmPendingRequest()
    {
        var (harness, apiStore, request) = IncomingRequestsOrderConvergenceScenario.CreateCreateOrderApprovalScenario();
        var identity = new PcWebIdentity(43, "PC-43", "operator", "PC", PcAccessRole.Operator);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore, pcIdentity: identity);
        host.Client.DefaultRequestHeaders.Remove(WpfMachineAuthorization.KeyHeader);
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/requests/{request.Id}/confirm");

        using var response = await host.Client.SendAsync(message);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(OrderRequestStatus.Pending, harness.GetOrderRequest(request.Id)!.Status);
        Assert.Equal(0, harness.OrderCount);
    }

    [Fact]
    public async Task PcOperator_CannotRejectPendingRequest()
    {
        var (harness, apiStore, request) = IncomingRequestsOrderConvergenceScenario.CreateCreateOrderApprovalScenario();
        var identity = new PcWebIdentity(43, "PC-43", "operator", "PC", PcAccessRole.Operator);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore, pcIdentity: identity);
        host.Client.DefaultRequestHeaders.Remove(WpfMachineAuthorization.KeyHeader);
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/requests/{request.Id}/reject");

        using var response = await host.Client.SendAsync(message);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(OrderRequestStatus.Pending, harness.GetOrderRequest(request.Id)!.Status);
        Assert.Equal(0, harness.OrderCount);
    }

    [Theory]
    [InlineData("confirm", null)]
    [InlineData("confirm", "wrong-wpf-key")]
    [InlineData("reject", null)]
    [InlineData("reject", "wrong-wpf-key")]
    public async Task MissingOrInvalidWpfKey_WithoutPcSession_ReturnsUnauthorized(
        string action,
        string? suppliedKey)
    {
        var (harness, apiStore, request) = IncomingRequestsOrderConvergenceScenario.CreateCreateOrderApprovalScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        host.Client.DefaultRequestHeaders.Remove(WpfMachineAuthorization.KeyHeader);
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/orders/requests/{request.Id}/{action}");
        if (suppliedKey != null)
        {
            message.Headers.Add(WpfMachineAuthorization.KeyHeader, suppliedKey);
        }

        using var response = await host.Client.SendAsync(message);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(OrderRequestStatus.Pending, harness.GetOrderRequest(request.Id)!.Status);
        Assert.Equal(0, harness.OrderCount);
    }

    [Fact]
    public async Task SecondConfirmation_ReturnsConflict_AndDoesNotCreateSecondOrder()
    {
        var (harness, apiStore, request) = IncomingRequestsOrderConvergenceScenario.CreateCreateOrderApprovalScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var first = await host.Client.PostAsync($"/api/orders/requests/{request.Id}/confirm", null);
        using var second = await host.Client.PostAsync($"/api/orders/requests/{request.Id}/confirm", null);

        first.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, harness.OrderCount);
    }

    [Fact]
    public async Task TrustedWpf_RejectsRequest_WithoutCanonicalMutation()
    {
        var (harness, apiStore, request) = IncomingRequestsOrderConvergenceScenario.CreateCreateOrderApprovalScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsync($"/api/orders/requests/{request.Id}/reject", null);

        response.EnsureSuccessStatusCode();
        var stored = Assert.IsType<OrderRequest>(harness.GetOrderRequest(request.Id));
        Assert.Equal(OrderRequestStatus.Rejected, stored.Status);
        Assert.Null(stored.AppliedOrderId);
        Assert.Equal(0, harness.OrderCount);
    }

    [Fact]
    public async Task TrustedWpf_ConfirmsSetOrderStatusRequest_UsingCanonicalCancellation()
    {
        var (harness, apiStore, original, orderId) = IncomingRequestsOrderConvergenceScenario.CreateSetStatusApprovalScenario();
        var request = new OrderRequest
        {
            Id = original.Id,
            RequestType = OrderRequestType.SetOrderStatus,
            PayloadJson = JsonSerializer.Serialize(new { order_id = orderId, status = "CANCELLED" }),
            Status = OrderRequestStatus.Pending,
            CreatedAt = original.CreatedAt
        };
        harness.SeedOrderRequest(request);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsync($"/api/orders/requests/{request.Id}/confirm", null);

        response.EnsureSuccessStatusCode();
        Assert.Equal(OrderStatus.Cancelled, harness.GetOrder(orderId).Status);
        Assert.Equal(OrderRequestStatus.Approved, harness.GetOrderRequest(request.Id)!.Status);
        Assert.Equal(orderId, harness.GetOrderRequest(request.Id)!.AppliedOrderId);
    }

    [Fact]
    public async Task TrustedWpf_ConfirmsCreateOrderRequest_Atomically()
    {
        var (harness, apiStore, request) = IncomingRequestsOrderConvergenceScenario.CreateCreateOrderApprovalScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/orders/requests/{request.Id}/confirm");
        message.Headers.Add("X-FlowStock-WPF-Audit-Actor", "wpf-operator");
        message.Content = JsonContent.Create(new { });

        using var response = await host.Client.SendAsync(message);

        response.EnsureSuccessStatusCode();
        Assert.Equal(1, harness.OrderCount);
        Assert.Equal(2, harness.TotalOrderLineCount);

        var storedRequest = Assert.IsType<OrderRequest>(harness.GetOrderRequest(request.Id));
        var createdOrderId = Assert.IsType<long>(storedRequest.AppliedOrderId);
        var order = harness.GetOrder(createdOrderId);
        Assert.Equal(OrderStatus.InProgress, order.Status);
        Assert.Equal(200, order.PartnerId);

        Assert.Equal(OrderRequestStatus.Approved, storedRequest.Status);
        Assert.Equal(createdOrderId, storedRequest.AppliedOrderId);
        Assert.Equal("WPF:wpf-operator", storedRequest.ResolvedBy);
        Assert.Contains("Создан заказ ID=", storedRequest.ResolutionNote);
    }

    [Fact]
    public async Task TrustedWpf_ConfirmsInternalCreateOrderWithSameDispatcher()
    {
        var (harness, apiStore, original) = IncomingRequestsOrderConvergenceScenario.CreateCreateOrderApprovalScenario();
        var request = new OrderRequest
        {
            Id = original.Id,
            RequestType = OrderRequestType.CreateOrder,
            PayloadJson = JsonSerializer.Serialize(new
            {
                order_ref = "IR-INTERNAL-001",
                order_type = "INTERNAL",
                lines = new[] { new { item_id = 1001, qty_ordered = 5d, production_purpose = "INTERNAL_STOCK" } }
            }),
            Status = OrderRequestStatus.Pending,
            CreatedAt = original.CreatedAt
        };
        harness.SeedOrderRequest(request);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsync($"/api/orders/requests/{request.Id}/confirm", null);

        response.EnsureSuccessStatusCode();
        var stored = Assert.IsType<OrderRequest>(harness.GetOrderRequest(request.Id));
        var order = harness.GetOrder(Assert.IsType<long>(stored.AppliedOrderId));
        Assert.Equal(OrderType.Internal, order.Type);
        Assert.Equal(OrderStatus.InProgress, order.Status);
        Assert.Null(order.PartnerId);
    }

    [Fact]
    public async Task ApproveSetOrderStatusRequest_Fails_WhenManualStatusSwitchIsDisabled()
    {
        var (harness, apiStore, request, orderId) = IncomingRequestsOrderConvergenceScenario.CreateSetStatusApprovalScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var temp = new TempSettingsScope(host.Client.BaseAddress!, useServerIncomingRequestOrderApproval: true);
        var settingsService = new SettingsService(temp.SettingsPath);
        var logger = new FileLogger(temp.LogPath);
        var requestsApi = new WpfIncomingRequestsApiService(settingsService, logger);
        var service = new IncomingRequestOrderApiBridgeService(settingsService, logger, requestsApi);

        var result = await service.ApproveAsync(request, "wpf-operator");

        Assert.False(result.IsSuccess);
        Assert.Equal(OrderStatus.Draft, harness.GetOrder(orderId).Status);

        var storedRequest = harness.GetOrderRequest(request.Id);
        Assert.NotNull(storedRequest);
        Assert.Equal(OrderRequestStatus.Pending, storedRequest!.Status);
        Assert.Null(storedRequest.AppliedOrderId);
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
