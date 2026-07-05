using System.Text;
using System.Text.Json;
using FlowStock.Server.Discovery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace FlowStock.Server.Tests.Discovery;

public sealed class FlowStockDiscoveryTests
{
    [Fact]
    public void DiscoveryConfigProducesExpectedResponseShape()
    {
        var options = CreateOptions(
            ("FLOWSTOCK_PUBLIC_BASE_URL", "https://flowstock.local:7154"),
            ("FLOWSTOCK_INSTANCE_NAME", "Warehouse A"));

        var response = options.ToResponse();

        Assert.Equal("FlowStock", response.Product);
        Assert.Equal(1, response.DiscoveryProtocolVersion);
        Assert.Equal("Warehouse A", response.InstanceName);
        Assert.Equal("https://flowstock.local:7154", response.CanonicalHttpsBaseUrl);
        Assert.Equal("1.2.3-test", response.ApplicationVersion);
        Assert.False(options.BehindRelay);
    }

    [Fact]
    public void DiscoveryConfigParsesBehindRelayMode()
    {
        var options = CreateOptions(
            ("FLOWSTOCK_PUBLIC_BASE_URL", "https://flowstock.local:7154"),
            ("FLOWSTOCK_INSTANCE_NAME", "Warehouse A"),
            ("FLOWSTOCK_DISCOVERY_BEHIND_RELAY", "1"));

        Assert.True(options.BehindRelay);
    }

