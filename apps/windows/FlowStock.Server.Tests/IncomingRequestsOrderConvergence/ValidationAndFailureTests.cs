using FlowStock.App;
using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.IncomingRequestsOrderConvergence.Infrastructure;

namespace FlowStock.Server.Tests.IncomingRequestsOrderConvergence;

[Collection("IncomingRequestsOrderConvergence")]
public sealed class ValidationAndFailureTests
{
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
