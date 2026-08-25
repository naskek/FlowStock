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
using Npgsql;

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

    [Fact]
    public async Task LegacyTaskRetirementApply_WpfAdmin_DerivesActorAndIgnoresCallerActor()
    {
        var store = CreateStore();
        var retirements = new Mock<IMarkingLegacyTaskRetirementStore>(MockBehavior.Strict);
        var taskId = Guid.Parse("bc65a644-5d31-4099-a00b-eb43e963aab2");
        retirements.Setup(value => value.Apply(
                454,
                taskId,
                "h1",
                "eligibility",
                "operation-1",
                "WPF:maintenance-operator",
                It.IsAny<DateTime>()))
            .Returns(AppliedRetirementResult(454, taskId));
        await using var host = await Host.StartAsync(
            store,
            pcIdentity: null,
            retirements: retirements);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/admin/marking/cutover/legacy-task-retirements/apply")
        {
            Content = JsonContent.Create(new
            {
                order_line_id = 454,
                marking_order_id = taskId,
                preflight_hash = "H1",
                eligibility_hash = "eligibility",
                idempotency_key = "operation-1",
                confirm = "APPLY",
                actor = "spoofed"
            })
        };
        request.Headers.Add(WpfMachineAuthorization.KeyHeader, WpfKey);
        request.Headers.Add(WpfMachineAuthorization.AuditActorHeader, "maintenance-operator");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        retirements.VerifyAll();
    }

    [Fact]
    public async Task LegacyTaskRetirementApply_WithoutTrustedIdentity_IsUnauthorizedAndDoesNotMutate()
    {
        var store = CreateStore();
        var retirements = new Mock<IMarkingLegacyTaskRetirementStore>(MockBehavior.Strict);
        await using var host = await Host.StartAsync(store, pcIdentity: null, retirements: retirements);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/admin/marking/cutover/legacy-task-retirements/apply",
            new
            {
                order_line_id = 454,
                marking_order_id = Guid.Parse("bc65a644-5d31-4099-a00b-eb43e963aab2"),
                preflight_hash = "h1",
                eligibility_hash = "eligibility",
                idempotency_key = "operation-1",
                confirm = "APPLY"
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        retirements.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("/api/admin/marking/cutover/legacy-task-retirements/dry-run")]
    [InlineData("/api/admin/marking/cutover/legacy-task-retirements/apply")]
    public async Task LegacyTaskRetirement_NonPositiveOrderLineId_ReturnsBadRequestWithoutCallingStore(
        string endpoint)
    {
        var store = CreateStore();
        var retirements = new Mock<IMarkingLegacyTaskRetirementStore>(MockBehavior.Strict);
        await using var host = await Host.StartAsync(store, pcIdentity: null, retirements: retirements);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                order_line_id = 0,
                marking_order_id = Guid.Parse("bc65a644-5d31-4099-a00b-eb43e963aab2"),
                preflight_hash = "h1",
                eligibility_hash = "eligibility",
                idempotency_key = "operation-invalid-line",
                confirm = "APPLY"
            })
        };
        request.Headers.Add(WpfMachineAuthorization.KeyHeader, WpfKey);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        retirements.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("line")]
    [InlineData("subject")]
    [InlineData("retirement")]
    [InlineData("enforce")]
    public async Task CutoverMutation_SerializationFailure_ReturnsStableConflict(string operation)
    {
        var store = CreateStore();
        var approvals = CreateApprovalStore();
        var retirements = new Mock<IMarkingLegacyTaskRetirementStore>(MockBehavior.Strict);
        const string errorCode = "MARKING_CUTOVER_SERIALIZATION_CONFLICT";
        var serializationFailure = new PostgresException(
            "could not serialize access due to read/write dependencies among transactions",
            "ERROR",
            "ERROR",
            PostgresErrorCodes.SerializationFailure);
        var taskId = Guid.Parse("bc65a644-5d31-4099-a00b-eb43e963aab2");

        string endpoint;
        object body;
        switch (operation)
        {
            case "line":
                approvals.Setup(value => value.ApproveMarkingCutoverLine(
                        It.IsAny<long>(), It.IsAny<int?>(), It.IsAny<string>(),
                        It.IsAny<string>(), It.IsAny<DateTime>()))
                    .Throws(serializationFailure);
                endpoint = "/api/admin/marking/cutover/line-approvals";
                body = new { order_line_id = 42, allowed_synthetic_qty = 5, preflight_hash = "h1" };
                break;
            case "subject":
                approvals.Setup(value => value.ApproveMarkingCutoverSubjects(
                        It.IsAny<long>(), It.IsAny<IReadOnlyList<MarkingCutoverSubjectApprovalIntent>>(),
                        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()))
                    .Throws(serializationFailure);
                endpoint = "/api/admin/marking/cutover/subject-approvals";
                body = new
                {
                    allowlist_id = 7,
                    subjects = new[] { new { marking_subject_id = Guid.NewGuid(), approved_quantity = 5 } },
                    preflight_hash = "h2"
                };
                break;
            case "retirement":
                retirements.Setup(value => value.Apply(
                        It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
                        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()))
                    .Throws(serializationFailure);
                endpoint = "/api/admin/marking/cutover/legacy-task-retirements/apply";
                body = new
                {
                    order_line_id = 454,
                    marking_order_id = taskId,
                    preflight_hash = "h1",
                    eligibility_hash = "eligibility",
                    idempotency_key = "operation-serialization",
                    confirm = "APPLY"
                };
                break;
            default:
                store.Setup(value => value.EnforceMarkingCutover(
                        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()))
                    .Throws(serializationFailure);
                endpoint = "/api/admin/marking/cutover/enforce";
                body = new { preflight_hash = "h2" };
                break;
        }

        await using var host = await Host.StartAsync(
            store,
            pcIdentity: null,
            approvals,
            retirements);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add(WpfMachineAuthorization.KeyHeader, WpfKey);

        using var response = await host.Client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(errorCode, payload, StringComparison.Ordinal);
    }

    private static Mock<IMarkingCutoverPreflightStore> CreateStore()
    {
        var store = new Mock<IMarkingCutoverPreflightStore>(MockBehavior.Strict);
        store.Setup(value => value.EnforceMarkingCutover(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()));
        return store;
    }

    private static MarkingLegacyTaskRetirementResult AppliedRetirementResult(
        long orderLineId,
        Guid markingOrderId) =>
        new(
            "apply", true, true, [], orderLineId, markingOrderId,
            0, 0, 0, 0, 0, 0, "h1", "h2", "eligibility", "SINGLE_TASK", false);

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
            Mock<IMarkingCutoverApprovalStore>? approvals = null,
            Mock<IMarkingLegacyTaskRetirementStore>? retirements = null)
        {
            var sessions = new Mock<IPcWebSessionResolver>(MockBehavior.Strict);
            sessions.Setup(value => value.Resolve(It.IsAny<HttpRequest>())).Returns(pcIdentity);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(store.Object);
            builder.Services.AddSingleton((approvals ?? CreateApprovalStore()).Object);
            builder.Services.AddSingleton((retirements
                ?? new Mock<IMarkingLegacyTaskRetirementStore>(MockBehavior.Strict)).Object);
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
