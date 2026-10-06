using System.Net;
using System.Net.Http;
using System.Text;
using FlowStock.App;

namespace FlowStock.Server.Tests.Wpf;

public sealed class WpfCatalogAsyncReadTests
{
    [Theory]
    [InlineData("uoms", "/api/uoms")]
    [InlineData("reasons", "/api/write-off-reasons")]
    [InlineData("tara", "/api/taras")]
    [InlineData("types", "/api/item-types?include_inactive=1")]
    [InlineData("vat", "/api/vat-rates?include_inactive=true")]
    public async Task Slow_HTTP_does_not_capture_UI_context_and_keeps_read_contract(string catalog, string expectedPath)
    {
        var dir = Path.Combine(Path.GetTempPath(), "FlowStock-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var handler = new DelayedHandler(entered, release);
            var settings = new SettingsService(Path.Combine(dir, "settings.json"));
            settings.Save(new BackupSettings
            {
                Server = new ServerSettings { ServerBaseUrl = "http://127.0.0.1:7154", WpfAdminApiKey = "test-key" }
            });
            var service = new WpfCatalogApiService(settings,
                new FileLogger(Path.Combine(dir, "test.log")), () => handler);
            var ui = new RecordingContext();
            var previous = SynchronizationContext.Current;
            Task<(bool Success, int Count, string Name)> read;
            try
            {
                SynchronizationContext.SetSynchronizationContext(ui);
                read = Read();
                Assert.False(read.IsCompleted);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            try
            {
                await entered.Task.WaitAsync(cancellation.Token);
                Assert.False(read.IsCompleted);
                Assert.Equal(expectedPath, handler.Path);
                Assert.Equal("GET", handler.Method);
                Assert.Equal(1, handler.CallCount);
            }
            finally
            {
                release.TrySetResult();
            }
            var result = await read.WaitAsync(cancellation.Token);
            Assert.True(result.Success);
            Assert.Equal(1, result.Count);
            Assert.Equal("Тест", result.Name);
            Assert.Equal(0, ui.Posts);

            async Task<(bool, int, string)> Read()
            {
                switch (catalog)
                {
                    case "uoms":
                        var uoms = await service.TryGetUomsAsync(cancellation.Token).ConfigureAwait(false);
                        return (uoms.IsSuccess, uoms.Value.Count, uoms.Value[0].Name);
                    case "reasons":
                        var reasons = await service.TryGetWriteOffReasonsAsync(cancellation.Token).ConfigureAwait(false);
                        return (reasons.IsSuccess, reasons.Value.Count, reasons.Value[0].Name);
                    case "tara":
                        var tara = await service.TryGetTarasAsync(cancellation.Token).ConfigureAwait(false);
                        return (tara.IsSuccess, tara.Value.Count, tara.Value[0].Name);
                    case "types":
                        var types = await service.TryGetItemTypesAsync(true, cancellation.Token).ConfigureAwait(false);
                        return (types.IsSuccess, types.Value.Count, types.Value[0].Name);
                    default:
                        var vat = await service.TryGetVatRatesAsync(true, cancellation.Token).ConfigureAwait(false);
                        return (vat.IsSuccess, vat.Value.Count, vat.Value[0].Name);
                }
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref Posts);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

    private sealed class DelayedHandler(TaskCompletionSource entered, TaskCompletionSource release) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Method { get; private set; }
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Path = request.RequestUri!.PathAndQuery;
            Method = request.Method.Method;
            CallCount++;
            entered.SetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"id":1,"name":"Тест","code":"T","is_active":true,"rate":20}]""",
                    Encoding.UTF8, "application/json")
            };
        }
    }
}
