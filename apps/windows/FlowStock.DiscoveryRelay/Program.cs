using System.Runtime.InteropServices;

namespace FlowStock.DiscoveryRelay;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = DiscoveryRelayOptions.FromEnvironment();
            if (args.Length > 0 && string.Equals(args[0], "healthcheck", StringComparison.OrdinalIgnoreCase))
            {
                return await DiscoveryRelayHealthcheck.RunAsync(options.BackendTimeout, CancellationToken.None);
            }

            if (args.Length > 0 && string.Equals(args[0], "backend-healthcheck", StringComparison.OrdinalIgnoreCase))
            {
                return await DiscoveryRelayHealthcheck.RunAsync(
                    options.BackendEndpoint.Port,
                    options.BackendTimeout,
                    CancellationToken.None);
            }

            using var cts = new CancellationTokenSource();
            using var signalRegistrations = RegisterShutdownSignals(cts);
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cts.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            try
            {
                var relay = new UdpDiscoveryRelay(options, log: Log);
                await relay.RunAsync(cts.Token);
                return 0;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"FlowStock discovery relay failed: {error.Message}");
            return 1;
        }
    }

    private static IDisposable RegisterShutdownSignals(CancellationTokenSource cts)
    {
        var registrations = new List<IDisposable>();
        if (OperatingSystem.IsLinux())
        {
            registrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                cts.Cancel();
            }));
            registrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
            {
                context.Cancel = true;
                cts.Cancel();
            }));
        }

        return new CompositeDisposable(registrations);
    }

    private sealed class CompositeDisposable(IReadOnlyList<IDisposable> registrations) : IDisposable
    {
        public void Dispose()
        {
            foreach (var registration in registrations)
            {
                registration.Dispose();
            }
        }
    }

    private static void Log(RelayLogEntry entry)
    {
        Console.Error.WriteLine(
            "FlowStock discovery relay outcome={0} source={1} request_bytes={2} response_bytes={3} duration_ms={4} detail={5}",
            entry.Outcome,
            entry.SourceIp ?? "",
            entry.RequestBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            entry.ResponseBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            entry.DurationMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            entry.Detail ?? "");
    }
}
