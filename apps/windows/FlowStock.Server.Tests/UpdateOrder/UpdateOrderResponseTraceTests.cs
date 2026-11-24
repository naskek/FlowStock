using System.Collections.Concurrent;
using FlowStock.App;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FlowStock.Server.Tests.UpdateOrder;

public sealed class UpdateOrderResponseTraceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedPut_DelayedBody_IsCorrelatedAndRemainsCancellableWithoutRetry(bool cancelAfterHeaders)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var headersReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var traces = new ConcurrentQueue<string>();
        var calls = 0;
        var persistedQty = 9000;
        string? serverRequestId = null;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var api = builder.Build();
        api.MapPut("/api/orders/420", async (HttpContext context) =>
        {
            Interlocked.Increment(ref calls);
            serverRequestId = context.Request.Headers["X-FlowStock-Request-Id"].ToString();
            persistedQty = 3600; // simulate commit before response delivery
            context.Response.ContentType = "application/json";
            await context.Response.StartAsync();
            await context.Response.Body.FlushAsync();
            await release.Task;
            await context.Response.WriteAsync("{\"ok\":true,\"order_id\":420,\"order_ref\":\"422\",\"status\":\"IN_PROGRESS\"}");
        });
        await api.StartAsync();
        var url = api.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var cancellation = new CancellationTokenSource();
        var client = new UpdateOrderApiClient(line =>
        {
            traces.Enqueue(line);
            if (line.Contains("phase=headers")) headersReceived.TrySetResult();
        });
        try
        {
            var call = client.UpdateAsync(new ServerCloseClientOptions { BaseUrl = url }, 420,
                new UpdateOrderApiRequest { OrderRef = "422" }, cancellation.Token);
            await headersReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(call.IsCompleted);
            Assert.Equal(3600, persistedQty);
            if (cancelAfterHeaders)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);
                Assert.DoesNotContain(traces, x => x.Contains("phase=body"));
                Assert.Contains(traces, x => x.Contains("phase=finished") && x.Contains("cancelled=True"));
            }
            else
            {
                release.TrySetResult();
                var response = await call.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(response.Response!.Ok);
                Assert.Contains(traces, x => x.Contains("phase=body"));
            }
            Assert.True(Guid.TryParseExact(serverRequestId, "N", out _));
            Assert.All(traces, x => Assert.Contains($"request_id={serverRequestId}", x));
            Assert.All(traces, x => Assert.DoesNotContain("order_ref", x));
            Assert.Equal(1, calls);
        }
        finally
        {
            release.TrySetResult();
            await api.StopAsync();
        }
    }
}
