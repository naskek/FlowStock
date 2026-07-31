using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using Microsoft.Extensions.Logging;

namespace FlowStock.Server.Tests.UpdateOrder;

public sealed class PartialOutboundPermissionTests
{
    [Theory]
    [InlineData(OrderType.Internal, OrderStatus.InProgress, false)]
    [InlineData(OrderType.Customer, OrderStatus.Draft, false)]
    [InlineData(OrderType.Customer, OrderStatus.InProgress, true)]
    [InlineData(OrderType.Customer, OrderStatus.Accepted, true)]
    [InlineData(OrderType.Customer, OrderStatus.Shipped, false)]
    [InlineData(OrderType.Customer, OrderStatus.Cancelled, false)]
    [InlineData(OrderType.Customer, OrderStatus.Merged, false)]
    public void EffectivePermission_IsFailClosedOutsideActiveCustomer(
        OrderType type,
        OrderStatus status,
        bool expected)
    {
        var order = new Order
        {
            Type = type,
            Status = status,
            AllowPartialOutbound = true
        };

        Assert.Equal(expected, order.EffectiveAllowPartialOutbound);
    }

    [Fact]
    public void CustomerToInternal_ResetsPersistedPermission_AndReverseTransitionDoesNotRestoreIt()
    {
        var harness = CreateHarness(OrderType.Customer, OrderStatus.InProgress);
        harness.SeedPartner(new Partner { Id = 7, Code = "C-7", Name = "Клиент" });
        harness.Store.UpdateOrderPartialOutboundPermission(42, true);
        var service = new OrderService(harness.Store);

        UpdateOrder(service, OrderType.Internal, partnerId: null);

        Assert.Equal(OrderType.Internal, harness.GetOrder(42).Type);
        Assert.False(harness.GetOrder(42).AllowPartialOutbound);

        UpdateOrder(service, OrderType.Customer, partnerId: 7);

        Assert.Equal(OrderType.Customer, harness.GetOrder(42).Type);
        Assert.False(harness.GetOrder(42).AllowPartialOutbound);
    }

    [Fact]
    public void OrdinaryCustomerSave_PreservesPersistedPermission()
    {
        var harness = CreateHarness(OrderType.Customer, OrderStatus.InProgress);
        harness.SeedPartner(new Partner { Id = 7, Code = "C-7", Name = "Клиент" });
        harness.Store.UpdateOrderPartialOutboundPermission(42, true);

        UpdateOrder(new OrderService(harness.Store), OrderType.Customer, partnerId: 7);

        Assert.True(harness.GetOrder(42).AllowPartialOutbound);
    }

    [Fact]
    public void LegacyInternalPermission_IsNotReactivatedByCustomerTransition()
    {
        var harness = CreateHarness(OrderType.Internal, OrderStatus.InProgress);
        harness.SeedPartner(new Partner { Id = 7, Code = "C-7", Name = "Клиент" });
        harness.Store.UpdateOrderPartialOutboundPermission(42, true);

        UpdateOrder(new OrderService(harness.Store), OrderType.Customer, partnerId: 7);

        Assert.Equal(OrderType.Customer, harness.GetOrder(42).Type);
        Assert.False(harness.GetOrder(42).AllowPartialOutbound);
        Assert.False(harness.GetOrder(42).EffectiveAllowPartialOutbound);
    }

    [Fact]
    public void CustomerToInternal_UpdateFailure_RollsBackTypeAndPermission()
    {
        var harness = CreateHarness(OrderType.Customer, OrderStatus.InProgress);
        harness.Store.UpdateOrderPartialOutboundPermission(42, true);
        harness.FailNextUpdateOrder();

        Assert.Throws<InvalidOperationException>(() =>
            UpdateOrder(new OrderService(harness.Store), OrderType.Internal, partnerId: null));

        Assert.Equal(OrderType.Customer, harness.GetOrder(42).Type);
        Assert.True(harness.GetOrder(42).AllowPartialOutbound);
    }

