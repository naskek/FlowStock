namespace LightWms.Core.Models;

public sealed class Partner
{
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Code { get; init; }
    public DateTime CreatedAt { get; init; }

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Code) && !string.IsNullOrWhiteSpace(Name))
            {
                return $"{Code} - {Name}";
            }

            return !string.IsNullOrWhiteSpace(Code) ? Code : Name;
        }
    }
}
