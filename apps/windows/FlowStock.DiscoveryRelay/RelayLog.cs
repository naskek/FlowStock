namespace FlowStock.DiscoveryRelay;

public sealed record RelayLogEntry(
    string Outcome,
    string? SourceIp = null,
    int? RequestBytes = null,
    int? ResponseBytes = null,
    long? DurationMs = null,
    string? Detail = null);
