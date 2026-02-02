namespace LightWms.Core.Models;

public sealed class ImportResult
{
    public int Imported { get; set; }
    public int OperationsImported { get; set; }
    public int ItemsUpserted { get; set; }
    public int Duplicates { get; set; }
    public int Errors { get; set; }
    public int DocumentsCreated { get; set; }
    public int LinesImported { get; set; }
    public int HuRegistryErrors { get; set; }
    public IReadOnlyList<string> DeviceIds { get; set; } = Array.Empty<string>();
}