    [Fact]
    public void DiscoveryConfigRejectsInvalidBehindRelayMode()
    {
        Assert.Throws<InvalidOperationException>(() => CreateOptions(
            ("FLOWSTOCK_PUBLIC_BASE_URL", "https://flowstock.local:7154"),
            ("FLOWSTOCK_INSTANCE_NAME", "FlowStock"),
            ("FLOWSTOCK_DISCOVERY_BEHIND_RELAY", "true")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://flowstock.local:7154")]
    [InlineData("https://flowstock.local:7154/tsd")]
    [InlineData("https://flowstock.local:7154?x=1")]
    [InlineData("https://flowstock.local:7154#x")]
    [InlineData("https://user:pass@flowstock.local:7154")]
    [InlineData("not a url")]
    public void DiscoveryConfigRejectsInvalidCanonicalUrl(string value)
    {
        Assert.Throws<InvalidOperationException>(() => CreateOptions(
            ("FLOWSTOCK_PUBLIC_BASE_URL", value),
            ("FLOWSTOCK_INSTANCE_NAME", "FlowStock")));
    }

    [Fact]
    public void DiscoveryConfigRequiresPublicHostToMatchTlsNameOrSans()
    {
        Assert.Throws<InvalidOperationException>(() => CreateOptions(
            ("FLOWSTOCK_PUBLIC_BASE_URL", "https://other.local:7154"),
            ("FLOWSTOCK_INSTANCE_NAME", "FlowStock"),
            ("FLOWSTOCK_TLS_SERVER_NAME", "flowstock.local"),
            ("FLOWSTOCK_TLS_SANS", "DNS:flowstock.local")));

        var options = CreateOptions(
            ("FLOWSTOCK_PUBLIC_BASE_URL", "https://flowstock.local:7154"),
            ("FLOWSTOCK_INSTANCE_NAME", "FlowStock"),
            ("FLOWSTOCK_TLS_SERVER_NAME", "server.local"),
            ("FLOWSTOCK_TLS_SANS", "DNS:flowstock.local"));

        Assert.Equal("https://flowstock.local:7154", options.CanonicalHttpsBaseUrl);
    }

    [Fact]
    public void DiscoveryConfigRequiresTlsIdentitySettings()
    {
        Assert.Throws<InvalidOperationException>(() => CreateOptionsWithoutTlsDefaults(
            ("FLOWSTOCK_PUBLIC_BASE_URL", "https://flowstock.local:7154"),
            ("FLOWSTOCK_INSTANCE_NAME", "FlowStock")));
    }

    [Fact]
    public void DiscoveryConfigRejectsMissingInstanceName()
    {
        Assert.Throws<InvalidOperationException>(() => CreateOptions(
            ("FLOWSTOCK_PUBLIC_BASE_URL", "https://flowstock.local:7154"),
            ("FLOWSTOCK_INSTANCE_NAME", "")));
    }

    [Fact]
    public void UdpValidRequestReturnsSameNonceAndCanonicalConfig()
    {
        var options = CreateOptions(
            ("FLOWSTOCK_PUBLIC_BASE_URL", "https://flowstock.local:7154"),
            ("FLOWSTOCK_INSTANCE_NAME", "FlowStock"));
        var packet = Encoding.UTF8.GetBytes("""
            {"product":"FlowStock","discovery_protocol_version":1,"nonce":"0123456789abcdef0123456789abcdef"}
            """);

        var responseBytes = FlowStockDiscoveryUdpService.TryCreateResponse(packet, options);

        Assert.NotNull(responseBytes);
        using var document = JsonDocument.Parse(responseBytes!);
        var root = document.RootElement;
        Assert.Equal("FlowStock", root.GetProperty("product").GetString());
        Assert.Equal(1, root.GetProperty("discovery_protocol_version").GetInt32());
        Assert.Equal("0123456789abcdef0123456789abcdef", root.GetProperty("nonce").GetString());
        Assert.Equal("FlowStock", root.GetProperty("instance_name").GetString());
        Assert.Equal("https://flowstock.local:7154", root.GetProperty("canonical_https_base_url").GetString());
    }

    [Theory]
    [InlineData("""{"product":"Other","discovery_protocol_version":1,"nonce":"abc"}""")]
    [InlineData("""{"product":"FlowStock","discovery_protocol_version":2,"nonce":"0123456789abcdef0123456789abcdef"}""")]
    [InlineData("""{"product":"FlowStock","discovery_protocol_version":1,"nonce":"abc"}""")]
    [InlineData("""{"product":"FlowStock","discovery_protocol_version":1,"nonce":"0123456789ABCDEF0123456789ABCDEF"}""")]
    [InlineData("""{"product":"FlowStock","discovery_protocol_version":1,"nonce":""}""")]
    [InlineData("""{"product":"FlowStock","discovery_protocol_version":1}""")]
    [InlineData("""not-json""")]
    public void UdpInvalidPacketsAreIgnored(string json)
    {
        var options = CreateOptions(
            ("FLOWSTOCK_PUBLIC_BASE_URL", "https://flowstock.local:7154"),
            ("FLOWSTOCK_INSTANCE_NAME", "FlowStock"));

        Assert.Null(FlowStockDiscoveryUdpService.TryCreateResponse(Encoding.UTF8.GetBytes(json), options));
    }

    [Fact]
    public void UdpOversizedPacketIsIgnored()
    {
        var options = CreateOptions(
            ("FLOWSTOCK_PUBLIC_BASE_URL", "https://flowstock.local:7154"),
            ("FLOWSTOCK_INSTANCE_NAME", "FlowStock"));
        var packet = Encoding.UTF8.GetBytes(new string('x', FlowStockDiscoveryOptions.MaxUdpPacketBytes + 1));

        Assert.Null(FlowStockDiscoveryUdpService.TryCreateResponse(packet, options));
    }

    [Fact]
    public void UdpRateLimiterAppliesPerSourceAndBoundsTrackedSources()
    {
        var now = DateTimeOffset.UtcNow;
        var limiter = new FlowStockDiscoveryRateLimiter(
            globalLimitPerWindow: 100,
            perSourceLimitPerWindow: 2,
            maxTrackedSources: 2,
            window: TimeSpan.FromSeconds(10),
            clock: () => now);

        var first = System.Net.IPAddress.Parse("192.168.1.10");
        Assert.True(limiter.Allow(first));
        Assert.True(limiter.Allow(first));
        Assert.False(limiter.Allow(first));

        Assert.True(limiter.Allow(System.Net.IPAddress.Parse("192.168.1.11")));
        Assert.True(limiter.Allow(System.Net.IPAddress.Parse("192.168.1.12")));
        Assert.True(limiter.TrackedSourceCount <= 2);
    }

    [Fact]
    public void UdpRateLimiterNormalModeKeepsPerSourceAndGlobalLimits()
    {
        var limiter = FlowStockDiscoveryRateLimiter.Create(CreateOptions(
            ("FLOWSTOCK_PUBLIC_BASE_URL", "https://flowstock.local:7154"),
            ("FLOWSTOCK_INSTANCE_NAME", "FlowStock")));
        var source = System.Net.IPAddress.Parse("192.168.1.52");

        for (var i = 0; i < FlowStockDiscoveryRateLimiter.NormalPerSourceLimitPerWindow; i++)
        {
            Assert.True(limiter.Allow(source));
        }

        Assert.False(limiter.Allow(source));

        var globalLimiter = FlowStockDiscoveryRateLimiter.Create(CreateOptions(
            ("FLOWSTOCK_PUBLIC_BASE_URL", "https://flowstock.local:7154"),
            ("FLOWSTOCK_INSTANCE_NAME", "FlowStock")));
        for (var i = 1; i <= FlowStockDiscoveryRateLimiter.NormalGlobalLimitPerWindow; i++)
        {
            Assert.True(globalLimiter.Allow(System.Net.IPAddress.Parse($"10.30.0.{i}")));
        }

        Assert.False(globalLimiter.Allow(System.Net.IPAddress.Parse("10.30.1.1")));
    }

    [Fact]
    public void UdpRateLimiterBehindRelayDisablesPerSourceAndKeepsGlobalCeiling()
    {
        var options = CreateOptions(
            ("FLOWSTOCK_PUBLIC_BASE_URL", "https://flowstock.local:7154"),
            ("FLOWSTOCK_INSTANCE_NAME", "FlowStock"),
            ("FLOWSTOCK_DISCOVERY_BEHIND_RELAY", "1"));
        var limiter = FlowStockDiscoveryRateLimiter.Create(options);
        var source = System.Net.IPAddress.Parse("172.18.0.1");

        for (var i = 0; i < FlowStockDiscoveryRateLimiter.BehindRelayGlobalLimitPerWindow; i++)
        {
            Assert.True(limiter.Allow(source));
        }

        Assert.False(limiter.Allow(source));
        Assert.Equal(0, limiter.TrackedSourceCount);
    }

    [Fact]
    public void UdpRateLimiterBehindRelayAcceptsCalculatedBurstWithinOneBackendWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var limiter = new FlowStockDiscoveryRateLimiter(
            globalLimitPerWindow: FlowStockDiscoveryRateLimiter.BehindRelayGlobalLimitPerWindow,
            perSourceLimitPerWindow: null,
            window: FlowStockDiscoveryRateLimiter.DefaultWindow,
            clock: () => now);
        var source = System.Net.IPAddress.Parse("172.18.0.1");

        // Relay fixed windows can contribute 240 LAN requests plus 40 local healthchecks
        // inside one backend window when boundaries do not align.
        for (var i = 0; i < 280; i++)
        {
            Assert.True(limiter.Allow(source));
        }
    }

    [Fact]
    public void UdpRateLimiterBehindRelayRejectsRequestAfterAbsoluteBackendCeiling()
    {
        var now = DateTimeOffset.UtcNow;
        var limiter = new FlowStockDiscoveryRateLimiter(
            globalLimitPerWindow: FlowStockDiscoveryRateLimiter.BehindRelayGlobalLimitPerWindow,
            perSourceLimitPerWindow: null,
            window: FlowStockDiscoveryRateLimiter.DefaultWindow,
            clock: () => now);
        var source = System.Net.IPAddress.Parse("172.18.0.1");

        for (var i = 0; i < 320; i++)
        {
            Assert.True(limiter.Allow(source));
        }

        Assert.False(limiter.Allow(source));
        Assert.Equal(0, limiter.TrackedSourceCount);
    }

    [Fact]
    public async Task DiscoveryEndpointRespondsThroughHttpPipelineWithoutBusinessServices()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(CreateOptions(
            ("FLOWSTOCK_PUBLIC_BASE_URL", "https://flowstock.local:7154"),
            ("FLOWSTOCK_INSTANCE_NAME", "Warehouse A")));
        var app = builder.Build();
        FlowStockDiscoveryEndpoints.Map(app);
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/api/discovery");
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("FlowStock", document.RootElement.GetProperty("product").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("discovery_protocol_version").GetInt32());
        Assert.Equal("Warehouse A", document.RootElement.GetProperty("instance_name").GetString());
        Assert.Equal("https://flowstock.local:7154", document.RootElement.GetProperty("canonical_https_base_url").GetString());
    }

    [Fact]
    public async Task TsdStaticPipelineSupportsHeadForLargeShell()
    {
        var tempRoot = Directory.CreateTempSubdirectory("flowstock-tsd-static-test-");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempRoot.FullName, "index.html"),
                "<!doctype html><html><body>" + new string('x', 4096) + "</body></html>");
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            var app = builder.Build();
            var provider = new PhysicalFileProvider(tempRoot.FullName);
            app.Map("/tsd", tsdApp =>
            {
                tsdApp.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
                tsdApp.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
            });
            await app.StartAsync();

