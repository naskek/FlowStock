namespace LightWms.Server;

public sealed class CreateDocRequest
{
    public string? Op { get; set; }
}

public sealed class CreateDocResponse
{
    public string DocUid { get; init; } = string.Empty;
    public string DocRef { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
}

public sealed class AddMoveLineRequest
{
    public string? Barcode { get; set; }
    public double Qty { get; set; }
    public string? FromLocCode { get; set; }
    public string? ToLocCode { get; set; }
    public string? FromHu { get; set; }
    public string? ToHu { get; set; }
    public string? EventId { get; set; }
}

public sealed class CloseDocRequest
{
    public string? EventId { get; set; }
}

public sealed class ApiResult
{
    public ApiResult(bool ok, string? error = null)
    {
        Ok = ok;
        Error = error;
    }

    public bool Ok { get; init; }
    public string? Error { get; init; }
}
