using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Concurrent;
using System.Threading.Channels;
using FlowStock.DiscoveryRelay;
using Xunit;

namespace FlowStock.DiscoveryRelay.Tests;

public sealed class UdpDiscoveryRelayTests
{
    [Fact]
    public async Task RelayRoutesConcurrentResponsesToMatchingClients()
    {
        using var backend = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var backendTask = EchoBackendAsync(backend, 2, cts.Token);
        await using var relay = await RelayHarness.StartAsync(backend, maxInFlight: 4, cts.Token);
        using var first = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var second = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var firstPayload = Encoding.UTF8.GetBytes("first");
        var secondPayload = Encoding.UTF8.GetBytes("second");

        await first.SendAsync(firstPayload, firstPayload.Length, relay.PublicEndpoint);
        await second.SendAsync(secondPayload, secondPayload.Length, relay.PublicEndpoint);
        var firstResponseTask = first.ReceiveAsync(cts.Token);
        var secondResponseTask = second.ReceiveAsync(cts.Token);
        var firstResponse = await firstResponseTask;
        var secondResponse = await secondResponseTask;

        Assert.Equal("first", Encoding.UTF8.GetString(firstResponse.Buffer));
        Assert.Equal("second", Encoding.UTF8.GetString(secondResponse.Buffer));
        await backendTask;
    }

    [Fact]
    public async Task AcceptsExactlyMaxPacketBytes()
    {
        using var backend = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var backendTask = EchoBackendAsync(backend, 1, cts.Token);
        await using var relay = await RelayHarness.StartAsync(backend, maxInFlight: 1, cts.Token);
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var payload = Enumerable.Repeat((byte)'x', DiscoveryRelayConstants.MaxPacketBytes).ToArray();

        await client.SendAsync(payload, payload.Length, relay.PublicEndpoint);
        var response = await client.ReceiveAsync(cts.Token);

        Assert.Equal(DiscoveryRelayConstants.MaxPacketBytes, response.Buffer.Length);
        await backendTask;
    }

