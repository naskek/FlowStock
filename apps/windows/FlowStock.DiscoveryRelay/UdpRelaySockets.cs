using System.Net;
using System.Net.Sockets;

namespace FlowStock.DiscoveryRelay;

public readonly record struct ReceivedDatagram(int BytesReceived, IPEndPoint RemoteEndPoint);

public interface IUdpRelaySocketFactory
{
    IUdpPublicSocket CreatePublicSocket();

    IUdpBackendSocket CreateBackendSocket(IPEndPoint backendEndpoint);
}

public interface IUdpPublicSocket : IAsyncDisposable
{
    IPEndPoint? LocalEndPoint { get; }

    void Bind(IPEndPoint endpoint);

    ValueTask<ReceivedDatagram> ReceiveFromAsync(byte[] buffer, CancellationToken cancellationToken);

    ValueTask SendToAsync(byte[] packet, IPEndPoint remoteEndpoint, CancellationToken cancellationToken);
}

public interface IUdpBackendSocket : IAsyncDisposable
{
    ValueTask SendAsync(byte[] packet, CancellationToken cancellationToken);

    ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken cancellationToken);
}

public sealed class SocketUdpRelaySocketFactory : IUdpRelaySocketFactory
{
    public IUdpPublicSocket CreatePublicSocket() => new SocketUdpPublicSocket();

    public IUdpBackendSocket CreateBackendSocket(IPEndPoint backendEndpoint) =>
        new SocketUdpBackendSocket(backendEndpoint);
}

internal sealed class SocketUdpPublicSocket : IUdpPublicSocket
{
    private readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    public IPEndPoint? LocalEndPoint => (IPEndPoint?)socket.LocalEndPoint;

    public void Bind(IPEndPoint endpoint) => socket.Bind(endpoint);

    public async ValueTask<ReceivedDatagram> ReceiveFromAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, cancellationToken);
            return new ReceivedDatagram(received.ReceivedBytes, (IPEndPoint)received.RemoteEndPoint);
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.MessageSize)
        {
            return new ReceivedDatagram(DiscoveryRelayConstants.PacketBufferBytes, new IPEndPoint(IPAddress.Any, 0));
        }
    }

    public async ValueTask SendToAsync(byte[] packet, IPEndPoint remoteEndpoint, CancellationToken cancellationToken) =>
        _ = await socket.SendToAsync(packet, SocketFlags.None, remoteEndpoint, cancellationToken);

    public ValueTask DisposeAsync()
    {
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class SocketUdpBackendSocket : IUdpBackendSocket
{
    private readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    public SocketUdpBackendSocket(IPEndPoint backendEndpoint)
    {
        socket.Connect(backendEndpoint);
    }

    public async ValueTask SendAsync(byte[] packet, CancellationToken cancellationToken) =>
        _ = await socket.SendAsync(packet, SocketFlags.None, cancellationToken);

    public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken cancellationToken) =>
        socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);

    public ValueTask DisposeAsync()
    {
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