            var client = app.GetTestClient();
            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/tsd/"));

            response.EnsureSuccessStatusCode();
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UdpResponderReturnsUnicastResponseOnLoopbackAndStopsOnCancellation()
    {
        var options = CreateOptions(
            ("FLOWSTOCK_PUBLIC_BASE_URL", "https://flowstock.local:7154"),
            ("FLOWSTOCK_INSTANCE_NAME", "FlowStock"));
        using var server = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        using var client = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        var serverEndpoint = (System.Net.IPEndPoint)server.Client.LocalEndPoint!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var responder = FlowStockDiscoveryUdpService.ReceiveAndRespondOnceAsync(
            server,
            options,
            new FlowStockDiscoveryRateLimiter(),
            cts.Token);
        var request = Encoding.UTF8.GetBytes(
            """{"product":"FlowStock","discovery_protocol_version":1,"nonce":"0123456789abcdef0123456789abcdef"}""");

        await client.SendAsync(request, request.Length, serverEndpoint);
        var received = await client.ReceiveAsync(cts.Token);
        var handled = await responder;

        Assert.True(handled);
        using var document = JsonDocument.Parse(received.Buffer);
        Assert.Equal("0123456789abcdef0123456789abcdef", document.RootElement.GetProperty("nonce").GetString());

        using var cancelCts = new CancellationTokenSource();
        cancelCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FlowStockDiscoveryUdpService.ReceiveAndRespondOnceAsync(
                server,
                options,
                new FlowStockDiscoveryRateLimiter(),
                cancelCts.Token));
    }

    [Fact]
    public async Task UdpMalformedFloodDoesNotConsumeValidRequestRateLimit()
    {
        var options = CreateOptions(
            ("FLOWSTOCK_PUBLIC_BASE_URL", "https://flowstock.local:7154"),
            ("FLOWSTOCK_INSTANCE_NAME", "FlowStock"));
        using var server = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        using var client = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        var serverEndpoint = (System.Net.IPEndPoint)server.Client.LocalEndPoint!;
        var limiter = new FlowStockDiscoveryRateLimiter(
            globalLimitPerWindow: 100,
            perSourceLimitPerWindow: 1,
            maxTrackedSources: 8);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        for (var i = 0; i < 5; i++)
        {
            var malformedResponder = FlowStockDiscoveryUdpService.ReceiveAndRespondOnceAsync(
                server,
                options,
                limiter,
                cts.Token);
            var malformed = Encoding.UTF8.GetBytes("""{"product":"Other","discovery_protocol_version":1,"nonce":"abc"}""");

            await client.SendAsync(malformed, malformed.Length, serverEndpoint);

            Assert.False(await malformedResponder);
        }

        var validResponder = FlowStockDiscoveryUdpService.ReceiveAndRespondOnceAsync(
            server,
            options,
            limiter,
            cts.Token);
        var valid = Encoding.UTF8.GetBytes(
            """{"product":"FlowStock","discovery_protocol_version":1,"nonce":"0123456789abcdef0123456789abcdef"}""");

        await client.SendAsync(valid, valid.Length, serverEndpoint);
        var received = await client.ReceiveAsync(cts.Token);

        Assert.True(await validResponder);
        using var document = JsonDocument.Parse(received.Buffer);
        Assert.Equal("0123456789abcdef0123456789abcdef", document.RootElement.GetProperty("nonce").GetString());
    }

    private static FlowStockDiscoveryOptions CreateOptions(params (string Key, string Value)[] values)
    {
        var data = new Dictionary<string, string?>
        {
            ["FLOWSTOCK_TLS_SERVER_NAME"] = "flowstock.local",
            ["FLOWSTOCK_TLS_SANS"] = "DNS:flowstock.local",
        };
        foreach (var pair in values)
        {
            data[pair.Key] = pair.Value;
        }

        return CreateOptionsFromData(data);
    }

    private static FlowStockDiscoveryOptions CreateOptionsWithoutTlsDefaults(params (string Key, string Value)[] values)
    {
        var data = values.ToDictionary(
            pair => pair.Key,
            pair => (string?)pair.Value);
        return CreateOptionsFromData(data);
    }

    private static FlowStockDiscoveryOptions CreateOptionsFromData(Dictionary<string, string?> data)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(data)
            .Build();

        return FlowStockDiscoveryOptions.FromConfiguration(configuration, "1.2.3-test");
    }
}
