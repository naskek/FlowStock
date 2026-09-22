using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FlowStock.Server.Telegram;

namespace FlowStock.Server.Tests.Telegram;

public sealed class TelegramBotClientTests
{
    [Fact]
    public async Task SendsOneExactStaticMessage()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new TelegramBotClient("test-token", "test-chat", handler);

        await client.SendNewOrderRequestAsync(CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.telegram.org/bottest-token/sendMessage", request.Uri);
        Assert.Equal("application/json", request.MediaType);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("test-chat", body.RootElement.GetProperty("chat_id").GetString());
        Assert.Equal(
            "Новый заказ. Требуется подтверждение в FlowStock.",
            body.RootElement.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpErrorsEndAfterOneAttemptWithoutThrowing(HttpStatusCode statusCode)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(statusCode));
        using var client = new TelegramBotClient("test-token", "test-chat", handler);

        await client.SendNewOrderRequestAsync(CancellationToken.None);

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutAndTransportFailureEndAfterOneAttemptWithoutThrowing(bool timeout)
    {
        var handler = new RecordingHandler(_ => throw (timeout
            ? new TaskCanceledException("private-timeout")
            : new HttpRequestException("private-transport-error")));
        using var client = new TelegramBotClient("test-token", "test-chat", handler);

        await client.SendNewOrderRequestAsync(CancellationToken.None);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ResponseIsDisposedWithoutReadingItsBody()
    {
        var content = new TrackingContent();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        });
        using var client = new TelegramBotClient("test-token", "test-chat", handler);

        await client.SendNewOrderRequestAsync(CancellationToken.None);

        Assert.True(content.Disposed);
        Assert.False(content.ReadAttempted);
    }

    [Fact]
    public void ProductionHandlerUsesExplicitNonBypassingSocksProxy()
    {
        var proxyUri = new Uri("socks5://tailscale-egress:1055");
        using var handler = TelegramBotClient.CreateProxyHandler(proxyUri);

        Assert.True(handler.UseProxy);
        var proxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.False(proxy.BypassProxyOnLocal);
        Assert.Equal(proxyUri, proxy.GetProxy(new Uri("https://api.telegram.org")));
        Assert.Equal(TimeSpan.FromSeconds(5), TelegramBotClient.RequestTimeout);
    }

    [Fact]
    public async Task UnavailableProxyDoesNotFallBackToDirectConnection()
    {
        var endpoints = new List<DnsEndPoint>();
        var handler = TelegramBotClient.CreateProxyHandler(new Uri("socks5://tailscale-egress:1055"));
        handler.ConnectCallback = (context, _) =>
        {
            endpoints.Add(context.DnsEndPoint);
            return ValueTask.FromException<Stream>(new SocketException((int)SocketError.HostUnreachable));
        };
        using var client = new TelegramBotClient("test-token", "test-chat", handler);

        await client.SendNewOrderRequestAsync(CancellationToken.None);

        var endpoint = Assert.Single(endpoints);
        Assert.Equal("tailscale-egress", endpoint.Host);
        Assert.Equal(1055, endpoint.Port);
        Assert.DoesNotContain(endpoints, candidate =>
            string.Equals(candidate.Host, "api.telegram.org", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string? MediaType, string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

        internal RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = response;
        }

        internal List<RecordedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri?.AbsoluteUri ?? string.Empty,
                request.Content?.Headers.ContentType?.MediaType,
                request.Content == null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken)));
            return _response(request);
        }
    }

    private sealed class TrackingContent : HttpContent
    {
        internal bool Disposed { get; private set; }

        internal bool ReadAttempted { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            ReadAttempted = true;
            throw new InvalidOperationException("Response body must not be read.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
