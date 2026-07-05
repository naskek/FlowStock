namespace FlowStock.DiscoveryRelay;

public static class DiscoveryRelayConstants
{
    public const string Product = "FlowStock";
    public const int ProtocolVersion = 1;
    public const int PublicUdpPort = 7155;
    public const int MaxPacketBytes = 1024;
    public const int PacketBufferBytes = MaxPacketBytes + 1;
    public const int DefaultBackendPort = 17155;
    public const int DefaultTimeoutMs = 2000;
    public const int DefaultMaxInFlight = 64;
    public const int LanPerSourceLimit = 20;
    public const int LanGlobalLimit = 120;
    public const int LocalHealthcheckLimit = 20;
    public static readonly TimeSpan RateLimitWindow = TimeSpan.FromSeconds(10);
}
