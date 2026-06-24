using System.Net;
using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace FlowStock.Server.Tests.OrderControl;

public sealed class OrderControlEndpointTests
{
    [Theory]
    [InlineData("/api/order-control/tasks")]
    [InlineData("/api/order-control/tasks?activeOnly=false")]
    public async Task ListTasks_AllowsMissingOrFalseActiveOnly(string path)
    {
        await using var host = await OrderControlHost.StartAsync(CreateStore());

        using var response = await host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, json.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task ListTasks_ActiveOnlyTrueReturnsOnlyActiveTasks()
    {
        await using var host = await OrderControlHost.StartAsync(CreateStore());

        using var response = await host.Client.GetAsync("/api/order-control/tasks?activeOnly=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal("CTRL-2026-000001", row.GetProperty("task_ref").GetString());
    }

    private static IDataStore CreateStore()
    {
        var rows = new[]
        {
            new OrderControlTaskSummary
            {
                Task = new OrderControlTask
                {
                    Id = 1,
                    TaskRef = "CTRL-2026-000001",
                    Status = OrderControlTaskStatus.New,
                    CreatedAt = DateTime.UtcNow,
                    ExpectedHuCount = 2,
                    CheckedHuCount = 1
                },
                Orders =
                [
                    new OrderControlTaskOrder
                    {
                        TaskId = 1,
                        OrderId = 10,
                        OrderRef = "080",
                        PartnerName = "Client",
                        IsActive = true
                    }
                ]
            },
            new OrderControlTaskSummary
            {
                Task = new OrderControlTask
                {
                    Id = 2,
                    TaskRef = "CTRL-2026-000002",
                    Status = OrderControlTaskStatus.Completed,
                    CreatedAt = DateTime.UtcNow,
                    ExpectedHuCount = 1,
                    CheckedHuCount = 1
                },
                Orders =
                [
                    new OrderControlTaskOrder
                    {
                        TaskId = 2,
                        OrderId = 11,
                        OrderRef = "081",
                        PartnerName = "Client",
                        IsActive = false
                    }
                ]
            }
        };

        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.GetOrderControlTasks(It.IsAny<string?>(), It.IsAny<bool>()))
            .Returns<string?, bool>((_, activeOnly) => activeOnly
                ? rows.Where(row => OrderControlTaskStatus.IsActive(row.Task.Status)).ToArray()
                : rows);
        return store.Object;
    }

    private sealed class OrderControlHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private OrderControlHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<OrderControlHost> StartAsync(IDataStore store)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(OrderControlEndpoints).Assembly.FullName,
                EnvironmentName = Environments.Production
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(store);
            builder.Services.AddSingleton<OrderControlService>();
            var app = builder.Build();
            OrderControlEndpoints.Map(app);
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single();
            return new OrderControlHost(app, new HttpClient { BaseAddress = new Uri(address!, UriKind.Absolute) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
