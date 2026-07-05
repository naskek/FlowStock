using System.Net;
using FlowStock.DiscoveryRelay;
using Xunit;

namespace FlowStock.DiscoveryRelay.Tests;

public sealed class DiscoveryRelayRateLimiterTests
{
    [Fact]
    public void HealthcheckTrafficDoesNotConsumeLanQuotas()
    {
        var limiter = new DiscoveryRelayRateLimiter();
        for (var i = 0; i < DiscoveryRelayConstants.LocalHealthcheckLimit; i++)
        {
            Assert.True(limiter.Allow(IPAddress.Loopback));
        }

        for (var i = 1; i <= DiscoveryRelayConstants.LanGlobalLimit; i++)
        {
            Assert.True(limiter.Allow(IPAddress.Parse($"10.10.0.{i}")));
        }

        Assert.False(limiter.Allow(IPAddress.Parse("10.10.1.1")));
    }

    [Fact]
    public void LocalHealthcheckBucketStopsAfterTwentyRequests()
    {
        var limiter = new DiscoveryRelayRateLimiter();
        for (var i = 0; i < DiscoveryRelayConstants.LocalHealthcheckLimit; i++)
        {
            Assert.True(limiter.Allow(IPAddress.Loopback));
        }

        Assert.False(limiter.Allow(IPAddress.Loopback));
    }

    [Fact]
    public void OneLanSourceDoesNotBlockAnotherSource()
    {
        var limiter = new DiscoveryRelayRateLimiter();
        var first = IPAddress.Parse("192.168.1.52");
        for (var i = 0; i < DiscoveryRelayConstants.LanPerSourceLimit; i++)
        {
            Assert.True(limiter.Allow(first));
        }

        Assert.False(limiter.Allow(first));
        Assert.True(limiter.Allow(IPAddress.Parse("192.168.1.53")));
    }
}
