using FlowStock.Core.Abstractions;
using FlowStock.Core.Services;
using FlowStock.Core.Services.Warehouse;
using FlowStock.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowStock.Server.Tests.WarehouseTasks.Infrastructure;

internal sealed class WarehouseTaskHttpHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private WarehouseTaskHttpHost(WebApplication app, HttpClient client, WarehouseBundleServiceHarness harness)
    {
        _app = app;
        Client = client;
        Harness = harness;
    }

    public HttpClient Client { get; }

    public WarehouseBundleServiceHarness Harness { get; }

    public static async Task<WarehouseTaskHttpHost> StartAsync(WarehouseBundleServiceHarness harness)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(PlannerEndpoints).Assembly.FullName,
            EnvironmentName = Environments.Production
        });

        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddSingleton(harness.Store);
        builder.Services.AddSingleton<WarehouseActionBundleService>();
        builder.Services.AddSingleton<WarehouseTaskExecutionService>();
        builder.Services.AddSingleton<DocumentService>();

        var app = builder.Build();
        PlannerEndpoints.Map(app);
        WarehouseTaskEndpoints.Map(app);

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

        return new WarehouseTaskHttpHost(app, client, harness);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