    [Theory]
    [InlineData(DiscoveryRelayConstants.MaxPacketBytes + 1)]
    [InlineData(4096)]
    public async Task DropsOversizedRequests(int bytes)
    {
        using var backend = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var relay = await RelayHarness.StartAsync(backend, maxInFlight: 1, cts.Token);
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var payload = Enumerable.Repeat((byte)'x', bytes).ToArray();

        await client.SendAsync(payload, payload.Length, relay.PublicEndpoint);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.ReceiveAsync(cts.Token).AsTask());
    }

    [Theory]
    [InlineData(DiscoveryRelayConstants.MaxPacketBytes + 1)]
    [InlineData(4096)]
    public async Task DropsOversizedBackendResponses(int bytes)
    {
        using var backend = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var backendTask = OversizedBackendAsync(backend, bytes, cts.Token);
        await using var relay = await RelayHarness.StartAsync(backend, maxInFlight: 1, cts.Token);
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var payload = Encoding.UTF8.GetBytes("request");

        await client.SendAsync(payload, payload.Length, relay.PublicEndpoint);

        await backendTask;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReceiveAsync(cts.Token).AsTask());
    }

    [Fact]
    public async Task BackendTimeoutProducesNoClientResponse()
    {
        using var backend = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var relay = await RelayHarness.StartAsync(backend, maxInFlight: 1, cts.Token, timeout: TimeSpan.FromMilliseconds(100));
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var payload = Encoding.UTF8.GetBytes("request");

        await client.SendAsync(payload, payload.Length, relay.PublicEndpoint);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReceiveAsync(cts.Token).AsTask());
    }

    [Fact]
    public async Task SinglePublicSendSocketErrorDoesNotBlockNextResponse()
    {
        var publicSocket = new FakePublicSocket();
        publicSocket.FailNextSendWithSocketError(SocketError.NetworkUnreachable);
        var factory = new FakeSocketFactory(publicSocket);
        factory.EnqueueBackendResponse(Encoding.UTF8.GetBytes("first-response"));
        factory.EnqueueBackendResponse(Encoding.UTF8.GetBytes("second-response"));
        var logs = new ConcurrentBag<RelayLogEntry>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var relay = new UdpDiscoveryRelay(
            new DiscoveryRelayOptions(new IPEndPoint(IPAddress.Loopback, 0), new IPEndPoint(IPAddress.Loopback, 17155), TimeSpan.FromSeconds(1), 2),
            socketFactory: factory,
            log: logs.Add);
        var runTask = Task.Run(() => relay.RunAsync(cts.Token));

        await publicSocket.ReceiveQueue.Writer.WriteAsync(new ReceivedDatagram(5, new IPEndPoint(IPAddress.Parse("192.168.1.52"), 30001)));
        await WaitForLogAsync(logs, "drop-send-error", cts.Token);
        await publicSocket.ReceiveQueue.Writer.WriteAsync(new ReceivedDatagram(6, new IPEndPoint(IPAddress.Parse("192.168.1.53"), 30002)));
        var sent = await publicSocket.WaitForSentCountAsync(1, cts.Token);
        cts.Cancel();
        await runTask;

        Assert.Single(sent);
        Assert.Equal("second-response", Encoding.UTF8.GetString(sent[0].Packet));
        Assert.Contains(logs, entry => entry.Outcome == "drop-send-error");
    }

    [Fact]
    public async Task FatalSendLoopErrorStopsRelayRun()
    {
        var publicSocket = new FakePublicSocket();
        publicSocket.FailNextSendFatally(new InvalidOperationException("fatal-send-loop"));
        var factory = new FakeSocketFactory(publicSocket);
        factory.EnqueueBackendResponse(Encoding.UTF8.GetBytes("response"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var relay = new UdpDiscoveryRelay(
            new DiscoveryRelayOptions(new IPEndPoint(IPAddress.Loopback, 0), new IPEndPoint(IPAddress.Loopback, 17155), TimeSpan.FromSeconds(1), 1),
            socketFactory: factory);
        var runTask = Task.Run(() => relay.RunAsync(cts.Token));

        await publicSocket.ReceiveQueue.Writer.WriteAsync(new ReceivedDatagram(7, new IPEndPoint(IPAddress.Parse("192.168.1.52"), 30001)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runTask);
        Assert.Equal("fatal-send-loop", error.Message);
    }

    [Fact]
    public async Task ShutdownWhileBackendResponseIsPendingCompletesPromptly()
    {
        var publicSocket = new FakePublicSocket();
        var factory = new FakeSocketFactory(publicSocket);
        factory.EnqueuePendingBackendResponse();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var relay = new UdpDiscoveryRelay(
            new DiscoveryRelayOptions(new IPEndPoint(IPAddress.Loopback, 0), new IPEndPoint(IPAddress.Loopback, 17155), TimeSpan.FromSeconds(10), 1),
            socketFactory: factory);
        var runTask = Task.Run(() => relay.RunAsync(cts.Token));

        await publicSocket.ReceiveQueue.Writer.WriteAsync(new ReceivedDatagram(7, new IPEndPoint(IPAddress.Parse("192.168.1.52"), 30001)));
        await factory.WaitForBackendReceiveAsync(cts.Token);
        cts.Cancel();
        var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(runTask, completed);
        await runTask;
    }

    [Fact]
    public async Task ShutdownWorkerDeadlineReturnsBeforeDockerStopTimeoutWhenBackendIgnoresCancellation()
    {
        var publicSocket = new FakePublicSocket();
        var factory = new FakeSocketFactory(publicSocket);
        factory.EnqueueStuckBackendResponse();
        var logs = new ConcurrentBag<RelayLogEntry>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var relay = new UdpDiscoveryRelay(
            new DiscoveryRelayOptions(new IPEndPoint(IPAddress.Loopback, 0), new IPEndPoint(IPAddress.Loopback, 17155), TimeSpan.FromSeconds(10), 1),
            socketFactory: factory,
            log: logs.Add);
        var runTask = Task.Run(() => relay.RunAsync(cts.Token));

        await publicSocket.ReceiveQueue.Writer.WriteAsync(new ReceivedDatagram(7, new IPEndPoint(IPAddress.Parse("192.168.1.52"), 30001)));
        await factory.WaitForBackendReceiveAsync(cts.Token);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        cts.Cancel();
        var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(runTask, completed);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Shutdown took {stopwatch.Elapsed}.");
        Assert.Contains(logs, entry => entry.Outcome == "shutdown-worker-timeout");
        await runTask;
    }

    private static async Task EchoBackendAsync(UdpClient backend, int count, CancellationToken cancellationToken)
    {
        for (var i = 0; i < count; i++)
        {
            var received = await backend.ReceiveAsync(cancellationToken);
            await backend.SendAsync(received.Buffer, received.Buffer.Length, received.RemoteEndPoint);
        }
    }

    private static async Task OversizedBackendAsync(UdpClient backend, int bytes, CancellationToken cancellationToken)
    {
        var received = await backend.ReceiveAsync(cancellationToken);
        var response = Enumerable.Repeat((byte)'r', bytes).ToArray();
        await backend.SendAsync(response, response.Length, received.RemoteEndPoint);
    }

    private static async Task WaitForLogAsync(IEnumerable<RelayLogEntry> logs, string outcome, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (logs.Any(entry => entry.Outcome == outcome))
            {
                return;
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    private sealed class RelayHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource cts;
        private readonly Task task;

        private RelayHarness(IPEndPoint publicEndpoint, CancellationTokenSource cts, Task task)
        {
            PublicEndpoint = publicEndpoint;
            this.cts = cts;
            this.task = task;
        }

        public IPEndPoint PublicEndpoint { get; }

        public static async Task<RelayHarness> StartAsync(
            UdpClient backend,
            int maxInFlight,
            CancellationToken parentToken,
            TimeSpan? timeout = null)
        {
            var backendEndpoint = (IPEndPoint)backend.Client.LocalEndPoint!;
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            var relay = new UdpDiscoveryRelay(new DiscoveryRelayOptions(
                new IPEndPoint(IPAddress.Loopback, 0),
                backendEndpoint,
                timeout ?? TimeSpan.FromSeconds(1),
                maxInFlight));
            var task = Task.Run(() => relay.RunAsync(linkedCts.Token));
            while (relay.BoundEndpoint == null)
            {
                await Task.Delay(10, parentToken);
            }

            return new RelayHarness(relay.BoundEndpoint, linkedCts, task);
        }

        public async ValueTask DisposeAsync()
        {
            cts.Cancel();
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                cts.Dispose();
            }
        }
    }

    private sealed class FakeSocketFactory(FakePublicSocket publicSocket) : IUdpRelaySocketFactory
    {
        private readonly Queue<Func<FakeBackendSocket>> backendFactories = new();
        private readonly TaskCompletionSource backendReceiveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void EnqueueBackendResponse(byte[] response) =>
            backendFactories.Enqueue(() => new FakeBackendSocket(response, backendReceiveStarted));

        public void EnqueuePendingBackendResponse() =>
            backendFactories.Enqueue(() => new FakeBackendSocket(null, backendReceiveStarted));

        public void EnqueueStuckBackendResponse() =>
            backendFactories.Enqueue(() => new FakeBackendSocket(null, backendReceiveStarted, ignoreCancellation: true));

        public IUdpPublicSocket CreatePublicSocket() => publicSocket;

        public IUdpBackendSocket CreateBackendSocket(IPEndPoint backendEndpoint) =>
            backendFactories.Count > 0 ? backendFactories.Dequeue()() : new FakeBackendSocket(Array.Empty<byte>(), backendReceiveStarted);

        public Task WaitForBackendReceiveAsync(CancellationToken cancellationToken) =>
            backendReceiveStarted.Task.WaitAsync(cancellationToken);
    }

    private sealed class FakePublicSocket : IUdpPublicSocket
    {
        private readonly List<SentPacket> sent = new();
        private readonly TaskCompletionSource sentChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Exception? nextSendError;
        private IPEndPoint? localEndPoint;

        public Channel<ReceivedDatagram> ReceiveQueue { get; } = Channel.CreateUnbounded<ReceivedDatagram>();

        public IPEndPoint? LocalEndPoint => localEndPoint;

        public void Bind(IPEndPoint endpoint) =>
            localEndPoint = endpoint.Port == 0
                ? new IPEndPoint(endpoint.Address, Random.Shared.Next(20_000, 40_000))
                : endpoint;

        public void FailNextSendWithSocketError(SocketError error) =>
            nextSendError = new SocketException((int)error);

        public void FailNextSendFatally(Exception error) => nextSendError = error;

        public async ValueTask<ReceivedDatagram> ReceiveFromAsync(byte[] buffer, CancellationToken cancellationToken)
        {
            var datagram = await ReceiveQueue.Reader.ReadAsync(cancellationToken);
            Array.Fill(buffer, (byte)'x', 0, datagram.BytesReceived);
            return datagram;
        }

        public ValueTask SendToAsync(byte[] packet, IPEndPoint remoteEndpoint, CancellationToken cancellationToken)
        {
            if (nextSendError != null)
            {
                var error = nextSendError;
                nextSendError = null;
                throw error;
            }

            lock (sent)
            {
                sent.Add(new SentPacket(packet, remoteEndpoint));
            }

            sentChanged.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public async Task<IReadOnlyList<SentPacket>> WaitForSentCountAsync(int count, CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (sent)
                {
                    if (sent.Count >= count)
                    {
                        return sent.ToArray();
                    }
                }

                await sentChanged.Task.WaitAsync(cancellationToken);
            }
        }

        public ValueTask DisposeAsync()
        {
            ReceiveQueue.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeBackendSocket(byte[]? response, TaskCompletionSource receiveStarted, bool ignoreCancellation = false) : IUdpBackendSocket
    {
        private readonly TaskCompletionSource neverCompletes = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask SendAsync(byte[] packet, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken cancellationToken)
        {
            receiveStarted.TrySetResult();
            if (response == null)
            {
                if (ignoreCancellation)
                {
                    await neverCompletes.Task;
                    return 0;
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }

            response.CopyTo(buffer, 0);
            return response.Length;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record SentPacket(byte[] Packet, IPEndPoint RemoteEndpoint);
}
