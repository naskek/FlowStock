using FlowStock.Core.Abstractions;
using FlowStock.Core.Services;
using FlowStock.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowStock.Server.Tests.CloseDocument.Infrastructure;

internal sealed class CloseDocumentHttpHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private CloseDocumentHttpHost(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    public HttpClient Client { get; }

    public static async Task<CloseDocumentHttpHost> StartAsync(CloseDocumentHarness harness, InMemoryApiDocStore apiStore)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(CloseDocumentEndpoint).Assembly.FullName,
            EnvironmentName = Environments.Production
        });

        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddSingleton(harness.Store);
        builder.Services.AddSingleton<IApiDocStore>(apiStore);
        builder.Services.AddSingleton<DocumentService>();

        var app = builder.Build();
        DocumentDraftEndpoints.Map(app);
        CloseDocumentEndpoint.Map(app);
        OpsEndpoint.Map(app);

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

        return new CloseDocumentHttpHost(app, client);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
