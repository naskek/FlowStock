using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowStock.Server.Tests.CloseDocument.Infrastructure;

internal sealed class CloseDocumentHttpHost : IAsyncDisposable
{
    internal const string WpfAdminApiKey = "test-wpf-admin-key-at-least-32-chars";

    private readonly WebApplication _app;

    private CloseDocumentHttpHost(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    public HttpClient Client { get; }

    public static async Task<CloseDocumentHttpHost> StartAsync(
        CloseDocumentHarness harness,
        InMemoryApiDocStore apiStore,
        Action<ILoggingBuilder>? configureLogging = null,
        PcWebIdentity? pcIdentity = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(CloseDocumentEndpoint).Assembly.FullName,
            EnvironmentName = Environments.Production
        });

        builder.WebHost.UseUrls("http://127.0.0.1:0");
        configureLogging?.Invoke(builder.Logging);

        builder.Services.AddSingleton(harness.Store);
        builder.Services.AddSingleton<IApiDocStore>(apiStore);
        builder.Services.AddSingleton<DocumentService>();
        builder.Services.AddSingleton<OutboundPickingService>();
        builder.Services.AddSingleton<MarkingExcelService>();
        builder.Services.AddSingleton<ProductionPalletService>();
        builder.Services.AddSingleton(new WpfMachineAuthorization(WpfAdminApiKey));
        builder.Services.AddSingleton<IPcWebSessionResolver>(new TestPcWebSessionResolver(pcIdentity));
        builder.Services.AddSingleton<PartnerRoleResolver>();

        var app = builder.Build();
        OrderCreateEndpoint.Map(app);
        OrderUpdateEndpoint.Map(app);
        OrderLinesEndpoint.Map(app);
        OrderDeleteEndpoint.Map(app);
        OrderStatusEndpoint.Map(app);
        OrderRequestManagementEndpoint.Map(app);
        OrderPartialOutboundPermissionEndpoint.Map(app);
        OrderMarkingExportEndpoint.Map(app);
        ProductionNeedCreateOrdersEndpoint.Map(app);
        NewLedgerTransitionEndpoints.Map(app);
        MarkingCreateFromProductionNeedsEndpoint.Map(app);
        DocumentDraftEndpoints.Map(app);
        CloseDocumentEndpoint.Map(app);
        ProductionPalletEndpoints.Map(app);
        OpsEndpoint.Map(app);
        WarehouseProductionStateEndpoint.Map(app);
        TsdOutboundPickingEndpoints.Map(app);
        OrderStatusDiagnosticsEndpoint.Map(app);
        OverShippedOrderDiagnosticsEndpoint.Map(app);
        ProductionPlanConsistencyDiagnosticsEndpoint.Map(app);
        await app.StartAsync();

        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>();

        var address = addresses?.Addresses.SingleOrDefault();
        if (string.IsNullOrWhiteSpace(address))
        {
            await app.StopAsync();
            await app.DisposeAsync();
            throw new InvalidOperationException("HTTP test host did not expose a listening address.");
        }

        var client = new HttpClient
        {
            BaseAddress = new Uri(address, UriKind.Absolute)
        };
        client.DefaultRequestHeaders.Add(WpfMachineAuthorization.KeyHeader, WpfAdminApiKey);

        return new CloseDocumentHttpHost(app, client);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal sealed class TestPcWebSessionResolver(PcWebIdentity? identity) : IPcWebSessionResolver
{
    public PcWebIdentity? Resolve(HttpRequest request) => identity;
}

internal sealed record CapturedLogEntry(
    string Category,
    LogLevel Level,
    string Message,
    IReadOnlyDictionary<string, object?> Properties);

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<CapturedLogEntry> _entries = new();
    private readonly object _sync = new();

    public IReadOnlyList<CapturedLogEntry> Entries
    {
        get
        {
            lock (_sync)
            {
                return _entries.ToArray();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class CaptureLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            lock (owner._sync)
            {
                owner._entries.Add(new CapturedLogEntry(
                    category,
                    logLevel,
                    formatter(state, exception),
                    properties));
            }
        }
    }
}

internal sealed class ThrowingLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ThrowingLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class ThrowingLogger(string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (string.Equals(categoryName, "FlowStock.Server.OrderOperations", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Injected logger provider failure.");
            }
        }
    }
}