    [Fact]
    public async Task Command_EnablesAndDisablesPermission_Idempotently()
    {
        var harness = CreateHarness(OrderType.Customer, OrderStatus.InProgress);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var enabled = await host.Client.PutAsJsonAsync(
            "/api/orders/42/partial-outbound-permission",
            new { allow_partial_outbound = true, device_id = "WPF-TEST" });
        var first = await ReadJson(enabled);

        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.True(first.GetProperty("ok").GetBoolean());
        Assert.True(first.GetProperty("changed").GetBoolean());
        Assert.True(harness.GetOrder(42).AllowPartialOutbound);

        using var repeated = await host.Client.PutAsJsonAsync(
            "/api/orders/42/partial-outbound-permission",
            new { allow_partial_outbound = true });
        var second = await ReadJson(repeated);
        Assert.False(second.GetProperty("changed").GetBoolean());
        Assert.Equal("UNCHANGED", second.GetProperty("result").GetString());

        using var disabled = await host.Client.PutAsJsonAsync(
            "/api/orders/42/partial-outbound-permission",
            new { allow_partial_outbound = false });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.False(harness.GetOrder(42).AllowPartialOutbound);
    }

    [Theory]
    [InlineData(OrderType.Internal, OrderStatus.InProgress, "ORDER_PARTIAL_OUTBOUND_NOT_CUSTOMER")]
    [InlineData(OrderType.Customer, OrderStatus.Draft, "ORDER_PARTIAL_OUTBOUND_NOT_ACTIVE")]
    [InlineData(OrderType.Customer, OrderStatus.Shipped, "ORDER_PARTIAL_OUTBOUND_TERMINAL")]
    public async Task Command_RejectsUnsupportedOrder(
        OrderType type,
        OrderStatus status,
        string expectedError)
    {
        var harness = CreateHarness(type, status);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var response = await host.Client.PutAsJsonAsync(
            "/api/orders/42/partial-outbound-permission",
            new { allow_partial_outbound = true });
        var payload = await ReadJson(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedError, payload.GetProperty("error").GetString());
        Assert.False(harness.GetOrder(42).EffectiveAllowPartialOutbound);
    }

