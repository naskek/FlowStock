using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FlowStock.Server.Discovery;

public sealed class FlowStockDiscoveryUdpService(
    FlowStockDiscoveryOptions options,
    ILogger<FlowStockDiscoveryUdpService> logger,
    FlowStockDiscoveryRateLimiter? rateLimiter = null,
    int udpPort = FlowStockDiscoveryOptions.UdpPort) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly FlowStockDiscoveryRateLimiter rateLimiter = rateLimiter ?? new FlowStockDiscoveryRateLimiter();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReceiveAndRespondOnceAsync(udp, options, rateLimiter, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "FlowStock UDP discovery receive failed");
                continue;
            }

        }
    }

    public static async Task<bool> ReceiveAndRespondOnceAsync(
        UdpClient udp,
        FlowStockDiscoveryOptions options,
        FlowStockDiscoveryRateLimiter rateLimiter,
        CancellationToken cancellationToken)
    {
        var received = await udp.ReceiveAsync(cancellationToken);
        var response = TryCreateResponse(received.Buffer, options);
        if (response == null)
        {
            return false;
        }

        if (!rateLimiter.Allow(received.RemoteEndPoint.Address))
        {
            return false;
        }

        await udp.SendAsync(response, response.Length, received.RemoteEndPoint);
        return true;
    }

    public static byte[]? TryCreateResponse(byte[] packet, FlowStockDiscoveryOptions options)
    {
        if (packet.Length == 0 || packet.Length > FlowStockDiscoveryOptions.MaxUdpPacketBytes)
        {
            return null;
        }

        FlowStockDiscoveryUdpRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<FlowStockDiscoveryUdpRequest>(packet, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (request == null
            || request.Product != FlowStockDiscoveryOptions.Product
            || request.DiscoveryProtocolVersion != FlowStockDiscoveryOptions.ProtocolVersion
            || !DiscoveryNonce.IsValid(request.Nonce))
        {
            return null;
        }

        var response = new FlowStockDiscoveryUdpResponse(
            FlowStockDiscoveryOptions.Product,
            FlowStockDiscoveryOptions.ProtocolVersion,
            request.Nonce!,
            options.InstanceName,
            options.CanonicalHttpsBaseUrl,
            options.ApplicationVersion);
        var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        return responseBytes.Length <= FlowStockDiscoveryOptions.MaxUdpPacketBytes ? responseBytes : null;
    }
}

public sealed class FlowStockDiscoveryRateLimiter(
    int globalLimitPerWindow = 120,
    int perSourceLimitPerWindow = 20,
    int maxTrackedSources = 256,
    TimeSpan? window = null,
    Func<DateTimeOffset>? clock = null)
{
    private readonly TimeSpan window = window ?? TimeSpan.FromSeconds(10);
    private readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<IPAddress, RateCounter> perSource = new();
    private RateCounter global = new(DateTimeOffset.MinValue, 0);

    public bool Allow(IPAddress source)
    {
        var now = clock();
        global = NextCounter(global, now);
        if (global.Count >= globalLimitPerWindow)
        {
            return false;
        }

        Cleanup(now);
        perSource.TryGetValue(source, out var sourceCounter);
        sourceCounter = NextCounter(sourceCounter, now);
        if (sourceCounter.Count >= perSourceLimitPerWindow)
        {
            perSource[source] = sourceCounter;
            return false;
        }

        global = global with { Count = global.Count + 1 };
        perSource[source] = sourceCounter with { Count = sourceCounter.Count + 1 };
        Cleanup(now);
        return true;
    }

    public int TrackedSourceCount => perSource.Count;

    private RateCounter NextCounter(RateCounter counter, DateTimeOffset now) =>
        now - counter.WindowStartedAt >= window ? new RateCounter(now, 0) : counter;

    private void Cleanup(DateTimeOffset now)
    {
        foreach (var stale in perSource
                     .Where(pair => now - pair.Value.WindowStartedAt >= window)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            perSource.Remove(stale);
        }

        if (perSource.Count <= maxTrackedSources)
        {
            return;
        }

        foreach (var key in perSource
                     .OrderBy(pair => pair.Value.WindowStartedAt)
                     .Take(perSource.Count - maxTrackedSources)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            perSource.Remove(key);
        }
    }

    private readonly record struct RateCounter(DateTimeOffset WindowStartedAt, int Count);
}

internal static partial class DiscoveryNonce
{
    private static readonly Regex NonceRegex = CreateNonceRegex();

    public static bool IsValid(string? value) =>
        value != null && NonceRegex.IsMatch(value);

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex CreateNonceRegex();
}

internal sealed record FlowStockDiscoveryUdpRequest(
    [property: JsonPropertyName("product")] string? Product,
    [property: JsonPropertyName("discovery_protocol_version")] int DiscoveryProtocolVersion,
    [property: JsonPropertyName("nonce")] string? Nonce);

internal sealed record FlowStockDiscoveryUdpResponse(
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("discovery_protocol_version")] int DiscoveryProtocolVersion,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("instance_name")] string InstanceName,
    [property: JsonPropertyName("canonical_https_base_url")] string CanonicalHttpsBaseUrl,
    [property: JsonPropertyName("application_version")] string ApplicationVersion);
