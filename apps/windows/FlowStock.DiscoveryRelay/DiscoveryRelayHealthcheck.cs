using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FlowStock.DiscoveryRelay;

public static class DiscoveryRelayHealthcheck
{
    public static Task<int> RunAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        RunAsync(DiscoveryRelayConstants.PublicUdpPort, timeout, cancellationToken);

    public static async Task<int> RunAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var request = Encoding.UTF8.GetBytes(
            $$"""{"product":"{{DiscoveryRelayConstants.Product}}","discovery_protocol_version":{{DiscoveryRelayConstants.ProtocolVersion}},"nonce":"{{nonce}}"}""");
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect(new IPEndPoint(IPAddress.Loopback, port));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        await socket.SendAsync(request, SocketFlags.None, timeoutCts.Token);
        var buffer = new byte[DiscoveryRelayConstants.PacketBufferBytes];
        var received = await socket.ReceiveAsync(buffer, SocketFlags.None, timeoutCts.Token);
        if (received is <= 0 or > DiscoveryRelayConstants.MaxPacketBytes)
        {
            return 1;
        }

        using var document = JsonDocument.Parse(buffer.AsMemory(0, received));
        var root = document.RootElement;
        return root.TryGetProperty("product", out var product)
            && product.GetString() == DiscoveryRelayConstants.Product
            && root.TryGetProperty("discovery_protocol_version", out var version)
            && version.GetInt32() == DiscoveryRelayConstants.ProtocolVersion
            && root.TryGetProperty("nonce", out var responseNonce)
            && responseNonce.GetString() == nonce
                ? 0
                : 1;
    }
}
