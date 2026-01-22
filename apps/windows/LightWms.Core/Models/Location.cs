namespace LightWms.Core.Models;

public sealed class Location
{
    public long Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Code : $"{Code} - {Name}";
}
