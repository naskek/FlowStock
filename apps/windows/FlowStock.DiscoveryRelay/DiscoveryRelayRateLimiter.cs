using System.Net;

namespace FlowStock.DiscoveryRelay;

public sealed class DiscoveryRelayRateLimiter(Func<DateTimeOffset>? clock = null)
{
    private readonly object gate = new();
    private readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<IPAddress, RateCounter> perSource = new();
    private RateCounter lanGlobal = new(DateTimeOffset.MinValue, 0);
    private RateCounter localHealthcheck = new(DateTimeOffset.MinValue, 0);

    public bool Allow(IPAddress source)
    {
        lock (gate)
        {
            var now = clock();
            Cleanup(now);
            if (IPAddress.IsLoopback(source))
            {
                localHealthcheck = NextCounter(localHealthcheck, now);
                if (localHealthcheck.Count >= DiscoveryRelayConstants.LocalHealthcheckLimit)
                {
                    return false;
                }

                localHealthcheck = localHealthcheck with { Count = localHealthcheck.Count + 1 };
                return true;
            }

            lanGlobal = NextCounter(lanGlobal, now);
            if (lanGlobal.Count >= DiscoveryRelayConstants.LanGlobalLimit)
            {
                return false;
            }

            perSource.TryGetValue(source, out var sourceCounter);
            sourceCounter = NextCounter(sourceCounter, now);
            if (sourceCounter.Count >= DiscoveryRelayConstants.LanPerSourceLimit)
            {
                perSource[source] = sourceCounter;
                return false;
            }

            lanGlobal = lanGlobal with { Count = lanGlobal.Count + 1 };
            perSource[source] = sourceCounter with { Count = sourceCounter.Count + 1 };
            return true;
        }
    }

    private RateCounter NextCounter(RateCounter counter, DateTimeOffset now) =>
        now - counter.WindowStartedAt >= DiscoveryRelayConstants.RateLimitWindow
            ? new RateCounter(now, 0)
            : counter;

    private void Cleanup(DateTimeOffset now)
    {
        foreach (var stale in perSource
                     .Where(pair => now - pair.Value.WindowStartedAt >= DiscoveryRelayConstants.RateLimitWindow)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            perSource.Remove(stale);
        }
    }

    private readonly record struct RateCounter(DateTimeOffset WindowStartedAt, int Count);
}
