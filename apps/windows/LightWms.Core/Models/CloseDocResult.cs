namespace LightWms.Core.Models;

public sealed class CloseDocResult
{
    public bool Success { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
