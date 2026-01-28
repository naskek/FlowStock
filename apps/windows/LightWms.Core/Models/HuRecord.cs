namespace LightWms.Core.Models;

public sealed class HuRecord
{
    public long Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Status { get; init; } = "ACTIVE";
    public DateTime CreatedAt { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? ClosedAt { get; init; }
    public string? Note { get; init; }
}
