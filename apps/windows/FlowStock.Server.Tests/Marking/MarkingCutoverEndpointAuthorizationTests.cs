using System.Net;
using System.Net.Http.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models.Marking;
using FlowStock.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace FlowStock.Server.Tests.Marking;

public sealed class MarkingCutoverEndpointAuthorizationTests
{
    private const string WpfKey = "marking-cutover-test-key-at-least-32-chars";

    [Fact]
    public async Task Enforce_WithoutTrustedIdentity_ReturnsUnauthorizedAndDoesNotMutate()
    {
        var store = CreateStore();
        await using var host = await Host.StartAsync(store, pcIdentity: null);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/admin/marking/cutover/enforce",
            new { preflight_hash = "abc", approved_by = "spoofed" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        store.Verify(value => value.EnforceMarkingCutover(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task Enforce_PcOperator_ReturnsForbiddenAndDoesNotMutate()
    {
        var store = CreateStore();
        await using var host = await Host.StartAsync(
            store,
            new PcWebIdentity(1, "PC-1", "operator", "PC", PcAccessRole.Operator));

        using var response = await host.Client.PostAsJsonAsync(
            "/api/admin/marking/cutover/enforce",
            new { preflight_hash = "abc" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        store.Verify(value => value.EnforceMarkingCutover(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task Enforce_WpfAdmin_DerivesAuditActorAndIgnoresCallerApprovedBy()
    {
        var store = CreateStore();
        await using var host = await Host.StartAsync(store, pcIdentity: null);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/marking/cutover/enforce")
        {
            Content = JsonContent.Create(new { preflight_hash = "ABC", approved_by = "spoofed" })
        };
        request.Headers.Add(WpfMachineAuthorization.KeyHeader, WpfKey);
        request.Headers.Add(WpfMachineAuthorization.AuditActorHeader, "maintenance-operator");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        store.Verify(value => value.EnforceMarkingCutover(
            "abc", "WPF:maintenance-operator", It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task Enforce_PcAdmin_DerivesActorFromSession()
    {
        var store = CreateStore();
        await using var host = await Host.StartAsync(
            store,
            new PcWebIdentity(2, "PC-2", "admin", "PC", PcAccessRole.Admin));

        using var response = await host.Client.PostAsJsonAsync(
            "/api/admin/marking/cutover/enforce",
            new { preflight_hash = "ABC", approved_by = "spoofed" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        store.Verify(value => value.EnforceMarkingCutover(
            "abc", "PC:admin", It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task LineApproval_WpfAdmin_DerivesActorAndDoesNotAcceptCallerActor()
    {
        var store = CreateStore();
        var approvals = CreateApprovalStore();
        approvals.Setup(value => value.ApproveMarkingCutoverLine(
                42,
                300,
                "H1",
                "WPF:maintenance-operator",
                It.IsAny<DateTime>()))
            .Returns(new MarkingCutoverLineApprovalResult(7, 42, 300, "h1", "h2", false));
        await using var host = await Host.StartAsync(store, pcIdentity: null, approvals);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/marking/cutover/line-approvals")
        {
            Content = JsonContent.Create(new
            {
                order_line_id = 42,
                allowed_synthetic_qty = 300,
                preflight_hash = "H1",
                actor = "spoofed"
            })
        };
        request.Headers.Add(WpfMachineAuthorization.KeyHeader, WpfKey);
        request.Headers.Add(WpfMachineAuthorization.AuditActorHeader, "maintenance-operator");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        approvals.VerifyAll();
    }

    private static Mock<IMarkingCutoverPreflightStore> CreateStore()
    {
        var store = new Mock<IMarkingCutoverPreflightStore>(MockBehavior.Strict);
        store.Setup(value => value.EnforceMarkingCutover(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()));
        return store;
    }

    private static Mock<IMarkingCutoverApprovalStore> CreateApprovalStore()
        => new(MockBehavior.Strict);

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private Host(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<Host> StartAsync(
            Mock<IMarkingCutoverPreflightStore> store,
            PcWebIdentity? pcIdentity,
            Mock<IMarkingCutoverApprovalStore>? approvals = null)
        {
            var sessions = new Mock<IPcWebSessionResolver>(MockBehavior.Strict);
            sessions.Setup(value => value.Resolve(It.IsAny<HttpRequest>())).Returns(pcIdentity);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(store.Object);
            builder.Services.AddSingleton((approvals ?? CreateApprovalStore()).Object);
            builder.Services.AddSingleton(new WpfMachineAuthorization(WpfKey));
            builder.Services.AddSingleton(sessions.Object);
            var app = builder.Build();
            MarkingCutoverEndpoints.Map(app);
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new Host(app, new HttpClient { BaseAddress = new Uri(address) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }
}