    [Fact]
    public async Task Command_ReturnsStableWireAndNotFoundErrors()
    {
        var harness = CreateHarness(OrderType.Customer, OrderStatus.InProgress);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var malformed = await host.Client.PutAsync(
            "/api/orders/42/partial-outbound-permission",
            new StringContent("{"));
        Assert.Equal("INVALID_JSON", (await ReadJson(malformed)).GetProperty("error").GetString());

        using var missing = await host.Client.PutAsJsonAsync(
            "/api/orders/42/partial-outbound-permission",
            new { device_id = "WPF-TEST" });
        Assert.Equal("MISSING_ALLOW_PARTIAL_OUTBOUND", (await ReadJson(missing)).GetProperty("error").GetString());

        using var notFound = await host.Client.PutAsJsonAsync(
            "/api/orders/999/partial-outbound-permission",
            new { allow_partial_outbound = true });
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal("ORDER_NOT_FOUND", (await ReadJson(notFound)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task StructuredLogger_CapturesAttemptSuccessDomainFailureAndNotFound()
    {
        var successHarness = CreateHarness(OrderType.Customer, OrderStatus.InProgress);
        using var successLogs = new CapturingLoggerProvider();
        await using (var successHost = await CloseDocumentHttpHost.StartAsync(
                         successHarness,
                         new InMemoryApiDocStore(),
                         logging => logging.AddProvider(successLogs)))
        {
            using var success = await successHost.Client.PutAsJsonAsync(
                "/api/orders/42/partial-outbound-permission",
                new { allow_partial_outbound = true, device_id = "UNTRUSTED-DEVICE" });
            Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        }

        var successEntries = PermissionEntries(successLogs);
        Assert.Collection(
            successEntries,
            attempt =>
            {
                Assert.Equal("ATTEMPT", attempt.Properties["Phase"]);
                Assert.Equal("PENDING", attempt.Properties["Result"]);
                Assert.Equal(true, attempt.Properties["RequestedValue"]);
                Assert.Equal("UNTRUSTED-DEVICE", attempt.Properties["DeviceId"]);
                Assert.Null(attempt.Properties["ActorId"]);
            },
            result =>
            {
                Assert.Equal("RESULT", result.Properties["Phase"]);
                Assert.Equal("SUCCESS", result.Properties["Result"]);
                Assert.Equal("SO-042", result.Properties["OrderRef"]);
                Assert.Equal(false, result.Properties["OldValue"]);
                Assert.Equal(true, result.Properties["ResultingValue"]);
                Assert.Equal(true, result.Properties["Changed"]);
            });

        var failureHarness = CreateHarness(OrderType.Customer, OrderStatus.Shipped);
        using var failureLogs = new CapturingLoggerProvider();
        await using (var failureHost = await CloseDocumentHttpHost.StartAsync(
                         failureHarness,
                         new InMemoryApiDocStore(),
                         logging => logging.AddProvider(failureLogs)))
        {
            using var domainFailure = await failureHost.Client.PutAsJsonAsync(
                "/api/orders/42/partial-outbound-permission",
                new { allow_partial_outbound = true });
            Assert.Equal(HttpStatusCode.BadRequest, domainFailure.StatusCode);

            using var notFound = await failureHost.Client.PutAsJsonAsync(
                "/api/orders/999/partial-outbound-permission",
                new { allow_partial_outbound = true });
            Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        }

        var resultEntries = PermissionEntries(failureLogs)
            .Where(entry => Equals(entry.Properties["Phase"], "RESULT"))
            .ToArray();
        var terminal = Assert.Single(resultEntries, entry => Equals(entry.Properties["OrderId"], 42L));
        Assert.Equal("ORDER_PARTIAL_OUTBOUND_TERMINAL", terminal.Properties["ErrorCode"]);
        Assert.Equal("SO-042", terminal.Properties["OrderRef"]);
        var notFoundResult = Assert.Single(resultEntries, entry => Equals(entry.Properties["OrderId"], 999L));
        Assert.Equal("ORDER_NOT_FOUND", notFoundResult.Properties["ErrorCode"]);
        Assert.Null(notFoundResult.Properties["OrderRef"]);
    }

    [Fact]
    public async Task StructuredLoggerFailure_DoesNotChangeBusinessResult()
    {
        var harness = CreateHarness(OrderType.Customer, OrderStatus.InProgress);
        using var throwingLogs = new ThrowingLoggerProvider();
        await using var host = await CloseDocumentHttpHost.StartAsync(
            harness,
            new InMemoryApiDocStore(),
            logging => logging.AddProvider(throwingLogs));

        using var response = await host.Client.PutAsJsonAsync(
            "/api/orders/42/partial-outbound-permission",
            new { allow_partial_outbound = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(harness.GetOrder(42).AllowPartialOutbound);
    }

    [Fact]
    public void MigrationAndSqlMappings_EnforceTerminalResetWithoutGenericLostUpdate()
    {
        var root = FindRepoRoot();
        var migration = File.ReadAllText(Path.Combine(
            root,
            "deploy",
            "postgres",
            "migrations",
            "V0031__customer_partial_outbound_permission.sql"));
        var store = File.ReadAllText(Path.Combine(
            root,
            "apps",
            "windows",
            "FlowStock.Data",
            "PostgresDataStore.cs"));
        var logging = File.ReadAllText(Path.Combine(
            root,
            "apps",
            "windows",
            "FlowStock.Server",
            "ServerOperationLogging.cs"));
        var wpfUpdate = File.ReadAllText(Path.Combine(
            root,
            "apps",
            "windows",
            "FlowStock.App",
            "Services",
            "WpfUpdateOrderService.cs"));
        var wpfWindow = File.ReadAllText(Path.Combine(
            root,
            "apps",
            "windows",
            "FlowStock.App",
            "OrderDetailsWindow.xaml.cs"));

        Assert.Contains("ck_orders_terminal_partial_outbound_false", migration, StringComparison.Ordinal);
        Assert.Contains("status NOT IN ('SHIPPED', 'CANCELLED', 'MERGED')", migration, StringComparison.Ordinal);
        Assert.Contains("WHEN @status IN (@shipped_status, @cancelled_status, @merged_status) THEN FALSE", store, StringComparison.Ordinal);

        var genericStart = store.IndexOf("public void UpdateOrder(Order order)", StringComparison.Ordinal);
        var genericEnd = store.IndexOf("public void UpdateOrderStatus", genericStart, StringComparison.Ordinal);
        var genericUpdate = store[genericStart..genericEnd];
        Assert.DoesNotContain("allow_partial_outbound", genericUpdate, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("allow_partial_outbound", wpfUpdate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER_PARTIAL_OUTBOUND_PERMISSION_CHANGE", logging, StringComparison.Ordinal);
        Assert.Contains("phase={Phase}", logging, StringComparison.Ordinal);
        Assert.Contains("actor_id={ActorId}", logging, StringComparison.Ordinal);
        Assert.Contains("DateTimeOffset.UtcNow", logging, StringComparison.Ordinal);

        var handlerStart = wpfWindow.IndexOf("private async void AllowPartialOutboundCheckBox_Click", StringComparison.Ordinal);
        var handlerEnd = wpfWindow.IndexOf("private void BeginLoad", handlerStart, StringComparison.Ordinal);
        var handler = wpfWindow[handlerStart..handlerEnd];
        Assert.DoesNotContain("LoadOrder(", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("OrderRefBox", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("PartnerCombo", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("DueDatePicker", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("CommentBox", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("_lines", handler, StringComparison.Ordinal);
        Assert.Contains("_partialOutboundPermissionInFlight", handler, StringComparison.Ordinal);
        Assert.Contains("_suppressPartialOutboundPermissionChange", wpfWindow, StringComparison.Ordinal);
        Assert.True(
            handler.IndexOf("TryGetCanonicalState", StringComparison.Ordinal)
            < handler.IndexOf("if (!result.IsSuccess)", StringComparison.Ordinal));
    }

    [Fact]
    public void TerminalWriter_ResetsPermission_AndTransactionRollbackRestoresBothValues()
    {
        var harness = CreateHarness(OrderType.Customer, OrderStatus.InProgress);
        harness.Store.UpdateOrderPartialOutboundPermission(42, true);

        Assert.Throws<InvalidOperationException>(() => harness.Store.ExecuteInTransaction(store =>
        {
            store.UpdateOrderStatus(42, OrderStatus.Shipped);
            throw new InvalidOperationException("rollback");
        }));

        Assert.Equal(OrderStatus.InProgress, harness.GetOrder(42).Status);
        Assert.True(harness.GetOrder(42).AllowPartialOutbound);

        harness.Store.UpdateOrderStatus(42, OrderStatus.Shipped);
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(42).Status);
        Assert.False(harness.GetOrder(42).AllowPartialOutbound);
    }

    [Theory]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Merged)]
    public void EveryTerminalStatus_ResetsPermission_AndRepeatedTransitionKeepsFalse(OrderStatus terminalStatus)
    {
        var type = terminalStatus == OrderStatus.Merged ? OrderType.Internal : OrderType.Customer;
        var harness = CreateHarness(type, OrderStatus.InProgress);
        harness.Store.UpdateOrderPartialOutboundPermission(42, true);

        harness.Store.UpdateOrderStatus(42, terminalStatus);
        harness.Store.UpdateOrderStatus(42, terminalStatus);

        Assert.Equal(terminalStatus, harness.GetOrder(42).Status);
        Assert.False(harness.GetOrder(42).AllowPartialOutbound);
    }

    private static CloseDocumentHarness CreateHarness(OrderType type, OrderStatus status)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedOrder(new Order
        {
            Id = 42,
            OrderRef = "SO-042",
            Type = type,
            Status = status,
            CreatedAt = DateTime.UtcNow
        });
        return harness;
    }

    private static void UpdateOrder(OrderService service, OrderType type, long? partnerId)
    {
        service.UpdateOrder(
            42,
            "SO-042",
            partnerId,
            dueDate: null,
            comment: "updated",
            lines: Array.Empty<OrderLineView>(),
            type: type);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static CapturedLogEntry[] PermissionEntries(CapturingLoggerProvider provider) =>
        provider.Entries
            .Where(entry => string.Equals(
                entry.Properties.GetValueOrDefault("OperationCode")?.ToString(),
                "ORDER_PARTIAL_OUTBOUND_PERMISSION_CHANGE",
                StringComparison.Ordinal))
            .ToArray();

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "AGENTS.md")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Корень репозитория не найден.");
    }
}
