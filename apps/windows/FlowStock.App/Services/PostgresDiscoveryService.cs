using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FlowStock.App;

public sealed class PostgresDiscoveryService
{
    private static readonly int[] CandidatePorts = { 5432, 15432 };
    private const int TcpConnectTimeoutMs = 250;
    private const int MaxParallelism = 48;
    private static readonly string[] ExcludedInterfaceMarkers =
    {
        "tailscale",
        "wireguard",
        "zerotier",
        "vpn",
        "hyper-v",
        "vethernet",
        "vmware",
        "virtualbox",
        "docker"
    };

    public async Task<IReadOnlyList<PostgresDiscoveryCandidate>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var endpoints = BuildCandidateEndpoints();
        var results = new ConcurrentBag<PostgresDiscoveryCandidate>();
        using var gate = new SemaphoreSlim(MaxParallelism);

        var tasks = endpoints.Select(async endpoint =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (await IsTcpPortOpenAsync(endpoint.Host, endpoint.Port, cancellationToken))
                {
                    results.Add(endpoint);
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        return results
            .DistinctBy(candidate => $"{candidate.Host}:{candidate.Port}")
            .OrderByDescending(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Host, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Port)
            .ToList();
    }

    private static IReadOnlyList<PostgresDiscoveryCandidate> BuildCandidateEndpoints()
    {
        var candidates = new Dictionary<string, PostgresDiscoveryCandidate>(StringComparer.OrdinalIgnoreCase);

        AddCandidate(candidates, "127.0.0.1", 5432, "Этот ПК", priority: 300);
        AddCandidate(candidates, "127.0.0.1", 15432, "Этот ПК", priority: 290);

        foreach (var address in GetPrivateLanAddresses())
        {
            AddCandidate(candidates, address, 5432, "Этот ПК", priority: 280);
            AddCandidate(candidates, address, 15432, "Этот ПК", priority: 270);

            foreach (var lanHost in EnumerateSubnetHosts(address))
            {
                if (string.Equals(lanHost, address, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AddCandidate(candidates, lanHost, 5432, "Локальная сеть", priority: 200);
                AddCandidate(candidates, lanHost, 15432, "Локальная сеть", priority: 190);
            }
        }

        return candidates.Values.ToList();
    }

    private static IEnumerable<string> GetPrivateLanAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel
                || nic.NetworkInterfaceType == NetworkInterfaceType.Unknown)
            {
                continue;
            }

            var descriptor = $"{nic.Name} {nic.Description}".ToLowerInvariant();
            if (ExcludedInterfaceMarkers.Any(marker => descriptor.Contains(marker, StringComparison.Ordinal)))
            {
                continue;
            }

            IPInterfaceProperties? properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch
            {
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                var address = unicast.Address.ToString();
                if (IsPrivateLanAddress(address))
                {
                    yield return address;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateSubnetHosts(string address)
    {
        if (!IPAddress.TryParse(address, out var ipAddress))
        {
            yield break;
        }

        var bytes = ipAddress.GetAddressBytes();
        if (bytes.Length != 4)
        {
            yield break;
        }

        for (var lastOctet = 1; lastOctet <= 254; lastOctet++)
        {
            bytes[3] = (byte)lastOctet;
            yield return new IPAddress(bytes).ToString();
        }
    }

    private static bool IsPrivateLanAddress(string address)
    {
        if (!IPAddress.TryParse(address, out var ipAddress))
        {
            return false;
        }

        var bytes = ipAddress.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        return bytes[0] switch
        {
            10 => true,
            172 when bytes[1] >= 16 && bytes[1] <= 31 => true,
            192 when bytes[1] == 168 => true,
            _ => false
        };
    }

    private static void AddCandidate(
        IDictionary<string, PostgresDiscoveryCandidate> candidates,
        string host,
        int port,
        string source,
        int priority)
    {
        var key = $"{host}:{port}";
        candidates[key] = new PostgresDiscoveryCandidate(host, port, source, priority);
    }

    private static async Task<bool> IsTcpPortOpenAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TcpConnectTimeoutMs);

        try
        {
            await client.ConnectAsync(host, port, timeoutCts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record PostgresDiscoveryCandidate(string Host, int Port, string Source, int Priority)
{
    public string Display => $"{Host}:{Port} ({Source})";
}
