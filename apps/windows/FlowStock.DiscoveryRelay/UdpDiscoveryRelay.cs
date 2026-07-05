using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace FlowStock.DiscoveryRelay;

public sealed class UdpDiscoveryRelay(
    DiscoveryRelayOptions options,
    DiscoveryRelayRateLimiter? rateLimiter = null,
    IUdpRelaySocketFactory? socketFactory = null,
    Action<RelayLogEntry>? log = null)
{
    private static readonly TimeSpan ShutdownWorkerWaitTimeout = TimeSpan.FromSeconds(4);
    private readonly DiscoveryRelayRateLimiter rateLimiter = rateLimiter ?? new DiscoveryRelayRateLimiter();
    private readonly IUdpRelaySocketFactory socketFactory = socketFactory ?? new SocketUdpRelaySocketFactory();
    private readonly Action<RelayLogEntry> log = log ?? (_ => { });
    private readonly SemaphoreSlim slots = new(options.MaxInFlight, options.MaxInFlight);
    private readonly object workerGate = new();
    private readonly List<Task> workerTasks = new();
    private readonly Channel<PendingResponse> responses = Channel.CreateBounded<PendingResponse>(
        new BoundedChannelOptions(options.MaxInFlight)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    public IPEndPoint? BoundEndpoint { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var publicSocket = socketFactory.CreatePublicSocket();
        publicSocket.Bind(options.PublicEndpoint);
        BoundEndpoint = publicSocket.LocalEndPoint!;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sendTask = Task.Run(() => SendLoopAsync(publicSocket, linkedCts.Token), CancellationToken.None);
        var receiveTask = Task.Run(() => ReceiveLoopAsync(publicSocket, linkedCts.Token), CancellationToken.None);

        try
        {
            var completed = await Task.WhenAny(receiveTask, sendTask);
            if (completed == sendTask)
            {
                linkedCts.Cancel();
                responses.Writer.TryComplete();
                await sendTask;
                return;
            }

            await receiveTask;
        }
        finally
        {
            linkedCts.Cancel();
            responses.Writer.TryComplete();
            await WaitForWorkersAsync();
            await ObserveTaskAsync(sendTask);
            await ObserveTaskAsync(receiveTask);
        }
    }

    private async Task ReceiveLoopAsync(IUdpPublicSocket publicSocket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var buffer = new byte[DiscoveryRelayConstants.PacketBufferBytes];
            ReceivedDatagram received;
            try
            {
                received = await publicSocket.ReceiveFromAsync(buffer, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            var remoteEndpoint = received.RemoteEndPoint;
            if (received.BytesReceived is <= 0 or > DiscoveryRelayConstants.MaxPacketBytes)
            {
                log(new RelayLogEntry("drop-size", remoteEndpoint.Address.ToString(), received.BytesReceived));
                continue;
            }

            if (!rateLimiter.Allow(remoteEndpoint.Address))
            {
                log(new RelayLogEntry("drop-rate-limit", remoteEndpoint.Address.ToString(), received.BytesReceived));
                continue;
            }

            if (!slots.Wait(0))
            {
                log(new RelayLogEntry("drop-overload", remoteEndpoint.Address.ToString(), received.BytesReceived));
                continue;
            }

            var packet = buffer.AsSpan(0, received.BytesReceived).ToArray();
            var worker = Task.Run(
                () => RelayOneAsync(packet, remoteEndpoint, cancellationToken),
                CancellationToken.None);
            lock (workerGate)
            {
                workerTasks.Add(worker);
            }

            _ = worker.ContinueWith(
                task =>
                {
                    lock (workerGate)
                    {
                        workerTasks.Remove(task);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task RelayOneAsync(
        byte[] packet,
        IPEndPoint clientEndpoint,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var backend = socketFactory.CreateBackendSocket(options.BackendEndpoint);
            await backend.SendAsync(packet, cancellationToken);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(options.BackendTimeout);
            var response = new byte[DiscoveryRelayConstants.PacketBufferBytes];
            var received = await backend.ReceiveAsync(response, timeoutCts.Token);
            if (received is <= 0 or > DiscoveryRelayConstants.MaxPacketBytes)
            {
                log(new RelayLogEntry("drop-backend-size", clientEndpoint.Address.ToString(), packet.Length, received, stopwatch.ElapsedMilliseconds));
                return;
            }

            var pending = new PendingResponse(
                response.AsMemory(0, received).ToArray(),
                clientEndpoint,
                packet.Length,
                stopwatch.ElapsedMilliseconds);
            try
            {
                await responses.Writer.WriteAsync(pending, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                log(new RelayLogEntry("drop-shutdown", clientEndpoint.Address.ToString(), packet.Length, received, stopwatch.ElapsedMilliseconds));
            }
            catch (ChannelClosedException)
            {
                log(new RelayLogEntry("drop-send-closed", clientEndpoint.Address.ToString(), packet.Length, received, stopwatch.ElapsedMilliseconds));
            }
        }
        catch (OperationCanceledException)
        {
            log(new RelayLogEntry("drop-timeout-or-cancel", clientEndpoint.Address.ToString(), packet.Length, DurationMs: stopwatch.ElapsedMilliseconds));
        }
        catch (Exception error)
        {
            log(new RelayLogEntry("drop-error", clientEndpoint.Address.ToString(), packet.Length, DurationMs: stopwatch.ElapsedMilliseconds, Detail: error.GetType().Name));
        }
        finally
        {
            slots.Release();
        }
    }

    private async Task SendLoopAsync(IUdpPublicSocket publicSocket, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var response in responses.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await publicSocket.SendToAsync(response.Packet, response.ClientEndpoint, cancellationToken);
                    log(new RelayLogEntry(
                        "ok",
                        response.ClientEndpoint.Address.ToString(),
                        response.RequestBytes,
                        response.Packet.Length,
                        response.DurationMs));
                }
                catch (OperationCanceledException)
                {
                    log(new RelayLogEntry("drop-shutdown", response.ClientEndpoint.Address.ToString(), response.RequestBytes, response.Packet.Length, response.DurationMs));
                }
                catch (SocketException error)
                {
                    log(new RelayLogEntry(
                        "drop-send-error",
                        response.ClientEndpoint.Address.ToString(),
                        response.RequestBytes,
                        response.Packet.Length,
                        response.DurationMs,
                        error.SocketErrorCode.ToString()));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task WaitForWorkersAsync()
    {
        Task[] snapshot;
        lock (workerGate)
        {
            snapshot = workerTasks.ToArray();
        }

        if (snapshot.Length == 0)
        {
            return;
        }

        var observed = Task.WhenAll(snapshot.Select(ObserveTaskAsync));
        var completed = await Task.WhenAny(observed, Task.Delay(ShutdownWorkerWaitTimeout));
        if (completed != observed)
        {
            log(new RelayLogEntry("shutdown-worker-timeout", Detail: snapshot.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    private static async Task ObserveTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private sealed record PendingResponse(byte[] Packet, IPEndPoint ClientEndpoint, int RequestBytes, long DurationMs);
}
